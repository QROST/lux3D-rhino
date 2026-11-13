import json
from pathlib import Path
import tempfile
import unittest

from package_staging import stage_package


class PackageStagingTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.repository = self.root / "repository with spaces"
        self.repository.mkdir()
        self.stage = self.root / "stage"
        self.stage.mkdir()
        # Exercise the actual distribution list, not a parallel fixture list.
        self.groups = json.loads((Path(__file__).resolve().parents[1] / "packaging/files.json").read_text())
        (self.repository / "packaging").mkdir()
        self.save_groups()
        for group in self.groups:
            for filename in group["files"]:
                path = self.repository / group["source"] / filename
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(filename.encode())

    def save_groups(self):
        (self.repository / "packaging/files.json").write_text(json.dumps(self.groups))

    def test_packages_only_reviewed_files_even_with_dirty_build_and_docs(self):
        for filename in ("docs/private-notes.txt", "docs/untracked.png",
                         "src/Lux3D.Rhino/bin/Release/net7.0/stale.dll",
                         "src/Lux3D.Sidecar/bin/Release/net8.0/osx-arm64/Lux3D.Sidecar",
                         "src/Lux3D.Sidecar/bin/Release/net8.0/Lux3D.Sidecar.exe"):
            path = self.repository / filename
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("must not ship")
        staged = stage_package(self.repository, self.stage)
        expected = {str(Path(g["destination"]) / f).replace("\\", "/").casefold()
                    for g in self.groups for f in g["files"]}
        self.assertEqual(expected, set(staged))
        self.assertEqual(len(expected), len([p for p in self.stage.rglob("*") if p.is_file()]))
        self.assertTrue((self.stage / "docs/logo-aholo.png").is_file())
        self.assertTrue((self.stage / "NOTICE").is_file())
        for group in self.groups:
            for filename in group["files"]:
                self.assertEqual((self.repository / group["source"] / filename).read_bytes(),
                                 (self.stage / group["destination"] / filename).read_bytes())

    def test_missing_required_file_fails_before_copy(self):
        (self.repository / "NOTICE").unlink()
        with self.assertRaisesRegex(ValueError, "missing"):
            stage_package(self.repository, self.stage)
        self.assertEqual([], list(self.stage.iterdir()))

    def test_rejects_traversal_and_duplicate_destination(self):
        for group in ({"source": "../outside", "destination": "", "files": ["secret"]}, self.groups[0]):
            with self.subTest(group=group):
                original = self.groups[:]
                self.groups.append(group)
                self.save_groups()
                with self.assertRaises(ValueError):
                    stage_package(self.repository, self.stage)
                self.groups = original

    def test_rejects_nonempty_stage(self):
        (self.stage / "private.txt").write_text("must not ship")
        with self.assertRaisesRegex(ValueError, "empty"):
            stage_package(self.repository, self.stage)

    def test_rejects_link_to_private_source(self):
        path = self.repository / "NOTICE"
        path.unlink()
        private = self.root / "private.txt"
        private.write_text("private")
        try:
            path.symlink_to(private)
        except OSError:
            self.skipTest("Creating symlinks requires OS permissions on this host.")
        with self.assertRaisesRegex(ValueError, "symlink"):
            stage_package(self.repository, self.stage)


if __name__ == "__main__":
    unittest.main()
