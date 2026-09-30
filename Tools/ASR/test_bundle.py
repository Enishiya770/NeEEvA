import hashlib
import io
import json
from pathlib import Path
import tarfile
import tempfile
import unittest

from extract_bundle import install
from validate_endpoint import validate_health


class BundleTests(unittest.TestCase):
    def archive(self, directory, name="src/test.py", corrupt=False, link=False):
        bundle = directory / "input.tar"
        body = b"# fixture\n"
        with tarfile.open(bundle, "w") as archive:
            row = tarfile.TarInfo(name)
            row.size = len(body)
            if link:
                row.type = tarfile.SYMTYPE
                row.linkname = "/tmp/outside"
            archive.addfile(row, io.BytesIO(body) if not link else None)
            manifest = json.dumps({"files": [{"path": name, "bytes": len(body), "sha256": "bad" if corrupt else hashlib.sha256(body).hexdigest()}]}).encode()
            row = tarfile.TarInfo("bundle-manifest.json")
            row.size = len(manifest)
            archive.addfile(row, io.BytesIO(manifest))
        return bundle

    def test_valid_bundle_and_existing_root_preserved(self):
        with tempfile.TemporaryDirectory() as value:
            directory = Path(value)
            bundle = self.archive(directory)
            root = directory / "asr"
            install(bundle, root)
            self.assertEqual(b"# fixture\n", (root / "src/test.py").read_bytes())
            with self.assertRaises(FileExistsError):
                install(bundle, root)

    def test_traversal_links_and_checksum_fail_before_root_created(self):
        for options in [{"name": "../outside"}, {"name": "/outside"}, {"link": True}, {"corrupt": True}]:
            with self.subTest(options=options), tempfile.TemporaryDirectory() as value:
                directory = Path(value)
                bundle = self.archive(directory, **options)
                root = directory / "asr"
                with self.assertRaises(ValueError):
                    install(bundle, root)
                self.assertFalse(root.exists())

    def test_health_does_not_accept_cpu_or_just_http_success(self):
        self.assertIn("asr_device", validate_health({"ok": True, "asr_device": "cpu"}))


if __name__ == "__main__":
    unittest.main()
