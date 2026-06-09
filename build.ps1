param([switch]$Kill)

$projectPath = $PSScriptRoot
$publishDir   = Join-Path $projectPath "publish"

if ($Kill) {
    Stop-Process -Name "WhisperNote" -Force -ErrorAction SilentlyContinue
}

if (Get-Process -Name "WhisperNote" -ErrorAction SilentlyContinue) {
    Write-Host "ERROR: WhisperNote is still running. Close it or use -Kill." -ForegroundColor Red
    exit 1
}

Write-Host "Publishing Release to $publishDir ..." -ForegroundColor Cyan
dotnet publish "$projectPath\WhisperNote.csproj" `
    -c Release `
    -r win-x64 `
    -o $publishDir `
    /p:PublishSingleFile=true `
    /p:SelfContained=false `
    /p:ExcludeFromSingleFile=true

if ($LASTEXITCODE -eq 0) {
    Write-Host "Done. Run: $publishDir\WhisperNote.exe" -ForegroundColor Green
}
