$WshShell = New-Object -ComObject WScript.Shell
$DesktopPath = [System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::Desktop)
$ShortcutPath = Join-Path $DesktopPath "FloatBar.lnk"
$Shortcut = $WshShell.CreateShortcut($ShortcutPath)
$Shortcut.TargetPath = "$PSScriptRoot\FloatBar.exe"
$Shortcut.WorkingDirectory = "$PSScriptRoot"
$Shortcut.Description = "FloatBar - Lightweight CPU & Disk Monitor"
$Shortcut.Save()
Write-Host "[SUCCESS] FloatBar shortcut created on Desktop: $ShortcutPath"
