#!/usr/bin/env bash
set -euo pipefail

script_path=$0
trap 'rm -f -- "$script_path"' EXIT

MODE=${1:-}
WINDOW_HOURS=${2:-24}
QUERY_KIND=${3:-recent}
IDENTIFIER=${4:-}
MAX_ROWS=${5:-100}
DATABASE=${6:-MOODLE}
INCLUDE_RAW_PAYLOAD=${7:-false}

case "$MODE" in
  preflight|query) ;;
  *) echo 'Invalid diagnostics operation.' >&2; exit 2 ;;
esac
case "$WINDOW_HOURS" in
  1|6|24|168) ;;
  *) echo 'Invalid diagnostics window.' >&2; exit 2 ;;
esac
case "$MAX_ROWS" in
  20|100|500) ;;
  *) echo 'Invalid diagnostics row limit.' >&2; exit 2 ;;
esac
case "$DATABASE" in
  ''|*[!A-Za-z0-9_]*) echo 'Invalid database identifier.' >&2; exit 2 ;;
esac
case "$INCLUDE_RAW_PAYLOAD" in
  true|false) ;;
  *) echo 'Invalid raw payload option.' >&2; exit 2 ;;
esac
if [ "$INCLUDE_RAW_PAYLOAD" = true ] && { [ "$MODE" != query ] || [ "$QUERY_KIND" != call_id ]; }; then
  echo 'Raw payload retrieval requires a single call_id query.' >&2
  exit 2
fi

if [ "$MODE" = preflight ]; then
  python3 - "$WINDOW_HOURS" <<'PY'
import datetime as dt
import json
import math
import re
import shutil
import subprocess
import sys

window_hours = int(sys.argv[1])
allowed_metrics = {
    "moodle_webservice_calls",
    "moodle_webservice_failures",
    "moodle_webservice_transport_attempts",
    "moodle_webservice_retries",
}

def run(args, timeout=45):
    try:
        return subprocess.run(args, capture_output=True, text=True, timeout=timeout, check=False)
    except (OSError, subprocess.TimeoutExpired):
        return None

def docker_format(args):
    result = run(["docker", *args])
    return result.stdout.strip() if result and result.returncode == 0 else ""

def parse_state(value):
    if isinstance(value, dict):
        return value
    if isinstance(value, list):
        result = {}
        for item in value:
            if isinstance(item, dict) and "Key" in item:
                result[str(item["Key"])] = item.get("Value")
        return result
    return {}

def parse_timestamp(value):
    if value is None:
        return None
    try:
        parsed = dt.datetime.fromisoformat(str(value).replace("Z", "+00:00"))
        if parsed.tzinfo is None:
            parsed = parsed.replace(tzinfo=dt.timezone.utc)
        return parsed
    except (TypeError, ValueError):
        return None

def metric_records(line):
    docker_stamp = None
    payload = line
    prefix = re.match(r"^(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d+)?Z)\s+(.*)$", line)
    if prefix:
        docker_stamp, payload = prefix.groups()
    try:
        item = json.loads(payload)
    except (json.JSONDecodeError, TypeError):
        item = None
    state = parse_state(item.get("State") if isinstance(item, dict) else None)
    message = ""
    if isinstance(item, dict):
        message = str(item.get("Message") or item.get("RenderedMessage") or item.get("message") or item.get("@m") or "")
        state.update(parse_state(item.get("Attributes")))
        state.update(parse_state(item.get("Properties")))
    if state.get("Metric") is None or state.get("Count") is None:
        for key, value in re.findall(r"([A-Za-z][A-Za-z0-9_]*)=([^\s]+)", message or payload):
            state.setdefault(key, value.strip('"'))
    metric = state.get("Metric")
    count = state.get("Count")
    if metric not in allowed_metrics:
        return None
    try:
        count = int(count)
    except (TypeError, ValueError):
        return None
    if count < 0:
        return None
    parsed = parse_timestamp(state.get("WindowEndUtc"))
    source = "WindowEndUtc" if parsed is not None else None
    if parsed is None:
        for key in ("Timestamp", "timestamp", "@t"):
            parsed = parse_timestamp(state.get(key) or (item.get(key) if isinstance(item, dict) else None))
            if parsed is not None:
                source = key
                break
    if parsed is None:
        parsed = parse_timestamp(docker_stamp)
        if parsed is not None:
            source = "dockerTimestamp"
    return metric, count, parsed, source

