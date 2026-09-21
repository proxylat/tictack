#!/usr/bin/env pwsh
# Root shim: the diagnostics live in benchmarks\. Forwards everything, keeps the exit code.
& "$PSScriptRoot\benchmarks\diag.ps1" @args
exit $LASTEXITCODE
