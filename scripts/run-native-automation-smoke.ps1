param(
    [int]$Port = 9331
)

$ErrorActionPreference = "Stop"
$baseUrl = "http://127.0.0.1:$Port"
$desktopUiaScript = Join-Path $PSScriptRoot "run-desktop-uia.ps1"
$results = [System.Collections.Generic.List[object]]::new()
$tempProjectPath = Join-Path $env:TEMP ("winmux-smoke-" + [Guid]::NewGuid().ToString("N"))

function Add-Check {
    param(
        [string]$Name,
        [string]$Detail
    )

    $results.Add([pscustomobject]@{
            name = $Name
            status = "ok"
            detail = $Detail
        })
}

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Invoke-AutomationGet {
    param([string]$Path)

    return Invoke-RestMethod -Uri "$baseUrl$Path" -TimeoutSec 10
}

function Invoke-AutomationPost {
    param(
        [string]$Path,
        [object]$Body
    )

    $json = if ($null -eq $Body) { "" } else { $Body | ConvertTo-Json -Depth 20 }
    return Invoke-RestMethod -Method Post -Uri "$baseUrl$Path" -ContentType "application/json" -Body $json -TimeoutSec 10
}

function Invoke-DesktopUiaTree {
    param([object]$Body)

    $json = $Body | ConvertTo-Json -Depth 20 -Compress
    return (& $desktopUiaScript tree $json) | ConvertFrom-Json
}

function Invoke-DesktopUiaAction {
    param([object]$Body)

    $json = $Body | ConvertTo-Json -Depth 20 -Compress
    return (& $desktopUiaScript action $json) | ConvertFrom-Json
}

