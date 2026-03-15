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
    $OutputDirectory = Join-Path $repoRoot "tmp/automation-captures/winmux-feature-tour-$timestamp"
}

$recordingOutputDirectory = Join-Path $OutputDirectory "recording"
$tempProjectPath = Join-Path $env:TEMP ("winmux-feature-tour-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $recordingOutputDirectory -Force | Out-Null

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

function Initialize-FeatureTourGitRepo {
    param([string]$Path)

    New-Item -ItemType Directory -Path $Path -Force | Out-Null
    Invoke-Git -C $Path init | Out-Null
    Invoke-Git -C $Path config user.email "winmux-tour@example.com" | Out-Null
    Invoke-Git -C $Path config user.name "WinMux Tour" | Out-Null

    @(
        "alpha"
        "beta"
        "gamma"
    ) | Set-Content -Path (Join-Path $Path "notes.txt")
    "feature tour" | Set-Content -Path (Join-Path $Path "README.md")

    Invoke-Git -C $Path add notes.txt README.md | Out-Null
    Invoke-Git -C $Path commit -m "Initial snapshot" | Out-Null

    @(
        "alpha"
        "beta updated"
        "gamma"
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

function Wait-ForTerminalReady {
    param([string]$TabId)

    return Wait-Until -FailureMessage "Terminal tab '$TabId' was not ready." -Condition {
        $terminalState = Invoke-AutomationPost "/terminal-state" @{ tabId = $TabId }
        $snapshot = @($terminalState.tabs) | Where-Object { $_.tabId -eq $TabId } | Select-Object -First 1
        $visibleText = if ($null -eq $snapshot) { "" } else { [string]$snapshot.visibleText }
        $bufferTail = if ($null -eq $snapshot) { "" } else { [string]$snapshot.bufferTail }
        $escape = [string][char]27
        $hasVisibleContent = -not [string]::IsNullOrWhiteSpace($visibleText)
        $cleanBufferTail = $bufferTail `
            -replace ([regex]::Escape($escape) + "\[[0-9;?]*[A-Za-z]"), "" `
            -replace ([regex]::Escape($escape) + "\][^\u0007]*\u0007"), ""
        $hasBufferContent = -not [string]::IsNullOrWhiteSpace($cleanBufferTail.Trim())
        if ($null -ne $snapshot -and
            $snapshot.rendererReady -eq $true -and
            $snapshot.started -eq $true -and
            ($hasVisibleContent -or $hasBufferContent)) {
            return $snapshot
        }

        return $null
    } -Attempts 40 -DelayMilliseconds 250
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

function Wait-ForDiffPaneReady {
    param(
        [string]$ThreadId,
        [string]$Path
    )

    return Wait-Until -FailureMessage "Diff pane for '$Path' did not become ready." -Condition {
        $latestState = Invoke-AutomationGet "/state"
        $latestProject = Get-ProjectById -State $latestState -ProjectId $latestState.projectId
        $latestThread = Get-ThreadById -Project $latestProject -ThreadId $ThreadId
        if ($null -eq $latestThread) {
            return $null
        }

        $diffTab = @($latestThread.tabs) | Where-Object { $_.kind -eq "diff" -and $_.title -eq "Diff $Path" } | Select-Object -First 1
        if ($latestState.selectedDiffPath -ne $Path -or $null -eq $diffTab) {
            return $null
        }

        $diffState = Invoke-AutomationPost "/diff-state" @{ paneId = $diffTab.id; maxLines = 30 }
        $snapshot = @($diffState.panes) | Where-Object { $_.paneId -eq $diffTab.id } | Select-Object -First 1
        if ($null -eq $snapshot) {
            return $null
        }

        if (@($snapshot.lines).Count -eq 0 -and [string]::IsNullOrWhiteSpace($snapshot.summary)) {
            return $null
        }

        return [pscustomobject]@{
            State = $latestState
            DiffTab = $diffTab
        }
    } -Attempts 40 -DelayMilliseconds 300
}

Initialize-FeatureTourGitRepo -Path $tempProjectPath

$recordingStop = $null

try {
    $health = Invoke-AutomationGet "/health"
    if (-not $health.ok) {
        throw "Native automation server is not healthy."
    }

    $null = Invoke-AutomationPost "/action" @{ action = "showTerminal" }
    $null = Invoke-AutomationPost "/action" @{ action = "setTheme"; value = "dark" }

    $recordingStart = Invoke-AutomationPost "/recording/start" @{
        fps = $Fps
        maxDurationMs = 90000
        jpegQuality = 84
        outputDirectory = $recordingOutputDirectory
        keepFrames = [bool]$KeepFrames
    }

    Pause-Step 1000
    $null = Invoke-AutomationPost "/ui-action" @{ action = "click"; automationId = "shell-pane-toggle" }
    Pause-Step 900
    $null = Invoke-AutomationPost "/ui-action" @{ action = "click"; automationId = "shell-pane-toggle" }
    Pause-Step 900

    $null = Invoke-AutomationPost "/ui-action" @{ action = "click"; automationId = "shell-new-project" }
    Wait-Until -FailureMessage "New-project dialog did not appear." -Condition {
        $tree = Invoke-AutomationGet "/ui-tree"
        if (@($tree.interactiveNodes) | Where-Object { $_.automationId -eq "dialog-project-path" }) {
            return $tree
        }

        return $null
    } | Out-Null
    Pause-Step 700
    $null = Invoke-AutomationPost "/ui-action" @{ action = "setText"; automationId = "dialog-project-path"; value = $tempProjectPath }
    Pause-Step 450
    $null = Invoke-AutomationPost "/ui-action" @{ action = "setText"; automationId = "dialog-project-shell-profile"; value = "wsl" }
    Pause-Step 450
    $null = Invoke-AutomationPost "/ui-action" @{ action = "click"; text = "Add project" }

    $state = Wait-Until -FailureMessage "Feature-tour project did not become active." -Condition {
        $latestState = Invoke-AutomationGet "/state"
        if ($latestState.projectPath -eq $tempProjectPath -and -not [string]::IsNullOrWhiteSpace($latestState.activeThreadId)) {
            return $latestState
        }

        return $null
    }

    $featureProjectId = $state.projectId
    $featureThreadId = $state.activeThreadId
    $featureProject = Get-ProjectById -State $state -ProjectId $featureProjectId
    $featureThread = Get-ThreadById -Project $featureProject -ThreadId $featureThreadId
    $terminalTab = @($featureThread.tabs) | Where-Object { $_.kind -eq "terminal" } | Select-Object -First 1

    Pause-Step 1200
    $terminalSnapshot = Wait-ForTerminalReady -TabId $terminalTab.id
    Pause-Step 1800
    $null = Invoke-AutomationPost "/action" @{ action = "input"; value = "pwd`r" }
    Pause-Step 1200
    $null = Invoke-AutomationPost "/action" @{ action = "input"; value = "git status --short`r" }
    Pause-Step 1600

    $null = Invoke-AutomationPost "/ui-action" @{ action = "click"; automationId = "shell-project-add-thread-$featureProjectId" }
    $state = Wait-Until -FailureMessage "Second thread did not become active." -Condition {
        $latestState = Invoke-AutomationGet "/state"
        if ($latestState.activeThreadId -ne $featureThreadId -and $latestState.projectId -eq $featureProjectId) {
            return $latestState
        }

        return $null
    }
    $secondThreadId = $state.activeThreadId
    Pause-Step 900
    $null = Invoke-AutomationPost "/ui-action" @{ action = "doubleClick"; automationId = "shell-thread-$secondThreadId" }
    Wait-Until -FailureMessage "Thread rename dialog did not appear." -Condition {
        $tree = Invoke-AutomationGet "/ui-tree"
        if (@($tree.interactiveNodes) | Where-Object { $_.automationId -eq "dialog-thread-name" }) {
            return $tree
        }

        return $null
    } | Out-Null
    $null = Invoke-AutomationPost "/ui-action" @{ action = "setText"; automationId = "dialog-thread-name"; value = "Review Thread" }
    Pause-Step 400
    $null = Invoke-AutomationPost "/ui-action" @{ action = "click"; text = "Save" }
    Pause-Step 900

    $branchWorktree = Join-Path $tempProjectPath "branch-worktree"
    New-Item -ItemType Directory -Force -Path $branchWorktree | Out-Null
    $null = Invoke-AutomationPost "/action" @{ action = "setThreadWorktree"; threadId = $secondThreadId; value = $branchWorktree }
    Pause-Step 700
    $null = Invoke-AutomationPost "/action" @{ action = "newThread"; projectId = $featureProjectId }
    $state = Wait-Until -FailureMessage "Inherited-worktree thread did not become active." -Condition {
        $latestState = Invoke-AutomationGet "/state"
        if ($latestState.projectId -eq $featureProjectId -and $latestState.activeThreadId -ne $secondThreadId) {
            return $latestState
        }

        return $null
    }
    Pause-Step 1000

    $null = Invoke-AutomationPost "/action" @{ action = "selectThread"; threadId = $featureThreadId }
    $state = Wait-Until -FailureMessage "Primary thread was not restored." -Condition {
        $latestState = Invoke-AutomationGet "/state"
        if ($latestState.projectId -eq $featureProjectId -and $latestState.activeThreadId -eq $featureThreadId) {
            return $latestState
        }

        return $null
    }
    Pause-Step 900

    $null = Invoke-AutomationPost "/action" @{ action = "showOverview" }
    Wait-Until -FailureMessage "Thread overview did not appear." -Condition {
        $latestState = Invoke-AutomationGet "/state"
        if ($latestState.activeView -eq "overview") {
            return $latestState
        }

        return $null
    } | Out-Null
    Pause-Step 1000
    $null = Invoke-AutomationPost "/action" @{ action = "showTerminal" }
    Pause-Step 900

    $null = Invoke-AutomationPost "/action" @{ action = "newBrowserPane" }
    $state = Wait-Until -FailureMessage "Browser pane did not appear." -Condition {
        $latestState = Invoke-AutomationGet "/state"
        $latestProject = Get-ProjectById -State $latestState -ProjectId $latestState.projectId
        $latestThread = Get-ThreadById -Project $latestProject -ThreadId $latestState.activeThreadId
        if (@($latestThread.tabs | Where-Object { $_.kind -eq "browser" }).Count -ge 1) {
            return $latestState
        }

        return $null
    }
    Pause-Step 1200
    $null = Invoke-AutomationPost "/action" @{ action = "navigateBrowser"; value = "https://example.com" }
    Wait-Until -FailureMessage "Browser did not complete navigation." -Condition {
        $events = Invoke-AutomationGet "/events"
        if (@($events.events | Where-Object { $_.category -eq "browser" -and $_.name -eq "navigate.completed" -and $_.data.uri -eq "https://example.com/" -and $_.data.success -eq "True" }).Count -gt 0) {
            return $true
        }

        return $null
    } | Out-Null
    Pause-Step 1400
    $null = Invoke-AutomationPost "/action" @{ action = "newBrowserTab"; value = "https://example.org" }
    Wait-Until -FailureMessage "Browser tab did not appear." -Condition {
        $browserState = Invoke-AutomationPost "/browser-state" @{}
        $pane = @($browserState.panes) | Select-Object -First 1
        if ($null -ne $pane -and $pane.tabCount -ge 2) {
            return $pane
        }

        return $null
    } | Out-Null
    Pause-Step 1200

    $null = Invoke-AutomationPost "/ui-action" @{ action = "click"; automationId = "shell-nav-settings" }
    Wait-Until -FailureMessage "Settings did not open." -Condition {
        $tree = Invoke-AutomationGet "/ui-tree"
        if ($tree.activeView -eq "settings") {
            return $tree
        }

        return $null
    } | Out-Null
    Pause-Step 1000
    $null = Invoke-AutomationPost "/ui-action" @{ action = "click"; automationId = "settings-theme-light" }
    Pause-Step 1000
    $null = Invoke-AutomationPost "/ui-action" @{ action = "click"; automationId = "settings-theme-dark" }
    Pause-Step 1000
    $null = Invoke-AutomationPost "/action" @{ action = "showTerminal" }
    Pause-Step 900

    $null = Invoke-AutomationPost "/action" @{ action = "setTheme"; value = "light" }
    Pause-Step 900
    $null = Invoke-AutomationPost "/action" @{ action = "refreshDiff" }
    Wait-Until -FailureMessage "Git diff state did not refresh for the feature-tour project." -Condition {
        $latestState = Invoke-AutomationGet "/state"
        if ($latestState.projectId -eq $featureProjectId -and $latestState.changedFileCount -ge 1) {
            return $latestState
        }

        return $null
    } | Out-Null
    $null = Invoke-AutomationPost "/action" @{ action = "selectDiffFile"; value = "notes.txt" }
    $diffReady = Wait-ForDiffPaneReady -ThreadId $featureThreadId -Path "notes.txt"
    $null = Invoke-AutomationPost "/action" @{ action = "selectTab"; tabId = $diffReady.DiffTab.id }
    Pause-Step 1400

    Show-ContextMenu -AutomationId "shell-project-$featureProjectId" -PauseMs 800
    $null = Invoke-AutomationPost "/ui-action" @{
        action = "invokeMenuItem"
        automationId = "shell-project-$featureProjectId"
        menuItemText = "Remove project"
    }
    $null = Wait-Until -FailureMessage "Feature-tour project did not remove." -Condition {
        $latestState = Invoke-AutomationGet "/state"
        if ((@($latestState.projects) | Where-Object { $_.id -eq $featureProjectId }).Count -eq 0) {
            return $latestState
        }

        return $null
    }
    Pause-Step 1000

    $recordingStop = Invoke-AutomationPost "/recording/stop" $null
    if (-not $recordingStop.ok) {
        throw "The feature-tour recording did not stop cleanly."
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
    } | ConvertTo-Json -Depth 12
}
catch {
    try {
        if ($null -eq $recordingStop) {
            $recordingStop = Invoke-AutomationPost "/recording/stop" $null
        }
    }
    catch {
    }

    [pscustomobject]@{
        ok = $false
        error = $_.Exception.Message
        outputDirectory = $OutputDirectory
    } | ConvertTo-Json -Depth 10
    exit 1
}
