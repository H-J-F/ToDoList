"""Read-only, targeted pre-publication check of tracked history and pending files.
Does not print matching content or inspect ignored Data, credentials, or caches.
"""
import json
import pathlib
import re
import subprocess
import sys

root = pathlib.Path(__file__).resolve().parents[1]
def git(*args):
    return subprocess.check_output(["git", "-C", str(root), *args])

patterns = [rb"gh[pousr]_[A-Za-z0-9]{30,}", rb"github_pat_[A-Za-z0-9_]{60,}",
            rb"AKIA[A-Z0-9]{16}", rb"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----",
            rb"sk-(?:proj-)?[A-Za-z0-9_-]{40,}"]
issues = []
def check(name, data, scope):
    path = pathlib.PurePosixPath(name)
    if any(p.lower() in {"data", "backup"} for p in path.parts) or path.suffix.lower() in {".db", ".pfx", ".pem", ".key"} or path.name == ".env":
        issues.append({"path": name, "scope": scope, "reason": "private-data filename"})
    if b"\0" not in data and any(re.search(pattern, data) for pattern in patterns):
        issues.append({"path": name, "scope": scope, "reason": "credential pattern"})

objects = git("rev-list", "--objects", "--all").decode().splitlines()
count = 0
for line in objects:
    oid, _, name = line.partition(" ")
    if not name or git("cat-file", "-t", oid).strip() != b"blob":
        continue
    count += 1
    check(name, git("cat-file", "blob", oid), "history")
paths = git("ls-files", "--cached", "--others", "--exclude-standard", "-z").decode().split("\0")
for name in filter(None, paths):
    file = root / name
    if file.is_file():
        check(name, file.read_bytes(), "working-tree")
report = {"Passed": not issues, "HistoryBlobs": count, "WorkingTreeFiles": len(paths)-1,
          "Scope": "filename and common credential-pattern scan; not a security guarantee", "Issues": issues}
print(json.dumps(report, indent=2))
sys.exit(1 if issues else 0)
