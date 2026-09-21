"""Verify unpacked local builds; exclude explicitly preserved user Data."""
import hashlib
import json
import pathlib
import sys

root = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else "Build").resolve()
reports, commits = [], set()
for folder in sorted(root.glob("ToDoList-*-win-x64-*")):
    if not folder.is_dir():
        continue
    manifest = json.loads((folder / "BUILD.json").read_text(encoding="utf-8-sig"))
    files = [p for p in folder.rglob("*") if p.is_file()
             and not any(part.lower() in ("data", "backup") for part in p.relative_to(folder).parts)]
    names = {p.relative_to(folder).as_posix() for p in files}
    assert {"LICENSE", "THIRD-PARTY.md", "DEPENDENCIES.json", "ToDoList.exe", "BUILD.json"} <= names
    assert not any(p.suffix.lower() in (".db", ".pdb", ".ttf", ".otf", ".zip") for p in files)
    assert not manifest["WorkingTreeDirty"], "Build requires a committed source tree"
    for item in json.loads((folder / "licenses/SOURCES.json").read_text(encoding="utf-8-sig")):
        assert hashlib.sha256((folder / "licenses" / item["file"]).read_bytes()).hexdigest() == item["sha256"]
    total = sum(p.stat().st_size for p in files)
    assert manifest["Variant"] != "lite" or total < 50_000_000
    commits.add(manifest["Commit"])
    reports.append({"Variant": manifest["Variant"], "FileBytes": total, "Commit": manifest["Commit"], "Files": len(files)})
assert reports and len(commits) == 1
print(json.dumps({"Passed": True, "Builds": reports}, indent=2))
