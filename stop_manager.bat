@echo off
setlocal
echo Requesting all R-fbm windows to close gracefully...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$items = Get-Process -Name 'Rfbm.Native' -ErrorAction SilentlyContinue; if (-not $items) { Write-Host 'R-fbm is not running.'; exit 0 }; $closed = 0; foreach ($item in $items) { if ($item.CloseMainWindow()) { $closed++ } }; Write-Host ('Close request sent to {0} window(s).' -f $closed)"
if errorlevel 1 pause
