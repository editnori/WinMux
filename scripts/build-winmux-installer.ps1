param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory,
    [string]$OutputPath = "",
    [string]$InstallPath = "%LocalAppData%\\Programs\\WinMux",
    [string]$SevenZipPath = "C:\Users\lqassem\scoop\shims\7z.exe",
    [string]$SevenZipSfxPath = "C:\Users\lqassem\scoop\apps\7zip\current\7z.sfx"
)

$ErrorActionPreference = "Stop"

$publishDirectory = (Resolve-Path $PublishDirectory).Path
if (-not (Test-Path (Join-Path $publishDirectory "WinMux.exe"))) {
    throw "Publish directory '$publishDirectory' does not contain WinMux.exe."
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path (Split-Path $publishDirectory -Parent) "WinMux-win-x64-installer.exe"
}

if (-not (Test-Path $SevenZipPath)) {
    throw "7-Zip executable not found at '$SevenZipPath'."
}

if (-not (Test-Path $SevenZipSfxPath)) {
    throw "7-Zip SFX module not found at '$SevenZipSfxPath'."
}

$outputPath = [System.IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path $outputPath -Parent
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("winmux-installer-" + [Guid]::NewGuid().ToString("N"))
$archivePath = Join-Path $tempRoot "payload.7z"
$configPath = Join-Path $tempRoot "config.txt"
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

try {
    Push-Location $publishDirectory
    & $SevenZipPath a -t7z -mx=9 -mmt=on $archivePath * | Out-Null
    Pop-Location

    $config = @"
;!@Install@!UTF-8!
Title="WinMux"
BeginPrompt="Install WinMux to $InstallPath ?"
ExtractTitle="Installing WinMux"
ExtractDialogText="Extracting WinMux files..."
InstallPath="$InstallPath"
OverwriteMode="2"
RunProgram="WinMux.exe"
GUIMode="1"
;!@InstallEnd@!
"@
    [System.IO.File]::WriteAllText($configPath, $config, [System.Text.UTF8Encoding]::new($false))

    $sfxBytes = [System.IO.File]::ReadAllBytes($SevenZipSfxPath)
    $configBytes = [System.IO.File]::ReadAllBytes($configPath)
    $archiveBytes = [System.IO.File]::ReadAllBytes($archivePath)

    $stream = [System.IO.File]::Open($outputPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    try {
        $stream.Write($sfxBytes, 0, $sfxBytes.Length)
        $stream.Write($configBytes, 0, $configBytes.Length)
        $stream.Write($archiveBytes, 0, $archiveBytes.Length)
    }
    finally {
        $stream.Dispose()
    }

    [pscustomobject]@{
        ok = $true
        outputPath = $outputPath
        publishDirectory = $publishDirectory
        installPath = $InstallPath
    } | ConvertTo-Json -Depth 5
}
finally {
    if (Get-Location | Where-Object { $_.Path -eq $publishDirectory }) {
        Pop-Location
    }

    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
