#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
dotnet build Lux3D.Rhino.sln --configuration Release
dotnet test Lux3D.Rhino.sln --configuration Release --no-build
python3 scripts/create-fixtures.py --check
YAK="/Applications/Rhino 8.app/Contents/Resources/bin/yak"
if [ ! -x "$YAK" ]; then
  echo "Rhino 8 Yak CLI is missing." >&2
  exit 1
fi
mkdir -p artifacts
STAGE="$(mktemp -d "$ROOT/artifacts/package.XXXXXX")"
cp -R src/Lux3D.Rhino/bin/Release/net7.0/. "$STAGE/"
mkdir -p "$STAGE/sidecar" "$STAGE/fixtures"
cp -R src/Lux3D.Sidecar/bin/Release/net8.0/. "$STAGE/sidecar/"
cp packaging/manifest.yml "$STAGE/"
cp LICENSE NOTICE README.md "$STAGE/"
cp -R docs "$STAGE/"
cp fixtures/*.glb fixtures/manifest.json fixtures/README.md "$STAGE/fixtures/"
(cd "$STAGE" && "$YAK" build --platform mac)
python3 - "$STAGE" <<'PY'
import hashlib, json, pathlib, sys
stage = pathlib.Path(sys.argv[1])
packages = list(stage.glob('*.yak'))
if len(packages) != 1: raise SystemExit('Expected one Yak package.')
package = packages[0]
digest = hashlib.sha256(package.read_bytes()).hexdigest()
package.with_suffix(package.suffix + '.sha256').write_text(digest + '  ' + package.name + '\n')
root = stage.parent.parent
(root / 'artifacts/latest-package.json').write_text(json.dumps({'path':str(package), 'sha256':digest}, indent=2)+'\n')
print('Package:', package)
print('SHA256:', digest)
PY
