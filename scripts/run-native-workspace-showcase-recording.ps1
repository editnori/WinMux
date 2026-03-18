param(
    [int]$Port = 9331,
    [string]$OutputDirectory = "",
    [int]$Fps = 16,
    [int]$WindowWidth = 1560,
    [int]$WindowHeight = 1040,
    [switch]$KeepFrames
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "native-automation-client.ps1")
Initialize-WinMuxAutomationClient -Port $Port | Out-Null
$repoRoot = Split-Path $PSScriptRoot -Parent

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $OutputDirectory = Join-Path $repoRoot "tmp/automation-captures/winmux-workspace-showcase-$timestamp"
}

$tempProjectPath = Join-Path $env:TEMP ("winmux-workspace-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

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

function Initialize-WorkspaceGitRepo {
    param([string]$Path)

    New-Item -ItemType Directory -Path $Path -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $Path "src") -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $Path "docs") -Force | Out-Null

    Invoke-Git -C $Path init | Out-Null
    Invoke-Git -C $Path config user.email "winmux-workspace@example.com" | Out-Null
    Invoke-Git -C $Path config user.name "WinMux Workspace" | Out-Null

    @(
        "# WinMux Workspace Showcase",
        "",
        "This repo exists for the cinematic workspace demo."
    ) | Set-Content -Path (Join-Path $Path "README.md")

    @(
        "alpha",
        "beta",
        "gamma"
    ) | Set-Content -Path (Join-Path $Path "notes.txt")

    @(
        "namespace WorkspaceShowcase;",
        "",
        "internal static class Program",
        "{",
        '    public static string Name => "WinMux";',
        "}"
    ) | Set-Content -Path (Join-Path $Path "src/Program.cs")

    @(
        "# Guide",
        "",
        "- open notes.txt",
        "- review the patch",
        "- show the browser pane"
    ) | Set-Content -Path (Join-Path $Path "docs/guide.md")

    Invoke-Git -C $Path add README.md notes.txt src/Program.cs docs/guide.md | Out-Null
    Invoke-Git -C $Path commit -m "Initial workspace snapshot" | Out-Null

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

function Wait-ForEditorPane {
    param(
        [string]$ThreadId,
        [string]$ExpectedPath
    )

    return Wait-Until -FailureMessage "Editor pane for '$ExpectedPath' did not become ready." -Condition {
        $latestState = Invoke-AutomationGet "/state"
        $latestProject = Get-ProjectById -State $latestState -ProjectId $latestState.projectId
        $latestThread = Get-ThreadById -Project $latestProject -ThreadId $ThreadId
        $editorTab = @($latestThread.tabs) | Where-Object { $_.kind -eq "editor" } | Select-Object -First 1
        if ($null -eq $editorTab) {
            return $null
        }

        $editorState = Invoke-AutomationPost "/editor-state" @{ paneId = $editorTab.id; maxChars = 2000; maxFiles = 20 }
        $pane = @($editorState.panes) | Where-Object { $_.paneId -eq $editorTab.id } | Select-Object -First 1
        if ($null -eq $pane) {
            return $null
        }

        if ($pane.selectedPath -eq $ExpectedPath) {
            return [pscustomobject]@{
                State = $latestState
                EditorTab = $editorTab
            }
        }

        return $null
    } -Attempts 40 -DelayMilliseconds 300
}

function Wait-ForBrowserPane {
    param([string]$ThreadId)

    return Wait-Until -FailureMessage "Browser pane did not become ready." -Condition {
        $latestState = Invoke-AutomationGet "/state"
        $latestProject = Get-ProjectById -State $latestState -ProjectId $latestState.projectId
        $latestThread = Get-ThreadById -Project $latestProject -ThreadId $ThreadId
        $browserTab = @($latestThread.tabs) | Where-Object { $_.kind -eq "browser" } | Select-Object -First 1
        if ($null -eq $browserTab) {
            return $null
        }

        $browserState = Invoke-AutomationPost "/browser-state" @{ paneId = $browserTab.id }
        $pane = @($browserState.panes) | Where-Object { $_.paneId -eq $browserTab.id } | Select-Object -First 1
        if ($null -eq $pane) {
            return $null
        }

        return [pscustomobject]@{
            State = $latestState
            BrowserTab = $browserTab
        }
    } -Attempts 40 -DelayMilliseconds 300
}

$recordingStop = $null
$originalWindow = $null
$recordingStarted = $false
$workspaceProjectId = $null
$workspaceThreadId = $null

