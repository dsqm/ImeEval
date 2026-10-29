@echo off
rem ===============================================
rem ImeEval 一键编译发布：Release 框架依赖单文件 → publish\ImeEval.exe
rem ===============================================
cd /d "%~dp0"

rem ── 环境变量兜底（异常环境下 NuGet 需要，缺失会报 path1 null）──
if not defined SystemDrive set "SystemDrive=C:"
if not defined SystemRoot set "SystemRoot=%SystemDrive%\WINDOWS"
if not defined APPDATA (
    if defined USERPROFILE (set "APPDATA=%USERPROFILE%\AppData\Roaming") else (set "APPDATA=%SystemDrive%\Users\%USERNAME%\AppData\Roaming")
)
if not defined LOCALAPPDATA (
    if defined USERPROFILE (set "LOCALAPPDATA=%USERPROFILE%\AppData\Local") else (set "LOCALAPPDATA=%SystemDrive%\Users\%USERNAME%\AppData\Local")
)
if not defined PROGRAMDATA set "PROGRAMDATA=%SystemDrive%\ProgramData"
if not defined ALLUSERSPROFILE set "ALLUSERSPROFILE=%SystemDrive%\ProgramData"
if not defined ProgramFiles set "ProgramFiles=%SystemDrive%\Program Files"
if not defined CommonProgramFiles set "CommonProgramFiles=%ProgramFiles%\Common Files"
if not defined PUBLIC set "PUBLIC=%SystemDrive%\Users\Public"

rem ── ImeEval.exe 正在运行会锁住文件导致编译失败，提前拦截 ──
%SystemRoot%\System32\tasklist.exe /FI "IMAGENAME eq ImeEval.exe" 2>nul | %SystemRoot%\System32\find.exe /I "ImeEval.exe" >nul
if not errorlevel 1 (
    echo.
    echo [错误] 检测到 ImeEval.exe 正在运行，请先关闭程序窗口再编译。
    echo.
    if not defined CI pause
    exit /b 1
)

echo.
echo ======== ImeEval 编译发布中... ========
echo.
call dotnet publish --nologo -v q -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
if errorlevel 1 (
    echo.
    echo ======== 编译失败 ========
    if not defined CI pause >nul
    exit /b 1
)
echo.
echo ======== 完成：publish\ImeEval.exe（框架依赖单文件，需 .NET 10 桌面运行时） ========
echo.
exit /b 0
