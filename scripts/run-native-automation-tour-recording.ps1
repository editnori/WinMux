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
    $OutputDirectory = Join-Path $repoRoot "tmp/automation-captures/winmux-automation-tour-$timestamp"
}

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

$recordingStop = $null
$recordingStarted = $false
$originalWindow = $null

try {
    $health = Invoke-AutomationGet "/health"
    if (-not $health.ok) {
        throw "Native automation server is not healthy."
    }

    $state = Invoke-AutomationGet "/state"
    $project = @($state.projects) | Where-Object { $_.id -eq $state.projectId } | Select-Object -First 1
    $thread = @($project.threads) | Where-Object { $_.id -eq $state.activeThreadId } | Select-Object -First 1
    $terminalTab = @($thread.tabs) | Where-Object { $_.kind -eq "terminal" } | Select-Object -First 1
    if ($null -eq $terminalTab) {
        $newTab = Invoke-AutomationPost "/action" @{ action = "newTab" }
        if (-not $newTab.ok) {
            throw "Could not create a terminal pane for the automation tour."
        }

        $state = Invoke-AutomationGet "/state"
        $project = @($state.projects) | Where-Object { $_.id -eq $state.projectId } | Select-Object -First 1
        $thread = @($project.threads) | Where-Object { $_.id -eq $state.activeThreadId } | Select-Object -First 1
        $terminalTab = @($thread.tabs) | Where-Object { $_.kind -eq "terminal" } | Select-Object -First 1
    }
    else {
        $null = Invoke-AutomationPost "/action" @{ action = "selectTab"; tabId = $terminalTab.id }
    }

    $originalWindow = Get-WinMuxWindow
    Focus-WinMuxWindow | Out-Null
    Set-WinMuxTopmost -Enabled $true
    Center-WinMuxWindow
    Resize-WinMuxWindow -Width $WindowWidth -Height $WindowHeight
    Center-WinMuxWindow
    Focus-WinMuxWindow | Out-Null
    Pause-Step 800

    $null = Invoke-AutomationPost "/action" @{ action = "setTheme"; value = "light" }
    Pause-Step 900
    Wait-ForTerminalReady -TabId $terminalTab.id | Out-Null

    $recordingStart = Invoke-AutomationPost "/recording/start" @{
        fps = $Fps
        maxDurationMs = 70000
        jpegQuality = 84
        outputDirectory = $OutputDirectory
        keepFrames = [bool]$KeepFrames
    }
    $recordingStarted = $true

    $commands = @(
        "clear",
        "printf 'LLM automation via bun`n`n'",
        "bun run native:health",
        "bun run native:state",
        "bun run native:action -- '{""action"":""togglePane""}'",
        "bun run native:action -- '{""action"":""togglePane""}'",
        "bun run native:action -- '{""action"":""showSettings""}'",
        "bun run native:action -- '{""action"":""showTerminal""}'",
        "bun run native:action -- '{""action"":""setTheme"",""value"":""dark""}'",
        "bun run native:action -- '{""action"":""setTheme"",""value"":""light""}'",
        "bun run native:screenshot"
    )

    foreach ($command in $commands) {
        $null = Invoke-AutomationPost "/action" @{
            action = "input"
            value = "$command`r"
        }

        Pause-Step ($(switch ($command) {
            "bun run native:state" { 2400; break }
            "bun run native:screenshot" { 2600; break }
            default { 1500 }
        }))
    }
}
finally {
    if ($recordingStarted) {
        try {
            $recordingStop = Invoke-AutomationPost "/recording/stop" $null
        }
        catch {
        }
    }

    try {
        $null = Invoke-AutomationPost "/action" @{ action = "setTheme"; value = "dark" }
    }
    catch {
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
    throw "The automation tour recording did not stop cleanly."
}

[pscustomobject]@{
    ok = $true
    outputDirectory = $OutputDirectory
    videoPath = $recordingStop.videoPath
    manifestPath = $recordingStop.manifestPath
    keepFrames = $recordingStop.keepFrames
    framesRetained = $recordingStop.framesRetained
    capturedFrames = $recordingStop.capturedFrames
    terminalTabId = $terminalTab.id
} | ConvertTo-Json -Depth 12
