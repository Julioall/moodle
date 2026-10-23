#!/usr/bin/env bash
set -u

domain="${1:-}"
timeout_seconds="${2:-300}"
retry_seconds="${3:-10}"
health_path="${4:-/health}"

if [ -z "$domain" ]; then
  echo "usage: check-public-readiness.sh <domain> [timeout-seconds] [retry-seconds] [health-path]" >&2
  exit 2
fi

case "$timeout_seconds" in
  ''|*[!0-9]*) echo "timeout-seconds must be an integer" >&2; exit 2 ;;
esac

case "$retry_seconds" in
  ''|*[!0-9]*) echo "retry-seconds must be an integer" >&2; exit 2 ;;
esac

if [ "$timeout_seconds" -lt 1 ] || [ "$retry_seconds" -lt 1 ]; then
  echo "timeout-seconds and retry-seconds must be greater than zero" >&2
  exit 2
fi

started_at=$SECONDS
deadline=$((started_at + timeout_seconds))
attempt=0
last_state="not checked"

while [ "$SECONDS" -le "$deadline" ]; do
  attempt=$((attempt + 1))

  if timeout 8s getent ahosts "$domain" >/dev/null 2>&1; then
    if curl --fail --silent --show-error --connect-timeout 5 --max-time 15 "https://$domain$health_path" >/dev/null; then
      elapsed=$((SECONDS - started_at))
      echo "Public endpoint ready after ${elapsed}s (attempt ${attempt}): https://$domain$health_path"
      exit 0
    fi
    last_state="DNS resolved, but HTTPS healthcheck failed"
  else
    last_state="system resolver could not resolve the domain"
  fi

  elapsed=$((SECONDS - started_at))
  if [ "$SECONDS" -ge "$deadline" ]; then
    break
  fi

  echo "Public endpoint not ready after ${elapsed}s (attempt ${attempt}): ${last_state}. Retrying in ${retry_seconds}s..."
  sleep "$retry_seconds"
done

echo "Public endpoint did not become ready within ${timeout_seconds}s: ${last_state}." >&2

echo >&2
echo "Resolver configuration:" >&2
cat /etc/resolv.conf >&2 2>/dev/null || true

echo >&2
echo "System resolver result for $domain:" >&2
timeout 8s getent ahosts "$domain" >&2 2>/dev/null || true

echo >&2
echo "Google Public DNS A diagnostic (bypassing the local resolver):" >&2
curl --silent --show-error --connect-timeout 5 --max-time 15 \
  --resolve dns.google:443:8.8.8.8 \
  "https://dns.google/resolve?name=$domain&type=A" >&2 || true

echo >&2
echo "Google Public DNS AAAA diagnostic (bypassing the local resolver):" >&2
curl --silent --show-error --connect-timeout 5 --max-time 15 \
  --resolve dns.google:443:8.8.8.8 \
  "https://dns.google/resolve?name=$domain&type=AAAA" >&2 || true

echo >&2
exit 1
