param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory,
    [string]$OutputPath = "",
    [string]$AppVersion = "0.0.0-local",
    [string]$InnoSetupCompilerPath = "",
    [string]$InstallerScriptPath = "",
    [string]$WebView2BootstrapperPath = "",
    [string]$WebView2BootstrapperUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703&clcid=0x409",
    [switch]$SkipWebView2Bootstrapper
)

$ErrorActionPreference = "Stop"

function Resolve-InnoSetupCompilerPath {
    param(
        [string]$PreferredPath
    )

    $candidates = [System.Collections.Generic.List[string]]::new()

    if (-not [string]::IsNullOrWhiteSpace($PreferredPath)) {
        $candidates.Add($PreferredPath)
    }

    if (-not [string]::IsNullOrWhiteSpace($env:ISCC_PATH)) {
        $candidates.Add($env:ISCC_PATH)
    }

    $command = Get-Command "iscc.exe" -ErrorAction SilentlyContinue
    if ($null -ne $command -and -not [string]::IsNullOrWhiteSpace($command.Source)) {
        $candidates.Add($command.Source)
    }

    $candidates.Add("C:\Program Files (x86)\Inno Setup 6\ISCC.exe")
    $candidates.Add("C:\Program Files\Inno Setup 6\ISCC.exe")

    foreach ($candidate in $candidates | Select-Object -Unique) {
        if (Test-Path $candidate) {
            return (Resolve-Path $candidate).Path
        }
    }

    throw "Inno Setup compiler not found. Install Inno Setup 6 or pass -InnoSetupCompilerPath."
}

function Convert-ToNumericVersion {
    param(
        [string]$Version
    )

    $match = [System.Text.RegularExpressions.Regex]::Match($Version, '\d+(?:\.\d+){0,3}')
    if (-not $match.Success) {
        return "0.0.0.0"
    }

    $parts = [System.Collections.Generic.List[string]]::new()
    foreach ($part in $match.Value.Split('.')) {
        if (-not [string]::IsNullOrWhiteSpace($part)) {
            $parts.Add($part)
        }
    }

    while ($parts.Count -lt 4) {
        $parts.Add("0")
    }

    return (($parts | Select-Object -First 4) -join ".")
}

function Resolve-WebView2BootstrapperPath {
    param(
        [string]$PreferredPath,
        [string]$DownloadUrl,
        [string]$DownloadDirectory
    )

    $candidates = [System.Collections.Generic.List[string]]::new()

    if (-not [string]::IsNullOrWhiteSpace($PreferredPath)) {
        $candidates.Add($PreferredPath)
    }

    if (-not [string]::IsNullOrWhiteSpace($env:WEBVIEW2_BOOTSTRAPPER_PATH)) {
        $candidates.Add($env:WEBVIEW2_BOOTSTRAPPER_PATH)
    }

    foreach ($candidate in $candidates | Select-Object -Unique) {
        if (Test-Path $candidate) {
            return (Resolve-Path $candidate).Path
        }
    }

    if ([string]::IsNullOrWhiteSpace($DownloadUrl)) {
        throw "WebView2 bootstrapper path not provided and no download URL was configured."
    }

    New-Item -ItemType Directory -Path $DownloadDirectory -Force | Out-Null
    $downloadPath = Join-Path $DownloadDirectory "MicrosoftEdgeWebview2Setup.exe"

    Invoke-WebRequest -Uri $DownloadUrl -OutFile $downloadPath

    if (-not (Test-Path $downloadPath)) {
        throw "Failed to download WebView2 bootstrapper from '$DownloadUrl'."
    }

    return $downloadPath
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$publishDirectory = (Resolve-Path $PublishDirectory).Path

if (-not (Test-Path (Join-Path $publishDirectory "WinMux.exe"))) {
    throw "Publish directory '$publishDirectory' does not contain WinMux.exe."
}

if ([string]::IsNullOrWhiteSpace($InstallerScriptPath)) {
    $InstallerScriptPath = Join-Path $repoRoot "installer\WinMuxInstaller.iss"
}

$installerScriptPath = (Resolve-Path $InstallerScriptPath).Path

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path (Split-Path $publishDirectory -Parent) "WinMux-win-x64-installer.exe"
}

$outputPath = [System.IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path $outputPath -Parent
$outputBaseFilename = [System.IO.Path]::GetFileNameWithoutExtension($outputPath)
$innoSetupCompilerPath = Resolve-InnoSetupCompilerPath -PreferredPath $InnoSetupCompilerPath
$numericVersion = Convert-ToNumericVersion -Version $AppVersion
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("winmux-installer-" + [Guid]::NewGuid().ToString("N"))
$resolvedWebView2BootstrapperPath = $null

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

try {
    if (-not $SkipWebView2Bootstrapper) {
        $resolvedWebView2BootstrapperPath = Resolve-WebView2BootstrapperPath `
            -PreferredPath $WebView2BootstrapperPath `
            -DownloadUrl $WebView2BootstrapperUrl `
            -DownloadDirectory $tempRoot
    }

    $arguments = @(
        "/Qp",
        "/DPublishDir=$publishDirectory",
        "/DOutputDir=$outputDirectory",
        "/DOutputBaseFilename=$outputBaseFilename",
        "/DAppVersion=$AppVersion",
        "/DAppVersionNumeric=$numericVersion",
        "/DRepoRoot=$repoRoot"
    )

    if (-not [string]::IsNullOrWhiteSpace($resolvedWebView2BootstrapperPath)) {
        $arguments += "/DWebView2BootstrapperFile=$resolvedWebView2BootstrapperPath"
    }

    $arguments += $installerScriptPath

    & $innoSetupCompilerPath @arguments

    if (-not (Test-Path $outputPath)) {
        throw "Installer build completed but '$outputPath' was not created."
    }
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}

[pscustomobject]@{
    ok = $true
    appVersion = $AppVersion
    installerScriptPath = $installerScriptPath
    innoSetupCompilerPath = $innoSetupCompilerPath
    outputPath = $outputPath
    publishDirectory = $publishDirectory
    webView2BootstrapperPath = $resolvedWebView2BootstrapperPath
} | ConvertTo-Json -Depth 5
