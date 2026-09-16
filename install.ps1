[CmdletBinding()]
param (
    [string]$Version = "latest",
    [string]$InstallDir = "$env:LOCALAPPDATA\Glacier\bin",
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"

Write-Host "======================================================================" -ForegroundColor Cyan
Write-Host "  GLACIER CLI: The World-Leading Pure C# .NET 10 AI Platform" -ForegroundColor Cyan
Write-Host "======================================================================" -ForegroundColor Cyan

if ($Uninstall) {
    Write-Host "`n[Glacier Installer] Uninstalling Glacier CLI..." -ForegroundColor Yellow
    if (Test-Path "$InstallDir\glacier.exe") {
        Remove-Item "$InstallDir\glacier.exe" -Force -ErrorAction SilentlyContinue
        Write-Host "  [OK] Removed: $InstallDir\glacier.exe" -ForegroundColor Green
    }
    
    $UserPath = [Environment]::GetEnvironmentVariable("Path", "User")
    if ($UserPath -like "*$InstallDir*") {
        $NewPath = ($UserPath -split ";" | Where-Object { $_ -ne $InstallDir -and $_ -ne "" }) -join ";"
        [Environment]::SetEnvironmentVariable("Path", $NewPath, "User")
        Write-Host "  [OK] Removed $InstallDir from User PATH." -ForegroundColor Green
    }
    Write-Host "`nGlacier CLI has been successfully uninstalled.`n" -ForegroundColor Cyan
    return
}

# 1. Create Target Directory
if (!(Test-Path $InstallDir)) {
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
}

$ExeTarget = Join-Path $InstallDir "glacier.exe"

# 2. Copy local build or download from GitHub release
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path -ErrorAction SilentlyContinue
$LocalDistExe = Join-Path $ScriptDir "dist\win-x64\glacier.exe"

if (Test-Path $LocalDistExe) {
    Write-Host "`n[1/3] Copying local standalone binary to $InstallDir..." -ForegroundColor Yellow
    Copy-Item $LocalDistExe -Destination $ExeTarget -Force
    Write-Host "  [OK] Standalone binary copied." -ForegroundColor Green
} else {
    Write-Host "`n[1/3] Downloading latest Glacier CLI from GitHub Releases..." -ForegroundColor Yellow
    $DownloadUrl = "https://github.com/ian-cowley/Glacier.Cli/releases/latest/download/glacier-win-x64.zip"
    $ZipPath = Join-Path $env:TEMP "glacier-win-x64.zip"

    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls13
        Invoke-WebRequest -Uri $DownloadUrl -OutFile $ZipPath -UseBasicParsing
        Write-Host "  [OK] Download complete. Extracting..." -ForegroundColor Green
        Expand-Archive -Path $ZipPath -DestinationPath $InstallDir -Force
        Remove-Item $ZipPath -Force -ErrorAction SilentlyContinue
    } catch {
        Write-Warning "Could not download from GitHub release ($($_.Exception.Message))."
        Write-Host "Fallback: Installing via .NET Global Tool..." -ForegroundColor Yellow
        dotnet tool install -g Glacier.Cli --ignore-failed-sources
        return
    }
}

# 3. Permanently Register into User PATH
Write-Host "`n[2/3] Configuring Environment Variables..." -ForegroundColor Yellow
$UserPath = [Environment]::GetEnvironmentVariable("Path", "User")
if ($UserPath -notlike "*$InstallDir*") {
    $NewPath = if ([string]::IsNullOrWhiteSpace($UserPath)) { $InstallDir } else { "$UserPath;$InstallDir" }
    [Environment]::SetEnvironmentVariable("Path", $NewPath, "User")
    $env:Path = "$env:Path;$InstallDir"
    Write-Host "  [OK] Added '$InstallDir' to User PATH." -ForegroundColor Green
} else {
    Write-Host "  [OK] PATH already includes '$InstallDir'." -ForegroundColor Green
}

# 4. Verification
Write-Host "`n[3/3] Verifying Glacier CLI Installation..." -ForegroundColor Yellow
if (Test-Path $ExeTarget) {
    Write-Host "`n======================================================================" -ForegroundColor Green
    Write-Host "  SUCCESS: Glacier CLI is installed and ready to use!" -ForegroundColor Green
    Write-Host "  Binary Location: $ExeTarget" -ForegroundColor Gray
    Write-Host "======================================================================" -ForegroundColor Green
    Write-Host "`nQuickstart Commands (open a new terminal window):" -ForegroundColor Cyan
    Write-Host "  glacier devices                                    # Audit NVIDIA/AMD/CPU accelerators"
    Write-Host "  glacier pull Qwen/Qwen2.5-7B-Instruct-GGUF         # Download foundation model"
    Write-Host "  glacier run model.gguf `"Explain relativity`"        # Instant GPU/CPU inference"
    Write-Host "  glacier tune model.gguf --data data.jsonl          # In-process 20-min fine-tuning"
    Write-Host "  glacier serve model.gguf --port 11434              # PagedAttention OpenAI server`n"
} else {
    Write-Error "Installation failed: $ExeTarget not found."
}
