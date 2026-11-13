# Lux3D for Rhino

English | [简体中文](readme.zh-CN.md)

An independent Rhino integration by **CuriousArc · qrost**, not an official Manycore, Aholo, or McNeel product. Features include Manycore OAuth sign-in, Lux3D text/single-image generation, persistent asset caching, GLB candidate previews, and independent Block placement. The current source candidate is **0.1.4-beta.6**; the latest public Yak release verified on 2026-11-13 is **0.1.4-beta.4**. This Yak distribution targets **macOS / Rhino 8.32+**. Windows implementation and build scripts are present; host acceptance and a public Windows distribution remain pending.
The interface uses English by default. At plugin load, Rhino’s UI language takes precedence over the system UI language; Chinese locales (including Traditional Chinese locales) use the Simplified Chinese translation. If Rhino cannot report its language, the system UI language is used, with English for other or unknown languages. Restart Rhino after changing its language. This includes the private background process and OAuth callback page; prompts, model names and saved identifiers are not translated. Numeric formatting keeps the user’s regional settings. Known complete diagnostic messages from older task records are localized when read; dynamic legacy diagnostics and unrecognized provider messages retain their original text.


**Generating models in this version requires an API Key for the selected region. Signing in with Manycore OAuth does not remove that requirement. You can also skip OAuth sign-in in the plugin and configure an API Key directly.**

See [verification records](docs/verification.md) for the current status. The user has confirmed OAuth sign-in, API Key generation, and successful placement/display of the Buddha model with version 0.1.3 on macOS. The 0.1.4-beta.4 language selection and layout, UI and preview interactions, full PBR, Undo/Redo, save/reopen behavior, and Windows host acceptance still require focused verification. Success with an earlier version does not establish acceptance of new changes.

## This preview release

The local beta.6 candidate adds persistent query backoff with jitter and honors the service Retry-After header. Raw provider status and the next query time are retained in history. This only retries queries to the original task; generation is never automatically resubmitted. Native save/reopen checks now reject missing texture evidence; four-channel PBR appearance still needs manual acceptance.

The local beta.5 candidate hardens submission receipts and upload failures, preserves conclusive task failures during recovery, checks task identity before downloading, and protects candidate resources when the panel closes during native import. Package assembly now uses a reviewed file allowlist. These changes have not yet been published or accepted in the Rhino host. See the [delivery checklist](docs/hackathon-delivery.md) for remaining acceptance and demo evidence.

- Public attribution uses **CuriousArc · qrost** throughout the documentation, copyright notice, Yak metadata, and plug-in description.
- Rhino Plug-in Properties now includes **CuriousArc** as Publisher, the project website, and a description with the maintainer handle. These native fields are separate from Yak authors; beta.1 omitted them. Restart Rhino after upgrading.
- Set height, position, and rotation directly on the Generate tab, with visible API Key status. Remove individual candidates without clearing the group.
- Placement reports actual stages and elapsed time. Task results and errors remain available during the panel session; automatic queries do not overwrite unresolved import errors.
- Size and angle changes reuse the current candidate's meshes and materials, with rapid input changes coalesced. There are no new geometry count limits or automatic decimation.
- Direct placement reads, validates, and snapshots files in the background. Native Rhino preflight and final import still run on the UI thread and may briefly block interaction. Diagnostics include monotonic stage timings; no measured speedup factor is claimed yet.

The Yak package name is `lux3d-rhino`, version `0.1.4-beta.4`. In Rhino for macOS, run `PackageManager`, enable prerelease versions, and select this version. Save your documents and restart Rhino after installation. The .NET 8 Runtime and a regional API Key are still required. You can also build a local candidate from source using the steps below; consult the [verification records](docs/verification.md) and Yak server for upload status.

## OAuth sign-in and generation authorization

Official sources reviewed: 2026-11-01 (UTC).

| Connection state | Can this plugin submit generation tasks? |
| --- | --- |
| Manycore OAuth sign-in only | No; the current generation adapter does not use OAuth tokens |
| Valid regional API Key only | Yes; OAuth sign-in in the plugin is not required, and service permissions and credits still apply |
| Both OAuth and API Key configured | Yes; generation still authenticates with the API Key |

There are two distinct reasons:

