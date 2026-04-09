@echo off
echo ============================================================
echo  BinaryCarver -- Build Script
echo ============================================================

where dotnet >nul 2>nul
if errorlevel 1 (
    echo ERROR: dotnet SDK not found. Install .NET 9 SDK.
    exit /b 1
)

echo .NET SDK found:
dotnet --version

echo [1/3] Restoring NuGet packages...
dotnet restore "%~dp0BinaryCarver.csproj" -r win-x64 --verbosity quiet
if errorlevel 1 ( echo Restore FAILED & exit /b 1 )

echo [2/3] Building Release x64...
dotnet build "%~dp0BinaryCarver.csproj" -c Release -r win-x64 --no-restore --verbosity minimal
if errorlevel 1 ( echo BUILD FAILED & exit /b 1 )

echo [3/3] Publishing self-contained x64...
dotnet publish "%~dp0BinaryCarver.csproj" -c Release -r win-x64 --self-contained true -o "%~dp0publish" --verbosity minimal
if errorlevel 1 ( echo PUBLISH FAILED & exit /b 1 )

echo.
echo ============================================================
echo  BUILD SUCCESSFUL
echo  Output: %~dp0publish\BinaryCarver.exe
echo ============================================================
