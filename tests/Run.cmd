@echo off
setlocal
pushd "%~dp0.." || exit /b 1
set "SCREENSHOW_CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist bin mkdir bin
"%SCREENSHOW_CSC%" /nologo /target:exe /main:ScreenShow.Smoke /codepage:65001 /win32manifest:src\ScreenShow.manifest /out:bin\Smoke.exe /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Security.dll src\*.cs tests\Smoke.cs
if errorlevel 1 (
    popd
    exit /b 1
)
bin\Smoke.exe %*
set "SCREENSHOW_RESULT=%ERRORLEVEL%"
popd
exit /b %SCREENSHOW_RESULT%
