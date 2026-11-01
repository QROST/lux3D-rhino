#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
PACKAGE="$(python3 - <<'PY'
import hashlib, json, pathlib
metadata = json.loads(pathlib.Path('artifacts/latest-package.json').read_text())
p = pathlib.Path(metadata['path'])
if hashlib.sha256(p.read_bytes()).hexdigest() != metadata['sha256']:
    raise SystemExit('Package checksum mismatch.')
print(p)
PY
)"
"/Applications/Rhino 8.app/Contents/Resources/bin/yak" install "$PACKAGE"
python3 scripts/verify-install.py
printf '\nLux3D installed. Open or restart Rhino manually, then run Lux3DPanel.\n'
