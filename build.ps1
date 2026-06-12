param([switch]$Kill)

$projectPath = $PSScriptRoot
$publishDir   = Join-Path $projectPath "publish"

function Stop-RunningApp {
    $processes = @(Get-Process -Name "WhisperNote" -ErrorAction SilentlyContinue)
    if ($processes.Count -eq 0) {
        return
    }

    if ($Kill) {
        Write-Host "Force-closing running WhisperNote..." -ForegroundColor Yellow
        $processes | Stop-Process -Force -ErrorAction SilentlyContinue
        return
    }

    Write-Host "Closing running WhisperNote..." -ForegroundColor Yellow
    foreach ($process in $processes) {
        if ($process.MainWindowHandle -ne 0) {
            [void]$process.CloseMainWindow()
        }
    }

    Start-Sleep -Seconds 3

    $remaining = @(Get-Process -Name "WhisperNote" -ErrorAction SilentlyContinue)
    if ($remaining.Count -gt 0) {
        Write-Host "WhisperNote did not close cleanly; force-closing..." -ForegroundColor Yellow
        $remaining | Stop-Process -Force -ErrorAction SilentlyContinue
    }
}

Stop-RunningApp

if (Get-Process -Name "WhisperNote" -ErrorAction SilentlyContinue) {
    Write-Host "ERROR: WhisperNote is still running and could not be closed." -ForegroundColor Red
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
