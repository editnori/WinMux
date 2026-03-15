param(
    [int]$Port = 9222,
    [int]$AutomationPort = 9331,
    [string]$Platform = "x64",
    [string]$Configuration = "Debug",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

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
    Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$AutomationPort/action" -ContentType "application/json" -Body (@{ action = "saveSession" } | ConvertTo-Json -Compress) -TimeoutSec 2 | Out-Null
}
catch {
}

Get-Process SelfContainedDeployment -ErrorAction SilentlyContinue | Stop-Process -Force
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

    return Get-ChildItem -Path $candidateRoot -Filter "SelfContainedDeployment.exe" -Recurse -File |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}

$exePath = Resolve-AppExecutablePath -Root $repoRoot -TargetFrameworkValue $targetFramework -PlatformValue $Platform -ConfigurationValue $Configuration
if (-not (Test-Path $exePath)) {
    throw "Could not find built app at $exePath"
}

$env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=$Port --remote-debugging-address=0.0.0.0"
$env:NATIVE_TERMINAL_WEB_ROOT = $webRoot
$env:NATIVE_TERMINAL_AUTOMATION_PORT = "$AutomationPort"

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
$automationHealthUrl = "http://127.0.0.1:$AutomationPort/health"
$lastTargets = $null
$rendererReady = $false
$automationReady = $false

for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 250

    try {
        $targets = Invoke-RestMethod -Uri $targetsUrl -TimeoutSec 2
        $lastTargets = $targets

        $rendererTargets = @($targets | Where-Object {
            $_.type -eq "page" -and (
                $_.url -like "*terminal-host.html*" -or
                $_.title -like "*terminal*" -or
                $_.url -like "http://*" -or
                $_.url -like "https://*"
            )
        })

        if ($rendererTargets.Count -gt 0) {
            $rendererReady = $true
            $lastTargets = $rendererTargets
        }
    }
    catch {
    }

    if (-not $automationReady) {
        try {
            $null = Invoke-RestMethod -Uri $automationHealthUrl -TimeoutSec 2
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
