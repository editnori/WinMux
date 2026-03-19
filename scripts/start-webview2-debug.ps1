param(
    [int]$Port = 9222,
    [int]$AutomationPort = 9331,
    [string]$Platform = "x64",
    [string]$Configuration = "Debug",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "native-automation-client.ps1")

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "SelfContainedDeployment.csproj"
$dotnetPath = "C:\Program Files\dotnet\dotnet.exe"
$webRoot = Join-Path $repoRoot "Web"
$projectXml = [xml](Get-Content $projectPath)
$targetFramework = $projectXml.Project.PropertyGroup.TargetFramework | Select-Object -First 1

if ([string]::IsNullOrWhiteSpace($targetFramework)) {
    throw "Could not resolve TargetFramework from $projectPath"
}

try {
    Initialize-WinMuxAutomationClient -Port $AutomationPort | Out-Null
    Invoke-AutomationPost "/action" @{ action = "saveSession" } -TimeoutSec 2 -CompressJson | Out-Null
}
catch {
}

@("WinMux", "SelfContainedDeployment") | ForEach-Object {
    Get-Process $_ -ErrorAction SilentlyContinue | Stop-Process -Force
}
Start-Sleep -Milliseconds 750

if (-not $SkipBuild) {
    & $dotnetPath build $projectPath -p:Platform=$Platform | Out-Host
}

function Resolve-AppExecutablePath {
    param(
        [string]$Root,
        [string]$TargetFrameworkValue,
        [string]$PlatformValue,
        [string]$ConfigurationValue
    )

    $candidateRoot = Join-Path $Root "bin\$PlatformValue\$ConfigurationValue\$TargetFrameworkValue"
    if (-not (Test-Path $candidateRoot)) {
        return $null
    }

    return Get-ChildItem -Path $candidateRoot -Filter "*.exe" -Recurse -File |
        Where-Object { $_.Name -in @("WinMux.exe", "SelfContainedDeployment.exe") } |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}

$exePath = Resolve-AppExecutablePath -Root $repoRoot -TargetFrameworkValue $targetFramework -PlatformValue $Platform -ConfigurationValue $Configuration
if (-not (Test-Path $exePath)) {
    throw "Could not find built app at $exePath"
}

$env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=$Port --remote-debugging-address=127.0.0.1"
$env:NATIVE_TERMINAL_WEB_ROOT = $webRoot
$env:NATIVE_TERMINAL_AUTOMATION_PORT = "$AutomationPort"
$tokenBytes = [byte[]]::new(32)
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$rng.GetBytes($tokenBytes)
$rng.Dispose()
$automationToken = [Convert]::ToBase64String($tokenBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
$env:NATIVE_TERMINAL_AUTOMATION_TOKEN = $automationToken
$env:WINMUX_AUTOMATION_TOKEN = $automationToken
$env:WINMUX_ENABLE_WEBVIEW_DEVTOOLS = "1"
$env:WINMUX_ENABLE_BROWSER_DEVTOOLS = "1"

$process = Start-Process -FilePath $exePath -WorkingDirectory $repoRoot -PassThru

Write-Host "Started native app PID $($process.Id)"
Write-Host "WebView2 CDP endpoint: http://127.0.0.1:$Port"
Write-Host "Native automation endpoint: http://127.0.0.1:$AutomationPort"
Write-Host "Renderer source root: $webRoot"
Write-Host ""
Write-Host "Playwright attach:"
Write-Host "  chromium.connectOverCDP('http://127.0.0.1:$Port')"
Write-Host ""

$targetsUrl = "http://127.0.0.1:$Port/json/list"
$lastTargets = $null
$rendererReady = $false
$automationReady = $false

Initialize-WinMuxAutomationClient -Port $AutomationPort | Out-Null

for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 250

    try {
        $targets = Invoke-RestMethod -Uri $targetsUrl -TimeoutSec 2
        $lastTargets = $targets

        $pageTargets = @($targets | Where-Object { $_.type -eq "page" })
        $rendererTargets = @($targets | Where-Object {
            $_.type -eq "page" -and (
                $_.url -like "winmux://*" -or
                $_.url -like "http://*" -or
                $_.url -like "https://*" -or
                $_.url -like "file://*"
            )
        })

        if ($rendererTargets.Count -gt 0) {
            $rendererReady = $true
            $lastTargets = $rendererTargets
        }
        elseif ($pageTargets.Count -gt 0) {
            $lastTargets = $pageTargets
        }
    }
    catch {
    }

    if (-not $automationReady) {
        try {
            $null = Invoke-AutomationGet "/health" -TimeoutSec 2
            $automationReady = $true
        }
        catch {
        }
    }

    if ($rendererReady -and $automationReady) {
        Write-Host "Detected WebView2 targets:"
        $lastTargets | Select-Object title, url, type | Format-Table -AutoSize
        Write-Host "Native automation server is healthy."
        exit 0
    }

    if ($automationReady -and $lastTargets -and @($lastTargets).Count -gt 0) {
        Write-Warning "Native automation is healthy and WebView2 has page targets, but the renderer is still on a transient page."
        $lastTargets | Select-Object title, url, type | Format-Table -AutoSize
        Write-Host "Native automation server is healthy."
        exit 0
    }
}

if ($lastTargets -and -not $rendererReady) {
    Write-Warning "Only transient WebView2 targets were detected so far. The renderer may still be navigating."
    $lastTargets | Select-Object title, url, type | Format-Table -AutoSize
}
elseif (-not $rendererReady) {
    Write-Warning "WebView2 debug port did not report targets yet. The app may still be starting."
}

if (-not $automationReady) {
    Write-Warning "Native automation endpoint did not respond yet. Check the app health and automation log."
}

exit 1
