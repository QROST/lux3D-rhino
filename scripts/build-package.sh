#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SKIP_TESTS=0
NO_RESTORE=0
YAK="${LUX3D_YAK_PATH:-/Applications/Rhino 8.app/Contents/Resources/bin/yak}"
while [ "$#" -gt 0 ]; do
  case "$1" in
    --skip-tests) SKIP_TESTS=1; shift ;;
    --no-restore) NO_RESTORE=1; shift ;;
    --yak) [ "$#" -ge 2 ] || { echo '--yak requires a path.' >&2; exit 2; }; YAK="$2"; shift 2 ;;
    --help) echo 'Usage: build-package.sh [--skip-tests] [--no-restore] [--yak PATH]'; exit 0 ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done
[ "$(uname -s)" = Darwin ] || { echo 'Use build-package.ps1 on Windows.' >&2; exit 1; }
case "$YAK" in /*) ;; *) YAK="$PWD/$YAK" ;; esac
[ -x "$YAK" ] || { echo "Rhino 8 Yak CLI is missing: $YAK" >&2; exit 1; }
command -v dotnet >/dev/null || { echo '.NET 8 SDK/runtime is required.' >&2; exit 1; }
command -v python3 >/dev/null || { echo 'Python 3.9+ is required for package validation.' >&2; exit 1; }
dotnet --list-runtimes | awk '$1 == "Microsoft.NETCore.App" && $2 ~ /^8\./ { found=1 } END { exit !found }' || { echo 'Microsoft.NETCore.App 8.x runtime is required for the sidecar.' >&2; exit 1; }
cd "$ROOT"
BUILD_ARGS=(build Lux3D.Rhino.sln --configuration Release -p:UseAppHost=false)
[ "$NO_RESTORE" -eq 0 ] || BUILD_ARGS+=(--no-restore)
dotnet "${BUILD_ARGS[@]}"
CHECKS=skipped
if [ "$SKIP_TESTS" -eq 0 ]; then
  dotnet test Lux3D.Rhino.sln --configuration Release --no-build --no-restore
  python3 scripts/create-fixtures.py --check
  python3 -m unittest discover -s scripts -p 'test_*.py'
  CHECKS=passed
fi
mkdir -p artifacts
STAGE="$(mktemp -d "$ROOT/artifacts/package-mac.XXXXXX")"
python3 scripts/stage-package.py "$STAGE"
(cd "$STAGE" && "$YAK" build --platform mac)
python3 scripts/record-package.py "$STAGE" --platform mac --checks "$CHECKS"
printf '\nCandidate only. Host/account acceptance remains separate.\n'
