param(
    [int]$Port = 9331,
    [string]$OutputDirectory = "",
    [ValidateSet("standard", "cinematic")]
    [string]$Mode = "standard",
    [int]$Fps = 0,
    [int]$WindowWidth = 0,
    [int]$WindowHeight = 0,
    [switch]$KeepFrames
)

$ErrorActionPreference = "Stop"
$baseUrl = "http://127.0.0.1:$Port"
$repoRoot = Split-Path $PSScriptRoot -Parent
$delayMultiplier = if ($Mode -eq "cinematic") { 1.85 } else { 1.0 }
$defaultFps = if ($Fps -gt 0) { $Fps } elseif ($Mode -eq "cinematic") { 16 } else { 12 }
$targetWindowWidth = if ($WindowWidth -gt 0) { $WindowWidth } elseif ($Mode -eq "cinematic") { 1560 } else { 1240 }
$targetWindowHeight = if ($WindowHeight -gt 0) { $WindowHeight } elseif ($Mode -eq "cinematic") { 1040 } else { 860 }

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $OutputDirectory = Join-Path $repoRoot "tmp/automation-captures/winmux-demo-$Mode-$timestamp"
}

$tempProjectPath = Join-Path $env:TEMP ("winmux-demo-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tempProjectPath -Force | Out-Null
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

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

    $delay = [int][Math]::Round($Milliseconds * $delayMultiplier)
    Start-Sleep -Milliseconds ([Math]::Max(80, $delay))
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

function Wait-ForTerminalReady {
    param([string]$TabId)

    return Wait-Until -FailureMessage "Terminal tab '$TabId' was not ready." -Condition {
        $terminalState = Invoke-AutomationPost "/terminal-state" @{ tabId = $TabId }
        $snapshot = @($terminalState.tabs) | Where-Object { $_.tabId -eq $TabId } | Select-Object -First 1
        if ($null -ne $snapshot -and $snapshot.rendererReady -eq $true -and $snapshot.started -eq $true) {
            return $snapshot
        }

        return $null
    }
}

function Get-WinMuxWindow {
    $desktopWindows = Invoke-AutomationGet "/desktop-windows"
    return @($desktopWindows.windows) | Where-Object { $_.title -like "*WinMux*" } | Select-Object -First 1
}

function Focus-WinMuxWindow {
    function Test-Focused {
        for ($poll = 0; $poll -lt 6; $poll++) {
            $window = Get-WinMuxWindow
            if ($null -ne $window -and $window.focused -eq $true) {
                return $window
            }

            Start-Sleep -Milliseconds 140
        }

        return $null
    }

    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        $null = Invoke-AutomationPost "/desktop-action" @{
            action = "focusWindow"
            titleContains = "WinMux"
        }

        $focusedWindow = Test-Focused
        if ($focusedWindow) {
            return $focusedWindow
        }

        $null = Invoke-AutomationPost "/desktop-action" @{
            action = "clickPoint"
            titleContains = "WinMux"
        }

        $focusedWindow = Test-Focused
        if ($focusedWindow) {
            return $focusedWindow
        }
    }

    throw "WinMux did not become the focused window."
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

function Show-Hover {
    param(
        [string]$AutomationId,
        [int]$PauseMs = 450
    )

    try {
        $null = Invoke-AutomationPost "/ui-action" @{
            action = "hover"
            automationId = $AutomationId
        }
    }
    catch {
        return
    }

    Pause-Step $PauseMs
    try {
        $null = Invoke-AutomationPost "/ui-action" @{
            action = "normalState"
            automationId = $AutomationId
        }
    }
    catch {
    }
}

function Show-ContextMenu {
    param(
        [string]$AutomationId,
        [int]$PauseMs = 700
    )

    $null = Invoke-AutomationPost "/ui-action" @{
        action = "rightClick"
        automationId = $AutomationId
    }
    Pause-Step $PauseMs
}

function Set-TextDialogValue {
    param(
        [string]$AutomationId,
        [string]$Value
    )

    $null = Invoke-AutomationPost "/ui-action" @{
        action = "setText"
        automationId = $AutomationId
        value = $Value
    }
}

$recordingStop = $null
$originalWindow = $null
$recordingStarted = $false

try {
    $health = Invoke-AutomationGet "/health"
    if (-not $health.ok) {
        throw "Native automation server is not healthy."
    }

    $originalWindow = Get-WinMuxWindow
    Focus-WinMuxWindow | Out-Null

    Resize-WinMuxWindow -Width $targetWindowWidth -Height $targetWindowHeight
    Pause-Step 1200
    Focus-WinMuxWindow | Out-Null

    $null = Invoke-AutomationPost "/action" @{ action = "showTerminal" }
    $null = Invoke-AutomationPost "/action" @{ action = "setTheme"; value = "dark" }

    $initialState = Invoke-AutomationGet "/state"
    $originalProjectId = $initialState.projectId
    $originalThreadId = $initialState.activeThreadId
    $originalProject = Get-ProjectById -State $initialState -ProjectId $originalProjectId

    $recordingStart = Invoke-AutomationPost "/recording/start" @{
        fps = $defaultFps
        maxDurationMs = if ($Mode -eq "cinematic") { 90000 } else { 60000 }
        jpegQuality = 84
        outputDirectory = $OutputDirectory
        keepFrames = [bool]$KeepFrames
    }
    $recordingStarted = $true

    Pause-Step 1100
    Focus-WinMuxWindow | Out-Null
    Show-Hover -AutomationId "shell-pane-toggle" -PauseMs 500
    $null = Invoke-AutomationPost "/ui-action" @{ action = "click"; automationId = "shell-pane-toggle" }
    Pause-Step 1100
    $null = Invoke-AutomationPost "/ui-action" @{ action = "click"; automationId = "shell-pane-toggle" }
    Pause-Step 1000

    if ($initialState.activeTabId) {
        Focus-WinMuxWindow | Out-Null
        Wait-ForTerminalReady -TabId $initialState.activeTabId | Out-Null
        $null = Invoke-AutomationPost "/action" @{ action = "input"; value = "pwd`r" }
        Pause-Step 1200
        $null = Invoke-AutomationPost "/action" @{ action = "input"; value = "ls`r" }
        Pause-Step 1300
    }

    Show-Hover -AutomationId "shell-project-$originalProjectId" -PauseMs 550
    Show-Hover -AutomationId "shell-project-add-thread-$originalProjectId" -PauseMs 550
    Focus-WinMuxWindow | Out-Null
    $null = Invoke-AutomationPost "/ui-action" @{
        action = "click"
        automationId = "shell-project-add-thread-$originalProjectId"
    }
    $state = Wait-Until -FailureMessage "New thread did not become active." -Condition {
        $latestState = Invoke-AutomationGet "/state"
        if ($latestState.activeThreadId -ne $originalThreadId) {
            return $latestState
        }

        return $null
    }
    $demoThreadId = $state.activeThreadId
    Pause-Step 950

    Show-Hover -AutomationId "shell-thread-$demoThreadId" -PauseMs 650
    Focus-WinMuxWindow | Out-Null
    $null = Invoke-AutomationPost "/ui-action" @{
        action = "doubleClick"
        automationId = "shell-thread-$demoThreadId"
    }
    Wait-Until -FailureMessage "Rename thread dialog did not appear." -Condition {
        $tree = Invoke-AutomationGet "/ui-tree"
        if (@($tree.interactiveNodes) | Where-Object { $_.automationId -eq "dialog-thread-name" }) {
            return $tree
        }

        return $null
    } | Out-Null
    Pause-Step 600
    Set-TextDialogValue -AutomationId "dialog-thread-name" -Value "Demo Thread"
    Pause-Step 550
    $null = Invoke-AutomationPost "/ui-action" @{ action = "click"; text = "Save" }
    $state = Wait-Until -FailureMessage "Demo thread rename did not persist." -Condition {
        $latestState = Invoke-AutomationGet "/state"
        $latestProject = Get-ProjectById -State $latestState -ProjectId $latestState.projectId
        $latestThread = Get-ThreadById -Project $latestProject -ThreadId $demoThreadId
        if ($latestThread.name -eq "Demo Thread") {
            return $latestState
        }

        return $null
    }
    Pause-Step 1000

    $null = Invoke-AutomationPost "/action" @{ action = "newTab" }
    Pause-Step 900
    $null = Invoke-AutomationPost "/action" @{ action = "newTab" }
    $state = Wait-Until -FailureMessage "New tabs did not appear." -Condition {
        $latestState = Invoke-AutomationGet "/state"
        $latestProject = Get-ProjectById -State $latestState -ProjectId $latestState.projectId
        $latestThread = Get-ThreadById -Project $latestProject -ThreadId $demoThreadId
        if (@($latestThread.tabs).Count -ge 3) {
            return $latestState
        }

        return $null
    }
    $demoProject = Get-ProjectById -State $state -ProjectId $state.projectId
    $demoThread = Get-ThreadById -Project $demoProject -ThreadId $demoThreadId
    $firstTabId = $demoThread.tabs[0].id
    $secondTabId = $demoThread.tabs[1].id
    $thirdTabId = $demoThread.tabs[2].id

    Focus-WinMuxWindow | Out-Null
    Wait-ForTerminalReady -TabId $state.activeTabId | Out-Null
    $null = Invoke-AutomationPost "/action" @{ action = "input"; value = "echo demo walkthrough`r" }
    Pause-Step 1200
    $null = Invoke-AutomationPost "/action" @{ action = "selectTab"; tabId = $firstTabId }
    Pause-Step 850
    $null = Invoke-AutomationPost "/action" @{ action = "selectTab"; tabId = $secondTabId }
    Pause-Step 850
    $null = Invoke-AutomationPost "/action" @{ action = "selectTab"; tabId = $thirdTabId }
    Pause-Step 850
    $null = Invoke-AutomationPost "/action" @{ action = "moveTabAfter"; tabId = $firstTabId; targetTabId = $thirdTabId }
    Pause-Step 950
    $null = Invoke-AutomationPost "/action" @{ action = "closeTab"; tabId = $secondTabId }
    Pause-Step 950

    Show-Hover -AutomationId "shell-nav-settings" -PauseMs 650
    Focus-WinMuxWindow | Out-Null
    $null = Invoke-AutomationPost "/ui-action" @{ action = "click"; automationId = "shell-nav-settings" }
    Wait-Until -FailureMessage "Settings view did not open." -Condition {
        $tree = Invoke-AutomationGet "/ui-tree"
        if ($tree.activeView -eq "settings") {
            return $tree
        }

        return $null
    } | Out-Null
    Pause-Step 1200
    Show-Hover -AutomationId "settings-theme-light" -PauseMs 700
    $null = Invoke-AutomationPost "/ui-action" @{ action = "click"; automationId = "settings-theme-light" }
    Pause-Step 1300
    $null = Invoke-AutomationPost "/ui-action" @{ action = "click"; automationId = "settings-shell-powershell" }
    Pause-Step 1000
    $null = Invoke-AutomationPost "/ui-action" @{ action = "click"; automationId = "settings-shell-wsl" }
    Pause-Step 1000
    $null = Invoke-AutomationPost "/ui-action" @{ action = "click"; automationId = "settings-theme-dark" }
    Pause-Step 1200
    $null = Invoke-AutomationPost "/action" @{ action = "showTerminal" }
    Pause-Step 1000

    Focus-WinMuxWindow | Out-Null
    Show-ContextMenu -AutomationId "shell-thread-$demoThreadId" -PauseMs 850
    $null = Invoke-AutomationPost "/ui-action" @{
        action = "invokeMenuItem"
        automationId = "shell-thread-$demoThreadId"
        menuItemText = "Duplicate"
    }
    $state = Wait-Until -FailureMessage "Thread duplicate did not become active." -Condition {
        $latestState = Invoke-AutomationGet "/state"
        if ($latestState.activeThreadId -ne $demoThreadId) {
            return $latestState
        }

        return $null
    }
    $duplicateThreadId = $state.activeThreadId
    Pause-Step 900

    $null = Invoke-AutomationPost "/ui-action" @{ action = "doubleClick"; automationId = "shell-thread-$duplicateThreadId" }
    Wait-Until -FailureMessage "Duplicate rename dialog did not appear." -Condition {
        $tree = Invoke-AutomationGet "/ui-tree"
        if (@($tree.interactiveNodes) | Where-Object { $_.automationId -eq "dialog-thread-name" }) {
            return $tree
        }

        return $null
    } | Out-Null
    Pause-Step 500
    Set-TextDialogValue -AutomationId "dialog-thread-name" -Value "Review Thread"
    Pause-Step 450
    $null = Invoke-AutomationPost "/ui-action" @{ action = "click"; text = "Save" }
    Pause-Step 950

    Show-ContextMenu -AutomationId "shell-thread-$duplicateThreadId" -PauseMs 800
    $null = Invoke-AutomationPost "/ui-action" @{
        action = "invokeMenuItem"
        automationId = "shell-thread-$duplicateThreadId"
        menuItemText = "Delete"
    }
    $state = Wait-Until -FailureMessage "Duplicate thread did not delete." -Condition {
        $latestState = Invoke-AutomationGet "/state"
        $latestProject = Get-ProjectById -State $latestState -ProjectId $latestState.projectId
        $deletedThread = Get-ThreadById -Project $latestProject -ThreadId $duplicateThreadId
        if ($null -eq $deletedThread) {
            return $latestState
        }

        return $null
    }
    Pause-Step 1000

    Focus-WinMuxWindow | Out-Null
    Show-Hover -AutomationId "shell-new-project" -PauseMs 700
    $null = Invoke-AutomationPost "/ui-action" @{ action = "click"; automationId = "shell-new-project" }
    Wait-Until -FailureMessage "New project dialog did not appear." -Condition {
        $tree = Invoke-AutomationGet "/ui-tree"
        if (@($tree.interactiveNodes) | Where-Object { $_.automationId -eq "dialog-project-path" }) {
            return $tree
        }

        return $null
    } | Out-Null
    Pause-Step 900
    Set-TextDialogValue -AutomationId "dialog-project-path" -Value $tempProjectPath
    Pause-Step 500
    Set-TextDialogValue -AutomationId "dialog-project-shell-profile" -Value "wsl"
    Pause-Step 500
    $null = Invoke-AutomationPost "/ui-action" @{ action = "click"; text = "Add project" }
    $state = Wait-Until -FailureMessage "New project did not become active." -Condition {
        $latestState = Invoke-AutomationGet "/state"
        if ($latestState.projectPath -eq $tempProjectPath) {
            return $latestState
        }

        return $null
    }
    $newProjectId = $state.projectId
    Pause-Step 1250

    $newProjectThreadId = $state.activeThreadId
    $null = Invoke-AutomationPost "/action" @{ action = "deleteThread"; threadId = $newProjectThreadId }
    $state = Wait-Until -FailureMessage "New project did not enter empty-thread state." -Condition {
        $latestState = Invoke-AutomationGet "/state"
        if ($latestState.projectId -eq $newProjectId) {
            $latestProject = Get-ProjectById -State $latestState -ProjectId $newProjectId
            if (@($latestProject.threads).Count -eq 0) {
                return $latestState
            }
        }

        return $null
    }
    Pause-Step 1100

    $null = Invoke-AutomationPost "/ui-action" @{
        action = "click"
        automationId = "shell-project-add-thread-$newProjectId"
    }
    $state = Wait-Until -FailureMessage "Empty project did not recover with a new thread." -Condition {
        $latestState = Invoke-AutomationGet "/state"
        if ($latestState.projectId -eq $newProjectId) {
            $latestProject = Get-ProjectById -State $latestState -ProjectId $newProjectId
            if (@($latestProject.threads).Count -ge 1) {
                return $latestState
            }
        }

        return $null
    }
    Pause-Step 1050

    $null = Invoke-AutomationPost "/ui-action" @{
        action = "click"
        automationId = "shell-project-$originalProjectId"
    }
    $state = Wait-Until -FailureMessage "Original project was not restored." -Condition {
        $latestState = Invoke-AutomationGet "/state"
        if ($latestState.projectId -eq $originalProjectId) {
            return $latestState
        }

        return $null
    }
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

    if ($originalWindow) {
        try {
            Resize-WinMuxWindow -Width ([int]$originalWindow.width) -Height ([int]$originalWindow.height)
            Focus-WinMuxWindow | Out-Null
        }
        catch {
        }
    }
}

if ($null -eq $recordingStop -or -not $recordingStop.ok) {
    throw "The demo recording did not stop cleanly."
}

[pscustomobject]@{
    ok = $true
    mode = $Mode
    outputDirectory = $OutputDirectory
    videoPath = $recordingStop.videoPath
    manifestPath = $recordingStop.manifestPath
    keepFrames = $recordingStop.keepFrames
    framesRetained = $recordingStop.framesRetained
    capturedFrames = $recordingStop.capturedFrames
    demoProjectPath = $tempProjectPath
    recordingId = $recordingStop.recordingId
    targetWindowWidth = $targetWindowWidth
    targetWindowHeight = $targetWindowHeight
    fps = $defaultFps
} | ConvertTo-Json -Depth 12
