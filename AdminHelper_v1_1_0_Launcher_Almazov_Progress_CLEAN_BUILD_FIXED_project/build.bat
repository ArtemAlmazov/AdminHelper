@echo off
cd /d "%~dp0"
title Admin Helper Launcher Edition Build

echo ==============================
echo Admin Helper Launcher Edition
echo ==============================
echo.

echo Checking dotnet SDK...
dotnet --version
if errorlevel 1 (
    echo.
    echo dotnet SDK not found.
    echo Install .NET 8 SDK.
    echo.
    pause
    exit /b
)

echo.
echo Restoring project...
dotnet restore

echo.
echo Publishing ONE EXE...
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:EnableCompressionInSingleFile=true

echo.
echo ==============================
echo Build finished
echo ==============================
echo.
echo Send admins ONLY this file:
echo bin\Release\net8.0-windows\win-x64\publish\AdminHelper.exe
echo.
pause
