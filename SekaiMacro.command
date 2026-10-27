#!/bin/bash
# macOS: double-click to open the control panel in the browser. Closing this Terminal window stops it.
cd "$(dirname "$0")"
if [ -x ./SekaiMacro ]; then exec ./SekaiMacro; fi
# Source checkout: the build from `dotnet build -c Release -o bin/mac`.
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
exec ./bin/mac/SekaiMacro
