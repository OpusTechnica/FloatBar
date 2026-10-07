@echo off
setlocal
echo =========================================================
echo Compiling FloatBar (High-Precision CPU and Live RAM Telemetry)
echo =========================================================

set CSC="C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist %CSC% (
    echo Error: C# compiler csc.exe not found at %CSC%
    exit /b 1
)

%CSC% /target:winexe /optimize+ /platform:x64 /out:"%~dp0FloatBar.exe" ^
    /reference:"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF\PresentationCore.dll" ^
    /reference:"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF\PresentationFramework.dll" ^
    /reference:"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF\WindowsBase.dll" ^
    /reference:"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.Xaml.dll" ^
    /reference:System.dll ^
    /reference:System.Core.dll ^
    /reference:System.Drawing.dll ^
    /reference:System.Windows.Forms.dll ^
    /reference:System.Management.dll ^
    "%~dp0FloatBar.cs"

if %errorlevel% equ 0 (
    echo [SUCCESS] FloatBar.exe compiled successfully!
) else (
    echo [ERROR] Compilation failed.
    exit /b 1
)
