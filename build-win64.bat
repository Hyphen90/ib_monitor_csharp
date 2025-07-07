@echo off
echo Building IBMonitor Win64 Single-File EXE...
echo.

dotnet publish --configuration Release

if %ERRORLEVEL% EQU 0 (
    echo.
    echo ✅ Build successful!
    echo.
    echo 📁 Output Directory: bin\Release\net8.0\win-x64\publish\
    echo 📄 Files created:
    echo    - IBMonitor.exe (~3.3MB)
    echo    - config.json (copied automatically)
    echo.
    echo The EXE is framework-dependent and requires .NET 8.0 Runtime on the target system.
    echo All configuration files are included for easy deployment.
    echo.
) else (
    echo.
    echo ❌ Build failed!
    echo.
)

pause
