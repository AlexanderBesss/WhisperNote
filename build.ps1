param(
    [switch]$Kill,           # force-close the running app before publishing
    [switch]$NoUpdate,       # never download: fail if a required backend is missing
    [switch]$UpdateBackends, # also fetch the CUDA 12.4, Vulkan and NPU backends when missing
    [switch]$ForceUpdate,    # run the update scripts even when binaries are already present
    [switch]$RefreshModels   # overwrite publish\models files from the source models folder
)

$ErrorActionPreference = 'Stop'

$projectPath = $PSScriptRoot
$repoRoot    = Split-Path $projectPath -Parent
$publishDir  = Join-Path $projectPath "publish"
$stagingDir  = Join-Path $projectPath "obj\publish-staging"
$manifestFile = Join-Path $projectPath "obj\publish-manifest.txt"
$llamaRoot   = Join-Path $repoRoot "llm-servers\llama\windows"
$modelsRoot  = Join-Path $repoRoot "models"

# llama.cpp backends: mirrored into publish\ so a rebuild removes exactly the
# files the current build no longer produces.
$backends = @(
    [pscustomobject]@{
        Name     = 'CUDA (NVIDIA)'
        Required = $true
        Source   = Join-Path $llamaRoot 'llama'
        Target   = 'llama'
        Marker   = 'llama-server.exe'
        Updater  = Join-Path $llamaRoot 'update-llama.ps1'
    },
    [pscustomobject]@{
        Name     = 'CUDA 12.4 (legacy NVIDIA: Maxwell, Pascal, Volta)'
        Required = $false
        Source   = Join-Path $llamaRoot 'cuda12'
        Target   = 'cuda12'
        Marker   = 'llama-server.exe'
        Updater  = Join-Path $llamaRoot 'cuda12\update-cuda12.ps1'
    },
    [pscustomobject]@{
        Name     = 'Vulkan (NVIDIA Pascal, AMD, Intel iGPU)'
        Required = $false
        Source   = Join-Path $llamaRoot 'vulkan'
        Target   = 'vulkan'
        Marker   = 'llama-server.exe'
        Updater  = Join-Path $llamaRoot 'vulkan\update-vulkan.ps1'
    },
    [pscustomobject]@{
        Name     = 'OpenVINO (Intel NPU)'
        Required = $false
        Source   = Join-Path $llamaRoot 'NPU\llama-ov'
        Target   = 'NPU\llama-ov'
        Marker   = 'llama-server.exe'
        Updater  = Join-Path $llamaRoot 'NPU\update-npu.ps1'
    }
)

# Model files: copied only when absent. Never deleted, never overwritten
# (unless -RefreshModels), because each file is 0.3-2 GB.
$modelFiles = @(
    [pscustomobject]@{ Source = Join-Path $modelsRoot 'unsloth\gemma-4-E2B-it-GGUF\gemma-4-E2B-it-UD-Q4_K_XL.gguf';  Target = 'models\gemma-4-E2B-it-UD-Q4_K_XL.gguf' }
    [pscustomobject]@{ Source = Join-Path $modelsRoot 'lmstudio-community\gemma-4-E2B-it-GGUF\mmproj-gemma-4-E2B-it-BF16.gguf'; Target = 'models\mmproj-BF16.gguf' }
    [pscustomobject]@{ Source = Join-Path $modelsRoot 'unslothai\Qwen3-ASR-1.7B-GGUF\Qwen3-ASR-1.7B-Q8_0.gguf'; Target = 'models\Qwen3-ASR-1.7B-Q8_0.gguf' }
    [pscustomobject]@{ Source = Join-Path $modelsRoot 'unslothai\Qwen3-ASR-1.7B-GGUF\mmproj-Qwen3-ASR-1.7B-Q8_0.gguf'; Target = 'models\mmproj-Qwen3-ASR-1.7B-Q8_0.gguf' }
)

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

function Invoke-BackendUpdater {
    param($Backend)

    if ($NoUpdate) {
        return $false
    }
    if (-not (Test-Path -LiteralPath $Backend.Updater)) {
        Write-Host "  No update script at $($Backend.Updater)." -ForegroundColor Red
        return $false
    }

    Write-Host "  Downloading $($Backend.Name) from the latest llama.cpp preview ..." -ForegroundColor Cyan
    & $Backend.Updater -NonInteractive
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  Update script for $($Backend.Name) failed (exit $LASTEXITCODE)." -ForegroundColor Red
        return $false
    }
    return $true
}