now = dt.datetime.now(dt.timezone.utc)
metrics_result = run(["docker", "logs", "--timestamps", f"--since={window_hours}h", "--tail", "25000", "moodle-app"], timeout=60)
totals = {name: 0 for name in allowed_metrics}
windows = set()
timestamp_sources = set()
if metrics_result and metrics_result.returncode == 0:
    for line in metrics_result.stdout.splitlines():
        record = metric_records(line)
        if record:
            name, count, stamp, stamp_source = record
            totals[name] += count
            if stamp:
                windows.add(stamp)
                timestamp_sources.add(stamp_source)

coverage_hours = None
if windows:
    coverage_hours = max((max(windows) - min(windows)).total_seconds() / 3600, 1 / 60)

calls = totals["moodle_webservice_calls"]
attempts = totals["moodle_webservice_transport_attempts"]
failures = totals["moodle_webservice_failures"]
retries = totals["moodle_webservice_retries"]
# The diagnostics stream persists terminal records only for observed calls that
# need diagnostics, plus failed transport attempts. Bound terminal rows by all
# calls and failed-attempt rows by final failures plus extra transport retries.
events_upper_bound = calls + failures + retries
daily_event_upper_bound = None
projection_30d_bytes = None
daily_raw_payload_upper_bound = None
raw_payload_8d_upper_bound_bytes = None
if coverage_hours is not None:
    daily_event_upper_bound = math.ceil(events_upper_bound * 24 / coverage_hours)
    # Conservative planning allowance: the serialized event contract is capped at 4 KiB;
    # 8 KiB per row reserves room for row and index overhead until production size is measured.
    projection_30d_bytes = daily_event_upper_bound * 8192 * 30
    daily_raw_payload_upper_bound = math.ceil((failures + retries) * 24 / coverage_hours)
    # Raw bodies are capped at 1 MiB each and can remain until the next 24-hour cleanup pass.
    raw_payload_8d_upper_bound_bytes = daily_raw_payload_upper_bound * 1048576 * 8
projection_total_bytes = None if projection_30d_bytes is None or raw_payload_8d_upper_bound_bytes is None else projection_30d_bytes + raw_payload_8d_upper_bound_bytes

docker_root = docker_format(["info", "--format", "{{.DockerRootDir}}"])
postgres_source = docker_format([
    "inspect", "--format",
    '{{range .Mounts}}{{if eq .Destination "/var/lib/postgresql/data"}}{{.Source}}{{end}}{{end}}',
    "moodle-postgres",
])

def free_bytes(path):
    if not path:
        return None
    try:
        return shutil.disk_usage(path).free
    except OSError:
        return None

log_configs = {}
for name in ("moodle-app", "moodle-postgres", "moodle-caddy"):
    raw = docker_format(["inspect", "--format", '{{.HostConfig.LogConfig.Type}}|{{json .HostConfig.LogConfig.Config}}', name])
    if not raw:
        log_configs[name] = None
        continue
    driver, separator, config_json = raw.partition("|")
    try:
        config = json.loads(config_json) if separator else {}
    except json.JSONDecodeError:
        config = {}
    log_configs[name] = {"driver": driver, "options": config}

images_result = run(["docker", "ps", "--format", "{{.Image}}"])
collector_patterns = re.compile(r"(otel|opentelemetry|datadog|newrelic|fluentbit|fluentd|promtail|vector|grafana|loki)", re.I)
collector_images = sorted({line.strip() for line in (images_result.stdout.splitlines() if images_result and images_result.returncode == 0 else []) if collector_patterns.search(line)})

free_candidates = [value for value in (free_bytes(docker_root), free_bytes(postgres_source)) if value is not None]
free_min = min(free_candidates) if free_candidates else None
capacity_ok = None if projection_total_bytes is None or free_min is None else free_min >= 2 * projection_total_bytes
rotation_ok = all(
    log_configs.get(name) is not None
    and log_configs[name]["driver"] == "json-file"
    and log_configs[name]["options"].get("max-size") == "10m"
    and log_configs[name]["options"].get("max-file") == "5"
    for name in ("moodle-app", "moodle-postgres", "moodle-caddy")
)

