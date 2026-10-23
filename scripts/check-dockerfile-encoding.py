from pathlib import Path

path = Path("deploy/Dockerfile")
data = path.read_bytes()

errors: list[str] = []

if data.startswith(b"\xef\xbb\xbf"):
    errors.append("Dockerfile contains an UTF-8 BOM")

try:
    data.decode("utf-8")
except UnicodeDecodeError:
    errors.append("Dockerfile must be valid UTF-8")

first_line = data.splitlines()[0] if data else b""
if first_line.startswith(b"# syntax="):
    errors.append("Dockerfile must use the built-in Dockerfile frontend and must not declare an external syntax frontend")
elif not first_line.startswith(b"FROM "):
    errors.append("Dockerfile must start with a FROM instruction")

if errors:
    for error in errors:
        print(f"::error::{error}")
    raise SystemExit(1)

print("Dockerfile encoding and built-in frontend policy are valid")
