# Restores Windows environment variables that are MISSING on this machine.
# Missing APPDATA / PROGRAMDATA makes NuGet crash with:
#   "Value cannot be null. (Parameter 'path1')"  -> every dotnet build fails.
#
# Run:  powershell -NoProfile -ExecutionPolicy Bypass -File fix-env.ps1
# Then open a NEW terminal (or sign out / in) so the permanent values load.

$ErrorActionPreference = 'Continue'

$u = $env:USERPROFILE
if ([string]::IsNullOrEmpty($u)) { $u = 'C:\Users\' + $env:USERNAME }

$map = [ordered]@{
    'APPDATA'              = "$u\AppData\Roaming"
    'LOCALAPPDATA'         = "$u\AppData\Local"
    'HOME'                 = $u
    'TEMP'                 = "$u\AppData\Local\Temp"
    'TMP'                  = "$u\AppData\Local\Temp"
    'PROGRAMDATA'          = 'C:\ProgramData'
    'ALLUSERSPROFILE'      = 'C:\ProgramData'
    'PUBLIC'               = 'C:\Users\Public'
    'SystemDrive'          = 'C:'
    'SystemRoot'           = 'C:\WINDOWS'
    'ProgramFiles'         = 'C:\Program Files'
    'CommonProgramFiles'   = 'C:\Program Files\Common Files'
}

Write-Host "Fixing missing Windows environment variables (user scope):" -ForegroundColor Cyan
foreach ($k in $map.Keys) {
    $cur = [Environment]::GetEnvironmentVariable($k, 'User')
    if ([string]::IsNullOrEmpty($cur)) {
        [Environment]::SetEnvironmentVariable($k, $map[$k], 'User')
        Write-Host ("  SET    {0,-20} = {1}" -f $k, $map[$k]) -ForegroundColor Green
    } else {
        Write-Host ("  keep   {0,-20} = {1}" -f $k, $cur)
    }
    # process scope too, so it works immediately in this session
    [Environment]::SetEnvironmentVariable($k, $map[$k], 'Process')
}

Write-Host ""
Write-Host "Verifying NuGet:" -ForegroundColor Cyan
& dotnet nuget locals all --list

Write-Host ""
Write-Host "Verifying build of ImeEval:" -ForegroundColor Cyan
Set-Location <repo root>
& dotnet build -v q --nologo

Write-Host ""
Write-Host "Done. Open a NEW terminal for the permanent values to apply." -ForegroundColor Yellow
