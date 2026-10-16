# ochoko を GPU 機に初めて入れるときに、管理者権限で一度だけ手で実行する。何度実行しても同じ状態になる。
#   - C:\apps\ochoko\{releases,data} と C:\ProgramData\ochoko を作り、権限を付ける
#   - ログオン時に実行ユーザーの非昇格で動くタスク ochoko を登録する
#   - 192.168.50.10 から TCP 8190 への受信を許す
#   - http://+:8190/ の URL ACL を実行ユーザーに与える
# アカウントは名前の解決が ssh 越しに失敗するので、SID で扱う。
param(
    # ochoko を動かすユーザーの SID。省略すると、このスクリプトを実行しているユーザー
    [string]$UserSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
)
$ErrorActionPreference = "Stop"

$App = "ochoko"
$Port = 8190
$Gateway = "192.168.50.10"
$AppRoot = "C:\apps\$App"
$Config = Join-Path $env:ProgramData $App
$Admins = "*S-1-5-32-544"
$System = "*S-1-5-18"

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not (New-Object Security.Principal.WindowsPrincipal $identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "管理者権限で実行する"
}
$null = New-Object Security.Principal.SecurityIdentifier $UserSid
$User = "*$UserSid"
Write-Host "==> 実行ユーザーの SID: $UserSid"

# 5.1 は外部コマンドの stderr をエラーとして扱うので、終了コードで判断する
function Invoke-Native([scriptblock]$Command, [string]$What, [switch]$AllowFailure) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    & $Command 2>&1 | Out-Host
    $code = $LASTEXITCODE
    $ErrorActionPreference = $previous
    if ($code -ne 0 -and -not $AllowFailure) { throw "$What に失敗した（終了コード $code）" }
}

# C:\apps は GPU 機の初期セットアップが作り、実行ユーザーに変更を許している。releases と data はそこから継承する
if (-not (Test-Path -LiteralPath (Split-Path $AppRoot) -PathType Container)) {
    throw "$(Split-Path $AppRoot) がない。先に GPU 機の初期セットアップを済ませる"
}
New-Item -ItemType Directory -Force -Path "$AppRoot\releases", "$AppRoot\data" | Out-Null

# 秘密を含む設定。管理者と SYSTEM は全権、実行ユーザーは読むだけ
New-Item -ItemType Directory -Force -Path $Config | Out-Null
Invoke-Native { icacls $Config /inheritance:r /grant:r "${Admins}:(OI)(CI)F" "${System}:(OI)(CI)F" "${User}:(OI)(CI)R" } "$Config の ACL 設定"

Write-Host "==> タスク $App を登録する"
$action = New-ScheduledTaskAction -Execute "$AppRoot\current\ochoko.exe" -WorkingDirectory "$AppRoot\current"
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $UserSid
# A.I.VOICE Editor(GUI)と同じ対話セッションで、同じ非昇格の権限で動かす
$principal = New-ScheduledTaskPrincipal -UserId $UserSid -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) `
    -MultipleInstances IgnoreNew `
    -DontStopIfGoingOnBatteries -AllowStartIfOnBatteries
Register-ScheduledTask -TaskName $App -Action $action -Trigger $trigger `
    -Principal $principal -Settings $settings -Force | Out-Null

Write-Host "==> ファイアウォール規則を入れる"
Get-NetFirewallRule -DisplayName $App -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -DisplayName $App -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port `
    -RemoteAddress $Gateway -Profile Any | Out-Null

Write-Host "==> URL ACL を与える"
$url = "http://+:$Port/"
Invoke-Native { netsh http delete urlacl url=$url } "URL ACL の削除" -AllowFailure
Invoke-Native { netsh http add urlacl url=$url "sddl=D:(A;;GX;;;$UserSid)" } "URL ACL の追加"

Get-ScheduledTask -TaskName $App | Select-Object TaskName, State
if (-not (Test-Path -LiteralPath (Join-Path $Config "tokens.json"))) {
    Write-Host "次に $Config\tokens.json を置く（{`"dev`": `"<token>`", `"prod`": `"<token>`"}）。Gateway の dev と prod で別のトークンにする"
}
