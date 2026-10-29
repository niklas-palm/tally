# Removes Tally, as Settings → Installed apps → Uninstall does: the app, its Start menu and startup entries, and its
# tally:// links. %LOCALAPPDATA%\Tally (unsent recordings, settings, logs, the .NET SDK), the sign-in in Credential
# Manager and the meetings in the cloud stay: docs/troubleshooting.md says how to remove them.
$ErrorActionPreference = 'SilentlyContinue'
# Run through iex, so there's no script folder: use where the installer always puts Tally
$programs = Join-Path $env:LOCALAPPDATA 'Programs\Tally'
$recording = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Tally\recordings\*\local.json') |
    Where-Object { (Get-Content -Raw $_.FullName) -match '"state"\s*:\s*"recording"' }
$running = Get-Process Tally | Where-Object { $_.Path -like "$programs*" }
if ($running -and $recording) {
    Write-Host 'Tally is recording. Uninstall it when the recording is done.' -ForegroundColor Red
    Start-Sleep 5  # from Settings, its window closes as it ends: long enough to read why
    exit 1
}
$running | Stop-Process -Force
Start-Sleep 1
Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'Tally.lnk')
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' Tally
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run' Tally
Remove-Item 'HKCU:\Software\Classes\tally' -Recurse
Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Tally' -Recurse
Remove-Item $programs -Recurse -Force
