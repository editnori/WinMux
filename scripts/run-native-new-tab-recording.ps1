param(
    [int]$Port = 9331,
    [string]$OutputDirectory = "",
    [int]$Fps = 16,
    [int]$WindowWidth = 1560,
    [int]$WindowHeight = 1040
)

$ErrorActionPreference = "Stop"
$baseUrl = "http://127.0.0.1:$Port"
$repoRoot = Split-Path $PSScriptRoot -Parent

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $OutputDirectory = Join-Path $repoRoot "tmp/automation-captures/winmux-new-tab-$timestamp"
}

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
    param([int]$Milliseconds)

    Start-Sleep -Milliseconds $Milliseconds
}

function Wait-Until {
    param(
        [scriptblock]$Condition,
        [string]$FailureMessage,
        [int]$Attempts = 30,
        [int]$DelayMilliseconds = 250
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

$recordingStop = $null
$originalWindow = $null
$recordingStarted = $false

try {
    $health = Invoke-AutomationGet "/health"
    if (-not $health.ok) {
        throw "Native automation server is not healthy."
    }

    $state = Invoke-AutomationGet "/state"
    $project = Get-ProjectById -State $state -ProjectId $state.projectId
    $thread = Get-ThreadById -Project $project -ThreadId $state.activeThreadId
    if ($null -eq $thread) {
        throw "No active thread is selected."
    }

    $beforeTabCount = @($thread.tabs).Count
    $beforeSelectedTabId = $state.activeTabId
    Wait-ForTerminalReady -TabId $beforeSelectedTabId | Out-Null

    $originalWindow = Get-WinMuxWindow
    Focus-WinMuxWindow | Out-Null
    Set-WinMuxTopmost -Enabled $true
    Center-WinMuxWindow
    Resize-WinMuxWindow -Width $WindowWidth -Height $WindowHeight
    Center-WinMuxWindow
    Focus-WinMuxWindow | Out-Null
    Pause-Step 900

    $null = Invoke-AutomationPost "/recording/start" @{
        fps = $Fps
        maxDurationMs = 20000
        jpegQuality = 84
        outputDirectory = $OutputDirectory
        keepFrames = $true
    }
    $recordingStarted = $true

    Pause-Step 1000
    Focus-WinMuxWindow | Out-Null
    $null = Invoke-AutomationPost "/action" @{ action = "newTab" }

    $state = Wait-Until -FailureMessage "New tab did not appear." -Condition {
        $latestState = Invoke-AutomationGet "/state"
        $latestProject = Get-ProjectById -State $latestState -ProjectId $latestState.projectId
        $latestThread = Get-ThreadById -Project $latestProject -ThreadId $latestState.activeThreadId
        if (@($latestThread.tabs).Count -eq ($beforeTabCount + 1)) {
            return $latestState
        }

        return $null
    }

    Pause-Step 1400
    $afterSelectedTabId = $state.activeTabId
    Wait-ForTerminalReady -TabId $afterSelectedTabId | Out-Null
    Pause-Step 900
    $null = Invoke-AutomationPost "/action" @{ action = "selectTab"; tabId = $beforeSelectedTabId }
    Pause-Step 1100
    $null = Invoke-AutomationPost "/action" @{ action = "selectTab"; tabId = $afterSelectedTabId }
    Pause-Step 1100
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
    throw "The new-tab recording did not stop cleanly."
}

[pscustomobject]@{
    ok = $true
    outputDirectory = $OutputDirectory
    videoPath = $recordingStop.videoPath
    manifestPath = $recordingStop.manifestPath
    keepFrames = $recordingStop.keepFrames
    framesRetained = $recordingStop.framesRetained
    capturedFrames = $recordingStop.capturedFrames
    beforeSelectedTabId = $beforeSelectedTabId
    afterSelectedTabId = $afterSelectedTabId
    beforeTabCount = $beforeTabCount
    afterTabCount = $beforeTabCount + 1
    windowWidth = $WindowWidth
    windowHeight = $WindowHeight
    fps = $Fps
} | ConvertTo-Json -Depth 12
