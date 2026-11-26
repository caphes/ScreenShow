@echo off
setlocal
pushd "%~dp0" || exit /b 1
set "SCREENSHOW_CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%SCREENSHOW_CSC%" (
    echo .NET Framework 4.8 or later is required.
    popd
    exit /b 1
)
if not exist bin mkdir bin
"%SCREENSHOW_CSC%" /nologo /target:winexe /optimize+ /codepage:65001 /win32manifest:src\ScreenShow.manifest /out:bin\ScreenShow.exe /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Security.dll src\*.cs
if errorlevel 1 (
    popd
    exit /b 1
)
echo %CD%\bin\ScreenShow.exe
popd