report = {
    "schemaVersion": 1,
    "mode": "preflight",
    "capturedAtUtc": now.isoformat(),
    "requestedWindowHours": window_hours,
    "metricsLogAvailable": bool(metrics_result and metrics_result.returncode == 0),
    "metricsWindowsObserved": len(windows),
    "metricsTimestampSources": sorted(timestamp_sources),
    "metricsCoverageHours": round(coverage_hours, 3) if coverage_hours is not None else None,
    "moodleWebServiceCalls": calls,
    "moodleWebServiceFailures": failures,
    "moodleTransportAttempts": attempts,
    "moodleTransportRetries": retries,
    "dailyEventUpperBound": daily_event_upper_bound,
    "eventBytesPlanningAssumption": 8192,
    "projection30dBytes": projection_30d_bytes,
    "dailyRawPayloadUpperBound": daily_raw_payload_upper_bound,
    "rawPayloadBytesPerErrorUpperBound": 1048576,
    "rawPayloadRetentionDaysWithCleanupGrace": 8,
    "rawPayload8dUpperBoundBytes": raw_payload_8d_upper_bound_bytes,
    "projectionTotalBytes": projection_total_bytes,
    "requiredFreeBytesAt2x": 2 * projection_total_bytes if projection_total_bytes is not None else None,
    "dockerRootFreeBytes": free_bytes(docker_root),
    "postgresDataFreeBytes": free_bytes(postgres_source),
    "minimumRelevantFreeBytes": free_min,
    "capacityGateEstimate": capacity_ok,
    "containerLogConfiguration": log_configs,
    "rotationGate": rotation_ok,
    "possibleCollectorImages": collector_images,
    "externalLogAndBackupDestinations": "not_discoverable_from_docker_container_metadata",
}
print(json.dumps(report, separators=(",", ":")))
PY
  exit 0
fi

case "$QUERY_KIND" in
  recent) FILTER="\"CreatedAtUtc\" >= now() - interval '${WINDOW_HOURS} hours'" ;;
  capacity) FILTER='' ;;
  call_id)
    [[ "$IDENTIFIER" =~ ^[a-f0-9]{32}$ ]] || { echo 'Invalid diagnostic identifier.' >&2; exit 2; }
    FILTER="\"CallId\" = '${IDENTIFIER}'"
    ;;
  retry_group_id)
    [[ "$IDENTIFIER" =~ ^[a-f0-9]{32}$ ]] || { echo 'Invalid diagnostic identifier.' >&2; exit 2; }
    FILTER="\"RetryGroupId\" = '${IDENTIFIER}'"
    ;;
  *) echo 'Invalid query mode.' >&2; exit 2 ;;
esac

IFS= read -r DIAGNOSTICS_PASSWORD || { echo 'Diagnostics credential is missing.' >&2; exit 2; }
[[ "$DIAGNOSTICS_PASSWORD" =~ ^[a-f0-9]{64}$ ]] || { echo 'Diagnostics credential format is invalid.' >&2; exit 2; }

