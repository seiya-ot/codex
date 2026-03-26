[CmdletBinding()]
param(
    [string]$Url = "http://127.0.0.1:5205",
    [switch]$RefreshManuals,
    [switch]$NoBuild
)

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir
$currentUser = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name

if ($currentUser -match "CodexSandboxOffline$") {
    throw "このスクリプトは seiya-ot の対話セッションで実行してください。CodexSandboxOffline で起動すると社内テナントへ接続できません。"
}

Set-Location $repoRoot

$env:DOTNET_CLI_HOME = Join-Path $repoRoot ".dotnet-home"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:NUGET_PACKAGES = Join-Path $repoRoot ".nuget/packages"
$env:APPDATA = Join-Path $repoRoot ".appdata"

$buildOutput = Join-Path $repoRoot ".user-session-build"
$dotnet = (Get-Command dotnet -CommandType Application).Source

Write-Host "User session :" $currentUser
Write-Host "Repository   :" $repoRoot
Write-Host "Build output :" $buildOutput

if (-not $NoBuild) {
    Write-Host "Building application for user session..."
    & $dotnet build --configfile NuGet.Config -o $buildOutput /p:UseAppHost=false
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed."
    }
}

$appDll = Join-Path $buildOutput "codex.dll"
if (-not (Test-Path $appDll)) {
    throw "Build output not found: $appDll"
}

if ($RefreshManuals) {
    Write-Host "Refreshing manual catalog in user session..."
    & $dotnet $appDll --refresh-manuals
    exit $LASTEXITCODE
}

Write-Host "Starting workbench on $Url"
Write-Host "Open this URL in the same user session to keep network settings consistent."
& $dotnet $appDll --urls $Url
