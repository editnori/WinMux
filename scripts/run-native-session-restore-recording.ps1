param(
    [int]$Port = 9331,
    [string]$OutputDirectory = "",
    [int]$Fps = 16,
    [int]$WindowWidth = 1560,
    [int]$WindowHeight = 1040,
    [switch]$KeepFrames
)

$ErrorActionPreference = "Stop"
$baseUrl = "http://127.0.0.1:$Port"
$repoRoot = Split-Path $PSScriptRoot -Parent
$startScriptPath = Join-Path $PSScriptRoot "start-webview2-debug.ps1"

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $OutputDirectory = Join-Path $repoRoot "tmp/automation-captures/winmux-session-restore-$timestamp"
}

$prepareDirectory = Join-Path $OutputDirectory "01-save-state"
$restoreDirectory = Join-Path $OutputDirectory "02-restored-state"
$tempProjectPath = Join-Path $env:TEMP ("winmux-session-restore-" + [Guid]::NewGuid().ToString("N"))

New-Item -ItemType Directory -Path $prepareDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $restoreDirectory -Force | Out-Null

function Invoke-AutomationGet {
    param([string]$Path)

    return Invoke-RestMethod -Uri "$baseUrl$Path" -TimeoutSec 20
}

function Invoke-AutomationPost {
    param(
        [string]$Path,
        [object]$Body
    )

    $json = if ($null -eq $Body) { "" } else { $Body | ConvertTo-Json -Depth 20 }
    return Invoke-RestMethod -Method Post -Uri "$baseUrl$Path" -ContentType "application/json" -Body $json -TimeoutSec 25
}

function Pause-Step {
    param([int]$Milliseconds = 900)

    Start-Sleep -Milliseconds ([Math]::Max(80, $Milliseconds))
}

function Wait-Until {
    param(
        [scriptblock]$Condition,
        [string]$FailureMessage,
        [int]$Attempts = 30,
        [int]$DelayMilliseconds = 300
    )

    for ($index = 0; $index -lt $Attempts; $index++) {
        $value = & $Condition
        if ($value) {
            return $value
        }

        Start-Sleep -Milliseconds $DelayMilliseconds
    }

    throw $FailureMessage
}

