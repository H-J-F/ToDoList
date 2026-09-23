"""Verify unpacked local builds; exclude explicitly preserved user Data."""
import hashlib
import json
import pathlib
import sys

root = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else "Build").resolve()
allow_dirty = "--allow-dirty" in sys.argv[2:]
version = next((value.split("=", 1)[1] for value in sys.argv[2:] if value.startswith("--version=")), None)
reports, commits = [], set()
for folder in sorted(root.glob("ToDoList-*-win-x64-*")):
    if not folder.is_dir():
        continue
    manifest = json.loads((folder / "BUILD.json").read_text(encoding="utf-8-sig"))
    if version is not None and manifest["Version"] != version:
        continue
    files = [p for p in folder.rglob("*") if p.is_file()
             and not any(part.lower() in ("data", "backup") for part in p.relative_to(folder).parts)]
    names = {p.relative_to(folder).as_posix() for p in files}
    assert {"LICENSE", "THIRD-PARTY.md", "DEPENDENCIES.json", "ToDoList.exe", "BUILD.json"} <= names
    if manifest["Variant"] == "lite":
        assert [p.name for p in files if p.suffix.lower() == ".exe"] == ["ToDoList.exe"], "Lite must have exactly one executable"
    assert not any(p.suffix.lower() in (".db", ".pdb", ".ttf", ".otf", ".zip") for p in files)
    assert allow_dirty or not manifest["WorkingTreeDirty"], "Build requires a committed source tree (or --allow-dirty for a local validation build)"
    for item in json.loads((folder / "licenses/SOURCES.json").read_text(encoding="utf-8-sig")):
        assert hashlib.sha256((folder / "licenses" / item["file"]).read_bytes()).hexdigest() == item["sha256"]
    total = sum(p.stat().st_size for p in files)
    assert manifest["Variant"] != "lite" or total < 50_000_000
    commits.add(manifest["Commit"])
    reports.append({"Variant": manifest["Variant"], "FileBytes": total, "Commit": manifest["Commit"], "Files": len(files)})
assert reports and len(commits) == 1
print(json.dumps({"Passed": True, "Builds": reports}, indent=2))
