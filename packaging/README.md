# Package contents

Both platform build scripts use `files.json` and the same Python 3.9+ staging
helper. This explicit list includes the runtime assemblies, required metadata,
licenses, reviewed documentation/images, and deterministic GLB fixtures.

Do not recursively copy `bin`, `docs`, or `fixtures` into a distribution. Local
notes, untracked images, stale assemblies, apphosts, and old RID subdirectories
must stay outside the package. Add a file to `files.json` only when it is intended
for public distribution. When adding a runtime dependency or changing target
frameworks, update this list along with the project.

The staging helper requires an empty destination and validates all required
files before copying. It rejects missing files, path traversal, duplicate
destinations, and symlink sources. The build scripts run its offline regression
tests unless tests are explicitly skipped:

```text
python3 -m unittest discover -s scripts -p 'test_*.py'
```

Windows requires Python even with `-SkipTests`, because staging uses the same
implementation as macOS. `py -3` is preferred, with `python.exe` as fallback.
Packaging and installation do not establish Rhino host acceptance or publish
anything to Yak/GitHub.
