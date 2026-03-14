param(
    [int]$Port = 9222,
    [string]$Platform = "x64",
    [string]$Configuration = "Debug",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "SelfContainedDeployment.csproj"
$dotnetPath = "C:\Program Files\dotnet\dotnet.exe"
$exePath = Join-Path $repoRoot "bin\$Platform\$Configuration\net6.0-windows10.0.19041.0\win10-$Platform\SelfContainedDeployment.exe"
$webRoot = Join-Path $repoRoot "Web"

if (-not $SkipBuild) {
    & $dotnetPath build $projectPath -p:Platform=$Platform | Out-Host
}

if (-not (Test-Path $exePath)) {
    throw "Could not find built app at $exePath"
}

$env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=$Port --remote-debugging-address=0.0.0.0"
$env:NATIVE_TERMINAL_WEB_ROOT = $webRoot

$process = Start-Process -FilePath $exePath -WorkingDirectory $repoRoot -PassThru

Write-Host "Started native app PID $($process.Id)"
Write-Host "WebView2 CDP endpoint: http://127.0.0.1:$Port"
Write-Host "Renderer source root: $webRoot"
Write-Host ""
Write-Host "Playwright attach:"
Write-Host "  chromium.connectOverCDP('http://127.0.0.1:$Port')"
Write-Host ""

$targetsUrl = "http://127.0.0.1:$Port/json/list"
$lastTargets = $null

for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 250

    try {
        $targets = Invoke-RestMethod -Uri $targetsUrl -TimeoutSec 2
        $lastTargets = $targets

        $rendererTargets = @($targets | Where-Object {
            $_.url -like "*terminal-host.html*" -or $_.title -like "*terminal*"
        })

        if ($rendererTargets.Count -gt 0) {
            Write-Host "Detected WebView2 targets:"
            $rendererTargets | Select-Object title, url, type | Format-Table -AutoSize
            exit 0
        }
    }
    catch {
    }
}

if ($lastTargets) {
    Write-Warning "Only transient WebView2 targets were detected so far. The renderer may still be navigating."
    $lastTargets | Select-Object title, url, type | Format-Table -AutoSize
}
else {
    Write-Warning "WebView2 debug port did not report targets yet. The app may still be starting."
}
