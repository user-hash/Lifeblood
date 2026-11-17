"""Publication boundary regressions; run with python -m unittest discover -s tests."""

import importlib.util
import io
from pathlib import Path
import unittest
import zipfile

spec = importlib.util.spec_from_file_location(
    "public_source", Path(__file__).resolve().parents[1] / "tools/prepare-public-source.py")
public_source = importlib.util.module_from_spec(spec)
spec.loader.exec_module(public_source)


def payload(files):
    output = io.BytesIO()
    with zipfile.ZipFile(output, "w") as archive:
        for name, content in files.items():
            archive.writestr(name, content)
    return output.getvalue()


class PublicSourceTests(unittest.TestCase):
    def test_source_and_product_guides_are_allowed(self):
        self.assertEqual(2, public_source.check_archive(payload({
            "src/Service.cs": "class Service {}", "guides/SETUP.md": "# Setup"})))

    def test_working_records_and_build_outputs_block_export(self):
        for name in ("docs/notes.md", "devmemory/intake.md", ".mcp.json",
                     "src/obj/cache.json", ".git/config", "unknown/data.json",
                     "../README.md", "/README.md"):
            with self.subTest(name=name), self.assertRaises(ValueError):
                public_source.check_archive(payload({name: "private"}))

    def test_machine_workspace_paths_block_export_even_in_a_product_guide(self):
        for content in ("/".join(["D:", "Projekti", "PrivateProject"]),
                        "\\".join(["C:", "Users", "Example", "Project"])):
            with self.subTest(content=content), self.assertRaises(ValueError):
                public_source.check_archive(payload({"guides/SETUP.md": content}))

    def test_empty_payload_blocks_export(self):
        with self.assertRaises(ValueError):
            public_source.check_archive(payload({}))
