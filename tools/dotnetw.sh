#!/usr/bin/env bash
# Wrapper: this shell is missing core Windows env vars (APPDATA/PROGRAMDATA/...),
# which makes NuGet crash with "Value cannot be null. (Parameter 'path1')".
# Supply them and exec dotnet. Usage: ./tools/dotnetw.sh build -v q
export APPDATA='C:\Users\<user>\AppData\Roaming'
export LOCALAPPDATA='C:\Users\<user>\AppData\Local'
export PROGRAMDATA='C:\ProgramData'
export ALLUSERSPROFILE='C:\ProgramData'
export HOME='C:\Users\<user>'
export SystemRoot='C:\WINDOWS'
export SystemDrive='C:'
export USERNAME='<user>'
export USERDOMAIN='<computer>'
export CommonProgramFiles='C:\Program Files\Common Files'
export ProgramFiles='C:\Program Files'
export PUBLIC='C:\Users\Public'
exec dotnet "$@"
