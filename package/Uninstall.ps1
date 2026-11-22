# Hallow Email uninstaller (run from Windows Settings > Apps, or directly).
$dest = Join-Path $env:LOCALAPPDATA 'Programs\Hallow Email'
Add-Type -AssemblyName System.Windows.Forms
$msg = "Uninstall Hallow Email?`n`nThis also removes what it keeps on this PC: the encrypted mail cache, saved app passwords / sign-ins, rules and the spam filter's learning. Your mail on the servers is not touched. Mail still waiting in the Outbox is not sent."
if ([System.Windows.Forms.MessageBox]::Show($msg, 'Uninstall Hallow Email', 'YesNo', 'Question') -ne 'Yes') { return }
if (Get-Process HallowEmail -ErrorAction SilentlyContinue) { try { & (Join-Path $dest 'HallowEmail.exe') --cmd quit | Out-Null; Start-Sleep -Milliseconds 2000 } catch { } }
Get-Process HallowEmail -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 600
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'HallowEmail' -ErrorAction SilentlyContinue
foreach ($dir in [Environment]::GetFolderPath('Programs'), [Environment]::GetFolderPath('Desktop')) { Remove-Item (Join-Path $dir 'Hallow Email.lnk') -ErrorAction SilentlyContinue }
Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\HallowEmail' -Recurse -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:APPDATA 'HallowEmail') -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:LOCALAPPDATA 'HallowEmail') -Recurse -Force -ErrorAction SilentlyContinue
Start-Process cmd.exe -ArgumentList "/c timeout /t 2 /nobreak >nul & rmdir /s /q `"$dest`"" -WindowStyle Hidden
[System.Windows.Forms.MessageBox]::Show("Hallow Email is uninstalled. Your mail is still on your providers' servers.", 'Hallow Email') | Out-Null
