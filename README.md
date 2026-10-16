# ochoko

A thin Voice MCP server for [A.I.VOICE](https://aivoice.jp/).
It receives text plus raw A.I.VOICE values and returns a WAV. It has no
character logic and no prosody IR; the caller converts semantic prosody into
A.I.VOICE values before calling. It was written as the A.I.VOICE backend for
[yunomi](https://github.com/ochanuco/yunomi).

It runs on the GPU box (Windows 11) as a logon scheduled task of the
interactive user, in the same non-elevated session as the A.I.VOICE Editor
GUI, and serves `http://+:8190/`. Only one gateway host (192.168.50.10) is
allowed to connect.

```text
src/Ochoko.Core          MCP JSON-RPC, tool validation, Bearer auth, keep-awake leases, WAV parsing, IVoiceEngine
src/Ochoko               ochoko.exe (net48, x64): HttpListener host, A.I.VOICE engine over COM, power request, logging
tests/Ochoko.Core.Tests  xunit tests for Ochoko.Core
deploy/                  hooks run by the deployer, and register.ps1
```

The exe targets .NET Framework 4.8 because A.I.VOICE's COM server is an
in-process .NET Framework component that cannot load into a .NET (Core)
process. The A.I.VOICE API DLL is not referenced (it cannot be committed);
`TtsControl` is called by late binding through its ProgID.

## MCP

`POST /mcp` speaks MCP Streamable HTTP in stateless JSON mode (protocol
version `2025-06-18` only). There are no sessions, no batches, and no SSE:
`GET /mcp` and `DELETE /mcp` return 405. A request other than `initialize`
whose `MCP-Protocol-Version` header is present and not `2025-06-18` gets 400;
an absent header is treated as `2025-06-18`.

Every request needs `Authorization: Bearer <token>`. The token maps to a
caller name (`dev`, `prod`, ...), which together with `partner_id` keys the
keep-awake lease. A
request that carries an `Origin` header is rejected with 403.

### `synthesize`

| Input | Type | Notes |
|---|---|---|
| `text` | string | 1..2000 characters |
| `preset` | string | A voice preset name from `list_presets`. The user's preset is never modified |
| `preset_overrides` | object, optional | `Volume`, `Speed`, `Pitch`, `PitchRange`, `MiddlePause`, `LongPause` (numbers); `Styles` (array of `{Name: string, Value: number}`, replaces the preset's `Styles` wholesale) |
| `master_overrides` | object, optional | `Volume`, `Speed`, `Pitch`, `PitchRange`, `MiddlePause`, `LongPause`, `SentencePause` (numbers) |

Unknown keys and wrong types are rejected with JSON-RPC error -32602.

Result: `content: [{type: "audio", data: <base64 WAV>, mimeType: "audio/wav"}, {type: "text", text: <structuredContent as JSON>}]`
and `structuredContent: {duration_ms, bytes}`.

One operation runs at a time. A call that cannot get the engine in time
returns a tool error `busy`; a call that takes longer than the per-call
timeout (45 s, after at most 5 s waiting for the engine, so a call finishes
within 50 s) returns a tool error and `/healthz` reports the engine as
`unhealthy`. While the timed-out operation is still running, later calls fail
immediately. If it is still stuck 20 s after the timeout, ochoko exits with
code 3 so the scheduled task can restart it. `unhealthy` otherwise lasts until
the engine is connected again by the next warm-up (every 30 s) or call.

Each call adds (or updates) a working preset named `ochoko:<preset>` in the
Editor's preset list, copied from the base preset with the overrides applied,
and selects it. The user's preset is never modified. `MasterControl` is set to
the baseline merged with `master_overrides` for the call and written back to
the baseline afterwards. The baseline is read from the Editor once and saved
to `master-baseline.json` in the data directory; later starts load that file.
Delete the file to capture the Editor's current `MasterControl` again.

### `list_presets`

No input. Result: `{presets: [{name, preset: <raw preset JSON>}], master_control: <raw baseline JSON>}`
(`master_control` is the baseline, not the Editor's current value).
`ochoko:` working presets are excluded.

### `keep_awake`

Input `{ttl_seconds: integer 1..900, partner_id: string 1..64 characters}`.
Both are required; `partner_id` is the character the lease belongs to and has
no default. A missing, empty (after trimming), non-string, or over-long
`partner_id` is rejected with `-32602`, so a caller that forgets it cannot
silently share a lease with others.
Result `{caller, partner_id, expires_at}` (UTC ISO 8601). Leases are keyed by
caller and `partner_id`. The process holds a `PowerRequestSystemRequired`
request while any lease is unexpired and clears it when none remain. Calling
again with the same caller and `partner_id` replaces that lease; leases of
other partners are untouched.

### `GET /healthz`

No auth. `200 {"status":"ok","version":"...","engine":"ready|starting|unavailable|unhealthy"}`.
`version` comes from `release.json` beside the exe, falling back to the
assembly version.

## Configuration

Settings and secrets live in `C:\ProgramData\ochoko\`; persistent data lives in
`C:\apps\ochoko\data\` (logs in `logs\`, temporary WAV files in `tmp\`,
`master-baseline.json`). Failures before the logger exists (for example an
unreadable `config.json`) are written to `C:\apps\ochoko\data\logs`, or
`%TEMP%\ochoko` if that is not writable, and the process exits with code 2.
Logs record the length of the text, never its content.

`tokens.json` (required) maps a caller name to its token. Use a different token
for each Gateway:

```json
{ "dev": "<token>", "prod": "<token>" }
```

`config.json` (optional) overrides defaults: `port` (8190), `progId`
(`AI.Talk.Editor.Api.TtsControl`), `hostName` (first of
`GetAvailableHostNames()`), `operationTimeoutSeconds` (45), `busyWaitSeconds`
(5), `warmupTimeoutSeconds` (180), `dataDir`. Keep `busyWaitSeconds` plus
`operationTimeoutSeconds` under 55 s, below the MCP client's default 60 s
request timeout.

## Registering on the GPU box

Run once, by hand, in an elevated PowerShell on the GPU box as the user that
runs A.I.VOICE (`C:\apps` must already exist, created by the GPU box's own
setup; the script stops if it does not):

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
3. It dispatches the GPU box's deployer workflow (`app=ochoko`) with a
   GitHub App token and waits for it to succeed. The deployer unpacks the Release
   to `C:\apps\ochoko\releases\<version>`, runs the old version's
   `deploy/stop.ps1` (stops the task and any remaining `ochoko.exe`), switches
   the `current` junction, starts the task, and runs `deploy/check.ps1`
   (polls `/healthz`).
4. Releases older than the newest 3 are deleted with their tags.

`workflow_dispatch` with `ref` redeploys a commit on `main`, which is how a
rollback is done.

Callers reach the server at `http://<box>:8190/mcp`.

## Verifying preset field names

The parameter names come from the official API reference
(https://aivoice.jp/manual/editor/api.html). Confirm them on the real machine
by calling `list_presets` and comparing the raw JSON with the keys listed in
`src/Ochoko.Core/ParamKeys.cs`. If a name differs, fix it in that one file.

```sh
curl -s http://<box>:8190/mcp \
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

## License

[MIT](LICENSE)
