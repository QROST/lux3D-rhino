#!/usr/bin/env python3
"""Record a completed platform-specific candidate; does not publish or install."""
from __future__ import annotations
import argparse
from datetime import datetime, timezone
from pathlib import Path
import xml.etree.ElementTree as ET
from package_metadata import REPOSITORY, atomic_json, inspect_package, manifest_identity

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("stage", type=Path)
parser.add_argument("--platform", choices=("mac", "win"), required=True)
parser.add_argument("--checks", choices=("passed", "skipped"), required=True)
args = parser.parse_args()
packages = list(args.stage.glob("*.yak"))
if len(packages) != 1:
    raise SystemExit("Expected exactly one Yak package in the new staging directory.")
metadata = inspect_package(packages[0], args.platform)
source_identity = manifest_identity((REPOSITORY / "packaging/manifest.yml").read_text(encoding="utf-8-sig"))
project_version = ET.parse(REPOSITORY / "Directory.Build.props").findtext(".//Version")
if source_identity != (metadata["name"], metadata["version"]) or project_version != metadata["version"]:
    raise SystemExit("Manifest, archive and Directory.Build.props versions must agree.")
metadata.update({"checks": args.checks, "createdUtc": datetime.now(timezone.utc).isoformat()})
package = Path(metadata["path"])
package.with_suffix(package.suffix + ".sha256").write_text(metadata["sha256"] + "  " + package.name + "\n", encoding="utf-8")
atomic_json(REPOSITORY / f"artifacts/latest-package-{args.platform}.json", metadata)
atomic_json(REPOSITORY / "artifacts/latest-package.json", metadata)
print("Package:", package)
print("SHA256:", metadata["sha256"])
print("Checks:", args.checks)
