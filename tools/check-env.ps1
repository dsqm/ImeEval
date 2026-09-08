# .NET / NuGet environment diagnostics for ImeBench
# Run:  powershell -NoProfile -ExecutionPolicy Bypass -File check-env.ps1
# Output: console + env-check.log (next to this script)

$ErrorActionPreference = 'Continue'
$logDir  = Split-Path -Parent $MyInvocation.MyCommand.Path
$logFile = Join-Path $logDir 'env-check.log'
$root    = Split-Path -Parent $logDir

function Log($msg) {
    Write-Host $msg
    Add-Content -Path $logFile -Value $msg
}

function Run($title, $exe, $args) {
    Log ""
    Log "=================================================="
    Log "  $title"
    Log "=================================================="
    $out = & $exe @args 2>&1
    $code = $LASTEXITCODE
    Log ($out | Out-String)
    Log "[exit=$code]"
    return $code
}

"=== ImeBench .NET env check  $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===" | Set-Content -Path $logFile
Log "Machine : $env:COMPUTERNAME"
Log "User    : $env:USERDOMAIN\$env:USERNAME"
Log "Root    : $root"

# ---------- 1. SDK ----------
Run "1. dotnet --list-sdks" "dotnet" @("--list-sdks")
Run "1b. dotnet --version" "dotnet" @("--version")

# ---------- 2. Environment variables ----------
Log ""
Log "=================================================="
Log "  2. Environment variables (null/empty is a red flag)"
Log "=================================================="
$vars = @(
    'USERPROFILE','APPDATA','LOCALAPPDATA','PROGRAMDATA','ALLUSERSPROFILE',
    'HOME','TEMP','TMP','NUGET_PACKAGES','DOTNET_ROOT','MSBUILDDISABLENODEREUSE',
    'DOTNET_CLI_TELEMETRY_OPTOUT','MSBuildSDKsPath'
)
foreach ($v in $vars) {
    $val = [Environment]::GetEnvironmentVariable($v)
    if ([string]::IsNullOrEmpty($val)) { $val = '<NOT SET>' }
    Log ("  {0,-28} = {1}" -f $v, $val)
}

# ---------- 3. NuGet (the failing part) ----------
$nugetCode = Run "3. dotnet nuget locals all --list" "dotnet" @("nuget","locals","all","--list")

# ---------- 4. dotnet --info (workload errors show up here) ----------
Run "4. dotnet --info" "dotnet" @("--info")

# ---------- 5. Minimal project ----------
Log ""
Log "=================================================="
Log "  5. Minimal net10.0 console project (clean room test)"
Log "=================================================="
$tmp = Join-Path $env:TEMP 'imeenvcheck'
if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
New-Item -ItemType Directory -Path $tmp -Force | Out-Null
Set-Location $tmp
& dotnet new console -o app 2>&1 | Out-Null
Set-Location (Join-Path $tmp 'app')
if (Test-Path (Join-Path $tmp 'app' 'global.json')) { Remove-Item (Join-Path $tmp 'app' 'global.json') -Force }
$minCode = Run "5a. dotnet build (minimal, no global.json)" "dotnet" @("build","-v","q","--nologo")

if ($minCode -ne 0) {
    Log ">>> Minimal build FAILED. Retrying with MSBuild node-reuse disabled..."
    $env:MSBUILDDISABLENODEREUSE = 1
    $minCode2 = Run "5b. dotnet build -nr:false (node reuse off)" "dotnet" @("build","-nr:false","-v","q","--nologo")
    Log ">>> KEY RESULT: if 5b succeeded but 5a failed, stale MSBuild worker nodes are the cause."
    Log ">>>             Fix: run  dotnet build-server shutdown  then retry."
}

# ---------- 6. ImeBench itself ----------
Set-Location $root
Log ""
Log "=================================================="
Log "  6. ImeBench project"
Log "=================================================="
Log "cwd = $root"
$benchCode = Run "6. dotnet build (ImeBench, net10.0-windows)" "dotnet" @("build","-v","q","--nologo")

if ($benchCode -ne 0) {
    Log ">>> ImeBench build FAILED. Trying with explicit package path..."
    $env:NUGET_PACKAGES = Join-Path $env:USERPROFILE '.nuget\packages'
    Run "6b. dotnet build with NUGET_PACKAGES set" "dotnet" @("build","-v","q","--nologo")
}

# ---------- 7. Verdict ----------
Log ""
Log "=================================================="
Log "  7. VERDICT"
Log "=================================================="
if ($nugetCode -eq 0) {
    Log "  [OK]   NuGet resolved local paths -> restore should work."
} else {
    Log "  [FAIL] NuGet cannot resolve local paths -> ALL dotnet restore/build will fail."
    Log "         This is a machine-level .NET/NuGet install problem, not a project problem."
    Log "         Suggested: Control Panel > Programs > repair/reinstall .NET SDK 10.0.400,"
    Log "         or run   dotnet-core-uninstall   and reinstall from https://dot.net/download"
}
if ($minCode -eq 0)   { Log "  [OK]   Minimal project builds." }   else { Log "  [FAIL] Minimal project does NOT build." }
if ($benchCode -eq 0) { Log "  [OK]   ImeBench builds." }          else { Log "  [FAIL] ImeBench does NOT build." }

Log ""
Log "Full log: $logFile"
Write-Host ""
Write-Host "Log written to: $logFile" -ForegroundColor Cyan
