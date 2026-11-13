"""Copy only reviewed distribution files; never recurse through build/docs trees."""
from __future__ import annotations

import json
from pathlib import Path, PurePosixPath
import shutil


def relative_path(value: str, allow_empty: bool = False) -> Path:
    if value == "" and allow_empty:
        return Path()
    parts = value.split("/")
    if (not value or "\\" in value or PurePosixPath(value).is_absolute()
            or any(part in ("", ".", "..") or ":" in part for part in parts)):
        raise ValueError(f"Unsafe staging path: {value!r}")
    return Path(*parts)


def stage_package(repository: Path, stage: Path) -> list[str]:
    repository = repository.resolve(strict=True)
    stage = stage.resolve(strict=True)
    if not stage.is_dir() or any(stage.iterdir()):
        raise ValueError("Package staging directory must exist and be empty.")
    groups = json.loads((repository / "packaging/files.json").read_text(encoding="utf-8"))
    copies, seen = [], set()
    for group in groups:
        source_root = relative_path(group["source"], allow_empty=True)
        destination_root = relative_path(group["destination"], allow_empty=True)
        for filename in group["files"]:
            filename = relative_path(filename)
            source = repository / source_root / filename
            destination = destination_root / filename
            key = destination.as_posix().casefold()
            if key in seen:
                raise ValueError(f"Duplicate staging destination: {destination}")
            seen.add(key)
            # Reject links at every level, including links that point inside the tree.
            current = source
            while current != repository:
                if current.is_symlink():
                    raise ValueError(f"Staging source must not be a symlink: {source}")
                current = current.parent
            if not source.is_file():
                raise ValueError(f"Required package source is missing: {source}")
            copies.append((source, stage / destination))
    if not copies:
        raise ValueError("Package staging list is empty.")
    for source, destination in copies:
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, destination)
    return sorted(seen)
