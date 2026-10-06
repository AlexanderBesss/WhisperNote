param(
    [switch]$Kill            # force-close the running app before publishing
)

$ErrorActionPreference = 'Stop'

$projectPath = $PSScriptRoot
$publishDir  = Join-Path $projectPath "publish"
$stagingDir  = Join-Path $projectPath "obj\publish-staging"
$manifestFile = Join-Path $projectPath "obj\publish-manifest.txt"

# The build never downloads or copies any runtime payload. The llama.cpp
# backends and the model files are fetched on first use by the app itself
# (BackendDownloader / ModelDownloader), into publish\, according to the
# hardware of the machine. The merge below only ever touches files a previous
# build produced, so those payload folders survive every rebuild.

function Stop-RunningApp {
    $processes = @(Get-Process -Name "WhisperNote", "llama-server" -ErrorAction SilentlyContinue)
    if ($processes.Count -eq 0) {
        return
    }

    if ($Kill) {
        Write-Host "Force-closing WhisperNote and its llama-server ..." -ForegroundColor Yellow
        $processes | Stop-Process -Force -ErrorAction SilentlyContinue
        return
    }

    Write-Host "Closing running WhisperNote ..." -ForegroundColor Yellow
    foreach ($process in $processes) {
        if ($process.MainWindowHandle -ne 0) {
            [void]$process.CloseMainWindow()
        }
    }

    Start-Sleep -Seconds 3

    $remaining = @(Get-Process -Name "WhisperNote", "llama-server" -ErrorAction SilentlyContinue)
    if ($remaining.Count -gt 0) {
        Write-Host "Processes did not close cleanly; force-closing ..." -ForegroundColor Yellow
        $remaining | Stop-Process -Force -ErrorAction SilentlyContinue
    }
}

function Copy-IfChanged {
    param([string]$Source, [string]$Dest)

    if (Test-Path -LiteralPath $Dest) {
        $src = Get-Item -LiteralPath $Source
        $dst = Get-Item -LiteralPath $Dest
        if ($src.LastWriteTime -le $dst.LastWriteTime) {
            return $false
        }
    }

    $dir = Split-Path $Dest -Parent
    if (-not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    Copy-Item -LiteralPath $Source -Destination $Dest -Force
    return $true
}

Stop-RunningApp

if (Get-Process -Name "WhisperNote" -ErrorAction SilentlyContinue) {
    Write-Host "ERROR: WhisperNote is still running and could not be closed." -ForegroundColor Red
    exit 1
}

# Publish into a private staging folder. The SDK only ever cleans the folder it
# publishes into, so publish\ keeps its payload, models, settings and logs.
if (Test-Path -LiteralPath $stagingDir) {
    Remove-Item -LiteralPath $stagingDir -Recurse -Force
}

Write-Host "Publishing Release to staging ..." -ForegroundColor Cyan
dotnet publish "$projectPath\WhisperNote.csproj" `
    -c Release `
    -r win-x64 `
    -o $stagingDir `
    /p:PublishSingleFile=true `
    /p:SelfContained=false `
    /p:ExcludeFromSingleFile=true

if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: dotnet publish failed (exit $LASTEXITCODE)." -ForegroundColor Red
    exit 1
}

if (-not (Test-Path -LiteralPath $publishDir)) {
    New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
}

# Merge the freshly built app files, then delete only the app files a previous
# build produced that this build no longer produces.
$prior = @()
if (Test-Path -LiteralPath $manifestFile) {
    $prior = @(Get-Content -LiteralPath $manifestFile | Where-Object { $_.Trim() -ne '' })
}

$current = @()
foreach ($file in Get-ChildItem -LiteralPath $stagingDir -Recurse -File) {
    $relative = $file.FullName.Substring($stagingDir.Length).Trim('\')
    $current += $relative
    Copy-IfChanged -Source $file.FullName -Dest (Join-Path $publishDir $relative) | Out-Null
}

$keep = @{}
foreach ($relative in $current) {
    $keep[$relative.ToLowerInvariant()] = $true
}

$deleted = 0
foreach ($relative in $prior) {
    if ($keep.ContainsKey($relative.Trim().ToLowerInvariant())) {
        continue
    }
    $stalePath = Join-Path $publishDir $relative.Trim()
    if (Test-Path -LiteralPath $stalePath) {
        Remove-Item -LiteralPath $stalePath -Force
        $deleted++
    }
}

Set-Content -LiteralPath $manifestFile -Value $current -Encoding UTF8

Write-Host "Done. $deleted stale build file(s) removed; payload, models, settings and logs kept." -ForegroundColor Green
Write-Host "Run: $publishDir\WhisperNote.exe" -ForegroundColor Green