- **Official REST contract:** the [authentication documentation](https://labs.aholo.ai/api-docs/en/authentication) requires the API Key directly in the `Authorization` header, without a `Bearer` prefix. The [Lux3D OpenAPI specification](https://labs.aholo.ai/api-docs/specifications/services/lux3d/openapi.en.json) defines the generation endpoints. An OAuth access token is not interchangeable with this API Key.
- **Current plugin integration:** generation, remote task queries, and image uploads use REST/API Key authentication. OAuth currently supports official browser authorization, session storage, and a read-only MCP tool-list probe. The plugin does not implement generation through OAuth/MCP or exchange OAuth tokens for API Keys. The official [local SDK MCP documentation](https://labs.aholo.ai/api-docs/en/mcp) also requires an API Key; that is a different connection method from the remote OAuth MCP endpoint probed by this plugin.

The official repository's published [remote MCP connection contract](https://github.com/manycore-research/Aholo-Lux3D/blob/ac93a14bc84b501d167f907d8d4fcc22c041a88a/plugins/common/aholo-lux3d/references/mcp/connection.json) also specifies a raw regional API Key. Switching to MCP is therefore not a verified key-free generation solution.

The extra API Key requirement therefore reflects the documented REST contract and this plugin's integration scope. **It does not establish that every Lux3D interface lacks OAuth generation support.** We have not verified a public OAuth generation contract that can replace this plugin's REST authentication. Successful sign-in or access to an MCP tool list also does not establish generation permission or available credits.

To start generating:

1. Select the international or China region on the **Connection** tab.
2. Obtain an API Key from the corresponding official regional platform. For the international region, see [Aholo Labs API Keys](https://labs.aholo.ai/api-keys) and the [official quick start](https://labs.aholo.ai/api-docs/en/quickstart). Keys are not interchangeable between regions.
3. Enter the key in the password field and click **Set API Key**. Choose session-only use or system credential storage. Validation only queries the task list; it neither creates a generation task nor checks remaining credits.
4. Submit a task from the **Generate** tab. OAuth sign-in is optional. Local GLB candidate preview and placement require neither OAuth nor an API Key.

## Local installation

Requirements: Rhino **8.32 or newer**, the .NET 8 SDK for builds, the .NET 8 Runtime for the background process, and Python 3.9+ for packaging and fixture checks. Rhino must use NETCore mode; the legacy Windows NETFramework mode is unsupported. The [official Rhino runtime guide](https://www.rhino3d.com/docs/guides/netcore/) describes `SetDotNetRuntime` and the required restart.

| Platform | Implementation scope | Current acceptance status |
| --- | --- | --- |
| macOS, Apple Silicon / Intel | Keychain, system-browser OAuth, Mac Yak build/install scripts | User confirmed real generation and display with 0.1.3 on Apple Silicon; new interactions and Intel unverified |
| Windows x64 | Current-user DPAPI credential encryption, system-browser OAuth, PowerShell Yak build/install scripts | Implementation adapted; Windows builds, installation, and host testing pending |

These commands build and install the current source. After installation, save your documents and restart Rhino to load the new version.

macOS, from the repository directory:

```bash
bash scripts/build-package.sh
bash scripts/install-local.sh
```

Windows, from the repository directory in Windows PowerShell 5.1 or newer:

```powershell
.\scripts\build-package.ps1
.\scripts\install-local.ps1
```

Build scripts run offline tests by default. Use `--skip-tests` / `-SkipTests` only to prepare an untested candidate. Each platform produces its own `mac` / `win` Yak package. Install scripts check the platform, version, and package hash, then compare installed files individually. Packages install into a separate `lux3d-rhino/<version>` directory; they are not published to a package server. Open or restart Rhino yourself and run `Lux3DPanel`.

The background process resolves `DOTNET_ROOT_<architecture>`, `DOTNET_ROOT`, standard installation directories, and PATH, then checks for .NET 8. It does not rely on the Rhino GUI inheriting your terminal environment. A missing Runtime produces an installation hint. Paths with spaces and non-ASCII characters are supported.

## Manycore OAuth sign-in (optional)

On the Connection tab, select the international or China region, click **OAuth sign-in (optional)**, and complete official authorization in your system browser. The plugin uses official resource/authorization-server discovery, dynamic public-client registration, PKCE S256, random state, and a local loopback callback. Enter account passwords only on the official website.

On macOS, tokens are stored in product-specific Keychain entries. On Windows, credentials are encrypted using the current user's DPAPI and written to a restricted directory. This supports longer OAuth sessions without Credential Manager's per-entry size limit. Credentials are excluded from `.3dm` files, task history, plaintext configuration, and logs. Disconnecting removes local credentials; manage remote authorization on the official account website. Failed, canceled, or timed-out sign-in does not create generation tasks.

For development, sign-in can also start from the command line. The terminal prints only an authorization link and a redacted result; open the link manually:

```bash
dotnet src/Lux3D.Sidecar/bin/Release/net8.0/Lux3D.Sidecar.dll --oauth-login global
```

The official OAuth metadata currently advertises only the `get_user_info` scope. That declaration alone does not establish the remote MCP's generation capabilities. The post-login probe only initializes MCP and reads its tool list; it does not invoke generation tools. See [provider-contract.md](docs/provider-contract.md) for discovery endpoints, authentication distinctions, and verification boundaries.

## Generation and candidates

Choose text or a single PNG/JPEG image. The panel currently offers `G1-Turbo` with a target face count of 10,000–300,000. `G1` is not exposed because its ZIP output path is not yet supported. Only clicking **Generate** submits a task that consumes service credits. A local task record is written before submission; an unknown submission outcome is never automatically resubmitted. Query or download failures can be retried against the original task, which remains bound to its original region and connection. History displays 30 entries per page and retains all original records.

The demo flow enables **Place in current document when ready** by default. Submission captures the target document, placement point, height, and rotation. Once the result passes validation, the plugin creates an independent Block and zooms to the model. The target document must remain open, active, and writable; closing the panel cancels this automatic placement intent. For existing tasks, click **Place in current document** on the task tab. If necessary, this resumes the original task's download without regenerating. Repeated clicks locate the existing Block rather than insert duplicates; Undo does not trigger automatic reinsertion.

You can also run `Lux3DPlaceTask <operation GUID>` to place an existing task using the current CPlane and an approximately one-meter reference height. The command finishes before asynchronous import begins, preserving an independent Undo record. Alternatively, disable automatic placement and add up to three candidates from the task tab, or load a local GLB. Adjust height, placement point, and rotation, then click **Accept current candidate**. Switching candidates changes only the temporary preview.

The plugin imposes no business limits on vertex or triangle counts, mesh counts, node depth, GLB file size, or total texture pixels. The generation target face count is not an import capacity limit. Container integrity, byte ranges, indices, references, cycles, and hashes are still checked; Rhino determines the actual import result. Static meshes with embedded resources are supported. Unsupported required extensions, animation, and skinning are reported explicitly. System-memory and runtime failures are distinguished from invalid formats; unlimited capacity is not promised. Structurally rejected outputs retain a reason code and diagnostic copy and are not treated as importable models. Full PBR appearance and texture transfer have a separate acceptance checklist; previews currently show geometry and base color.

If a generated mesh contains degenerate faces or invalid normals, version 0.1.3 repairs copies of the newly imported meshes after native Rhino import and checks validity again. It preserves the original GLB and valid geometry without decimation. Success messages report repair counts. Placement results and errors are also written to Rhino command history. Local stage diagnostics are stored under `host-diagnostics/` in the task data directory and exclude prompts, credentials, and download URLs. Offline repair verification is not reported as successful host display without the corresponding native import result.

## Manual host self-test

Run the following in Rhino's command line. It creates separate test documents rather than importing into your current document. Replace the absolute paths with your repository and output directories; Windows paths can use `C:/path/to/lux3D-rhino/...`:

```text
Lux3DSelfTest "/path/to/lux3D-rhino/fixtures/asymmetric-pbr.glb" "/path/to/lux3D-rhino/artifacts/rhino-selftest"
```

Reports and `.3dm` files are written to a new run directory and cover the loaded environment, scale/plane, candidates, Blocks, replay, Undo/Redo, and save/reopen behavior. Real PBR appearance, reopening after an actual Rhino restart, account sign-in, and paid generation require human acceptance. The reported macOS OAuth/API Key generation success does not establish acceptance for every platform or workflow.

## Data and development

Background state is stored under .NET's `LocalApplicationData/Lux3D-Rhino`: verified locally as `~/Library/Application Support/Lux3D-Rhino` on macOS, and corresponding to `%LOCALAPPDATA%\Lux3D-Rhino` on Windows. `tasks/` excludes credentials, `inputs/` holds submitted image snapshots, and `assets/` contains immutable GLBs named by SHA-256. There is no automatic telemetry upload or cache cleanup.

```bash
dotnet build Lux3D.Rhino.sln -c Release
dotnet test Lux3D.Rhino.sln -c Release --no-build
python3 scripts/create-fixtures.py --check
```

The original acceptance baseline remains in [PRD.md](PRD.md); it has not been rewritten as completed. See [NOTICE](NOTICE), [architecture.md](docs/architecture.md), and [provider-contract.md](docs/provider-contract.md) for attribution and architecture. Both READMEs cover the same scope. The plugin UI defaults to English and supports Simplified Chinese as described above; detailed development documents are primarily in Chinese.
