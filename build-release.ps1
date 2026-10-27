# Builds the release: a self-contained SekaiMacro.exe with the flow data next to it, zipped as dist\SekaiMacro-win-x64.zip.
# The version comes from <Version> in SekaiMacro.csproj; tag the GitHub release v<Version> so installed copies update to it.
$ErrorActionPreference='Stop'
Set-Location -LiteralPath $PSScriptRoot
$dist=Join-Path $PSScriptRoot 'dist'
$out=Join-Path $dist 'SekaiMacro'
if(Test-Path $dist) { Remove-Item $dist -Recurse -Force }
# Native libraries (WebView2Loader.dll) stay beside the exe, where WebView2 looks for them.
dotnet publish SekaiMacro.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o $out
if($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
# Package docs, a duplicate loader under runtimes\ and the IIS module are not used.
Remove-Item (Join-Path $out '*.xml'),(Join-Path $out 'runtimes'),(Join-Path $out 'aspnetcorev2_inprocess.dll') -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item flow.json,handlers.json,plan.json,stages.json,web-ui.ps1,README.md -Destination $out
Copy-Item templates -Destination (Join-Path $out 'templates') -Recurse
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($out,(Join-Path $dist 'SekaiMacro-win-x64.zip'),[IO.Compression.CompressionLevel]::Optimal,$true)
Get-Item (Join-Path $dist 'SekaiMacro-win-x64.zip') | Select-Object Name,Length
