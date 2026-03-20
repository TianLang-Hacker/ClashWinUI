param(
    [string]$GeoDataDir = (Join-Path ([Environment]::GetFolderPath("UserProfile")) ".config\\mihomo"),
    [switch]$ForceRefresh
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$ProgressPreference = "SilentlyContinue"

function Write-Log {
    param([string]$Message)
    Write-Output "[GeoData] $Message"
}

$assetUrls = [ordered]@{
    "geoip.metadb" = "https://github.com/MetaCubeX/meta-rules-dat/releases/download/latest/geoip.metadb"
    "geoip.dat"    = "https://github.com/MetaCubeX/meta-rules-dat/releases/download/latest/geoip.dat"
    "geosite.dat"  = "https://github.com/MetaCubeX/meta-rules-dat/releases/download/latest/geosite.dat"
}

$providers = @(
    @{ Name = "gh-proxy"; Prefix = "https://gh-proxy.com/" },
    @{ Name = "gh-llkk"; Prefix = "https://gh.llkk.cc/" },
    @{ Name = "direct"; Prefix = "" }
)

function Download-Asset {
    param(
        [Parameter(Mandatory = $true)][string]$AssetName,
        [Parameter(Mandatory = $true)][string]$AssetUrl,
        [Parameter(Mandatory = $true)][string]$OutputPath,
        [Parameter(Mandatory = $true)][hashtable]$Headers
    )

    $lastError = ""
    $attemptsPerProvider = 2

    foreach ($provider in $providers) {
        for ($attempt = 1; $attempt -le $attemptsPerProvider; $attempt++) {
            try {
                $candidate = if ([string]::IsNullOrWhiteSpace($provider.Prefix)) {
                    $AssetUrl
                }
                else {
                    "$($provider.Prefix)$AssetUrl"
                }

                Write-Log "Downloading $AssetName (provider: $($provider.Name), attempt: $attempt/$attemptsPerProvider): $candidate"
                Invoke-WebRequest -Uri $candidate -OutFile $OutputPath -Headers $Headers -TimeoutSec 180

                if (-not (Test-Path $OutputPath) -or ((Get-Item $OutputPath).Length -le 0)) {
                    throw "Downloaded file is missing or empty: $AssetName"
                }

                return $candidate
            }
            catch {
                $lastError = $_.Exception.Message
                Write-Log "Download failed for $AssetName (provider: $($provider.Name), attempt: $attempt/$attemptsPerProvider): $lastError"
            }
        }
    }

    throw "All download candidates failed for $AssetName. Last error: $lastError"
}

if (-not (Test-Path $GeoDataDir)) {
    New-Item -ItemType Directory -Path $GeoDataDir -Force | Out-Null
}

if (-not $ForceRefresh.IsPresent) {
    $allReady = $true
    foreach ($assetName in $assetUrls.Keys) {
        $targetPath = Join-Path $GeoDataDir $assetName
        if (-not (Test-Path $targetPath) -or ((Get-Item $targetPath).Length -le 0)) {
            $allReady = $false
            break
        }
    }

    if ($allReady) {
        Write-Log "GeoData already ready. Skip download."
        exit 0
    }
}

$headers = @{ "User-Agent" = "ClashWinUI-GeoDataDownloader" }
$tempRoot = Join-Path $env:TEMP ("ClashWinUI-GeoData-" + [guid]::NewGuid().ToString("N"))

try {
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

    foreach ($assetName in $assetUrls.Keys) {
        $url = $assetUrls[$assetName]
        $tempPath = Join-Path $tempRoot $assetName
        $downloadedFrom = Download-Asset -AssetName $assetName -AssetUrl $url -OutputPath $tempPath -Headers $headers
        Write-Log "Downloaded $assetName successfully from $downloadedFrom"
    }

    foreach ($assetName in $assetUrls.Keys) {
        $tempPath = Join-Path $tempRoot $assetName
        $targetPath = Join-Path $GeoDataDir $assetName
        Move-Item -Path $tempPath -Destination $targetPath -Force
        Write-Log "Updated $assetName -> $targetPath"
    }

    Write-Log "GeoData update completed."
    exit 0
}
catch {
    Write-Log "GeoData update failed: $($_.Exception.Message)"
    exit 1
}
finally {
    if (Test-Path $tempRoot) {
        Remove-Item -Path $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