function Wait-Until {
    param(
        [scriptblock]$Condition,
        [string]$FailureMessage,
        [int]$Attempts = 24,
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

try {
    $health = Invoke-AutomationGet "/health"
    Assert-True ($health.ok -eq $true) "Automation health check failed."
    Add-Check "health" "automation endpoint responded from pid $($health.pid)"

    $null = Invoke-AutomationPost "/events/clear" $null

    $desktopWindows = Invoke-AutomationGet "/desktop-windows"
    Assert-True (@($desktopWindows.windows | Where-Object { $_.title -eq "WinMux" }).Count -gt 0) "Desktop window enumeration did not find WinMux."
    Add-Check "desktop-windows" "desktop window enumeration is available"

    $state = Invoke-AutomationGet "/state"
    Assert-True (@($state.projects).Count -ge 1) "Expected at least one project."
    Assert-True (@($state.threads).Count -ge 1) "Expected at least one thread."
    Add-Check "initial-state" "$(@($state.projects).Count) project(s), $(@($state.threads).Count) thread(s)"

    $uiTree = Invoke-AutomationGet "/ui-tree"
    $interactiveNodes = @($uiTree.interactiveNodes)
    Assert-True ($interactiveNodes.Count -gt 0) "Expected interactive nodes in ui-tree."
    Assert-True (@($interactiveNodes | Where-Object { $_.automationId -eq "settings-theme-dark" }).Count -eq 0) "Hidden settings controls leaked into terminal ui-tree."
    Add-Check "terminal-ui-tree" "$($interactiveNodes.Count) visible interactive node(s)"

    $activeProject = Get-ProjectById -State $state -ProjectId $state.projectId
    $threadCountBefore = @($activeProject.threads).Count
    $addThreadResponse = Invoke-AutomationPost "/ui-action" @{
        action = "click"
        automationId = "shell-project-add-thread-$($activeProject.id)"
    }
    Assert-True ($addThreadResponse.ok -eq $true) "Add-thread UI action failed."

    $state = Invoke-AutomationGet "/state"
    $activeProject = Get-ProjectById -State $state -ProjectId $state.projectId
    Assert-True (@($activeProject.threads).Count -eq ($threadCountBefore + 1)) "Add-thread did not create a new thread."
    $activeThread = Get-ThreadById -Project $activeProject -ThreadId $state.activeThreadId
    Add-Check "add-thread" "selected thread is '$($activeThread.name)'"

    $tabCountBefore = @($activeThread.tabs).Count
    $newTabResponse = Invoke-AutomationPost "/action" @{ action = "newTab" }
    Assert-True ($newTabResponse.ok -eq $true) "newTab action failed."

    $state = Invoke-AutomationGet "/state"
    $activeProject = Get-ProjectById -State $state -ProjectId $state.projectId
    $activeThread = Get-ThreadById -Project $activeProject -ThreadId $state.activeThreadId
    Assert-True (@($activeThread.tabs).Count -eq ($tabCountBefore + 1)) "newTab did not create a second tab."
    Add-Check "new-tab" "$(@($activeThread.tabs).Count) tab(s) on active thread"

    $originalFirstTabId = $activeThread.tabs[0].id
    $originalSecondTabId = $activeThread.tabs[1].id
    $moveTab = Invoke-AutomationPost "/action" @{
        action = "moveTabAfter"
        tabId = $originalFirstTabId
        targetTabId = $originalSecondTabId
    }
    Assert-True ($moveTab.ok -eq $true) "moveTabAfter action failed."

    $state = Invoke-AutomationGet "/state"
    $activeProject = Get-ProjectById -State $state -ProjectId $state.projectId
    $activeThread = Get-ThreadById -Project $activeProject -ThreadId $state.activeThreadId
    Assert-True ($activeThread.tabs[0].id -eq $originalSecondTabId -and $activeThread.tabs[1].id -eq $originalFirstTabId) "moveTabAfter did not reorder the tab list."
    Add-Check "move-tab-after" "semantic tab reorder is available"

    $selectedSnapshot = Wait-Until -FailureMessage "terminal-state reported a tab that is not ready." -Condition {
        $terminalState = Invoke-AutomationPost "/terminal-state" @{ tabId = $state.activeTabId }
        $snapshot = @($terminalState.tabs) | Where-Object { $_.tabId -eq $state.activeTabId } | Select-Object -First 1
        if ($null -eq $snapshot) {
            return $null
        }

        if ($snapshot.rendererReady -eq $true -and $snapshot.started -eq $true) {
            return $snapshot
        }

        return $null
    }
    Add-Check "terminal-state" "$($selectedSnapshot.cols)x$($selectedSnapshot.rows) cursor=($($selectedSnapshot.cursorX),$($selectedSnapshot.cursorY))"

    $settingsResponse = Invoke-AutomationPost "/ui-action" @{
        action = "click"
        automationId = "shell-nav-settings"
    }
    Assert-True ($settingsResponse.ok -eq $true) "Could not open settings via ui-action."

    $desktopUiaTree = Invoke-DesktopUiaTree @{
        titleContains = "WinMux"
        maxDepth = 3
    }
    Assert-True ($null -ne $desktopUiaTree.root) "Desktop UIA tree did not return a root."
    Assert-True (@($desktopUiaTree.interactiveNodes).Count -gt 0) "Desktop UIA tree did not expose interactive nodes."
    Add-Check "desktop-uia-tree" "$(@($desktopUiaTree.interactiveNodes).Count) desktop UIA node(s)"

    $desktopUiaFocus = Invoke-DesktopUiaAction @{
        action = "focus"
        titleContains = "WinMux"
        name = "WinMux"
    }
    Assert-True ($desktopUiaFocus.ok -eq $true) "Desktop UIA focus action failed."
    Add-Check "desktop-uia-action" "desktop UIA actions are available"

    $settingsTree = Wait-Until -FailureMessage "Settings controls were not visible after opening Preferences." -Condition {
        $tree = Invoke-AutomationGet "/ui-tree"
        if ($tree.activeView -eq "settings" -and (@($tree.interactiveNodes) | Where-Object { $_.automationId -eq "settings-theme-light" })) {
            return $tree
        }

        return $null
    }
    $settingsNodes = @($settingsTree.interactiveNodes)
    Assert-True (@($settingsNodes | Where-Object { $_.automationId -eq "settings-shell-wsl" }).Count -gt 0) "Settings shell controls were not visible."
    Add-Check "settings-ui-tree" "settings controls are discoverable"

    $themeLightResponse = Invoke-AutomationPost "/ui-action" @{
        action = "click"
        automationId = "settings-theme-light"
    }
    Assert-True ($themeLightResponse.ok -eq $true) "Could not click light theme radio."

    $state = Invoke-AutomationGet "/state"
    Assert-True ($state.theme -eq "light") "Theme did not switch to light."
    Add-Check "theme-toggle" "theme switched to light"

    $showTerminalResponse = Invoke-AutomationPost "/action" @{ action = "showTerminal" }
    Assert-True ($showTerminalResponse.ok -eq $true) "Could not return to terminal view."

    $state = Invoke-AutomationGet "/state"
    $activeProject = Get-ProjectById -State $state -ProjectId $state.projectId
    $duplicateSourceThreadId = $state.activeThreadId
    $threadCountBeforeDuplicate = @($activeProject.threads).Count
    $invokeDuplicate = Invoke-AutomationPost "/ui-action" @{
        action = "invokeMenuItem"
        automationId = "shell-thread-$duplicateSourceThreadId"
        menuItemText = "Duplicate"
    }
    Assert-True ($invokeDuplicate.ok -eq $true) "Duplicate thread menu action failed."

    $state = Invoke-AutomationGet "/state"
    $activeProject = Get-ProjectById -State $state -ProjectId $state.projectId
    Assert-True (@($activeProject.threads).Count -eq ($threadCountBeforeDuplicate + 1)) "Duplicate thread did not create a new thread."
    $duplicateThreadId = $state.activeThreadId
    $null = Wait-Until -FailureMessage "Duplicated thread did not appear in the sidebar ui-tree." -Condition {
        $tree = Invoke-AutomationGet "/ui-tree"
        if (@($tree.interactiveNodes) | Where-Object { $_.automationId -eq "shell-thread-$duplicateThreadId" }) {
            return $true
        }

        return $null
    }
    Add-Check "duplicate-thread" "duplicated thread selected as '$((Get-ThreadById -Project $activeProject -ThreadId $duplicateThreadId).name)'"

    $invokeRename = Invoke-AutomationPost "/ui-action" @{
        action = "doubleClick"
        automationId = "shell-thread-$duplicateThreadId"
    }
    Assert-True ($invokeRename.ok -eq $true) "Rename thread double-click action failed."

    $renameTree = Wait-Until -FailureMessage "Rename dialog was not exposed in ui-tree." -Condition {
        $tree = Invoke-AutomationGet "/ui-tree"
        if (@($tree.interactiveNodes) | Where-Object { $_.automationId -eq "dialog-thread-name" }) {
            return $tree
        }

        return $null
    }

    $setThreadName = Invoke-AutomationPost "/ui-action" @{
        action = "setText"
        automationId = "dialog-thread-name"
        value = "Automation Rename"
    }
    Assert-True ($setThreadName.ok -eq $true) "Could not set rename dialog text."

    $saveRename = Invoke-AutomationPost "/ui-action" @{
        action = "click"
        text = "Save"
    }
    Assert-True ($saveRename.ok -eq $true) "Could not click the rename Save button."

    $renamedThread = Wait-Until -FailureMessage "Thread rename did not persist." -Condition {
        $latestState = Invoke-AutomationGet "/state"
        $latestProject = Get-ProjectById -State $latestState -ProjectId $latestState.projectId
        $latestThread = Get-ThreadById -Project $latestProject -ThreadId $duplicateThreadId
        if ($latestThread.name -eq "Automation Rename") {
            return $latestThread
        }

        return $null
    }
    Add-Check "rename-thread-dialog" "thread renamed through dialog automation"

    $threadCountBeforeDelete = @($activeProject.threads).Count
    $deleteThread = Invoke-AutomationPost "/action" @{
        action = "deleteThread"
        threadId = $duplicateThreadId
    }
    Assert-True ($deleteThread.ok -eq $true) "Delete thread action failed."

    $state = Wait-Until -FailureMessage "Deleted thread still present in state." -Condition {
        $latestState = Invoke-AutomationGet "/state"
        $latestProject = Get-ProjectById -State $latestState -ProjectId $latestState.projectId
        $deletedThread = Get-ThreadById -Project $latestProject -ThreadId $duplicateThreadId
        if ($null -eq $deletedThread -and @($latestProject.threads).Count -eq ($threadCountBeforeDelete - 1)) {
            return $latestState
        }

        return $null
    }
    $activeProject = Get-ProjectById -State $state -ProjectId $state.projectId
    Add-Check "delete-thread" "thread removed while project remained with $(@($activeProject.threads).Count) thread(s)"

    New-Item -ItemType Directory -Path $tempProjectPath -Force | Out-Null

    $newProjectDialog = Invoke-AutomationPost "/ui-action" @{
        action = "click"
        automationId = "shell-new-project"
    }
    Assert-True ($newProjectDialog.ok -eq $true) "Could not open new-project dialog."

    $projectTree = Wait-Until -FailureMessage "Project dialog path box was not exposed." -Condition {
        $tree = Invoke-AutomationGet "/ui-tree"
        if (@($tree.interactiveNodes) | Where-Object { $_.automationId -eq "dialog-project-path" }) {
            return $tree
        }

        return $null
    }

    $setProjectPath = Invoke-AutomationPost "/ui-action" @{
        action = "setText"
        automationId = "dialog-project-path"
        value = $tempProjectPath
    }
    Assert-True ($setProjectPath.ok -eq $true) "Could not set project path."

    $setProjectShell = Invoke-AutomationPost "/ui-action" @{
        action = "setText"
        automationId = "dialog-project-shell-profile"
        value = "wsl"
    }
    Assert-True ($setProjectShell.ok -eq $true) "Could not set project shell profile."

    $addProject = Invoke-AutomationPost "/ui-action" @{
        action = "click"
        text = "Add project"
    }
    Assert-True ($addProject.ok -eq $true) "Could not confirm new-project dialog."

    $state = Wait-Until -FailureMessage "New project was not activated." -Condition {
        $latestState = Invoke-AutomationGet "/state"
        if (@($latestState.projects).Count -ge 2 -and $latestState.projectPath -eq $tempProjectPath) {
            return $latestState
        }

        return $null
    }
    Add-Check "new-project-dialog" "new project added at $tempProjectPath"

    $threadIdToDelete = $state.activeThreadId
    $deleteLastThread = Invoke-AutomationPost "/action" @{
        action = "deleteThread"
        threadId = $threadIdToDelete
    }
    Assert-True ($deleteLastThread.ok -eq $true) "Deleting the only thread in a project failed."

    $state = Wait-Until -FailureMessage "Project did not enter the empty-thread state." -Condition {
        $latestState = Invoke-AutomationGet "/state"
        $latestProject = Get-ProjectById -State $latestState -ProjectId $latestState.projectId
        if ($latestProject.rootPath -eq $tempProjectPath -and @($latestProject.threads).Count -eq 0 -and [string]::IsNullOrWhiteSpace($latestState.activeThreadId)) {
            return $latestState
        }

        return $null
    }
    $emptyTree = Invoke-AutomationGet "/ui-tree"
    Assert-True (@($emptyTree.interactiveNodes | Where-Object { $_.automationId -eq "shell-empty-new-thread" }).Count -gt 0) "Empty project state was not visible."
    Add-Check "delete-last-thread" "project remained open with zero threads"

    $restoreThread = Invoke-AutomationPost "/ui-action" @{
        action = "click"
        automationId = "shell-empty-new-thread"
    }
    Assert-True ($restoreThread.ok -eq $true) "Could not create a thread from the empty project state."

    $state = Wait-Until -FailureMessage "Empty project did not recover after creating a new thread." -Condition {
        $latestState = Invoke-AutomationGet "/state"
        $latestProject = Get-ProjectById -State $latestState -ProjectId $latestState.projectId
        if ($latestProject.rootPath -eq $tempProjectPath -and @($latestProject.threads).Count -eq 1 -and -not [string]::IsNullOrWhiteSpace($latestState.activeThreadId)) {
            return $latestState
        }

        return $null
    }
    Add-Check "empty-project-recovery" "new thread created from empty project state"

    $annotatedShot = Invoke-AutomationPost "/screenshot" @{ path = ""; annotated = $true }
    Assert-True ($annotatedShot.ok -eq $true) "Annotated screenshot failed."
    Add-Check "annotated-screenshot" $annotatedShot.path

    $recordingStart = Invoke-AutomationPost "/recording/start" @{
        fps = 12
        maxDurationMs = 2000
        jpegQuality = 78
    }
    Assert-True ($recordingStart.recording -eq $true) "Native recording did not start."

    $recordingAction = Invoke-AutomationPost "/action" @{
        action = "newTab"
    }
    Assert-True ($recordingAction.ok -eq $true) "Could not create a tab during recording."
    Start-Sleep -Milliseconds 700

    $recordingStop = Invoke-AutomationPost "/recording/stop" $null
    Assert-True ($recordingStop.ok -eq $true) "Native recording did not stop cleanly."
    Assert-True ($recordingStop.capturedFrames -gt 0) "Native recording captured zero frames."
    Assert-True (-not [string]::IsNullOrWhiteSpace($recordingStop.manifestPath)) "Native recording did not write a manifest."
    Add-Check "recording" "$($recordingStop.capturedFrames) frame(s) captured"

    $renderTrace = Invoke-AutomationPost "/render-trace" @{
        frames = 3
        captureScreenshots = $false
        annotated = $false
        action = @{
            action = "newTab"
        }
    }
    Assert-True ($renderTrace.ok -eq $true) "Render trace failed."
    Assert-True (@($renderTrace.frames).Count -eq 3) "Render trace did not capture the expected number of frames."
    Add-Check "render-trace" "$(@($renderTrace.frames).Count) frame(s) captured"

    $events = Invoke-AutomationGet "/events"
    $eventNames = @($events.events | ForEach-Object { $_.name })
    Assert-True (@($eventNames | Where-Object { $_ -eq "pane.created" }).Count -gt 0) "Expected pane.created event."
    Assert-True (@($eventNames | Where-Object { $_ -eq "thread.renamed" }).Count -gt 0) "Expected thread.renamed event."
    Assert-True (@($eventNames | Where-Object { $_ -eq "thread.deleted" }).Count -gt 0) "Expected thread.deleted event."
    Add-Check "event-log" "$(@($events.events).Count) event(s) captured"

    $resetTheme = Invoke-AutomationPost "/action" @{
        action = "setTheme"
        value = "dark"
    }
    Assert-True ($resetTheme.ok -eq $true) "Failed to restore dark theme."

    $showTerminalResponse = Invoke-AutomationPost "/action" @{ action = "showTerminal" }
    Assert-True ($showTerminalResponse.ok -eq $true) "Failed to restore terminal view."

    [pscustomobject]@{
        ok = $true
        checks = $results
    } | ConvertTo-Json -Depth 10
}
catch {
    [pscustomobject]@{
        ok = $false
        error = $_.Exception.Message
        checks = $results
    } | ConvertTo-Json -Depth 10
    exit 1
}
