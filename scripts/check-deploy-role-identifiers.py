#!/usr/bin/env python3
"""Guard case-sensitive PostgreSQL role names used by the VPS deploy."""

from pathlib import Path

workflow = Path(".github/workflows/deploy-vps.yml").read_text(encoding="utf-8")
start_marker = "# Provision the non-administrative runtime role"
end_marker = "# The build completed successfully;"
assert start_marker in workflow and end_marker in workflow, "Cannot locate deploy role provisioning"
sql = workflow.split(start_marker, 1)[1].split(end_marker, 1)[0]

# PostgreSQL folds unquoted identifiers to lower case, but the Npgsql
# connection strings send these mixed-case usernames verbatim.
assert "Username=MOODLE_app;Password=$APP_ROLE_PASSWORD" in workflow

for role in ("MOODLE_app", "MOODLE_diag_reader"):
    role_in_sql = sql.count(role)
    quoted_identifiers = sql.count(f'"{role}"')
    role_lookup_literals = sql.count(f"rolname = '{role}'")
    assert role_in_sql > 0, f"Missing role {role} in SQL"
    assert role_in_sql == quoted_identifiers + role_lookup_literals, (
        f"{role} has an unquoted SQL reference that PostgreSQL would fold to lower case"
    )
    assert f'CREATE ROLE "{role}"' in sql, f"Missing quoted CREATE ROLE for {role}"
    assert f'ALTER ROLE "{role}"' in sql, f"Missing quoted ALTER ROLE for {role}"
    assert role_lookup_literals == 1, f"Expected exact-case pg_roles lookup for {role}"

print("PostgreSQL runtime/diagnostics role identifiers match connection configuration.")
