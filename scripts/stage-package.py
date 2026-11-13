#!/usr/bin/env python3
"""Stage the shared macOS/Windows distribution allowlist without publishing."""
import argparse
from pathlib import Path
from package_metadata import REPOSITORY
from package_staging import stage_package

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("stage", type=Path)
args = parser.parse_args()
print(f"Staged {len(stage_package(REPOSITORY, args.stage))} reviewed package files.")