function Invoke-Git {
    param(
        [Parameter(ValueFromRemainingArguments = $true)]
        [string[]]$Arguments
    )

    $output = & git @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Git command failed: git $($Arguments -join ' ')`n$output"
    }

    return $output
}

function Initialize-SessionRestoreGitRepo {
    param([string]$Path)

    New-Item -ItemType Directory -Path $Path -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $Path "src") -Force | Out-Null

    Invoke-Git -C $Path init | Out-Null
    Invoke-Git -C $Path config user.email "winmux-session@example.com" | Out-Null
    Invoke-Git -C $Path config user.name "WinMux Session" | Out-Null

    @(
        "# Session restore",
        "",
        "This workspace is used to prove session replay."
    ) | Set-Content -Path (Join-Path $Path "README.md")

    @(
        "alpha",
        "beta",
        "gamma"
    ) | Set-Content -Path (Join-Path $Path "notes.txt")

    @(
        "namespace RestoreDemo;",
        "",
        "internal static class Program",
        "{",
        '    public static string Name => "WinMux";',
        "}"
    ) | Set-Content -Path (Join-Path $Path "src/Program.cs")

    Invoke-Git -C $Path add README.md notes.txt src/Program.cs | Out-Null
    Invoke-Git -C $Path commit -m "Initial restore snapshot" | Out-Null

    @(
        "alpha",
        "beta updated",
        "gamma",
        "delta added"
    ) | Set-Content -Path (Join-Path $Path "notes.txt")
}

function Get-ProjectById {
    param(
        [object]$State,
        [string]$ProjectId
    )

    return @($State.projects) | Where-Object { $_.id -eq $ProjectId } | Select-Object -First 1
}

function Get-ThreadById {
    param(
        [object]$Project,
        [string]$ThreadId
    )

    return @($Project.threads) | Where-Object { $_.id -eq $ThreadId } | Select-Object -First 1
}

function Get-WinMuxWindow {
    $desktopWindows = Invoke-AutomationGet "/desktop-windows"
    return @($desktopWindows.windows) | Where-Object { $_.title -like "*WinMux*" } | Select-Object -First 1
}

function Focus-WinMuxWindow {
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        $null = Invoke-AutomationPost "/desktop-action" @{
            action = "focusWindow"
            titleContains = "WinMux"
        }

        for ($poll = 0; $poll -lt 6; $poll++) {
            $window = Get-WinMuxWindow
            if ($null -ne $window -and $window.focused -eq $true) {
                return $window
            }

            Start-Sleep -Milliseconds 140
        }

        $null = Invoke-AutomationPost "/desktop-action" @{
            action = "clickPoint"
            titleContains = "WinMux"
        }
    }

    throw "WinMux did not become the focused window."
}

function Set-WinMuxTopmost {
    param([bool]$Enabled)

    $null = Invoke-AutomationPost "/desktop-action" @{
        action = "setTopmost"
        titleContains = "WinMux"
        value = if ($Enabled) { "true" } else { "false" }
    }
}

function Center-WinMuxWindow {
    $null = Invoke-AutomationPost "/desktop-action" @{
        action = "centerWindow"
        titleContains = "WinMux"
    }
}

function Resize-WinMuxWindow {
    param(
        [int]$Width,
        [int]$Height
    )

    $null = Invoke-AutomationPost "/desktop-action" @{
        action = "resizeWindow"
        titleContains = "WinMux"
        width = $Width
        height = $Height
    }
}

function Move-WinMuxWindow {
    param(
        [int]$X,
        [int]$Y
    )

    $null = Invoke-AutomationPost "/desktop-action" @{
        action = "moveWindow"
        titleContains = "WinMux"
        x = $X
        y = $Y
    }
}

function Get-TerminalPrintableText {
    param([string]$Text)

    if ([string]::IsNullOrEmpty($Text)) {
        return ""
    }

    $escape = [string][char]27
    $clean = $Text `
        -replace ([regex]::Escape($escape) + "\[[0-9;?]*[ -/]*[@-~]"), "" `
        -replace ([regex]::Escape($escape) + "\][^\u0007]*\u0007"), ""
    $clean = [regex]::Replace($clean, "[\u0000-\u0008\u000B\u000C\u000E-\u001F]", "")
    return $clean.Trim()
}

function Wait-ForTerminalReady {
    param([string]$TabId)

    return Wait-Until -FailureMessage "Terminal tab '$TabId' was not ready." -Condition {
        $terminalState = Invoke-AutomationPost "/terminal-state" @{ tabId = $TabId }
        $snapshot = @($terminalState.tabs) | Where-Object { $_.tabId -eq $TabId } | Select-Object -First 1
        $visibleText = Get-TerminalPrintableText $(if ($null -eq $snapshot) { "" } else { [string]$snapshot.visibleText })
        $bufferTail = Get-TerminalPrintableText $(if ($null -eq $snapshot) { "" } else { [string]$snapshot.bufferTail })
        if ($null -ne $snapshot -and
            $snapshot.rendererReady -eq $true -and
            $snapshot.started -eq $true -and
            $snapshot.exited -ne $true -and
            (-not [string]::IsNullOrWhiteSpace($visibleText) -or -not [string]::IsNullOrWhiteSpace($bufferTail))) {
            return $snapshot
        }

        return $null
    } -Attempts 60 -DelayMilliseconds 250
}

function Wait-ForProjectState {
    param(
        [string]$ProjectPath,
        [string]$ThreadName,
        [int]$MinimumPaneCount = 1
    )

    return Wait-Until -FailureMessage "Workspace state for '$ProjectPath' was not restored." -Condition {
        $latestState = Invoke-AutomationGet "/state"
        if ($latestState.projectPath -ne $ProjectPath) {
            return $null
        }

        $latestProject = Get-ProjectById -State $latestState -ProjectId $latestState.projectId
        $thread = @($latestProject.threads) | Where-Object { $_.name -eq $ThreadName } | Select-Object -First 1
        if ($null -eq $thread -or @($thread.tabs).Count -lt $MinimumPaneCount) {
            return $null
        }

        return [pscustomobject]@{
            State = $latestState
            Thread = $thread
        }
    } -Attempts 60 -DelayMilliseconds 500
}

function Restart-WinMuxForRestore {
    & $startScriptPath -SkipBuild -AutomationPort $Port | Out-Host
}

$prepareRecordingStop = $null
$restoreRecordingStop = $null
$prepareStarted = $false
$restoreStarted = $false
$originalWindow = $null
$workspaceProjectId = $null
$workspaceThreadName = "Restored Session"

try {
    $health = Invoke-AutomationGet "/health"
    if (-not $health.ok) {
        throw "Native automation server is not healthy."
    }

    Initialize-SessionRestoreGitRepo -Path $tempProjectPath

    $originalWindow = Get-WinMuxWindow
    Focus-WinMuxWindow | Out-Null
    Set-WinMuxTopmost -Enabled $true
    Center-WinMuxWindow
    Resize-WinMuxWindow -Width $WindowWidth -Height $WindowHeight
    Center-WinMuxWindow
    Focus-WinMuxWindow | Out-Null
    Pause-Step 800

    $null = Invoke-AutomationPost "/action" @{ action = "showTerminal" }
    $null = Invoke-AutomationPost "/action" @{ action = "setTheme"; value = "light" }

    $newProject = Invoke-AutomationPost "/action" @{
        action = "newProject"
        value = $tempProjectPath
    }
    if (-not $newProject.ok) {
        throw "Could not create the session-restore project."
    }

    $state = Wait-Until -FailureMessage "Session-restore project did not become active." -Condition {
        $latestState = Invoke-AutomationGet "/state"
        if ($latestState.projectPath -eq $tempProjectPath -and -not [string]::IsNullOrWhiteSpace($latestState.activeThreadId)) {
            return $latestState
        }

        return $null
    }

    $workspaceProjectId = $state.projectId
    $threadId = $state.activeThreadId
    $project = Get-ProjectById -State $state -ProjectId $workspaceProjectId
    $thread = Get-ThreadById -Project $project -ThreadId $threadId
    $terminalTab = @($thread.tabs) | Where-Object { $_.kind -eq "terminal" } | Select-Object -First 1
    Wait-ForTerminalReady -TabId $terminalTab.id | Out-Null

    $null = Invoke-AutomationPost "/action" @{ action = "renameThread"; threadId = $threadId; value = $workspaceThreadName }
    Pause-Step 800
    $null = Invoke-AutomationPost "/action" @{ action = "newEditorPane"; value = "notes.txt" }
    Pause-Step 900
    $null = Invoke-AutomationPost "/action" @{ action = "newBrowserPane" }
    Pause-Step 900
    $null = Invoke-AutomationPost "/action" @{ action = "navigateBrowser"; value = "https://example.com" }
    Pause-Step 1600
    $null = Invoke-AutomationPost "/action" @{ action = "refreshDiff" }
    Pause-Step 1200
    $null = Invoke-AutomationPost "/action" @{ action = "selectDiffFile"; value = "notes.txt" }
    Pause-Step 1200
    $null = Invoke-AutomationPost "/action" @{ action = "setLayout"; threadId = $threadId; value = "quad" }
    Pause-Step 1200
    $null = Invoke-AutomationPost "/action" @{ action = "fitVisiblePanes"; threadId = $threadId }
    Pause-Step 1100

    $prepareRecording = Invoke-AutomationPost "/recording/start" @{
        fps = $Fps
        maxDurationMs = 45000
        jpegQuality = 84
        outputDirectory = $prepareDirectory
        keepFrames = [bool]$KeepFrames
    }
    $prepareStarted = $true

    Pause-Step 1400
    $latestState = Invoke-AutomationGet "/state"
    $project = Get-ProjectById -State $latestState -ProjectId $latestState.projectId
    $thread = Get-ThreadById -Project $project -ThreadId $latestState.activeThreadId
    $browserTab = @($thread.tabs) | Where-Object { $_.kind -eq "browser" } | Select-Object -First 1
    $editorTab = @($thread.tabs) | Where-Object { $_.kind -eq "editor" } | Select-Object -First 1
    $diffTab = @($thread.tabs) | Where-Object { $_.kind -eq "diff" } | Select-Object -First 1

    foreach ($tabId in @($browserTab.id, $editorTab.id, $diffTab.id)) {
        if (-not [string]::IsNullOrWhiteSpace($tabId)) {
            $null = Invoke-AutomationPost "/action" @{ action = "selectTab"; tabId = $tabId }
            Pause-Step 900
        }
    }

    $prepareRecordingStop = Invoke-AutomationPost "/recording/stop" $null
    $prepareStarted = $false
    if (-not $prepareRecordingStop.ok) {
        throw "The save-state recording did not stop cleanly."
    }

    $null = Invoke-AutomationPost "/action" @{ action = "saveSession" }
    Pause-Step 600

    Restart-WinMuxForRestore

    $restored = Wait-ForProjectState -ProjectPath $tempProjectPath -ThreadName $workspaceThreadName -MinimumPaneCount 4
    Focus-WinMuxWindow | Out-Null
    Set-WinMuxTopmost -Enabled $true
    Pause-Step 1000

    $restoreRecording = Invoke-AutomationPost "/recording/start" @{
        fps = $Fps
        maxDurationMs = 45000
        jpegQuality = 84
        outputDirectory = $restoreDirectory
        keepFrames = [bool]$KeepFrames
    }
    $restoreStarted = $true

    Pause-Step 1400
    foreach ($tab in @($restored.Thread.tabs)) {
        if (-not [string]::IsNullOrWhiteSpace($tab.id)) {
            $null = Invoke-AutomationPost "/action" @{ action = "selectTab"; tabId = $tab.id }
            Pause-Step 850
        }
    }

    $null = Invoke-AutomationPost "/action" @{ action = "showOverview" }
    Pause-Step 1100
    $null = Invoke-AutomationPost "/action" @{ action = "showTerminal" }
    Pause-Step 1000
}
finally {
    if ($prepareStarted) {
        try {
            $prepareRecordingStop = Invoke-AutomationPost "/recording/stop" $null
        }
        catch {
        }
    }

    if ($restoreStarted) {
        try {
            $restoreRecordingStop = Invoke-AutomationPost "/recording/stop" $null
        }
        catch {
        }
    }

    try {
        $null = Invoke-AutomationPost "/action" @{ action = "setTheme"; value = "dark" }
    }
    catch {
    }

    if (-not [string]::IsNullOrWhiteSpace($workspaceProjectId)) {
        try {
            $null = Invoke-AutomationPost "/action" @{ action = "deleteProject"; projectId = $workspaceProjectId }
        }
        catch {
        }
    }

    if ($originalWindow) {
        try {
            Set-WinMuxTopmost -Enabled $false
            Resize-WinMuxWindow -Width ([int]$originalWindow.width) -Height ([int]$originalWindow.height)
            Move-WinMuxWindow -X ([int]$originalWindow.x) -Y ([int]$originalWindow.y)
            Focus-WinMuxWindow | Out-Null
        }
        catch {
        }
    }
}

if ($null -eq $prepareRecordingStop -or -not $prepareRecordingStop.ok) {
    throw "The save-state recording did not stop cleanly."
}

if ($null -eq $restoreRecordingStop -or -not $restoreRecordingStop.ok) {
    throw "The restored-session recording did not stop cleanly."
}

[pscustomobject]@{
    ok = $true
    outputDirectory = $OutputDirectory
    projectPath = $tempProjectPath
    prepareVideoPath = $prepareRecordingStop.videoPath
    prepareManifestPath = $prepareRecordingStop.manifestPath
    restoreVideoPath = $restoreRecordingStop.videoPath
    restoreManifestPath = $restoreRecordingStop.manifestPath
    prepareFramesRetained = $prepareRecordingStop.framesRetained
    restoreFramesRetained = $restoreRecordingStop.framesRetained
} | ConvertTo-Json -Depth 12
