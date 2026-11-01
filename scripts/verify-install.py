#!/usr/bin/env python3
"""Compare the local installed package with the reviewed package without opening Rhino."""
import hashlib
import json
from pathlib import Path
import zipfile

root = Path(__file__).resolve().parents[1]
metadata = json.loads((root / 'artifacts/latest-package.json').read_text())
package = Path(metadata['path'])
installed = Path.home() / 'Library/Application Support/McNeel/Rhinoceros/packages/8.0/lux3d-rhino/0.1.0'
checks = []
with zipfile.ZipFile(package) as archive:
    for entry in archive.infolist():
        if entry.is_dir():
            continue
        candidate = installed / entry.filename
        if not candidate.resolve().is_relative_to(installed.resolve()):
            raise SystemExit('Invalid package path.')
        expected = hashlib.sha256(archive.read(entry)).hexdigest()
        actual = hashlib.sha256(candidate.read_bytes()).hexdigest() if candidate.is_file() else None
        checks.append({'file':entry.filename, 'matches':actual == expected, 'sha256':actual})
report = {'installedPath':str(installed), 'package':str(package), 'allFilesMatch':all(x['matches'] for x in checks), 'files':checks}
(root / 'artifacts/install-verification.json').write_text(json.dumps(report, indent=2)+'\n')
if not report['allFilesMatch']:
    raise SystemExit('Installed files differ from the reviewed package. See artifacts/install-verification.json.')
print(f'Verified {len(checks)} installed files: {installed}')