function Ensure-Backends {
    foreach ($backend in $backends) {
        $marker = Join-Path $backend.Source $backend.Marker
        $present = Test-Path -LiteralPath $marker

        if ($present -and -not $ForceUpdate) {
            continue
        }

        if (-not $backend.Required -and -not $UpdateBackends) {
            if (-not $present) {
                Write-Host "  $($backend.Name): not installed (optional). Run $($backend.Updater) to enable it." -ForegroundColor Yellow
            }
            continue
        }

        if (-not (Invoke-BackendUpdater $backend)) {
            if ($backend.Required) {
                Write-Host "ERROR: $($backend.Name) llama.cpp binaries are required and could not be obtained." -ForegroundColor Red
                Write-Host "       Run: $($backend.Updater)" -ForegroundColor Yellow
                exit 1
            }
            continue
        }

        if (-not (Test-Path -LiteralPath (Join-Path $backend.Source $backend.Marker))) {
            if ($backend.Required) {
                Write-Host "ERROR: update finished but $($backend.Source) still has no $($backend.Marker)." -ForegroundColor Red
                exit 1
            }
            Write-Host "  $($backend.Name) is still not installed; continuing without it." -ForegroundColor Yellow
        }
    }

    foreach ($backend in $backends) {
        if (-not (Test-Path -LiteralPath (Join-Path $backend.Source $backend.Marker))) {
            continue
        }
        $version = (Get-ChildItem -LiteralPath $backend.Source -Filter 'VERSION-*' -File |
                    Sort-Object LastWriteTime -Descending |
                    Select-Object -First 1)
        $label = if ($version) { $version.Name.Substring('VERSION-'.Length) } else { 'version unknown' }
        Write-Host "  $($backend.Name): $label" -ForegroundColor Gray
    }
}

function Ensure-Models {
    $missing = @()
    foreach ($model in $modelFiles) {
        if (-not (Test-Path -LiteralPath $model.Source)) {
            $missing += $model.Target
        }
    }

    if ($missing.Count -eq 0) {
        return
    }

    Write-Host "  Model files not in $modelsRoot (the app downloads them on first use):" -ForegroundColor Yellow
    foreach ($name in $missing) {
        Write-Host "    $name" -ForegroundColor Yellow
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

function Sync-BackendDirectory {
    param([string]$Source, [string]$TargetDir)

    $sourceRoot = $Source.TrimEnd('\')
    # A backend that is not installed has no binaries to mirror: leave publish\
    # untouched rather than creating an empty folder for it.
    if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot 'llama-server.exe'))) {
        return
    }

    if (-not (Test-Path -LiteralPath $TargetDir)) {
        New-Item -ItemType Directory -Path $TargetDir -Force | Out-Null
    }

    $updated = 0
    foreach ($file in Get-ChildItem -LiteralPath $sourceRoot -Recurse -File) {
        # Update scripts and their logs stay in the source tree, not in publish\.
        if ($file.Extension -in '.ps1', '.log') {
            continue
        }
        $relative = $file.FullName.Substring($sourceRoot.Length).Trim('\')
        if (Copy-IfChanged -Source $file.FullName -Dest (Join-Path $TargetDir $relative)) {
            $updated++
        }
    }

    # Remove published binaries the current backend folder no longer provides.
    $removed = 0
    foreach ($file in Get-ChildItem -LiteralPath $TargetDir -Recurse -File) {
        if ($file.Extension -in '.ps1', '.log') {
            continue
        }
        $relative = $file.FullName.Substring($TargetDir.Length).Trim('\')
        if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot $relative))) {
            Remove-Item -LiteralPath $file.FullName -Force
            $removed++
        }
    }

    $name = Split-Path $TargetDir -Leaf
    Write-Host "  $name : $updated updated, $removed removed" -ForegroundColor Gray
}

function Sync-ModelFiles {
    $copied = 0
    foreach ($model in $modelFiles) {
        if (-not (Test-Path -LiteralPath $model.Source)) {
            continue
        }
        $dest = Join-Path $publishDir $model.Target
        if ((Test-Path -LiteralPath $dest) -and -not $RefreshModels) {
            continue
        }
        $dir = Split-Path $dest -Parent
        if (-not (Test-Path -LiteralPath $dir)) {
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
        }
        Write-Host "  Copying $((Split-Path $model.Target -Leaf)) ..." -ForegroundColor Gray
        Copy-Item -LiteralPath $model.Source -Destination $dest -Force
        $copied++
    }
    if ($copied -gt 0) {
        Write-Host "  $copied model file(s) copied into publish\models." -ForegroundColor Gray
    }
}

Stop-RunningApp

if (Get-Process -Name "WhisperNote" -ErrorAction SilentlyContinue) {
    Write-Host "ERROR: WhisperNote is still running and could not be closed." -ForegroundColor Red
    exit 1
}

Write-Host "Checking runtime payload ..." -ForegroundColor Cyan
Ensure-Backends
Ensure-Models

# Publish into a private staging folder. The SDK only ever cleans the folder it
# publishes into, so publish\ keeps its models, settings and logs.
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

Write-Host "Syncing runtime payload into publish ..." -ForegroundColor Cyan
foreach ($backend in $backends) {
    Sync-BackendDirectory -Source $backend.Source -TargetDir (Join-Path $publishDir $backend.Target)
}
Sync-ModelFiles

Set-Content -LiteralPath $manifestFile -Value $current -Encoding UTF8

Write-Host "Done. $deleted stale build file(s) removed; models, settings and logs kept." -ForegroundColor Green
Write-Host "Run: $publishDir\WhisperNote.exe" -ForegroundColor Green
