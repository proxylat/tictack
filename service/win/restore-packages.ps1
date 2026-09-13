Write-Host "Restoring NuGet packages..."
dotnet restore "$PSScriptRoot\..\src\TicTack.csproj"
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: dotnet restore failed"
    exit 1
}
Write-Host "Packages restored."
