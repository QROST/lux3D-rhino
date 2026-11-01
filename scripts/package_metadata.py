"""Dependency-free package inspection shared by macOS build/install tooling."""
from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import stat
import sys
import zipfile

if sys.version_info < (3, 9):
    raise RuntimeError("Python 3.9 or later is required for package tooling.")

REPOSITORY = Path(__file__).resolve().parents[1]


def host_platform() -> str:
    if sys.platform == "darwin":
        return "mac"
    if os.name == "nt":
        return "win"
    raise ValueError("Local Rhino installation is supported only on macOS or Windows.")


def manifest_identity(text: str) -> tuple[str, str]:
    values = {}
    for key in ("name", "version"):
        matches = re.findall(r"^" + key + r":\s*([^\r\n]+?)\s*$", text, re.MULTILINE)
        if len(matches) != 1:
            raise ValueError(f"Expected exactly one manifest {key}.")
        values[key] = matches[0].strip().strip("\"'")
    if not re.fullmatch(r"[A-Za-z0-9_-]+", values["name"]):
        raise ValueError("Invalid package name.")
    if not re.fullmatch(r"\d+\.\d+\.\d+(?:\.\d+)?(?:-[A-Za-z0-9.-]+)?(?:\+[A-Za-z0-9.-]+)?", values["version"]):
        raise ValueError("Invalid package version.")
    return values["name"], values["version"]


def safe_entries(archive: zipfile.ZipFile) -> list[zipfile.ZipInfo]:
    entries, seen = [], set()
    for entry in archive.infolist():
        raw = entry.filename
        path = PurePosixPath(raw)
        if (not raw or "\\" in raw or raw.startswith("/") or
                any(part in ("", ".", "..") or ":" in part for part in raw.rstrip("/").split("/")) or
                path.is_absolute() or stat.S_ISLNK(entry.external_attr >> 16)):
            raise ValueError(f"Unsafe package entry: {raw!r}")
        normalized = raw.rstrip("/").casefold()
        if normalized in seen:
            raise ValueError(f"Duplicate package entry: {raw!r}")
        seen.add(normalized)
        if not entry.is_dir():
            entries.append(entry)
    if not entries:
        raise ValueError("Package is empty.")
    return entries


def inspect_package(path: Path, expected_platform: str) -> dict:
    path = path.resolve(strict=True)
    match = re.search(r"-rh(\d+)(?:_\d+)?-(mac|win)\.yak$", path.name, re.IGNORECASE)
    if not match or match.group(1) != "8" or match.group(2).lower() != expected_platform:
        raise ValueError(f"Expected a Rhino 8 {expected_platform} distribution, got {path.name}.")
    with zipfile.ZipFile(path) as archive:
        entries = safe_entries(archive)
        names = {entry.filename for entry in entries}
        if "manifest.yml" not in names:
            raise ValueError("Package must contain manifest.yml at its root.")
        name, version = manifest_identity(archive.read("manifest.yml").decode("utf-8-sig"))
        if name != "lux3d-rhino":
            raise ValueError("This installer accepts only the lux3d-rhino package.")
        if not path.name.lower().startswith(f"{name}-{version}-rh8".lower()):
            raise ValueError("Package filename does not match its manifest identity.")
        required = {"Lux3D.Rhino.rhp", "Lux3D.Core.dll", "sidecar/Lux3D.Sidecar.dll",
                    "sidecar/Lux3D.Sidecar.deps.json", "sidecar/Lux3D.Sidecar.runtimeconfig.json"}
        if not required.issubset(names):
            raise ValueError("Package is missing required Rhino/sidecar files.")
        if names.intersection({"sidecar/Lux3D.Sidecar", "sidecar/Lux3D.Sidecar.exe"}):
            raise ValueError("Expected framework-dependent sidecar without a platform apphost.")
        runtime = json.loads(archive.read("sidecar/Lux3D.Sidecar.runtimeconfig.json"))["runtimeOptions"]
        framework = runtime.get("framework", {})
        if framework.get("name") != "Microsoft.NETCore.App" or not framework.get("version", "").startswith("8."):
            raise ValueError("Sidecar must require Microsoft.NETCore.App 8.x.")
    return {"schemaVersion": 2, "path": str(path), "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
            "name": name, "version": version, "platform": expected_platform, "rhinoMajor": 8,
            "deploymentMode": "framework-dependent", "sidecarRuntime": "net8.0"}


def metadata_path(platform: str, explicit: str | None = None) -> Path:
    if explicit:
        return Path(explicit).expanduser().resolve(strict=True)
    preferred = REPOSITORY / f"artifacts/latest-package-{platform}.json"
    return preferred if preferred.is_file() else REPOSITORY / "artifacts/latest-package.json"


def load_metadata(platform: str, explicit: str | None = None) -> dict:
    selected = metadata_path(platform, explicit)
    data = json.loads(selected.read_text(encoding="utf-8-sig"))
    package = Path(data["path"]).expanduser()
    if not package.is_absolute():
        package = selected.parent / package
    actual = inspect_package(package, platform)
    if not isinstance(data.get("sha256"), str) or data["sha256"].lower() != actual["sha256"]:
        raise ValueError("Package checksum mismatch.")
    # Legacy {path, sha256} metadata is accepted only after manifest/tag validation.
    for field in ("name", "version", "platform", "rhinoMajor", "deploymentMode", "sidecarRuntime"):
        if field in data and data[field] != actual[field]:
            raise ValueError(f"Package metadata {field} does not match the archive.")
    return actual


def atomic_json(path: Path, data: dict) -> None:
    temporary = path.with_name(path.name + "." + str(os.getpid()) + ".tmp")
    try:
        temporary.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
        temporary.replace(path)
    finally:
        temporary.unlink(missing_ok=True)
