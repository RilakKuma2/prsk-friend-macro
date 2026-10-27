#!/bin/bash
# Builds the macOS release: SekaiMacro.app (Apple silicon) on a disk image dist/SekaiMacro-mac-arm64.dmg next to an
# Applications link. The app's window is mac/App.swift; inside it, Resources/app holds the self-contained panel with the
# flow data and the compiled OCR tool. The version comes from <Version> in SekaiMacro.csproj. Needs the .NET 10 SDK
# and the Xcode command line tools.
set -euo pipefail
cd "$(dirname "$0")"
dotnet=$(command -v dotnet || echo "$HOME/.dotnet/dotnet")
version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' SekaiMacro.csproj)
target=arm64-apple-macos13.0
dist=dist; app=$dist/SekaiMacro.app; data=$app/Contents/Resources/app
rm -rf "$dist"
"$dotnet" publish SekaiMacro.csproj -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o "$data"
cp flow.json handlers.json plan.json stages.json README.md "$data/"
cp -R templates "$data/"
xcrun swiftc -O -target "$target" -o "$data/sekai-ocr" mac/ocr.swift
mkdir -p "$app/Contents/MacOS"
xcrun swiftc -O -target "$target" -o "$app/Contents/MacOS/SekaiMacro" mac/App.swift
cp mac/AppIcon.icns "$app/Contents/Resources/"
sed "s/@VERSION@/$version/g" mac/Info.plist > "$app/Contents/Info.plist"
codesign --force --deep --sign - "$app"
staging=$dist/dmg
mkdir -p "$staging"
cp -R "$app" "$staging/"
ln -s /Applications "$staging/Applications"
hdiutil create -volname "셐 매크로" -srcfolder "$staging" -ov -format UDZO "$dist/SekaiMacro-mac-arm64.dmg" >/dev/null
rm -rf "$staging"
ls -l "$dist/SekaiMacro-mac-arm64.dmg"
