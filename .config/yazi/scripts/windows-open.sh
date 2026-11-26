#!/bin/sh
set -eu

script=$(wslpath -w "$(dirname "$0")/windows-open.ps1")
mode=$1
shift

for path do
    file=$(wslpath -aw "$path")
    powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File "$script" "$mode" "$file"
done