if [ "$QUERY_KIND" = capacity ]; then
  SQL="SELECT COALESCE(json_agg(json_build_object(
  'estimatedLiveRows', GREATEST(stats.\"n_live_tup\", 0),
  'heapBytes', pg_relation_size(stats.oid),
  'indexBytes', pg_indexes_size(stats.oid),
  'totalRelationBytes', pg_total_relation_size(stats.oid),
  'estimatedBytesPerLiveRow', CASE WHEN stats.\"n_live_tup\" > 0 THEN CEIL(pg_total_relation_size(stats.oid)::numeric / stats.\"n_live_tup\") ELSE NULL END,
  'rawPayloadRows', GREATEST(payload_stats.\"n_live_tup\", 0),
  'rawPayloadRelationBytes', pg_total_relation_size(payload_stats.oid),
  'hasDiagnosticsSelect', has_table_privilege(current_user, 'public.moodle_call_diagnostics', 'SELECT'),
  'hasRawPayloadSelect', has_table_privilege(current_user, 'public.moodle_call_diagnostic_payloads', 'SELECT'),
  'hasSchemaVersionSelect', has_table_privilege(current_user, 'public.MOODLE_schema_versions', 'SELECT'),
  'hasConnectorClientsSelect', has_table_privilege(current_user, 'public.connector_clients', 'SELECT'),
  'hasPublicCreate', has_schema_privilege(current_user, 'public', 'CREATE'),
  'isSuperuser', (SELECT r.rolsuper FROM pg_roles AS r WHERE r.rolname = current_user),
  'canCreateDb', (SELECT r.rolcreatedb FROM pg_roles AS r WHERE r.rolname = current_user),
  'canCreateRole', (SELECT r.rolcreaterole FROM pg_roles AS r WHERE r.rolname = current_user),
  'canModifyDiagnostics', has_table_privilege(current_user, 'public.moodle_call_diagnostics', 'INSERT') OR has_table_privilege(current_user, 'public.moodle_call_diagnostics', 'UPDATE') OR has_table_privilege(current_user, 'public.moodle_call_diagnostics', 'DELETE') OR has_table_privilege(current_user, 'public.moodle_call_diagnostics', 'TRUNCATE'),
  'canModifyRawPayloads', has_table_privilege(current_user, 'public.moodle_call_diagnostic_payloads', 'INSERT') OR has_table_privilege(current_user, 'public.moodle_call_diagnostic_payloads', 'UPDATE') OR has_table_privilege(current_user, 'public.moodle_call_diagnostic_payloads', 'DELETE') OR has_table_privilege(current_user, 'public.moodle_call_diagnostic_payloads', 'TRUNCATE'),
  'canAssumeDiagnosticsOwner', (SELECT pg_has_role(current_user, c.relowner, 'MEMBER') FROM pg_class AS c JOIN pg_namespace AS n ON n.oid = c.relnamespace WHERE n.nspname = 'public' AND c.relname = 'moodle_call_diagnostics')
)), '[]'::json)::text
FROM (
  SELECT c.oid, COALESCE(s.n_live_tup, 0) AS \"n_live_tup\"
  FROM pg_class AS c
  JOIN pg_namespace AS n ON n.oid = c.relnamespace
  LEFT JOIN pg_stat_user_tables AS s ON s.relid = c.oid
  WHERE n.nspname = 'public' AND c.relname = 'moodle_call_diagnostics'
) AS stats
CROSS JOIN (
  SELECT c.oid, COALESCE(s.n_live_tup, 0) AS \"n_live_tup\"
  FROM pg_class AS c
  JOIN pg_namespace AS n ON n.oid = c.relnamespace
  LEFT JOIN pg_stat_user_tables AS s ON s.relid = c.oid
  WHERE n.nspname = 'public' AND c.relname = 'moodle_call_diagnostic_payloads'
) AS payload_stats"
else
if [ "$INCLUDE_RAW_PAYLOAD" = true ]; then
  RAW_PAYLOAD_EXPRESSION="CASE WHEN q.\"Payload\" IS NULL THEN NULL ELSE encode(q.\"Payload\", 'base64') END"
else
  RAW_PAYLOAD_EXPRESSION='NULL::text'
