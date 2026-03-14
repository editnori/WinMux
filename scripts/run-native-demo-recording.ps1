param(
    [int]$Port = 9331,
    [string]$OutputDirectory = "",
    [int]$Fps = 12,
    [switch]$KeepFrames
)

$ErrorActionPreference = "Stop"
$baseUrl = "http://127.0.0.1:$Port"
$repoRoot = Split-Path $PSScriptRoot -Parent

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $OutputDirectory = Join-Path $repoRoot "tmp/automation-captures/winmux-demo-$timestamp"
}

$tempProjectPath = Join-Path $env:TEMP ("winmux-demo-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tempProjectPath -Force | Out-Null
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

function Invoke-AutomationGet {
    param([string]$Path)

    return Invoke-RestMethod -Uri "$baseUrl$Path" -TimeoutSec 15
}

function Invoke-AutomationPost {
    param(
        [string]$Path,
        [object]$Body
    )

    $json = if ($null -eq $Body) { "" } else { $Body | ConvertTo-Json -Depth 20 }
    return Invoke-RestMethod -Method Post -Uri "$baseUrl$Path" -ContentType "application/json" -Body $json -TimeoutSec 20
}

function Pause-Step {
    param([int]$Milliseconds = 900)

    Start-Sleep -Milliseconds $Milliseconds
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

$health = Invoke-AutomationGet "/health"
if (-not $health.ok) {
    throw "Native automation server is not healthy."
}

$null = Invoke-AutomationPost "/desktop-action" @{
    action = "focusWindow"
    titleContains = "WinMux"
}

$null = Invoke-AutomationPost "/action" @{ action = "showTerminal" }
$null = Invoke-AutomationPost "/action" @{ action = "setTheme"; value = "dark" }
$initialState = Invoke-AutomationGet "/state"
$originalProjectId = $initialState.projectId
$originalThreadId = $initialState.activeThreadId
$originalProject = Get-ProjectById -State $initialState -ProjectId $originalProjectId
$originalThread = Get-ThreadById -Project $originalProject -ThreadId $originalThreadId

$recordingStart = Invoke-AutomationPost "/recording/start" @{
    fps = $Fps
    maxDurationMs = 45000
    jpegQuality = 82
    outputDirectory = $OutputDirectory
    keepFrames = [bool]$KeepFrames
}

Pause-Step 1000

$null = Invoke-AutomationPost "/ui-action" @{ action = "click"; automationId = "shell-pane-toggle" }
Pause-Step 800
$null = Invoke-AutomationPost "/ui-action" @{ action = "click"; automationId = "shell-pane-toggle" }
Pause-Step 900

if ($initialState.activeTabId) {
    Wait-ForTerminalReady -TabId $initialState.activeTabId | Out-Null
    $null = Invoke-AutomationPost "/action" @{ action = "input"; value = "pwd`r" }
    Pause-Step 1200
}

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
Pause-Step 900

$demoThreadId = $state.activeThreadId
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
Pause-Step 500
$null = Invoke-AutomationPost "/ui-action" @{ action = "setText"; automationId = "dialog-thread-name"; value = "Demo Thread" }
Pause-Step 400
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
Pause-Step 900

$null = Invoke-AutomationPost "/action" @{ action = "newTab" }
$state = Wait-Until -FailureMessage "New tab did not become active." -Condition {
    $latestState = Invoke-AutomationGet "/state"
    $latestProject = Get-ProjectById -State $latestState -ProjectId $latestState.projectId
    $latestThread = Get-ThreadById -Project $latestProject -ThreadId $demoThreadId
    if (@($latestThread.tabs).Count -ge 2) {
        return $latestState
    }

    return $null
}
$demoProject = Get-ProjectById -State $state -ProjectId $state.projectId
$demoThread = Get-ThreadById -Project $demoProject -ThreadId $demoThreadId
$firstTabId = $demoThread.tabs[0].id
$secondTabId = $demoThread.tabs[1].id

Wait-ForTerminalReady -TabId $state.activeTabId | Out-Null
$null = Invoke-AutomationPost "/action" @{ action = "input"; value = "ls`r" }
Pause-Step 1200
$null = Invoke-AutomationPost "/action" @{ action = "selectTab"; tabId = $firstTabId }
Pause-Step 900
$null = Invoke-AutomationPost "/action" @{ action = "selectTab"; tabId = $secondTabId }
Pause-Step 900
$null = Invoke-AutomationPost "/action" @{ action = "moveTabAfter"; tabId = $firstTabId; targetTabId = $secondTabId }
Pause-Step 1000

$null = Invoke-AutomationPost "/ui-action" @{ action = "click"; automationId = "shell-nav-settings" }
Wait-Until -FailureMessage "Settings view did not open." -Condition {
    $tree = Invoke-AutomationGet "/ui-tree"
    if ($tree.activeView -eq "settings") {
        return $tree
    }

    return $null
} | Out-Null
Pause-Step 1200
$null = Invoke-AutomationPost "/ui-action" @{ action = "click"; automationId = "settings-theme-light" }
Pause-Step 1200
$null = Invoke-AutomationPost "/ui-action" @{ action = "click"; automationId = "settings-shell-powershell" }
Pause-Step 900
$null = Invoke-AutomationPost "/ui-action" @{ action = "click"; automationId = "settings-shell-wsl" }
Pause-Step 900
$null = Invoke-AutomationPost "/ui-action" @{ action = "click"; automationId = "settings-theme-dark" }
Pause-Step 1000
$null = Invoke-AutomationPost "/action" @{ action = "showTerminal" }
Pause-Step 900

$null = Invoke-AutomationPost "/ui-action" @{ action = "rightClick"; automationId = "shell-thread-$demoThreadId" }
Pause-Step 700
$null = Invoke-AutomationPost "/ui-action" @{ action = "invokeMenuItem"; automationId = "shell-thread-$demoThreadId"; menuItemText = "Duplicate" }
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
Pause-Step 400
$null = Invoke-AutomationPost "/ui-action" @{ action = "setText"; automationId = "dialog-thread-name"; value = "Review Thread" }
Pause-Step 400
$null = Invoke-AutomationPost "/ui-action" @{ action = "click"; text = "Save" }
Pause-Step 900

$null = Invoke-AutomationPost "/ui-action" @{ action = "rightClick"; automationId = "shell-thread-$duplicateThreadId" }
Pause-Step 700
$null = Invoke-AutomationPost "/ui-action" @{ action = "invokeMenuItem"; automationId = "shell-thread-$duplicateThreadId"; menuItemText = "Delete" }
$state = Wait-Until -FailureMessage "Duplicate thread did not delete." -Condition {
    $latestState = Invoke-AutomationGet "/state"
    $latestProject = Get-ProjectById -State $latestState -ProjectId $latestState.projectId
    $deletedThread = Get-ThreadById -Project $latestProject -ThreadId $duplicateThreadId
    if ($null -eq $deletedThread) {
        return $latestState
    }

    return $null
}
Pause-Step 900

$null = Invoke-AutomationPost "/ui-action" @{ action = "click"; automationId = "shell-new-project" }
Wait-Until -FailureMessage "New project dialog did not appear." -Condition {
    $tree = Invoke-AutomationGet "/ui-tree"
    if (@($tree.interactiveNodes) | Where-Object { $_.automationId -eq "dialog-project-path" }) {
        return $tree
    }

    return $null
} | Out-Null
Pause-Step 900
$null = Invoke-AutomationPost "/ui-action" @{ action = "setText"; automationId = "dialog-project-path"; value = $tempProjectPath }
Pause-Step 500
$null = Invoke-AutomationPost "/ui-action" @{ action = "setText"; automationId = "dialog-project-shell-profile"; value = "wsl" }
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
Pause-Step 1200

$null = Invoke-AutomationPost "/ui-action" @{
    action = "click"
    automationId = "shell-project-add-thread-$newProjectId"
}
Pause-Step 1000
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
Pause-Step 1000

$recordingStop = Invoke-AutomationPost "/recording/stop" $null

[pscustomobject]@{
    ok = $true
    outputDirectory = $OutputDirectory
    videoPath = $recordingStop.videoPath
    manifestPath = $recordingStop.manifestPath
    keepFrames = $recordingStop.keepFrames
    framesRetained = $recordingStop.framesRetained
    capturedFrames = $recordingStop.capturedFrames
    demoProjectPath = $tempProjectPath
    recordingId = $recordingStop.recordingId
} | ConvertTo-Json -Depth 10
