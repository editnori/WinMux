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

    return Invoke-RestMethod -Uri "$baseUrl$Path" -TimeoutSec 20
}

function Invoke-AutomationPost {
    param(
        [string]$Path,
        [object]$Body
    )

    $json = if ($null -eq $Body) { "" } else { $Body | ConvertTo-Json -Depth 20 }
    return Invoke-RestMethod -Method Post -Uri "$baseUrl$Path" -ContentType "application/json" -Body $json -TimeoutSec 20
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

function Get-LeadingBlankLineCount {
    param([string]$Text)

    if ([string]::IsNullOrEmpty($Text)) {
        return 0
    }

    $count = 0
    foreach ($line in ($Text -split "`n")) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            $count++
        }
        else {
            break
        }
    }

    return $count
}

function Test-TerminalReadySnapshot {
    param([object]$Snapshot)

    if ($null -eq $Snapshot) {
        return $false
    }

    $visibleText = Get-TerminalPrintableText $Snapshot.visibleText
    $bufferTail = Get-TerminalPrintableText $Snapshot.bufferTail

    return $Snapshot.rendererReady -eq $true -and
        $Snapshot.started -eq $true -and
        $Snapshot.exited -ne $true -and
        (-not [string]::IsNullOrWhiteSpace($visibleText) -or -not [string]::IsNullOrWhiteSpace($bufferTail))
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

function Initialize-SmokeGitRepo {
    param([string]$Path)

    New-Item -ItemType Directory -Path $Path -Force | Out-Null
    Invoke-Git -C $Path init | Out-Null
    Invoke-Git -C $Path config user.email "winmux-smoke@example.com" | Out-Null
    Invoke-Git -C $Path config user.name "WinMux Smoke" | Out-Null

    @(
        "alpha"
        "beta"
        "gamma"
    ) | Set-Content -Path (Join-Path $Path "notes.txt")

    Invoke-Git -C $Path add notes.txt | Out-Null
    Invoke-Git -C $Path commit -m "Initial snapshot" | Out-Null

    @(
        "alpha"
        "beta updated"
        "gamma"
        "delta added"
    ) | Set-Content -Path (Join-Path $Path "notes.txt")
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

    if ($state.activeView -ne "terminal") {
        $showTerminal = Invoke-AutomationPost "/action" @{ action = "showTerminal" }
        Assert-True ($showTerminal.ok -eq $true) "Could not normalize the shell back to terminal view."
        $state = Invoke-AutomationGet "/state"
    }

    $uiTree = Invoke-AutomationGet "/ui-tree"
    $interactiveNodes = @($uiTree.interactiveNodes)
    Assert-True ($interactiveNodes.Count -gt 0) "Expected interactive nodes in ui-tree."
    Assert-True (@($interactiveNodes | Where-Object { $_.automationId -eq "settings-theme-dark" }).Count -eq 0) "Hidden settings controls leaked into terminal ui-tree."
    Add-Check "terminal-ui-tree" "$($interactiveNodes.Count) visible interactive node(s)"

    $activeProject = Get-ProjectById -State $state -ProjectId $state.projectId
    $initialProjectId = $activeProject.id
    $initialThreadId = $state.activeThreadId
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
    $createdThreadId = $activeThread.id
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

    $readyTerminal = Wait-Until -FailureMessage "terminal-state reported no ready terminal tabs on the active thread." -Condition {
        $latestState = Invoke-AutomationGet "/state"
        $latestProject = Get-ProjectById -State $latestState -ProjectId $latestState.projectId
        $latestThread = Get-ThreadById -Project $latestProject -ThreadId $latestState.activeThreadId
        $terminalTabs = @($latestThread.tabs) | Where-Object { $_.kind -eq "terminal" }
        foreach ($terminalTab in $terminalTabs) {
            $terminalState = Invoke-AutomationPost "/terminal-state" @{ tabId = $terminalTab.id }
            $snapshot = @($terminalState.tabs) | Where-Object { $_.tabId -eq $terminalTab.id } | Select-Object -First 1
            if (Test-TerminalReadySnapshot $snapshot) {
                return [pscustomobject]@{
                    state = $latestState
                    tabId = $terminalTab.id
                    snapshot = $snapshot
                }
            }
        }

        return $null
    } -Attempts 40 -DelayMilliseconds 300
    $state = $readyTerminal.state
    $terminalTabId = $readyTerminal.tabId
    if ($state.activeTabId -ne $terminalTabId) {
        $selectReadyTerminal = Invoke-AutomationPost "/action" @{
            action = "selectTab"
            tabId = $terminalTabId
        }
        Assert-True ($selectReadyTerminal.ok -eq $true) "Could not select the ready terminal tab."
        $state = Invoke-AutomationGet "/state"
    }
    $selectedSnapshot = $readyTerminal.snapshot
    Add-Check "terminal-state" "$($selectedSnapshot.cols)x$($selectedSnapshot.rows) cursor=($($selectedSnapshot.cursorX),$($selectedSnapshot.cursorY))"

    $resizeStartCols = $selectedSnapshot.cols
    $resizeStartRows = $selectedSnapshot.rows
    $resizeStartLeadingBlanks = Get-LeadingBlankLineCount $selectedSnapshot.visibleText
    $setPaneSplit = Invoke-AutomationPost "/action" @{ action = "setPaneSplit"; value = "0.31,0.69" }
    Assert-True ($setPaneSplit.ok -eq $true) "setPaneSplit action failed."

    $resizedSnapshot = Wait-Until -FailureMessage "Terminal pane did not report a stable resized state." -Condition {
        $terminalState = Invoke-AutomationPost "/terminal-state" @{ tabId = $terminalTabId }
        $snapshot = @($terminalState.tabs) | Where-Object { $_.tabId -eq $terminalTabId } | Select-Object -First 1
        if ($null -eq $snapshot) {
            return $null
        }

        if ($snapshot.rendererReady -eq $true -and
            $snapshot.started -eq $true -and
            ($snapshot.cols -ne $resizeStartCols -or $snapshot.rows -ne $resizeStartRows)) {
            return $snapshot
        }

        return $null
    } -Attempts 30 -DelayMilliseconds 300
    $resizeEndLeadingBlanks = Get-LeadingBlankLineCount $resizedSnapshot.visibleText
    Assert-True (($resizeEndLeadingBlanks - $resizeStartLeadingBlanks) -le 1) "Terminal pane added too many leading blank lines after resize."
    Add-Check "pane-split-resize" "$($resizeStartCols)x$($resizeStartRows) -> $($resizedSnapshot.cols)x$($resizedSnapshot.rows), leading blanks $resizeStartLeadingBlanks -> $resizeEndLeadingBlanks"

    if (-not [string]::IsNullOrWhiteSpace($state.selectedDiffPath)) {
        $openDiffPane = Invoke-AutomationPost "/action" @{
            action = "selectDiffFile"
            value = $state.selectedDiffPath
        }
        Assert-True ($openDiffPane.ok -eq $true) "selectDiffFile action failed."

        $state = Wait-Until -FailureMessage "Diff pane did not become the active pane." -Condition {
            $latestState = Invoke-AutomationGet "/state"
            $latestProject = Get-ProjectById -State $latestState -ProjectId $latestState.projectId
            $latestThread = Get-ThreadById -Project $latestProject -ThreadId $latestState.activeThreadId
            $activePane = @($latestThread.panes) | Where-Object { $_.id -eq $latestState.activeTabId } | Select-Object -First 1
            if ($null -ne $activePane -and $activePane.kind -eq "diff" -and $latestThread.layout -eq "dual") {
                return $latestState
            }

            return $null
        }
        Add-Check "diff-pane" "diff pane opened for '$($state.selectedDiffPath)' in dual layout"
    }

    $activeProject = Get-ProjectById -State $state -ProjectId $state.projectId
    $activeThread = Get-ThreadById -Project $activeProject -ThreadId $state.activeThreadId

    $browserTabCountBefore = @($activeThread.tabs).Count
    $newBrowserPaneResponse = Invoke-AutomationPost "/action" @{ action = "newBrowserPane" }
    Assert-True ($newBrowserPaneResponse.ok -eq $true) "newBrowserPane action failed."

    $state = Invoke-AutomationGet "/state"
    $activeProject = Get-ProjectById -State $state -ProjectId $state.projectId
    $activeThread = Get-ThreadById -Project $activeProject -ThreadId $state.activeThreadId
    Assert-True (@($activeThread.tabs).Count -eq ($browserTabCountBefore + 1)) "newBrowserPane did not create a browser pane."

    $browserTree = Wait-Until -FailureMessage "Browser pane controls did not appear in ui-tree." -Condition {
        $tree = Invoke-AutomationGet "/ui-tree"
        $interactive = @($tree.interactiveNodes)
        if ((@($interactive | Where-Object { $_.automationId -eq "browser-pane-address" }).Count -gt 0) -and
            (@($interactive | Where-Object { $_.automationId -eq "browser-pane-pages" }).Count -gt 0) -and
            (@($interactive | Where-Object { $_.automationId -eq "browser-pane-extensions" }).Count -gt 0)) {
            return $tree
        }

        return $null
    }

    $navigateBrowserResponse = Invoke-AutomationPost "/action" @{
        action = "navigateBrowser"
        value = "https://example.com"
    }
    Assert-True ($navigateBrowserResponse.ok -eq $true) "navigateBrowser action failed."

    $null = Wait-Until -FailureMessage "Browser pane did not complete navigation to example.com." -Condition {
        $events = Invoke-AutomationGet "/events"
        if (@($events.events | Where-Object { $_.category -eq "browser" -and $_.name -eq "navigate.completed" -and $_.data.uri -eq "https://example.com/" -and $_.data.success -eq "True" }).Count -gt 0) {
            return $true
        }

        return $null
    }
    Add-Check "browser-pane" "browser pane rendered controls and completed navigation to example.com"

    $browserState = Invoke-AutomationPost "/browser-state" @{}
    $selectedBrowserPane = @($browserState.panes) | Where-Object { $_.paneId -eq $browserState.selectedPaneId } | Select-Object -First 1
    Assert-True ($null -ne $selectedBrowserPane) "browser-state did not return the selected pane."
    Assert-True ($selectedBrowserPane.initialized -eq $true) "Selected browser pane was not initialized."
    Assert-True (-not [string]::IsNullOrWhiteSpace($selectedBrowserPane.profileSeedStatus)) "Selected browser pane did not report profile seed status."
    Add-Check "browser-state" "$($selectedBrowserPane.profileSeedStatus)"

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
    $lightTerminalTree = Wait-Until -FailureMessage "Selected thread did not expose a light-theme selected state." -Condition {
        $tree = Invoke-AutomationGet "/ui-tree"
        $selectedThread = @($tree.interactiveNodes) | Where-Object { $_.automationId -eq "shell-thread-$($state.activeThreadId)" -and $_.selected -eq $true } | Select-Object -First 1
        if ($null -ne $selectedThread -and $selectedThread.background -eq "#FFE7E7EB") {
            return $selectedThread
        }

        return $null
    }
    Assert-True ($lightTerminalTree.background -eq "#FFE7E7EB") "Selected light-theme thread row used the wrong background color."
    Add-Check "light-thread-selection" $lightTerminalTree.background

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
    $deleteThread = Invoke-AutomationPost "/ui-action" @{
        action = "invokeMenuItem"
        automationId = "shell-thread-$duplicateThreadId"
        menuItemText = "Clear thread"
    }
    Assert-True ($deleteThread.ok -eq $true) "Clear thread menu action failed."

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

    Initialize-SmokeGitRepo -Path $tempProjectPath

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
    $tempProjectId = $state.projectId
    Add-Check "new-project-dialog" "new project added at $tempProjectPath"

    $tempProject = Get-ProjectById -State $state -ProjectId $tempProjectId
    $tempThread = Get-ThreadById -Project $tempProject -ThreadId $state.activeThreadId
    $tempTerminalTab = @($tempThread.tabs) | Where-Object { $_.kind -eq "terminal" } | Select-Object -First 1
    Assert-True ($null -ne $tempTerminalTab) "Temporary project did not create an initial terminal tab."

    $tempTerminalReady = Wait-Until -FailureMessage "Temporary project terminal did not become ready with shell output." -Condition {
        $terminalState = Invoke-AutomationPost "/terminal-state" @{ tabId = $tempTerminalTab.id }
        $snapshot = @($terminalState.tabs) | Where-Object { $_.tabId -eq $tempTerminalTab.id } | Select-Object -First 1
        if (Test-TerminalReadySnapshot $snapshot) {
            return $snapshot
        }

        return $null
    } -Attempts 80 -DelayMilliseconds 250
    Add-Check "new-project-terminal-state" "$($tempTerminalReady.cols)x$($tempTerminalReady.rows) shell output visible for new WSL project"

    $refreshTempDiff = Invoke-AutomationPost "/action" @{
        action = "refreshDiff"
    }
    Assert-True ($refreshTempDiff.ok -eq $true) "Could not refresh the temporary project's diff state."

    $state = Wait-Until -FailureMessage "Temporary git project did not report the expected changed file." -Condition {
        $latestState = Invoke-AutomationGet "/state"
        if ($latestState.projectPath -eq $tempProjectPath -and
            $latestState.changedFileCount -ge 1 -and
            $latestState.selectedDiffPath -eq "notes.txt") {
            return $latestState
        }

        return $null
    }

    $openPatchReview = Invoke-AutomationPost "/action" @{
        action = "selectDiffFile"
        value = "notes.txt"
    }
    Assert-True ($openPatchReview.ok -eq $true) "Could not open the temporary project's patch review pane."

    $state = Wait-Until -FailureMessage "Temporary project diff pane did not become the active pane." -Condition {
        $latestState = Invoke-AutomationGet "/state"
        $latestProject = Get-ProjectById -State $latestState -ProjectId $latestState.projectId
        $latestThread = Get-ThreadById -Project $latestProject -ThreadId $latestState.activeThreadId
        $activePane = @($latestThread.panes) | Where-Object { $_.id -eq $latestState.activeTabId } | Select-Object -First 1
        if ($latestState.projectPath -eq $tempProjectPath -and
            $null -ne $activePane -and
            $activePane.kind -eq "diff" -and
            $latestThread.layout -eq "dual") {
            return $latestState
        }

        return $null
    }

    $diffSnapshot = Wait-Until -FailureMessage "Diff-state did not expose the rendered patch lines for notes.txt." -Condition {
        $diffState = Invoke-AutomationPost "/diff-state" @{
            paneId = $state.activeTabId
            maxLines = 80
        }
        $snapshot = @($diffState.panes) | Where-Object { $_.paneId -eq $state.activeTabId } | Select-Object -First 1
        if ($null -eq $snapshot) {
            return $null
        }

        if ($snapshot.path -eq "notes.txt" -and $snapshot.hasDiff -eq $true -and @($snapshot.lines).Count -gt 0) {
            return $snapshot
        }

        return $null
    }

    $removedLine = @($diffSnapshot.lines) | Where-Object { $_.text -eq "-beta" -and $_.kind -eq "deletion" } | Select-Object -First 1
    $addedLine = @($diffSnapshot.lines) | Where-Object { $_.text -eq "+beta updated" -and $_.kind -eq "addition" } | Select-Object -First 1
    $hunkLine = @($diffSnapshot.lines) | Where-Object { $_.kind -eq "hunk" -and $_.text -like "@@*" } | Select-Object -First 1
    $metadataLine = @($diffSnapshot.lines) | Where-Object { $_.kind -eq "metadata" -and $_.text -eq "--- a/notes.txt" } | Select-Object -First 1

    Assert-True ($null -ne $removedLine) "Patch review did not render the deleted line for notes.txt."
    Assert-True ($null -ne $addedLine) "Patch review did not render the added line for notes.txt."
    Assert-True ($null -ne $hunkLine) "Patch review did not render a hunk header for notes.txt."
    Assert-True ($null -ne $metadataLine) "Patch review did not render diff metadata for notes.txt."
    Assert-True ($removedLine.foreground -eq "#FFDC2626") "Patch review used the wrong light-theme color for deleted lines."
    Assert-True ($addedLine.foreground -eq "#FF16A34A") "Patch review used the wrong light-theme color for added lines."
    Assert-True ($hunkLine.foreground -eq "#FF2563EB") "Patch review used the wrong light-theme color for hunk headers."
    Assert-True ($metadataLine.foreground -eq "#FF52525B") "Patch review used the wrong light-theme color for metadata lines."
    Add-Check "patch-render" "notes.txt rendered $($diffSnapshot.lineCount) line(s) with expected add/remove/hunk colors"

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

    $clearProjectThreads = Invoke-AutomationPost "/ui-action" @{
        action = "invokeMenuItem"
        automationId = "shell-project-$($state.projectId)"
        menuItemText = "Clear all threads"
    }
    Assert-True ($clearProjectThreads.ok -eq $true) "Clear all threads menu action failed."

    $state = Wait-Until -FailureMessage "Project did not return to the empty-thread state after clearProjectThreads." -Condition {
        $latestState = Invoke-AutomationGet "/state"
        $latestProject = Get-ProjectById -State $latestState -ProjectId $latestState.projectId
        if ($latestProject.rootPath -eq $tempProjectPath -and @($latestProject.threads).Count -eq 0 -and [string]::IsNullOrWhiteSpace($latestState.activeThreadId)) {
            return $latestState
        }

        return $null
    }

    $clearTree = Wait-Until -FailureMessage "Thread rows were still visible after clearProjectThreads." -Condition {
        $tree = Invoke-AutomationGet "/ui-tree"
        $threadRows = @($tree.interactiveNodes | Where-Object { $_.automationId -like "shell-thread-*" -and $_.automationId -ne "shell-thread-name" })
        $emptyState = @($tree.interactiveNodes | Where-Object { $_.automationId -eq "shell-empty-new-thread" })
        if ($threadRows.Count -eq 0 -and $emptyState.Count -gt 0) {
            return $tree
        }

        return $null
    }
    Add-Check "clear-project-threads" "project returned to empty state without stale thread rows"

    $restoreThread = Invoke-AutomationPost "/ui-action" @{
        action = "click"
        automationId = "shell-empty-new-thread"
    }
    Assert-True ($restoreThread.ok -eq $true) "Could not create a thread after clearProjectThreads."

    $state = Wait-Until -FailureMessage "Project did not recover after clearProjectThreads." -Condition {
        $latestState = Invoke-AutomationGet "/state"
        $latestProject = Get-ProjectById -State $latestState -ProjectId $latestState.projectId
        if ($latestProject.rootPath -eq $tempProjectPath -and @($latestProject.threads).Count -eq 1 -and -not [string]::IsNullOrWhiteSpace($latestState.activeThreadId)) {
            return $latestState
        }

        return $null
    }

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

    $restoreProject = Invoke-AutomationPost "/action" @{
        action = "selectProject"
        projectId = $initialProjectId
    }
    Assert-True ($restoreProject.ok -eq $true) "Failed to restore the initial project."

    if (-not [string]::IsNullOrWhiteSpace($initialThreadId)) {
        $restoreThread = Invoke-AutomationPost "/action" @{
            action = "selectThread"
            threadId = $initialThreadId
        }
        Assert-True ($restoreThread.ok -eq $true) "Failed to restore the initial thread."
    }

    if (-not [string]::IsNullOrWhiteSpace($createdThreadId)) {
        $cleanupThread = Invoke-AutomationPost "/action" @{
            action = "deleteThread"
            threadId = $createdThreadId
        }
        Assert-True ($cleanupThread.ok -eq $true) "Failed to clean up the created smoke thread."
    }

    if (-not [string]::IsNullOrWhiteSpace($tempProjectId)) {
        $cleanupProject = Invoke-AutomationPost "/action" @{
            action = "deleteProject"
            projectId = $tempProjectId
        }
        Assert-True ($cleanupProject.ok -eq $true) "Failed to clean up the temporary smoke project."
    }

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
