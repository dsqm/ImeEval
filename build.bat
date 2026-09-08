@echo off
rem ===============================================
rem ImeBench 一键编译：成功自动关窗，失败保留窗口
rem ===============================================
chcp 65001 >nul
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

rem ── ImeBench.exe 正在运行会锁住文件导致编译失败，提前拦截 ──
%SystemRoot%\System32\tasklist.exe /FI "IMAGENAME eq ImeBench.exe" 2>nul | %SystemRoot%\System32\find.exe /I "ImeBench.exe" >nul
if not errorlevel 1 (
    echo.
    echo [错误] 检测到 ImeBench.exe 正在运行，请先关闭程序窗口再编译。
    echo.
    pause
    exit /b 1
)

echo.
echo ======== ImeBench 编译中... ========
echo.
call dotnet build --nologo -v q
if errorlevel 1 (
    echo.
    echo ======== 编译失败，按任意键关闭窗口 ========
    pause >nul
    exit /b 1
)
exit /b 0
