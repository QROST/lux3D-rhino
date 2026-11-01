#!/usr/bin/env python3
"""Verify package identity and installed bytes without opening Rhino (Python 3.9+)."""
from __future__ import annotations

import argparse
import hashlib
import os
from pathlib import Path
import sys
import zipfile
from package_metadata import REPOSITORY, atomic_json, host_platform, load_metadata, safe_entries


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--metadata", help="Explicit candidate metadata; default prefers latest-package-<platform>.json")
    parser.add_argument("--packages-root", type=Path, help="Override the Rhino 8.0 packages root")
    parser.add_argument("--package-only", action="store_true", help="Validate package only; do not inspect installed files or write a report")
    parser.add_argument("--print-path", action="store_true", help="With --package-only, print only the verified absolute package path")
    parser.add_argument("--no-report", action="store_true", help="Inspect installed files without writing a report")
    args = parser.parse_args()
    if args.print_path and not args.package_only:
        parser.error("--print-path requires --package-only")
    platform = host_platform()
    metadata = load_metadata(platform, args.metadata)
    package = Path(metadata["path"])
    if args.package_only:
        print(package if args.print_path else f"Verified {metadata['name']} {metadata['version']} ({platform}): {package}")
        return 0
    if args.packages_root:
        package_root = args.packages_root.expanduser().resolve()
    elif platform == "mac":
        package_root = Path.home() / "Library/Application Support/McNeel/Rhinoceros/packages/8.0"
    else:
        if not os.environ.get("APPDATA"):
            raise ValueError("APPDATA is unavailable; supply --packages-root.")
        package_root = Path(os.environ["APPDATA"]) / "McNeel/Rhinoceros/packages/8.0"
    installed = package_root / metadata["name"] / metadata["version"]
    checks = []
    with zipfile.ZipFile(package) as archive:
        for entry in safe_entries(archive):
            candidate = installed / entry.filename
            if not candidate.resolve().is_relative_to(installed.resolve()):
                raise ValueError("Installed package path escapes the package directory.")
            expected = hashlib.sha256(archive.read(entry)).hexdigest()
            actual = hashlib.sha256(candidate.read_bytes()).hexdigest() if candidate.is_file() else None
            checks.append({"file": entry.filename, "matches": actual == expected, "sha256": actual})
    report = {"installedPath": str(installed), "package": str(package), "name": metadata["name"],
              "version": metadata["version"], "platform": platform, "allFilesMatch": all(x["matches"] for x in checks), "files": checks}
    if not args.no_report:
        atomic_json(REPOSITORY / f"artifacts/install-verification-{platform}.json", report)
        atomic_json(REPOSITORY / "artifacts/install-verification.json", report)
    if not report["allFilesMatch"]:
        raise ValueError("Installed files differ from the reviewed candidate; installation is not verified.")
    print(f"Verified {len(checks)} installed files: {installed}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, KeyError, zipfile.BadZipFile) as error:
        print(f"Verification failed: {error}", file=sys.stderr)
        raise SystemExit(1)
