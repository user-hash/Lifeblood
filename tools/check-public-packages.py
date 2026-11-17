"""Reject private workspace paths and working records in packaged payloads."""

import argparse
from pathlib import Path, PurePosixPath
import runpy
import xml.etree.ElementTree as ET
import zipfile

MACHINE_PATH = runpy.run_path(str(Path(__file__).with_name("prepare-public-source.py")))["MACHINE_PATH"]
PRIVATE_SEGMENTS = {"docs", "devmemory", ".planning", ".codex", ".claude", ".git"}


def check_package(path, commit=None):
    problems = []
    with zipfile.ZipFile(path) as archive:
        for entry in archive.infolist():
            if entry.is_dir():
                continue
            parts = PurePosixPath(entry.filename).parts
            if any(part.lower() in PRIVATE_SEGMENTS for part in parts):
                problems.append(f"Excluded working path: {entry.filename}")
            data = archive.read(entry)
            if MACHINE_PATH.search(data) or MACHINE_PATH.search(data.replace(b"\x00", b"")):
                problems.append(f"Machine workspace path in payload: {entry.filename}")
        if commit is not None:
            manifests = [name for name in archive.namelist() if name.endswith(".nuspec")]
            if len(manifests) != 1:
                problems.append("Expected one package manifest")
            else:
                repository = ET.fromstring(archive.read(manifests[0])).find("{*}metadata/{*}repository")
                if repository is None or repository.get("commit") != commit:
                    problems.append("Package source commit does not match the release commit")
    if problems:
        raise ValueError(f"{path}:\n" + "\n".join(problems))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--commit")
    parser.add_argument("packages", nargs="+", type=Path)
    args = parser.parse_args()
    for path in args.packages:
        check_package(path, args.commit)
        print(f"Public package check passed: {path.name}")


if __name__ == "__main__":
    main()
