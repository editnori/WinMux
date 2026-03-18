param(
    [int]$Port = 9331,
    [string]$OutputDirectory = "",
    [string]$ProjectPath = "",
    [string]$ShellProfileId = "wsl",
    [int]$Fps = 12,
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
    $OutputDirectory = Join-Path $repoRoot "tmp/automation-captures/winmux-new-project-$timestamp"
}

$tempProjectPath = if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
    Join-Path $env:TEMP ("winmux-project-" + [Guid]::NewGuid().ToString("N"))
}
else {
    $ProjectPath
}

if ([string]::IsNullOrWhiteSpace($ProjectPath) -and (Test-Path $tempProjectPath)) {
    Remove-Item -Recurse -Force $tempProjectPath
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

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

function Find-InteractiveNode {
    param(
        [object]$Tree,
        [string]$AutomationId
    )

    return @($Tree.interactiveNodes) | Where-Object { $_.automationId -eq $AutomationId } | Select-Object -First 1
}

function Find-UiNode {
    param(
        [object]$Node,
        [string]$AutomationId
    )

    if ($null -eq $Node) {
        return $null
    }

    if ($Node.automationId -eq $AutomationId) {
        return $Node
    }

    foreach ($child in @($Node.children)) {
        $match = Find-UiNode -Node $child -AutomationId $AutomationId
        if ($null -ne $match) {
            return $match
        }
    }

    return $null
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

$recordingStop = $null
$originalWindow = $null
$recordingStarted = $false

try {
    $health = Invoke-AutomationGet "/health"
    if (-not $health.ok) {
        throw "Native automation server is not healthy."
    }

    $null = Invoke-AutomationPost "/events/clear" $null

    $originalWindow = Get-WinMuxWindow
    Focus-WinMuxWindow | Out-Null
    Set-WinMuxTopmost -Enabled $true
    Center-WinMuxWindow
    Resize-WinMuxWindow -Width $WindowWidth -Height $WindowHeight
    Center-WinMuxWindow
    Focus-WinMuxWindow | Out-Null
    Pause-Step 700

    $null = Invoke-AutomationPost "/recording/start" @{
        fps = $Fps
        maxDurationMs = 20000
        jpegQuality = 84
        outputDirectory = $OutputDirectory
        keepFrames = $KeepFrames.IsPresent
    }
    $recordingStarted = $true

    Pause-Step 700
    $null = Invoke-AutomationPost "/ui-action" @{ action = "click"; automationId = "shell-new-project" }
    $dialogTree = Wait-Until -FailureMessage "New project dialog did not appear." -Condition {
        $tree = Invoke-AutomationGet "/ui-tree"
        if (Find-InteractiveNode -Tree $tree -AutomationId "dialog-project-path") {
            return $tree
        }

        return $null
    }

    $dialogBefore = Find-UiNode -Node $dialogTree.root -AutomationId "dialog-project-body"
    Pause-Step 500
    $null = Invoke-AutomationPost "/ui-action" @{
        action = "setText"
        automationId = "dialog-project-path"
        value = $tempProjectPath
    }
    Pause-Step 350
    $null = Invoke-AutomationPost "/ui-action" @{
        action = "setText"
        automationId = "dialog-project-shell-profile"
        value = $ShellProfileId
    }
    Pause-Step 500

    $dialogTreeAfter = Invoke-AutomationGet "/ui-tree"
    $dialogAfter = Find-UiNode -Node $dialogTreeAfter.root -AutomationId "dialog-project-body"
    $previewNode = Find-UiNode -Node $dialogTreeAfter.root -AutomationId "dialog-project-preview"
    $helperNode = Find-UiNode -Node $dialogTreeAfter.root -AutomationId "dialog-project-helper"

    $null = Invoke-AutomationPost "/ui-action" @{ action = "click"; text = "Add project" }
    $state = Wait-Until -FailureMessage "New project did not become active." -Condition {
        $latestState = Invoke-AutomationGet "/state"
        if ($latestState.projectPath -eq $tempProjectPath) {
            return $latestState
        }

        return $null
    } -Attempts 60 -DelayMilliseconds 250

    $terminalSnapshot = Wait-ForTerminalReady -TabId $state.activeTabId
    Pause-Step 900
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
    throw "The new-project recording did not stop cleanly."
}

$events = Invoke-AutomationGet "/events"
$matchingEvents = @($events.events | Where-Object {
    ($_.message -like "*CreateProcessCommon*") -or
    ($_.message -like "*790*")
})

[pscustomobject]@{
    ok = $true
    outputDirectory = $OutputDirectory
    videoPath = $recordingStop.videoPath
    manifestPath = $recordingStop.manifestPath
    keepFrames = $recordingStop.keepFrames
    framesRetained = $recordingStop.framesRetained
    capturedFrames = $recordingStop.capturedFrames
    newProjectPath = $tempProjectPath
    dialogWidthBefore = $dialogBefore.width
    dialogWidthAfter = $dialogAfter.width
    previewText = if ([string]::IsNullOrWhiteSpace($previewNode.text)) { $previewNode.name } else { $previewNode.text }
    helperText = if ([string]::IsNullOrWhiteSpace($helperNode.text)) { $helperNode.name } else { $helperNode.text }
    activeProjectPath = $state.projectPath
    activeTabId = $state.activeTabId
    terminalTitle = $terminalSnapshot.title
    terminalBufferTail = $terminalSnapshot.bufferTail
    errorEvents = $matchingEvents.Count
} | ConvertTo-Json -Depth 12
