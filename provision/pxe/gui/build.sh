#!/bin/sh
# Bygger WinSetupGui.exe (Windows x64) med mingw-w64 i en midlertidig Alpine-container.
# Brug (på en maskine med Docker, fx Unraid):  sh <WinSetup-mappen>/PXE/gui/build.sh
set -e
DIR="$(cd "$(dirname "$0")" && pwd)"
docker run --rm -v "$DIR":/src -w /src alpine:latest sh -c '
  apk add -q --no-cache mingw-w64-gcc >/dev/null
  x86_64-w64-mingw32-gcc -std=c11 -O2 -s -municode -mwindows -finput-charset=UTF-8 \
    -Wall -Wno-unused-function -Wno-missing-braces \
    -o WinSetupGui.exe WinSetupGui.c \
    -static -static-libgcc -lgdiplus -lmpr -liphlpapi -lws2_32 -lgdi32 -luser32 -lshell32 -lkernel32
  ls -la WinSetupGui.exe
'
