param(
    [string]$GeoDataDir = (Join-Path (Join-Path ([Environment]::GetFolderPath("UserProfile")) "ClashWinUI") "Geodata"),
    [switch]$ForceRefresh
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$ProgressPreference = "SilentlyContinue"
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[Console]::OutputEncoding = $utf8NoBom
$OutputEncoding = $utf8NoBom

function Write-Log {
    param([string]$Message)
    [Console]::Out.WriteLine("[GeoData] $Message")
}

function Get-PositiveInt64OrNull {
    param($Value)

    if ($null -eq $Value) {
        return $null
    }

    try {
        [Int64]$convertedValue = [Convert]::ToInt64($Value)
        if ($convertedValue -gt 0) {
            return $convertedValue
        }
    }
    catch {
    }

    return $null
}

function Write-DownloadProgress {
    param(
        [Parameter(Mandatory = $true)][string]$Stage,
        [Parameter(Mandatory = $true)][string]$FileName,
        [Parameter(Mandatory = $true)][Int64]$BytesReceived,
        $TotalBytes = $null
    )

    $totalBytesValue = Get-PositiveInt64OrNull -Value $TotalBytes
    $isIndeterminate = $null -eq $totalBytesValue
    $percentage = 0
    if (-not $isIndeterminate) {
        $percentage = [Math]::Min(100, [Math]::Round(($BytesReceived * 100.0) / $totalBytesValue, 2))
    }

    $payload = [ordered]@{
        stage = $Stage
        fileName = $FileName
        bytesReceived = $BytesReceived
        totalBytes = $totalBytesValue
        percentage = $percentage
        isIndeterminate = $isIndeterminate
    }

    [Console]::Out.WriteLine("CWUI_PROGRESS " + ($payload | ConvertTo-Json -Compress))
}

function Invoke-DownloadWithProgress {
    param(
        [Parameter(Mandatory = $true)][string]$Uri,
        [Parameter(Mandatory = $true)][string]$OutputPath,
        [Parameter(Mandatory = $true)][hashtable]$Headers,
        [Parameter(Mandatory = $true)][string]$Stage,
        [Parameter(Mandatory = $true)][string]$FileName,
        [int]$TimeoutSec = 180
    )

    $request = [System.Net.HttpWebRequest]::CreateHttp($Uri)
    $request.Method = "GET"
    $request.AllowAutoRedirect = $true
    $request.Timeout = $TimeoutSec * 1000
    $request.ReadWriteTimeout = $TimeoutSec * 1000
    foreach ($key in $Headers.Keys) {
        if ($key -ieq "User-Agent") {
            $request.UserAgent = [string]$Headers[$key]
        }
        else {
            $request.Headers[$key] = [string]$Headers[$key]
        }
    }

    $response = $null
    $source = $null
    $target = $null
    try {
        $response = $request.GetResponse()
        $totalBytes = Get-PositiveInt64OrNull -Value $response.ContentLength
        $source = $response.GetResponseStream()
        $target = [System.IO.File]::Create($OutputPath)
        $buffer = New-Object byte[] 81920
        [Int64]$bytesReceived = 0
        $lastReportedPercentage = -1
        $lastReportTime = [DateTime]::UtcNow.AddSeconds(-1)

        Write-DownloadProgress -Stage $Stage -FileName $FileName -BytesReceived 0 -TotalBytes $totalBytes

        while (($read = $source.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $target.Write($buffer, 0, $read)
            $bytesReceived += $read

            $percentage = if ($null -ne $totalBytes) {
                [int][Math]::Floor(($bytesReceived * 100.0) / $totalBytes)
            }
            else {
                -1
            }

            $now = [DateTime]::UtcNow
            if ($percentage -ne $lastReportedPercentage -or ($now - $lastReportTime).TotalMilliseconds -ge 500) {
                $lastReportedPercentage = $percentage
                $lastReportTime = $now
                Write-DownloadProgress -Stage $Stage -FileName $FileName -BytesReceived $bytesReceived -TotalBytes $totalBytes
            }
        }

        Write-DownloadProgress -Stage $Stage -FileName $FileName -BytesReceived $bytesReceived -TotalBytes $totalBytes
    }
    finally {
        if ($target) { $target.Dispose() }
        if ($source) { $source.Dispose() }
        if ($response) { $response.Dispose() }
    }
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
                Invoke-DownloadWithProgress -Uri $candidate -OutputPath $OutputPath -Headers $Headers -Stage "geodata" -FileName $AssetName -TimeoutSec 180

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
