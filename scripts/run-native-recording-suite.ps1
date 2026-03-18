param(
    [int]$Port = 9331,
    [string]$OutputDirectory = "",
    [ValidateSet("standard", "cinematic")]
    [string]$Mode = "cinematic"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot "native-automation-client.ps1")
Initialize-WinMuxAutomationClient -Port $Port | Out-Null
$startScriptPath = Join-Path $PSScriptRoot "start-webview2-debug.ps1"
$fps = if ($Mode -eq "cinematic") { 16 } else { 12 }
$windowWidth = if ($Mode -eq "cinematic") { 1560 } else { 1240 }
$windowHeight = if ($Mode -eq "cinematic") { 1040 } else { 860 }

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $OutputDirectory = Join-Path $repoRoot "tmp/automation-captures/winmux-recording-suite-$Mode-$timestamp"
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

function Ensure-WinMuxReady {
    try {
        $health = Invoke-AutomationGet "/health"
        if ($health.ok) {
            return
        }
    }
    catch {
    }

    & $startScriptPath -SkipBuild -AutomationPort $Port | Out-Host
}

function Prepare-WinMuxWindow {
    try {
        $null = Invoke-AutomationPost "/desktop-action" @{
            action = "focusWindow"
            titleContains = "WinMux"
        }
        $null = Invoke-AutomationPost "/desktop-action" @{
            action = "setTopmost"
            titleContains = "WinMux"
            value = "true"
        }
        $null = Invoke-AutomationPost "/desktop-action" @{
            action = "centerWindow"
            titleContains = "WinMux"
        }
        $null = Invoke-AutomationPost "/desktop-action" @{
            action = "resizeWindow"
            titleContains = "WinMux"
            width = $windowWidth
            height = $windowHeight
        }
        $null = Invoke-AutomationPost "/desktop-action" @{
            action = "centerWindow"
            titleContains = "WinMux"
        }
        $null = Invoke-AutomationPost "/action" @{
            action = "setTheme"
            value = "light"
        }
    }
    catch {
    }
}

function Finalize-WinMuxWindow {
    try {
        $null = Invoke-AutomationPost "/desktop-action" @{
            action = "setTopmost"
            titleContains = "WinMux"
            value = "false"
        }
        $null = Invoke-AutomationPost "/action" @{
            action = "setTheme"
            value = "dark"
        }
    }
    catch {
    }
}

function Invoke-RecordingScript {
    param(
        [string]$Name,
        [string]$ScriptPath,
        [hashtable]$Parameters,
        [string]$Summary
    )

    Ensure-WinMuxReady
    Prepare-WinMuxWindow

    $result = & $ScriptPath @Parameters | ConvertFrom-Json
    if (-not $result.ok) {
        throw "$Name recording failed."
    }

    return [pscustomobject]@{
        name = $Name
        summary = $Summary
        script = (Resolve-Path $ScriptPath).Path
        outputDirectory = $result.outputDirectory
        videoPath = $result.videoPath
        manifestPath = $result.manifestPath
        prepareVideoPath = $result.prepareVideoPath
        prepareManifestPath = $result.prepareManifestPath
        restoreVideoPath = $result.restoreVideoPath
        restoreManifestPath = $result.restoreManifestPath
    }
}

$recordings = New-Object System.Collections.Generic.List[object]

