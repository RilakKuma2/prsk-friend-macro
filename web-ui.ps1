# Runs the web UI without the app window. Open to the local network by default (no authentication); this PC only: -Local
param([int]$Port=8787,[switch]$Local)
Set-Location -LiteralPath $PSScriptRoot
$exe=@((Join-Path $PSScriptRoot 'SekaiMacro.exe'),(Join-Path $PSScriptRoot 'bin\gui\SekaiMacro.exe')) | Where-Object { Test-Path $_ } | Select-Object -First 1
if(-not $exe) { throw 'SekaiMacro.exe not found' }
$listen=if($Local){'127.0.0.1'}else{'0.0.0.0'}
Start-Process -FilePath $exe -ArgumentList 'web','--listen',$listen,'--port',$Port -WorkingDirectory $PSScriptRoot -NoNewWindow -Wait
