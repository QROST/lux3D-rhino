#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
YAK="${LUX3D_YAK_PATH:-/Applications/Rhino 8.app/Contents/Resources/bin/yak}"
METADATA=""
while [ "$#" -gt 0 ]; do
  case "$1" in
    --metadata) [ "$#" -ge 2 ] || { echo '--metadata requires a path.' >&2; exit 2; }; METADATA="$2"; shift 2 ;;
    --yak) [ "$#" -ge 2 ] || { echo '--yak requires a path.' >&2; exit 2; }; YAK="$2"; shift 2 ;;
    --help) echo 'Usage: install-local.sh [--metadata PATH] [--yak PATH]'; exit 0 ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done
[ "$(uname -s)" = Darwin ] || { echo 'Use install-local.ps1 on Windows.' >&2; exit 1; }
case "$YAK" in /*) ;; *) YAK="$PWD/$YAK" ;; esac
[ -x "$YAK" ] || { echo "Rhino 8 Yak CLI is missing: $YAK" >&2; exit 1; }
command -v python3 >/dev/null || { echo 'Python 3.9+ is required for package verification.' >&2; exit 1; }
command -v dotnet >/dev/null || { echo '.NET 8 runtime is required for the sidecar.' >&2; exit 1; }
dotnet --list-runtimes | awk '$1 == "Microsoft.NETCore.App" && $2 ~ /^8\./ { found=1 } END { exit !found }' || { echo 'Microsoft.NETCore.App 8.x runtime is required for the sidecar.' >&2; exit 1; }
if [ -n "$METADATA" ]; then
  case "$METADATA" in /*) ;; *) METADATA="$PWD/$METADATA" ;; esac
elif [ -f "$ROOT/artifacts/latest-package-mac.json" ]; then
  METADATA="$ROOT/artifacts/latest-package-mac.json"
else
  METADATA="$ROOT/artifacts/latest-package.json"
fi
VERIFY_ARGS=(--metadata "$METADATA")
cd "$ROOT"
PACKAGE="$(python3 scripts/verify-install.py "${VERIFY_ARGS[@]}" --package-only --print-path)"
"$YAK" install "$PACKAGE"
python3 scripts/verify-install.py "${VERIFY_ARGS[@]}"
printf '\nLux3D installed. Open or restart Rhino manually, then run Lux3DPanel.\n'
