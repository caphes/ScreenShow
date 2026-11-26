@echo off
setlocal
pushd "%~dp0" || exit /b 1

if defined JAVA_HOME (
    set "SCREENSHOW_JAVA=%JAVA_HOME%\bin\"
) else (
    where javac >nul 2>nul || goto :no_java
)
set "SCREENSHOW_SDK=%ANDROID_HOME%"
if not defined SCREENSHOW_SDK set "SCREENSHOW_SDK=%ANDROID_SDK_ROOT%"
if defined SCREENSHOW_SDK (
    set "SCREENSHOW_PLATFORM=%SCREENSHOW_SDK%\platforms\android-36\android.jar"
    set "SCREENSHOW_TOOLS=%SCREENSHOW_SDK%\build-tools\36.0.0"
) else (
    set "SCREENSHOW_PLATFORM=%CD%\.sdk\platform\android-36\android.jar"
    set "SCREENSHOW_TOOLS=%CD%\.sdk\build-tools\android-16"
)
if not exist "%SCREENSHOW_PLATFORM%" goto :no_sdk
if not exist "%SCREENSHOW_TOOLS%\apksigner.bat" goto :no_sdk

set "SCREENSHOW_WORK=.build\%RANDOM%-%RANDOM%"
mkdir "%SCREENSHOW_WORK%\classes" "%SCREENSHOW_WORK%\dex" || goto :failed
if not exist ..\bin mkdir ..\bin || goto :failed
if not exist .signing mkdir .signing || goto :failed
set "SCREENSHOW_KEY=.signing\screenshow-local.p12"
if not exist "%SCREENSHOW_KEY%" (
    "%SCREENSHOW_JAVA%keytool.exe" -genkeypair -keystore "%SCREENSHOW_KEY%" -storetype PKCS12 -storepass android -keypass android -alias screenshow -keyalg RSA -keysize 3072 -validity 10000 -dname "CN=ScreenShow Local Build"
    if errorlevel 1 goto :failed
)

"%SCREENSHOW_TOOLS%\aapt2.exe" compile --dir src\main\res -o "%SCREENSHOW_WORK%\resources.zip" || goto :failed
"%SCREENSHOW_TOOLS%\aapt2.exe" link -I "%SCREENSHOW_PLATFORM%" --manifest src\main\AndroidManifest.xml --java "%SCREENSHOW_WORK%" -o "%SCREENSHOW_WORK%\unsigned.apk" "%SCREENSHOW_WORK%\resources.zip" || goto :failed
"%SCREENSHOW_JAVA%javac.exe" -encoding UTF-8 --release 8 -Xlint:-options -classpath "%SCREENSHOW_PLATFORM%" -d "%SCREENSHOW_WORK%\classes" src\main\java\app\screenshow\MainActivity.java src\main\java\app\screenshow\RemoteScreen.java src\main\java\app\screenshow\RemoteSession.java "%SCREENSHOW_WORK%\app\screenshow\R.java" || goto :failed
"%SCREENSHOW_JAVA%jar.exe" cf "%SCREENSHOW_WORK%\bytecode.jar" -C "%SCREENSHOW_WORK%\classes" . || goto :failed
call "%SCREENSHOW_TOOLS%\d8.bat" --lib "%SCREENSHOW_PLATFORM%" --min-api 26 --release --output "%SCREENSHOW_WORK%\dex" "%SCREENSHOW_WORK%\bytecode.jar" || goto :failed
"%SCREENSHOW_JAVA%jar.exe" uf "%SCREENSHOW_WORK%\unsigned.apk" -C "%SCREENSHOW_WORK%\dex" classes.dex || goto :failed
"%SCREENSHOW_TOOLS%\zipalign.exe" -f 4 "%SCREENSHOW_WORK%\unsigned.apk" "%SCREENSHOW_WORK%\aligned.apk" || goto :failed
call "%SCREENSHOW_TOOLS%\apksigner.bat" sign --ks "%SCREENSHOW_KEY%" --ks-key-alias screenshow --ks-pass pass:android --key-pass pass:android --out ..\bin\ScreenShow-Android.apk "%SCREENSHOW_WORK%\aligned.apk" || goto :failed
call "%SCREENSHOW_TOOLS%\apksigner.bat" verify --verbose ..\bin\ScreenShow-Android.apk || goto :failed
echo %CD%\..\bin\ScreenShow-Android.apk
popd
exit /b 0

:no_java
echo A JDK is required. Set JAVA_HOME or add the JDK to PATH.
goto :failed

:no_sdk
echo Android platform 36 and build tools 36.0.0 are required. Set ANDROID_HOME.

:failed
popd
exit /b 1
