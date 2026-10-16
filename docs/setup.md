# Setting up ochoko

This installs ochoko by hand on one Windows machine. The automated pipeline in
[deployment.md](deployment.md) repeats steps 3 and 6 for each release; the
rest is done once.

## Requirements

- Windows 10 or 11, x64, with .NET Framework 4.8
- A.I.VOICE Editor installed, with at least one voice, for the user that will
  run ochoko. ochoko starts the Editor host itself if it is not running, so the
  user must be logged on interactively.
- The machine that calls ochoko (the gateway) has a fixed address reachable
  from this machine.

## 1. Prepare `C:\apps`

`register.ps1` expects `C:\apps` to exist and to be writable by the user that
runs ochoko. In an elevated PowerShell:

```powershell
New-Item -ItemType Directory -Force C:\apps | Out-Null
icacls C:\apps /grant "${env:USERNAME}:(OI)(CI)M"
```

## 2. Get a build

Download `ochoko-win-x64.zip` from the repository's Releases, or build it:

```sh
dotnet publish src/Ochoko -c Release -o stage
cp -r deploy stage/deploy
```

The zip (or `stage`) holds `ochoko.exe`, its dependencies, `deploy/`, and, for
Releases, `release.json`. Without `release.json`, `/healthz` reports the
assembly version.

## 3. Unpack it

Extract the build into `C:\apps\ochoko\releases\<version>` (any folder name)
and point `current` at it:

```powershell
$version = "r1-abcdef0"   # the folder you extracted into
New-Item -ItemType Directory -Force C:\apps\ochoko\releases | Out-Null
Expand-Archive ochoko-win-x64.zip "C:\apps\ochoko\releases\$version"
New-Item -ItemType Junction -Path C:\apps\ochoko\current -Target "C:\apps\ochoko\releases\$version"
```

## 4. Register

Run once, in an elevated PowerShell, as the user that runs A.I.VOICE:

```powershell
C:\apps\ochoko\current\deploy\register.ps1 -Gateway 192.0.2.10
# other user:      -UserSid S-1-5-21-...
# several callers: -Gateway 192.0.2.10,192.0.2.11  (CIDR also works)
```

It is idempotent. It creates `C:\apps\ochoko\{releases,data}` and
`C:\ProgramData\ochoko`, registers the logon task `ochoko` (non-elevated,
interactive, restarted on failure), opens inbound TCP 8190 from the gateway
addresses, and grants the URL ACL for `http://+:8190/`. Users are handled by
SID because name lookup fails over SSH sessions.

## 5. Configure

Settings and secrets live in `C:\ProgramData\ochoko\`; persistent data lives in
`C:\apps\ochoko\data\` (logs in `logs\`, temporary WAV files in `tmp\`,
`master-baseline.json`). Failures before the logger exists (for example an
unreadable `config.json`) are written to `C:\apps\ochoko\data\logs`, or
`%TEMP%\ochoko` if that is not writable, and the process exits with code 2.
Logs record the length of the text, never its content.

`tokens.json` (required) maps a caller name to its token. Give each caller its
own token; the caller name appears in logs and keys keep-awake leases.

```json
{ "dev": "<token>", "prod": "<token>" }
```

`config.json` (optional) overrides defaults: `port` (8190), `progId`
(`AI.Talk.Editor.Api.TtsControl`), `hostName` (first of
`GetAvailableHostNames()`), `operationTimeoutSeconds` (45), `busyWaitSeconds`
(5), `warmupTimeoutSeconds` (180), `dataDir`. Keep `busyWaitSeconds` plus
`operationTimeoutSeconds` under 55 s, below the MCP client's default 60 s
request timeout. Changing `port` also requires a new firewall rule and URL ACL;
`register.ps1` only covers 8190.

## 6. Start and verify

```powershell
Start-ScheduledTask ochoko
C:\apps\ochoko\current\deploy\check.ps1   # waits up to 60 s for /healthz
```

`/healthz` answers before A.I.VOICE is connected; `engine` turns `ready` once
the first warm-up finishes. From the gateway:

```sh
curl -s http://<box>:8190/mcp \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"list_presets","arguments":{}}}'
```

The parameter names come from the official API reference
(https://aivoice.jp/manual/editor/api.html). Compare the raw preset JSON with
the keys in `src/Ochoko.Core/ParamKeys.cs`; if a name differs, fix it in that
one file.

## Upgrading and rolling back

1. Extract the new build into its own `releases\<version>` folder.
2. Run the running version's `deploy\stop.ps1`. It stops the task and any
   `ochoko.exe` left over (a hand-started one keeps holding the port), and
   exits 1 if a process survives.
3. Point the `current` junction at the new folder
   (`Remove-Item C:\apps\ochoko\current; New-Item -ItemType Junction ...`).
4. `Start-ScheduledTask ochoko`, then run the new version's `deploy\check.ps1`,
   which also fails if an older version is still answering.

Rolling back is the same with an older folder.