fi
SQL="SELECT COALESCE(json_agg(json_build_object(
  'createdAtUtc', q.\"CreatedAtUtc\", 'eventType', q.\"EventType\", 'eventId', q.\"EventId\",
  'callId', q.\"CallId\", 'retryGroupId', q.\"RetryGroupId\", 'attemptNumber', q.\"AttemptNumber\",
  'attemptCount', q.\"AttemptCount\", 'moodleFunction', q.\"MoodleFunction\", 'operationKind', q.\"OperationKind\",
  'errorCategory', q.\"ErrorCategory\", 'classificationReason', q.\"ClassificationReason\",
  'moodleErrorCode', q.\"MoodleErrorCode\", 'moodleExceptionType', q.\"MoodleExceptionType\",
  'codeState', q.\"CodeState\", 'exceptionState', q.\"ExceptionState\", 'httpStatus', q.\"HttpStatus\",
  'responseKind', q.\"ResponseKind\", 'transportErrorType', q.\"TransportErrorType\",
  'socketErrorCode', q.\"SocketErrorCode\", 'retryDisposition', q.\"RetryDisposition\",
  'retryDelayMs', q.\"RetryDelayMs\", 'outcome', q.\"Outcome\", 'lastFailureEventId', q.\"LastFailureEventId\",
  'durationMs', q.\"DurationMs\", 'safeMessage', q.\"SafeMessage\",
  'rawPayloadState', q.\"RawPayloadState\", 'rawPayloadSizeBytes', q.\"RawPayloadSizeBytes\",
  'rawPayloadSha256', q.\"RawPayloadSha256\", 'rawPayloadContentType', q.\"RawPayloadContentType\",
  'rawPayloadAvailable', q.\"Payload\" IS NOT NULL,
  'rawPayloadBase64', ${RAW_PAYLOAD_EXPRESSION}
) ORDER BY q.\"CreatedAtUtc\" DESC, q.\"EventId\"), '[]'::json)::text
FROM (
  SELECT d.\"CreatedAtUtc\", d.\"EventType\", d.\"EventId\", d.\"CallId\", d.\"RetryGroupId\", d.\"AttemptNumber\",
    d.\"AttemptCount\", d.\"MoodleFunction\", d.\"OperationKind\", d.\"ErrorCategory\", d.\"ClassificationReason\",
    d.\"MoodleErrorCode\", d.\"MoodleExceptionType\", d.\"CodeState\", d.\"ExceptionState\", d.\"HttpStatus\",
    d.\"ResponseKind\", d.\"TransportErrorType\", d.\"SocketErrorCode\", d.\"RetryDisposition\", d.\"RetryDelayMs\",
    d.\"Outcome\", d.\"LastFailureEventId\", d.\"DurationMs\", d.\"SafeMessage\", d.\"RawPayloadState\",
    d.\"RawPayloadSizeBytes\", d.\"RawPayloadSha256\", d.\"RawPayloadContentType\", p.\"Payload\"
  FROM moodle_call_diagnostics AS d
  LEFT JOIN moodle_call_diagnostic_payloads AS p ON p.\"EventId\" = d.\"EventId\"
  WHERE ${FILTER}
  ORDER BY d.\"CreatedAtUtc\" DESC, d.\"EventId\" LIMIT ${MAX_ROWS}
) AS q"
fi

container_script='set -eu; umask 077; passfile="$(mktemp)"; trap '\''rm -f -- "$passfile"'\'' EXIT; cat > "$passfile"; PGPASSFILE="$passfile" psql -X -q -t -A -v ON_ERROR_STOP=1 -v VERBOSITY=sqlstate -h 127.0.0.1 -p 5432 -U moodle_diagnostics_operator -d "$2" -c "$1"'
result=$(
  printf '127.0.0.1:5432:%s:moodle_diagnostics_operator:%s\n' "$DATABASE" "$DIAGNOSTICS_PASSWORD" |
    docker exec -i moodle-postgres sh -c "$container_script" sh "$SQL" "$DATABASE" 2>&1
) || {
  sqlstate=$(printf '%s\n' "$result" | sed -nE 's/^ERROR:[[:space:]]*([0-9A-Z]{5})[[:space:]]*$/\1/p' | head -n 1)
  if [[ "$sqlstate" =~ ^[0-9A-Z]{5}$ ]]; then
    echo "Sanitized diagnostics query failed (SQLSTATE ${sqlstate})." >&2
  else
    echo 'Sanitized diagnostics query failed (SQLSTATE unavailable).' >&2
  fi
  unset result
  exit 1
}
unset DIAGNOSTICS_PASSWORD
python3 -c 'import json,sys; value=json.load(sys.stdin); assert isinstance(value,list); print(json.dumps({"schemaVersion":2,"mode":"query","rawPayloadIncluded":sys.argv[1]=="true","rowCount":len(value),"rows":value},separators=(",",":")))' "$INCLUDE_RAW_PAYLOAD" <<< "$result"
