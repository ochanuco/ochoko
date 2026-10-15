# ochoko

A thin Voice MCP server for [A.I.VOICE](https://aivoice.jp/), used by yunomi.
It receives text plus raw A.I.VOICE values and returns a WAV. It has no
character logic and no prosody IR; yunomi converts semantic prosody into
A.I.VOICE values before calling.

It runs on the GPU box (Windows 11) as a logon scheduled task of the
interactive user, in the same non-elevated session as the A.I.VOICE Editor
GUI, and serves `http://+:8190/`. The caller is the yunomi Gateway on docker01
(192.168.50.10).

```text
src/Ochoko.Core          MCP JSON-RPC, tool validation, Bearer auth, keep-awake leases, WAV parsing, IVoiceEngine
src/Ochoko               ochoko.exe (net48, x64): HttpListener host, A.I.VOICE engine over COM, power request, logging
tests/Ochoko.Core.Tests  xunit tests for Ochoko.Core
deploy/                  hooks run by chabatake's deployer, and register.ps1
```

The exe targets .NET Framework 4.8 because A.I.VOICE's COM server is an
in-process .NET Framework component that cannot load into a .NET (Core)
process. The A.I.VOICE API DLL is not referenced (it cannot be committed);
`TtsControl` is called by late binding through its ProgID.

## MCP

`POST /mcp` speaks MCP Streamable HTTP in stateless JSON mode (protocol
versions `2025-06-18` and `2025-03-26`). There are no sessions, no batches,
and no SSE: `GET /mcp` and `DELETE /mcp` return 405.

Every request needs `Authorization: Bearer <token>`. The token maps to a
caller name (`dev`, `prod`, ...), which also keys the keep-awake lease. A
request that carries an `Origin` header is rejected with 403.

### `synthesize`

| Input | Type | Notes |
|---|---|---|
| `text` | string | 1..2000 characters |
| `preset` | string | A voice preset name from `list_presets`. The user's preset is never modified |
| `preset_overrides` | object, optional | `Volume`, `Speed`, `Pitch`, `PitchRange`, `MiddlePause`, `LongPause` (numbers); `Styles` (array of `{Name: string, Value: number}`, replaces the preset's `Styles` wholesale) |
| `master_overrides` | object, optional | `Volume`, `Speed`, `Pitch`, `PitchRange`, `MiddlePause`, `LongPause`, `SentencePause` (numbers) |

Unknown keys and wrong types are rejected with JSON-RPC error -32602.

Result: `content: [{type: "audio", data: <base64 WAV>, mimeType: "audio/wav"}]`
and `structuredContent: {duration_ms, bytes}`.

One operation runs at a time. A call that cannot get the engine in time
returns a tool error `busy`; a call that takes longer than the per-call
timeout (60 s) returns a tool error and `/healthz` reports the engine as
`unhealthy` until the next call succeeds.

Each call works on a dedicated preset named `ochoko:<preset>`, copied from the
base preset with the overrides applied, and sets `MasterControl` to the
baseline captured at the first connect merged with `master_overrides`.

### `list_presets`

No input. Result: `{presets: [{name, preset: <raw preset JSON>}], master_control: <raw baseline JSON>}`.
`ochoko:` working presets are excluded.

### `keep_awake`

Input `{ttl_seconds: integer 1..900}`. Result `{caller, expires_at}` (UTC
ISO 8601). The process holds a `PowerRequestSystemRequired` request while any
lease is unexpired and clears it when none remain. Calling again as the same
caller replaces that caller's lease.

### `GET /healthz`

No auth. `200 {"status":"ok","version":"...","engine":"ready|starting|unavailable|unhealthy"}`.
`version` comes from `release.json` beside the exe, falling back to the
assembly version.

## Configuration

Settings and secrets live in `C:\ProgramData\ochoko\`; persistent data lives in
`C:\apps\ochoko\data\` (logs in `logs\`, temporary WAV files in `tmp\`).
Logs record the length of the text, never its content.

`tokens.json` (required) maps a caller name to its token. Use a different token
for each Gateway:

```json
{ "dev": "<token>", "prod": "<token>" }
```

`config.json` (optional) overrides defaults: `port` (8190), `progId`
(`AI.Talk.Editor.Api.TtsControl`), `hostName` (first of
`GetAvailableHostNames()`), `operationTimeoutSeconds` (60), `busyWaitSeconds`
(5), `warmupTimeoutSeconds` (180), `dataDir`.

## Registering on the GPU box

Run once, by hand, in an elevated PowerShell on the GPU box as the user that
runs A.I.VOICE (`C:\apps` must already exist from chabatake's `deploy.ps1`):

```powershell
.\register.ps1            # or: .\register.ps1 -UserSid S-1-5-21-...
```

It is idempotent. It creates `C:\apps\ochoko\{releases,data}` and
`C:\ProgramData\ochoko`, registers the logon task `ochoko` (non-elevated,
interactive, restarted on failure), opens inbound TCP 8190 from 192.168.50.10,
and grants the URL ACL for `http://+:8190/`. Then put `tokens.json` in
`C:\ProgramData\ochoko`. After the first deploy the script is available at
`C:\apps\ochoko\current\deploy\register.ps1`.

## Deploys

A push to `main` runs `.github/workflows/deploy.yml`:

1. `build` tests, publishes, and zips `ochoko-win-x64.zip` (exe, dependencies,
   `deploy/`, `release.json`).
2. `deploy` waits for approval in the `production` Environment, then
   publishes an immutable GitHub Release `r<run_number>-<sha7>`.
3. It dispatches chabatake's `gpu-box-app` workflow (`app=ochoko`) with a
   GitHub App token and waits for it to succeed. chabatake unpacks the Release
   to `C:\apps\ochoko\releases\<version>`, switches the `current` junction,
   starts the task, and runs `deploy/check.ps1` (polls `/healthz`).
4. Releases older than the newest 3 are deleted with their tags.

`workflow_dispatch` with `ref` redeploys a commit on `main`, which is how a
rollback is done.

## Verifying preset field names

The parameter names come from the official API reference
(https://aivoice.jp/manual/editor/api.html). Confirm them on the real machine
by calling `list_presets` and comparing the raw JSON with the keys listed in
`src/Ochoko.Core/ParamKeys.cs`. If a name differs, fix it in that one file.

```sh
curl -s http://192.168.10.254:8190/mcp \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"list_presets","arguments":{}}}'
```

## Development

```sh
dotnet build -c Release
dotnet test
```

`src/Ochoko` builds on macOS and Linux through
`Microsoft.NETFramework.ReferenceAssemblies`, but it only runs on Windows with
A.I.VOICE Editor installed.

## No licence

Like its sibling repositories, ochoko ships without a licence.
