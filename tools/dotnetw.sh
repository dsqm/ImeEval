#!/usr/bin/env bash
# Wrapper for shells missing core Windows env vars (APPDATA/PROGRAMDATA/...):
# NuGet then crashes with "Value cannot be null. (Parameter 'path1')".
# Fill in only the missing ones from the current user, then exec dotnet.
# Usage: ./tools/dotnetw.sh build -v q

if [ -z "${USERPROFILE:-}" ]; then
    USERPROFILE=$(cygpath -w "${HOME:-/c/Users/$USERNAME}" 2>/dev/null || printf 'C:%s' "\\Users\\$USERNAME")
fi
: "${SystemDrive:=C:}"
: "${SystemRoot:=$SystemDrive\\WINDOWS}"
: "${APPDATA:=$USERPROFILE\\AppData\\Roaming}"
: "${LOCALAPPDATA:=$USERPROFILE\\AppData\\Local}"
: "${PROGRAMDATA:=$SystemDrive\\ProgramData}"
: "${ALLUSERSPROFILE:=$PROGRAMDATA}"
: "${ProgramFiles:=$SystemDrive\\Program Files}"
: "${CommonProgramFiles:=$ProgramFiles\\Common Files}"
: "${PUBLIC:=$SystemDrive\\Users\\Public}"

export USERPROFILE SystemDrive SystemRoot APPDATA LOCALAPPDATA PROGRAMDATA \
       ALLUSERSPROFILE ProgramFiles CommonProgramFiles PUBLIC
exec dotnet "$@"
