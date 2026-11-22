# Hallow Email installer: copies the app to your user folder (no admin needed) and adds Start menu + Desktop shortcuts.
# It never signs in to anything by itself - you add your accounts in the app.
#   Install.ps1 [-NoStart]   (-NoStart: don't open the app afterwards)
param([switch]$NoStart)
$ErrorActionPreference = 'Stop'
$dest = Join-Path $env:LOCALAPPDATA 'Programs\Hallow Email'
New-Item -ItemType Directory -Force $dest | Out-Null
$wasRunning = [bool](Get-Process HallowEmail -ErrorAction SilentlyContinue)
if ($wasRunning) { try { $old = Join-Path $dest 'HallowEmail.exe'; if (Test-Path $old) { & $old --cmd quit | Out-Null; Start-Sleep -Milliseconds 2000 } } catch { } }   # saves the cache and the send queue first
Get-Process HallowEmail -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
Get-ChildItem $PSScriptRoot -File | Where-Object { ($_.Extension -in '.exe', '.dll', '.txt', '.md') -or $_.Name -eq 'Uninstall.ps1' } | Copy-Item -Destination $dest -Force
if (Test-Path (Join-Path $PSScriptRoot 'licenses')) { Copy-Item (Join-Path $PSScriptRoot 'licenses') $dest -Recurse -Force }
$exe = Join-Path $dest 'HallowEmail.exe'
# Windows Settings > Apps entry (per user, no admin)
$u = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\HallowEmail'
New-Item $u -Force | Out-Null
$ver = (Get-Item $exe).VersionInfo.FileVersion -replace '\.0\.0$', ''
$vals = @{ DisplayName = 'Hallow Email'; DisplayVersion = $ver; Publisher = 'Hallow'; DisplayIcon = $exe; InstallLocation = $dest
    UninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$(Join-Path $dest 'Uninstall.ps1')`""
    URLInfoAbout = 'https://github.com/Dom428/hallow-email' }
foreach ($k in $vals.Keys) { Set-ItemProperty $u -Name $k -Value $vals[$k] }
Set-ItemProperty $u -Name NoModify -Value 1 -Type DWord; Set-ItemProperty $u -Name NoRepair -Value 1 -Type DWord
Set-ItemProperty $u -Name EstimatedSize -Value ([int]((Get-ChildItem $dest -Recurse -File | Measure-Object Length -Sum).Sum / 1KB)) -Type DWord
$w = New-Object -ComObject WScript.Shell
foreach ($dir in [Environment]::GetFolderPath('Programs'), [Environment]::GetFolderPath('Desktop')) {
    $s = $w.CreateShortcut((Join-Path $dir 'Hallow Email.lnk'))
    $s.TargetPath = $exe; $s.WorkingDirectory = $dest; $s.Description = 'A private desktop email client, Hallow style'; $s.Save()
}
$rt = $false
foreach ($k in 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'HKCU:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}') { if (Test-Path $k) { $rt = $true } }
if (-not $rt) { Write-Host "Note: the Microsoft Edge WebView2 Runtime wasn't found - Hallow Email shows mail as plain text until it's installed (Windows 11 has it)." }
if ($NoStart) { Write-Host "Hallow Email is installed. It's in your Start menu and on your Desktop."; return }
if ($wasRunning) { Start-Process $exe -ArgumentList '--tray' } else { Start-Process $exe }
Write-Host "Hallow Email is installed. It's in your Start menu and on your Desktop."
