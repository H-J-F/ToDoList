"""Verify local dual-version ZIPs without extracting or modifying installations."""
import hashlib
import json
import pathlib
import sys
import zipfile

root = pathlib.Path(sys.argv[1]).resolve()
checksums = dict(line.split(None, 1)[::-1] for line in (root / "SHA256SUMS.txt").read_text().splitlines() if line.strip())
reports = []
commits = set()
for kind in ("lite", "portable"):
    archive = root / f"ToDoList-2.1.0-win-x64-{kind}.zip"
    digest = hashlib.file_digest(archive.open("rb"), "sha256").hexdigest()
    assert checksums[archive.name] == digest, "ZIP hash mismatch"
    with zipfile.ZipFile(archive) as zip:
        assert zip.testzip() is None, "Corrupt ZIP entry"
        files = [entry for entry in zip.infolist() if not entry.is_dir()]
        names = [entry.filename.replace("\\", "/") for entry in files]
        assert all(not any(part.lower() in ("data", "backup") for part in pathlib.PurePosixPath(name).parts) for name in names)
        assert all(not name.lower().endswith((".db", ".pdb", ".ttf", ".otf")) for name in names)
        assert {"LICENSE", "THIRD-PARTY.md", "DEPENDENCIES.json", "ToDoList.exe", "BUILD.json"}.issubset(names)
        manifest = json.loads(zip.read("BUILD.json"))
        assert manifest["Version"] == "2.1.0" and manifest["Variant"] == kind and not manifest["WorkingTreeDirty"]
        commits.add(manifest["Commit"])
        for license in json.loads(zip.read("licenses/SOURCES.json")):
            assert hashlib.sha256(zip.read("licenses/" + license["file"])).hexdigest() == license["sha256"], license["file"]
        total = sum(entry.file_size for entry in files)
        assert kind != "lite" or total < 50_000_000
        reports.append({"Variant": kind, "FileBytes": total, "ZipBytes": archive.stat().st_size, "Sha256": digest,
                        "Commit": manifest["Commit"], "Entries": len(files)})
assert len(commits) == 1, "Build variants came from different commits"
print(json.dumps({"Passed": True, "Packages": reports}, indent=2))