try {
    Ensure-WinMuxReady

    $recordings.Add((Invoke-RecordingScript -Name "overview" -ScriptPath (Join-Path $PSScriptRoot "run-native-demo-recording.ps1") -Parameters @{
        Mode = $Mode
        Fps = $fps
        WindowWidth = $windowWidth
        WindowHeight = $windowHeight
        OutputDirectory = (Join-Path $OutputDirectory "01-overview")
    } -Summary "Big-picture tour of threads, tabs, settings, and the shell chrome."))

    $recordings.Add((Invoke-RecordingScript -Name "workspace-showcase" -ScriptPath (Join-Path $PSScriptRoot "run-native-workspace-showcase-recording.ps1") -Parameters @{
        Fps = $fps
        WindowWidth = $windowWidth
        WindowHeight = $windowHeight
        OutputDirectory = (Join-Path $OutputDirectory "02-workspace-showcase")
    } -Summary "All pane types, fit/lock/zoom, file browser/editor flow, worktree terminals, and overflow-thread behavior."))

    $recordings.Add((Invoke-RecordingScript -Name "feature-tour" -ScriptPath (Join-Path $PSScriptRoot "run-native-feature-tour-recording.ps1") -Parameters @{
        Fps = $fps
        OutputDirectory = (Join-Path $OutputDirectory "03-feature-tour")
    } -Summary "Threads, worktree inheritance, browser panes, review flow, settings, and project cleanup."))

    $recordings.Add((Invoke-RecordingScript -Name "patch-review" -ScriptPath (Join-Path $PSScriptRoot "run-native-patch-review-recording.ps1") -Parameters @{
        Fps = $fps
        OutputDirectory = (Join-Path $OutputDirectory "04-patch-review")
    } -Summary "Focused review demo for structured diff rendering and patch-state automation."))

    $recordings.Add((Invoke-RecordingScript -Name "new-project" -ScriptPath (Join-Path $PSScriptRoot "run-native-new-project-recording.ps1") -Parameters @{
        Fps = $fps
        WindowWidth = $windowWidth
        WindowHeight = $windowHeight
        OutputDirectory = (Join-Path $OutputDirectory "05-new-project")
    } -Summary "Project creation, shell-profile selection, and empty-state recovery."))

    $recordings.Add((Invoke-RecordingScript -Name "tab-switch" -ScriptPath (Join-Path $PSScriptRoot "run-native-tab-switch-recording.ps1") -Parameters @{
        Fps = $fps
        WindowWidth = $windowWidth
        WindowHeight = $windowHeight
        OutputDirectory = (Join-Path $OutputDirectory "06-tab-switch")
    } -Summary "Fast pane switching and strip behavior under real terminal tabs."))

    $recordings.Add((Invoke-RecordingScript -Name "automation-tour" -ScriptPath (Join-Path $PSScriptRoot "run-native-automation-tour-recording.ps1") -Parameters @{
        Fps = $fps
        WindowWidth = $windowWidth
        WindowHeight = $windowHeight
        OutputDirectory = (Join-Path $OutputDirectory "07-automation-tour")
    } -Summary "Shows Bun-driven native control from inside WinMux itself."))

    $recordings.Add((Invoke-RecordingScript -Name "session-restore" -ScriptPath (Join-Path $PSScriptRoot "run-native-session-restore-recording.ps1") -Parameters @{
        Fps = $fps
        WindowWidth = $windowWidth
        WindowHeight = $windowHeight
        OutputDirectory = (Join-Path $OutputDirectory "08-session-restore")
    } -Summary "Two-part restore demo showing save-state and restored workspace replay after relaunch."))
}
finally {
    Finalize-WinMuxWindow
}

$manifest = [pscustomobject]@{
    ok = $true
    mode = $Mode
    outputDirectory = $OutputDirectory
    recordings = $recordings
}

$manifestPath = Join-Path $OutputDirectory "manifest.json"
$manifest | ConvertTo-Json -Depth 12 | Set-Content -Path $manifestPath

$markdownPath = Join-Path $OutputDirectory "README.md"
$markdown = @(
    "# WinMux Recording Suite"
    ""
    "Mode: $Mode"
    ""
    "Output directory: `$OutputDirectory`"
    ""
    "## Recordings"
)

foreach ($recording in $recordings) {
    $markdown += ""
    $markdown += "- $($recording.name): $($recording.summary)"
    if ($recording.videoPath) {
        $markdown += "  video: $($recording.videoPath)"
    }
    if ($recording.prepareVideoPath) {
        $markdown += "  save-state video: $($recording.prepareVideoPath)"
    }
    if ($recording.restoreVideoPath) {
        $markdown += "  restored-state video: $($recording.restoreVideoPath)"
    }
}

$markdown | Set-Content -Path $markdownPath

$manifest | ConvertTo-Json -Depth 12
