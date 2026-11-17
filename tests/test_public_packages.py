"""Package publication boundary regressions."""

import importlib.util
import io
from pathlib import Path
import unittest
import zipfile

spec = importlib.util.spec_from_file_location(
    "public_packages", Path(__file__).resolve().parents[1] / "tools/check-public-packages.py")
public_packages = importlib.util.module_from_spec(spec)
spec.loader.exec_module(public_packages)


def package(files):
    output = io.BytesIO()
    with zipfile.ZipFile(output, "w") as archive:
        for name, data in files.items():
            archive.writestr(name, data)
    output.seek(0)
    return output


class PublicPackageTests(unittest.TestCase):
    def test_portable_debug_metadata_is_allowed(self):
        public_packages.check_package(package({"tools/Service.dll": b"RSDS/_/src/Service.pdb"}))

    def test_machine_path_in_binary_debug_metadata_is_rejected(self):
        path = "\\".join(("D:", "CodexData", "scratch", "build", "Service.pdb"))
        for encoding in ("utf-8", "utf-16-le"):
            with self.subTest(encoding=encoding), self.assertRaises(ValueError):
                public_packages.check_package(package({"tools/Service.dll": b"RSDS" + path.encode(encoding)}))

    def test_working_record_is_rejected(self):
        with self.assertRaises(ValueError):
            public_packages.check_package(package({"docs/notes.md": "working notes"}))

    def test_source_commit_is_checked(self):
        manifest = '<package><metadata><repository commit="expected" /></metadata></package>'
        public_packages.check_package(package({"Tool.nuspec": manifest}), "expected")
        with self.assertRaises(ValueError):
            public_packages.check_package(package({"Tool.nuspec": manifest}), "different")

    def test_missing_manifest_is_rejected_when_commit_is_required(self):
        with self.assertRaises(ValueError):
            public_packages.check_package(package({"tools/Service.dll": b"binary"}), "expected")
