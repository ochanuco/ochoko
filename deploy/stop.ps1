# 版を止めるとき、配備ワークフローがタスクより先に実行する(作業フォルダは止める版、引数なし)。
# タスクを止めても、手で起動した ochoko.exe は残って :8190 を握り続けるので、プロセスも終わらせる。
# 終了コード 0 で成功。プロセスが残れば 1 を返し、配備は失敗する。
$ErrorActionPreference = "Stop"

$App = if ($env:APP_NAME) { $env:APP_NAME } else { "ochoko" }

if (Get-ScheduledTask -TaskName $App -ErrorAction SilentlyContinue) {
    Stop-ScheduledTask -TaskName $App -ErrorAction SilentlyContinue
}

foreach ($p in @(Get-Process -Name $App -ErrorAction SilentlyContinue)) {
    Write-Host "==> $App.exe (pid $($p.Id)) を終了する"
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
}

$deadline = (Get-Date).AddSeconds(15)
while ($true) {
    $left = @(Get-Process -Name $App -ErrorAction SilentlyContinue)
    if ($left.Count -eq 0) { break }
    if ((Get-Date) -gt $deadline) {
        Write-Host "$App.exe が終了しない: pid $($left.Id -join ', ')"
        exit 1
    }
    Start-Sleep -Milliseconds 500
}
Write-Host "$App は止まっている"
exit 0
