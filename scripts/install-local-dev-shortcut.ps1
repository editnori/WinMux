param(
    [string]$Platform = "x64",
    [string]$Configuration = "Release",
    [string]$ShortcutName = "WinMux Dev",
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$projectPath = Join-Path $repoRoot "SelfContainedDeployment.csproj"
$dotnetPath = "C:\Program Files\dotnet\dotnet.exe"

if (-not (Test-Path $projectPath)) {
    throw "Could not find project file at '$projectPath'."
}

if (-not (Test-Path $dotnetPath)) {
    throw "Could not find dotnet at '$dotnetPath'."
}

$projectXml = [xml](Get-Content $projectPath)
$targetFramework = $projectXml.Project.PropertyGroup.TargetFramework | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($targetFramework)) {
    throw "Could not resolve TargetFramework from '$projectPath'."
}

$runtimeIdentifier = switch ($Platform.ToLowerInvariant()) {
    "x64" { "win-x64" }
    "x86" { "win-x86" }
    "arm64" { "win-arm64" }
    default { throw "Unsupported platform '$Platform'. Expected x64, x86, or arm64." }
}

$publishProfilePath = Join-Path $repoRoot "Properties\PublishProfiles\win10-$Platform.pubxml"
if (-not (Test-Path $publishProfilePath)) {
    throw "Could not find publish profile '$publishProfilePath'."
}

if (-not $SkipPublish) {
    & $dotnetPath publish $projectPath -c $Configuration -p:Platform=$Platform -p:PublishProfile=$publishProfilePath | Out-Host
}

$publishDirectory = Join-Path $repoRoot "bin\$Configuration\$targetFramework\$runtimeIdentifier\publish"
$exePath = Join-Path $publishDirectory "WinMux.exe"
if (-not (Test-Path $exePath)) {
    $exePath = Join-Path $publishDirectory "SelfContainedDeployment.exe"
}

if (-not (Test-Path $exePath)) {
    throw "Could not find published app executable in '$publishDirectory'."
}

$desktopDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
if ([string]::IsNullOrWhiteSpace($desktopDirectory) -or -not (Test-Path $desktopDirectory)) {
    throw "Could not resolve the current user's desktop directory."
}

$shortcutPath = Join-Path $desktopDirectory ($ShortcutName + ".lnk")
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $exePath
$shortcut.WorkingDirectory = $publishDirectory
$shortcut.IconLocation = "$exePath,0"
$shortcut.Description = "Local WinMux development build"
$shortcut.Save()

[pscustomobject]@{
    ok = $true
    shortcutPath = $shortcutPath
    executablePath = $exePath
    publishDirectory = $publishDirectory
    configuration = $Configuration
    platform = $Platform
    runtimeIdentifier = $runtimeIdentifier
} | ConvertTo-Json -Depth 5
