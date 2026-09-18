#!/usr/bin/env pwsh
# Windows counterpart of benchmarks/create-fixture.sh — same tree, same names.
#
#   benchmarks/create-fixture.ps1 [-Root <dir>]
#
# Default root: $env:USERPROFILE\sync-bench. Refuses to wipe a drive root,
# your profile, or %TEMP%.
[CmdletBinding()]
param([string]$Root = (Join-Path $env:USERPROFILE 'sync-bench'))

$ErrorActionPreference = 'Stop'

$full = [IO.Path]::GetFullPath($Root)
$rootOfDrive = [IO.Path]::GetPathRoot($full).TrimEnd('\')
$tempRoot = [IO.Path]::GetTempPath().TrimEnd('\')
if ($full.TrimEnd('\') -in @($rootOfDrive, $env:USERPROFILE, $tempRoot)) {
    Write-Error "Refusing unsafe fixture root: $full"
    exit 1
}

foreach ($d in 'src', 'dst', 'state', 'archive') {
    $p = Join-Path $full $d
    if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force }
}
Remove-Item -LiteralPath (Join-Path $full 'tictack.log') -Force -ErrorAction SilentlyContinue
foreach ($d in 'src\small', 'src\medium', 'src\large', 'dst') {
    New-Item -ItemType Directory -Force -Path (Join-Path $full $d) | Out-Null
}

function Write-ZeroFile([string]$Path, [int]$Megabytes) {
    $buf = New-Object byte[] (1MB)
    $fs = [IO.File]::Create($Path)
    try { for ($c = 0; $c -lt $Megabytes; $c++) { $fs.Write($buf, 0, $buf.Length) } }
    finally { $fs.Dispose() }
}

for ($i = 1; $i -le 9000; $i++) {
    [IO.File]::WriteAllText((Join-Path $full ('src\small\{0:D5}.txt' -f $i)), "TicTack benchmark file $i`n")
}
for ($i = 1; $i -le 1000; $i++) {
    Write-ZeroFile (Join-Path $full ('src\medium\{0:D4}.bin' -f $i)) 1
}
for ($i = 1; $i -le 10; $i++) {
    Write-ZeroFile (Join-Path $full ('src\large\{0:D2}.bin' -f $i)) 100
}

$src = Join-Path $full 'src'
$files = (Get-ChildItem -LiteralPath $src -Recurse -File).Count
$bytes = (Get-ChildItem -LiteralPath $src -Recurse -File | Measure-Object -Property Length -Sum).Sum
Write-Host "Fixture ready: $src"
Write-Host "Files: $files"
Write-Host "Bytes: $bytes"