try {
    $health = Invoke-AutomationGet "/health"
    if (-not $health.ok) {
        throw "Native automation server is not healthy."
    }

    Initialize-WorkspaceGitRepo -Path $tempProjectPath

    $originalWindow = Get-WinMuxWindow
    Focus-WinMuxWindow | Out-Null
    Set-WinMuxTopmost -Enabled $true
    Center-WinMuxWindow
    Resize-WinMuxWindow -Width $WindowWidth -Height $WindowHeight
    Center-WinMuxWindow
    Focus-WinMuxWindow | Out-Null
    Pause-Step 900

    $null = Invoke-AutomationPost "/action" @{ action = "showTerminal" }
    $null = Invoke-AutomationPost "/action" @{ action = "setTheme"; value = "light" }

    $newProject = Invoke-AutomationPost "/action" @{
        action = "newProject"
        value = $tempProjectPath
    }
    if (-not $newProject.ok) {
        throw "Could not open the workspace showcase project."
    }

    $state = Wait-Until -FailureMessage "Workspace showcase project did not become active." -Condition {
        $latestState = Invoke-AutomationGet "/state"
        if ($latestState.projectPath -eq $tempProjectPath -and -not [string]::IsNullOrWhiteSpace($latestState.activeThreadId)) {
            return $latestState
        }

        return $null
    }

    $workspaceProjectId = $state.projectId
    $workspaceThreadId = $state.activeThreadId
    $workspaceProject = Get-ProjectById -State $state -ProjectId $workspaceProjectId
    $workspaceThread = Get-ThreadById -Project $workspaceProject -ThreadId $workspaceThreadId
    $terminalTab = @($workspaceThread.tabs) | Where-Object { $_.kind -eq "terminal" } | Select-Object -First 1

    $recordingStart = Invoke-AutomationPost "/recording/start" @{
        fps = $Fps
        maxDurationMs = 95000
        jpegQuality = 84
        outputDirectory = $OutputDirectory
        keepFrames = [bool]$KeepFrames
    }
    $recordingStarted = $true

    Pause-Step 1200
    Wait-ForTerminalReady -TabId $terminalTab.id | Out-Null
    $null = Invoke-AutomationPost "/action" @{ action = "input"; value = "pwd`r" }
    Pause-Step 1100
    $null = Invoke-AutomationPost "/action" @{ action = "input"; value = "git status --short`r" }
    Pause-Step 1500

    $null = Invoke-AutomationPost "/action" @{ action = "newEditorPane"; value = "notes.txt" }
    $editorReady = Wait-ForEditorPane -ThreadId $workspaceThreadId -ExpectedPath "notes.txt"
    $editorTabId = $editorReady.EditorTab.id
    Pause-Step 1000
    $null = Invoke-AutomationPost "/action" @{ action = "renamePane"; tabId = $editorTabId; value = "Notes editor" }
    Pause-Step 1000

    $null = Invoke-AutomationPost "/action" @{ action = "newBrowserPane" }
    $browserReady = Wait-ForBrowserPane -ThreadId $workspaceThreadId
    $browserTabId = $browserReady.BrowserTab.id
    Pause-Step 900
    $null = Invoke-AutomationPost "/events/clear" $null
    $null = Invoke-AutomationPost "/action" @{ action = "navigateBrowser"; value = "https://example.com" }
    Wait-Until -FailureMessage "Browser navigation did not complete." -Condition {
        $events = Invoke-AutomationGet "/events"
        if (@($events.events) | Where-Object { $_.category -eq "browser" -and $_.name -eq "navigate.completed" -and $_.data.uri -eq "https://example.com/" -and $_.data.success -eq "True" }) {
            return $true
        }

        return $null
    } -Attempts 40 -DelayMilliseconds 300 | Out-Null
    Pause-Step 1200

    $null = Invoke-AutomationPost "/action" @{ action = "refreshDiff" }
    $diffSnapshotState = Wait-Until -FailureMessage "Git diff state did not refresh for the workspace showcase project." -Condition {
        $latestState = Invoke-AutomationGet "/state"
        $latestProject = Get-ProjectById -State $latestState -ProjectId $workspaceProjectId
        $latestThread = if ($null -eq $latestProject) { $null } else { Get-ThreadById -Project $latestProject -ThreadId $workspaceThreadId }
        if ($null -eq $latestThread) {
            return $null
        }

        if ($latestState.projectId -eq $workspaceProjectId -and
            $latestThread.changedFileCount -ge 1 -and
            -not [string]::IsNullOrWhiteSpace($latestThread.selectedDiffPath)) {
            return [pscustomobject]@{
                State = $latestState
                Thread = $latestThread
            }
        }

        return $null
    } -Attempts 50 -DelayMilliseconds 300

    $diffPath = if (-not [string]::IsNullOrWhiteSpace($diffSnapshotState.Thread.selectedDiffPath)) {
        [string]$diffSnapshotState.Thread.selectedDiffPath
    } else {
        "notes.txt"
    }

    $null = Invoke-AutomationPost "/action" @{ action = "selectDiffFile"; value = $diffPath }
    $diffSelection = Wait-Until -FailureMessage "The workspace showcase diff pane did not appear." -Condition {
        $latestState = Invoke-AutomationGet "/state"
        $latestProject = Get-ProjectById -State $latestState -ProjectId $workspaceProjectId
        $latestThread = if ($null -eq $latestProject) { $null } else { Get-ThreadById -Project $latestProject -ThreadId $workspaceThreadId }
        if ($null -eq $latestThread) {
            return $null
        }

        $diffTab = @($latestThread.tabs) | Where-Object { $_.kind -eq "diff" } | Select-Object -First 1
        if ($null -eq $diffTab -or $latestThread.selectedDiffPath -ne $diffPath) {
            return $null
        }

        $diffState = Invoke-AutomationPost "/diff-state" @{ paneId = $diffTab.id; maxLines = 20 }
        $diffPane = @($diffState.panes) | Where-Object { $_.paneId -eq $diffTab.id } | Select-Object -First 1
        if ($null -eq $diffPane) {
            return $null
        }

        if ($diffPane.path -eq $diffPath -and $diffPane.hasDiff -eq $true) {
            return [pscustomobject]@{
                State = $latestState
                Thread = $latestThread
                DiffTab = $diffTab
            }
        }

        return $null
    } -Attempts 45 -DelayMilliseconds 300

    $diffTabId = $diffSelection.DiffTab.id
    Pause-Step 1200

    $null = Invoke-AutomationPost "/action" @{ action = "setLayout"; threadId = $workspaceThreadId; value = "quad" }
    Pause-Step 1100
    $null = Invoke-AutomationPost "/action" @{ action = "fitVisiblePanes"; threadId = $workspaceThreadId }
    Pause-Step 1200
    $null = Invoke-AutomationPost "/action" @{ action = "toggleFitVisiblePanesLock"; threadId = $workspaceThreadId }
    Pause-Step 1200

    $null = Invoke-AutomationPost "/action" @{ action = "selectTab"; tabId = $editorTabId }
    Pause-Step 900
    $null = Invoke-AutomationPost "/action" @{ action = "togglePaneZoom"; threadId = $workspaceThreadId; tabId = $editorTabId }
    Pause-Step 1400
    $null = Invoke-AutomationPost "/action" @{ action = "togglePaneZoom"; threadId = $workspaceThreadId; tabId = $editorTabId }
    Pause-Step 1000

    $branchWorktree = Join-Path $tempProjectPath "branch-worktree"
    New-Item -ItemType Directory -Force -Path $branchWorktree | Out-Null
    $null = Invoke-AutomationPost "/action" @{ action = "setThreadWorktree"; threadId = $workspaceThreadId; value = $branchWorktree }
    Pause-Step 900

    $null = Invoke-AutomationPost "/action" @{ action = "newTab" }
    $overflowState = Wait-Until -FailureMessage "Overflow thread did not appear after opening a fifth pane." -Condition {
        $latestState = Invoke-AutomationGet "/state"
        if ($latestState.projectId -eq $workspaceProjectId -and $latestState.activeThreadId -ne $workspaceThreadId) {
            return $latestState
        }

        return $null
    } -Attempts 40 -DelayMilliseconds 300
    $overflowThreadId = $overflowState.activeThreadId
    Pause-Step 900
    Wait-ForTerminalReady -TabId $overflowState.activeTabId | Out-Null
    $null = Invoke-AutomationPost "/action" @{ action = "input"; value = "pwd`r" }
    Pause-Step 1400
    $null = Invoke-AutomationPost "/action" @{ action = "renameThread"; threadId = $overflowThreadId; value = "Overflow Thread" }
    Pause-Step 900

    $null = Invoke-AutomationPost "/action" @{ action = "selectThread"; threadId = $workspaceThreadId }
    Pause-Step 900
    $null = Invoke-AutomationPost "/action" @{ action = "showSettings" }
    Pause-Step 1300
    $null = Invoke-AutomationPost "/action" @{ action = "showTerminal" }
    Pause-Step 1000

    $null = Invoke-AutomationPost "/action" @{ action = "setTheme"; value = "dark" }
    Pause-Step 1100
    $null = Invoke-AutomationPost "/action" @{ action = "setTheme"; value = "light" }
    Pause-Step 1200
}
finally {
    if ($recordingStarted) {
        try {
            $recordingStop = Invoke-AutomationPost "/recording/stop" $null
        }
        catch {
        }
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

if ($null -eq $recordingStop -or -not $recordingStop.ok) {
    throw "The workspace showcase recording did not stop cleanly."
}

[pscustomobject]@{
    ok = $true
    outputDirectory = $OutputDirectory
    videoPath = $recordingStop.videoPath
    manifestPath = $recordingStop.manifestPath
    keepFrames = $recordingStop.keepFrames
    framesRetained = $recordingStop.framesRetained
    capturedFrames = $recordingStop.capturedFrames
    projectPath = $tempProjectPath
    threadId = $workspaceThreadId
} | ConvertTo-Json -Depth 12
