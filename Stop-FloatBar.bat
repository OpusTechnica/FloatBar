@echo off
taskkill /F /IM FloatBar.exe 2>nul
if %errorlevel% equ 0 (
    echo FloatBar stopped.
) else (
    echo FloatBar is not currently running.
)
