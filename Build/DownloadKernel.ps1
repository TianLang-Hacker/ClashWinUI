param(
    [string]$KernelDir = (Join-Path ([Environment]::GetFolderPath("UserProfile")) "ClashWinUI\Kernel"),
    [string]$FallbackTag = "v1.19.21"
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

function Write-Log {
    param([string]$Message)
    Write-Output $Message
}

function Get-ArchitectureToken {
    $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToUpperInvariant()
    switch ($arch) {
        "X64" { return "amd64" }
        "ARM64" { return "arm64" }
        default { throw "Unsupported architecture: $arch. Only x86_64 (AMD64) and ARM64 are supported." }
    }
}

function Normalize-Tag {
    param([Parameter(Mandatory = $true)][string]$Tag)

    if ($Tag.StartsWith("v", [StringComparison]::OrdinalIgnoreCase)) {
        return $Tag
    }

    return "v$Tag"
}

function Get-LatestRelease {
    $apiUrl = "https://api.github.com/repos/MetaCubeX/mihomo/releases/latest"
    $providers = @(
        @{ Name = "direct"; Prefix = "" },
        @{ Name = "edgeone"; Prefix = "https://edgeone.gh-proxy.com/" },
        @{ Name = "hk"; Prefix = "https://hk.gh-proxy.com/" },
        @{ Name = "gh-proxy"; Prefix = "https://gh-proxy.com/" },
        @{ Name = "gh-llk"; Prefix = "https://gh.llk.cc/" }
    )

    $headers = @{ "User-Agent" = "ClashWinUI-KernelDownloader" }
    $attemptsPerProvider = 2
    $lastError = ""

    foreach ($provider in $providers) {
        for ($attempt = 1; $attempt -le $attemptsPerProvider; $attempt++) {
            try {
                $url = if ([string]::IsNullOrWhiteSpace($provider.Prefix)) { $apiUrl } else { "$($provider.Prefix)$apiUrl" }
                Write-Log "Fetching latest release metadata (provider: $($provider.Name), attempt: $attempt/$attemptsPerProvider): $url"
                return Invoke-RestMethod -Uri $url -Headers $headers -TimeoutSec 30
            }
            catch {
                $lastError = $_.Exception.Message
                Write-Log "Metadata query failed (provider: $($provider.Name), attempt: $attempt/$attemptsPerProvider): $lastError"
            }
        }
    }

    throw "Unable to fetch latest release metadata from all providers. Last error: $lastError"
}

function Download-Asset {
    param(
        [Parameter(Mandatory = $true)] [string]$AssetUrl,
        [Parameter(Mandatory = $true)] [string]$OutputPath
    )

    $downloadCandidates = @(
        $AssetUrl,
        "https://edgeone.gh-proxy.com/$AssetUrl",
        "https://hk.gh-proxy.com/$AssetUrl",
        "https://gh-proxy.com/$AssetUrl",
        "https://gh.llk.cc/$AssetUrl"
    )

    $headers = @{ "User-Agent" = "ClashWinUI-KernelDownloader" }
    $attemptCount = 0
    $lastError = ""

    foreach ($candidate in $downloadCandidates) {
        $attemptCount++
        try {
            Write-Log "Downloading kernel asset: $candidate"
            Invoke-WebRequest -Uri $candidate -OutFile $OutputPath -Headers $headers -TimeoutSec 180
            return $candidate
        }
        catch {
            $lastError = $_.Exception.Message
            Write-Log "Download failed from $candidate : $lastError"
        }
    }

    throw "All download candidates failed. Tried $attemptCount urls. Last error: $lastError"
}

function Get-ReleaseAsset {
    param(
        [Parameter(Mandatory = $true)] $Release,
        [Parameter(Mandatory = $true)] [string]$ArchToken
    )

    $asset = $Release.assets |
        Where-Object { $_.name -match "^mihomo-windows-$ArchToken.*\.(zip|gz)$" } |
        Select-Object -First 1

    if (-not $asset) {
        $asset = $Release.assets |
            Where-Object { $_.name -match "windows" -and $_.name -match $ArchToken -and $_.name -match "\.(zip|gz)$" } |
            Select-Object -First 1
    }

    return $asset
}

function Get-FallbackAssetUrls {
    param(
        [Parameter(Mandatory = $true)] [string]$Tag,
        [Parameter(Mandatory = $true)] [string]$ArchToken
    )

    $baseUrl = "https://github.com/MetaCubeX/mihomo/releases/download/$Tag"
    $assetNames = @(
        "mihomo-windows-$ArchToken-compatible-$Tag.zip",
        "mihomo-windows-$ArchToken-compatible-$Tag.gz",
        "mihomo-windows-$ArchToken-$Tag.zip",
        "mihomo-windows-$ArchToken-$Tag.gz",
        "mihomo-windows-$ArchToken-alpha-$Tag.zip",
        "mihomo-windows-$ArchToken-alpha-$Tag.gz"
    ) | Select-Object -Unique

    return $assetNames | ForEach-Object { "$baseUrl/$_" }
}

function Download-AssetWithFallbackCandidates {
    param(
        [Parameter(Mandatory = $true)] [string[]]$AssetUrls,
        [Parameter(Mandatory = $true)] [string]$OutputPath
    )

    $attemptedAssets = 0
    $lastError = ""

    foreach ($assetUrl in $AssetUrls) {
        $attemptedAssets++
        try {
            Write-Log "Trying asset candidate: $assetUrl"
            $selectedDownloadUrl = Download-Asset -AssetUrl $assetUrl -OutputPath $OutputPath
            return @{
                AssetUrl = $assetUrl
                DownloadUrl = $selectedDownloadUrl
                AttemptedAssets = $attemptedAssets
            }
        }
        catch {
            $lastError = $_.Exception.Message
            Write-Log "Asset candidate failed: $assetUrl : $lastError"
        }
    }

    throw "All asset candidates failed. Tried $attemptedAssets asset urls. Last error: $lastError"
}

function Assert-DirectoryWritable {
    param(
        [Parameter(Mandatory = $true)] [string]$Path
    )

    $probeFile = Join-Path $Path ("write-test-" + [guid]::NewGuid().ToString("N") + ".tmp")
    try {
        Set-Content -Path $probeFile -Value "write-check" -Encoding UTF8
        Remove-Item -Path $probeFile -Force
    }
    catch {
        throw "Target directory is not writable: $Path. $($_.Exception.Message)"
    }
}

try {
    $FallbackTag = Normalize-Tag -Tag $FallbackTag

    $targetExe = Join-Path $KernelDir "mihomo.exe"
    Write-Log "Resolved kernel directory: $KernelDir"
    Write-Log "Resolved kernel executable path: $targetExe"

    if (Test-Path $targetExe) {
        Write-Log "Kernel already exists at $targetExe. Skip download."
        exit 0
    }

    $archToken = Get-ArchitectureToken
    Write-Log "Detected architecture: $archToken"

    if (-not (Test-Path $KernelDir)) {
        Write-Log "Creating kernel directory: $KernelDir"
        New-Item -ItemType Directory -Path $KernelDir -Force | Out-Null
    }

    Assert-DirectoryWritable -Path $KernelDir
    Write-Log "Write access confirmed for: $KernelDir"

    $assetName = ""
    $assetUrls = @()

    try {
        $release = Get-LatestRelease
        $asset = Get-ReleaseAsset -Release $release -ArchToken $archToken
        if ($asset) {
            $assetName = $asset.name
            $assetUrls = @($asset.browser_download_url)
        }
        else {
            Write-Log "No suitable latest release asset found for architecture: $archToken"
        }
    }
    catch {
        Write-Log "Latest metadata unavailable: $($_.Exception.Message)"
    }

    if (-not $assetUrls -or $assetUrls.Count -eq 0) {
        Write-Log "Falling back to fixed release tag: $FallbackTag"
        $assetUrls = Get-FallbackAssetUrls -Tag $FallbackTag -ArchToken $archToken
        if (-not $assetUrls -or $assetUrls.Count -eq 0) {
            throw "No fallback asset candidates generated for architecture: $archToken and tag: $FallbackTag"
        }
        $assetName = [System.IO.Path]::GetFileName($assetUrls[0])
    }

    Write-Log "Selected asset: $assetName"

    $tempRoot = Join-Path $env:TEMP ("ClashWinUI-Kernel-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

    try {
        $archivePath = Join-Path $tempRoot "kernel-archive.zip"
        $downloadResult = Download-AssetWithFallbackCandidates -AssetUrls $assetUrls -OutputPath $archivePath
        Write-Log "Downloaded from: $($downloadResult.DownloadUrl)"

        $extractDir = Join-Path $tempRoot "extract"
        New-Item -ItemType Directory -Path $extractDir -Force | Out-Null

        if ($downloadResult.AssetUrl -match "\.zip($|\?)") {
            Expand-Archive -Path $archivePath -DestinationPath $extractDir -Force
        }
        elseif ($downloadResult.AssetUrl -match "\.gz($|\?)") {
            $outPath = Join-Path $extractDir "mihomo.exe"
            $inStream = [System.IO.File]::OpenRead($archivePath)
            $outStream = [System.IO.File]::Create($outPath)
            try {
                $gzipStream = New-Object System.IO.Compression.GZipStream($inStream, [System.IO.Compression.CompressionMode]::Decompress)
                try {
                    $gzipStream.CopyTo($outStream)
                }
                finally {
                    $gzipStream.Dispose()
                }
            }
            finally {
                $inStream.Dispose()
                $outStream.Dispose()
            }
        }
        else {
            throw "Unsupported archive format from url: $($downloadResult.AssetUrl)"
        }

        $exe = Get-ChildItem -Path $extractDir -Recurse -File -Filter *.exe |
            Where-Object { $_.Name -match "mihomo|clash" } |
            Select-Object -First 1

        if (-not $exe) {
            throw "No executable found after extraction."
        }

        Copy-Item -Path $exe.FullName -Destination $targetExe -Force
        Write-Log "Kernel downloaded successfully to: $targetExe"
    }
    finally {
        if (Test-Path $tempRoot) {
            Remove-Item -Path $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    exit 0
}
catch {
    Write-Log "Kernel download failed: $($_.Exception.Message)"
    exit 1
}
