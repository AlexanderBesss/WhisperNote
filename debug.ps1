Write-Host "Starting with hot reload ..." -ForegroundColor Cyan
dotnet watch --project "$PSScriptRoot\WhisperNote.csproj" --hot-reload
