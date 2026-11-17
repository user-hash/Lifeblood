"""Check and export one Git tree without working records or Git history."""

import argparse
import io
from pathlib import Path, PurePosixPath
import re
import subprocess
import zipfile


PUBLIC_DIRECTORIES = {
    ".github", "adapters", "guides", "invariants", "packs", "schemas",
    "skills", "src", "tests", "tools", "unity",
}
PUBLIC_ROOT_FILES = {
    ".editorconfig", ".gitattributes", ".gitignore", ".mcp.json.example",
    "CHANGELOG.md", "CLAUDE.md", "CONTRIBUTING.md", "Directory.Build.props",
    "LICENSE", "Lifeblood.sln", "README.md", "export-source.sh", "global.json",
    "nuget.config",
}
BUILD_SEGMENTS = {".git", "bin", "obj", "node_modules", "__pycache__"}
MACHINE_PATH = re.compile(rb"[A-Za-z]:[\\/](?:Users|Projekti|CodexData)[\\/]", re.I)


def check_archive(payload):
    problems = []
    files = 0
    with zipfile.ZipFile(io.BytesIO(payload)) as archive:
        for entry in archive.infolist():
            if entry.is_dir():
                continue
            path = PurePosixPath(entry.filename)
            parts = path.parts
            files += 1
            if (path.is_absolute() or ".." in parts or not parts
                    or parts[0] not in PUBLIC_DIRECTORIES | PUBLIC_ROOT_FILES
                    or (len(parts) == 1 and parts[0] not in PUBLIC_ROOT_FILES)
                    or any(p.lower() in BUILD_SEGMENTS for p in parts)
                    or entry.external_attr >> 16 & 0o170000 == 0o120000):
                problems.append(f"Excluded or unreviewed source path: {path}")
                continue
            if entry.file_size > 8 * 1024 * 1024:
                problems.append(f"Source file exceeds review bound: {path}")
                continue
            if MACHINE_PATH.search(archive.read(entry)):
                problems.append(f"Machine-specific workspace path: {path}")
    if problems:
        raise ValueError("\n".join(problems))
    if files == 0:
        raise ValueError("Source tree is empty.")
    return files


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--revision", default="HEAD")
    output = parser.add_mutually_exclusive_group(required=True)
    output.add_argument("--check", action="store_true")
    output.add_argument("--output", type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    tree = subprocess.check_output(
        ["git", "rev-parse", "--verify", "--end-of-options", args.revision + "^{tree}"],
        cwd=root, text=True).strip()
    payload = subprocess.check_output(["git", "archive", "--format=zip", tree], cwd=root)
    count = check_archive(payload)
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        with args.output.open("xb") as destination:
            destination.write(payload)
    print(f"Checked {count} public source files from tree {tree}; no working tree or Git history included.")
    if args.output:
        print(f"Exported: {args.output.resolve()}")


if __name__ == "__main__":
    main()
