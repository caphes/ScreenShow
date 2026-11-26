@echo off
setlocal
pushd "%~dp0..\.." || exit /b 1
set "SCREENSHOW_JAVA=java.exe"
set "SCREENSHOW_JAVAC=javac.exe"
if defined JAVA_HOME (
    set "SCREENSHOW_JAVA=%JAVA_HOME%\bin\java.exe"
    set "SCREENSHOW_JAVAC=%JAVA_HOME%\bin\javac.exe"
)
set "SCREENSHOW_CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "SCREENSHOW_TEST=%CD%\android\.build\interop-%RANDOM%-%RANDOM%"
mkdir "%SCREENSHOW_TEST%" || goto :failed
"%SCREENSHOW_JAVAC%" -encoding UTF-8 -d "%SCREENSHOW_TEST%" android\src\main\java\app\screenshow\RemoteSession.java android\tests\Interop.java || goto :failed
"%SCREENSHOW_CSC%" /nologo /target:exe /main:ScreenShow.InteropHost /codepage:65001 /win32manifest:src\ScreenShow.manifest "/out:%SCREENSHOW_TEST%\InteropHost.exe" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll src\*.cs android\tests\InteropHost.cs || goto :failed
"%SCREENSHOW_TEST%\InteropHost.exe" "%SCREENSHOW_JAVA%" "%SCREENSHOW_TEST%"
set "SCREENSHOW_RESULT=%ERRORLEVEL%"
popd
exit /b %SCREENSHOW_RESULT%

:failed
popd
exit /b 1
