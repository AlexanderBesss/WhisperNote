param(
    [switch]$Kill       # force-close the running app before publishing
)

$ErrorActionPreference = 'Stop'

# On PowerShell 7.3+ (CI's shell: pwsh) a non-zero native exit code becomes a
# terminating error under Stop, which would skip the explicit $LASTEXITCODE
# checks below. Keep those checks authoritative; a no-op on Windows PowerShell.
$PSNativeCommandUseErrorActionPreference = $false

$projectPath = $PSScriptRoot
$publishDir  = Join-Path $projectPath "publish"
$stagingDir  = Join-Path $projectPath "obj\publish-staging"
$manifestFile = Join-Path $projectPath "obj\publish-manifest.txt"

# Single source of truth for the build settings. CI runs this same script, so
# changing the RID, configuration or publish properties here never requires a
# matching change in .github\workflows\build.yml.
$configuration     = 'Release'
$runtimeIdentifier = 'win-x64'

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

# Tests always run first and build the app for the same configuration/RID the
# publish below uses, so that build is incremental and nothing compiles twice.
# Running them before closing the app means a failing test never kills a
# working one.
Write-Host "Running tests ($configuration $runtimeIdentifier) ..." -ForegroundColor Cyan
dotnet test (Join-Path $projectPath "tests\WhisperNote.Tests.csproj") `
    -c $configuration `
    -r $runtimeIdentifier `
    --verbosity minimal
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: tests failed (exit $LASTEXITCODE)." -ForegroundColor Red
    exit 1
}

Stop-RunningApp

# A force-killed app can take several seconds to disappear while its audio and
# server threads unwind, and Stop-Process returns before the handles are
# released. Poll before declaring failure so the check does not race the
# dying process.
$deadline = (Get-Date).AddSeconds(15)
while ((Get-Process -Name "WhisperNote" -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
}

if (Get-Process -Name "WhisperNote" -ErrorAction SilentlyContinue) {
    Write-Host "ERROR: WhisperNote is still running and could not be closed." -ForegroundColor Red
    exit 1
}

# Publish into a private staging folder. The SDK only ever cleans the folder it
# publishes into, so publish\ keeps its payload, models, settings and logs.
if (Test-Path -LiteralPath $stagingDir) {
    Remove-Item -LiteralPath $stagingDir -Recurse -Force
}

Write-Host "Publishing $configuration to staging ..." -ForegroundColor Cyan
dotnet publish "$projectPath\WhisperNote.csproj" `
    -c $configuration `
    -r $runtimeIdentifier `
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
