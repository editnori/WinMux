param(
    [int]$Port = 9331,
    [int]$Iterations = 8
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "native-automation-client.ps1")
Initialize-WinMuxAutomationClient -Port $Port | Out-Null
$metrics = [System.Collections.Generic.List[object]]::new()
$tempProjectPath = Join-Path $env:TEMP ("winmux-stress-" + [Guid]::NewGuid().ToString("N"))
$initialProjectId = $null
$initialThreadId = $null
$tempProjectId = $null

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Wait-Until {
    param(
        [scriptblock]$Condition,
        [string]$FailureMessage,
        [int]$Attempts = 40,
        [int]$DelayMilliseconds = 150
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

function Measure-Step {
    param(
        [string]$Name,
        [int]$Iteration,
        [scriptblock]$Action
    )

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $result = & $Action
    $sw.Stop()
    $metrics.Add([pscustomobject]@{
            name = $Name
            iteration = $Iteration
            ms = $sw.ElapsedMilliseconds
        }) | Out-Null
    return $result
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

function Get-ActiveThread {
    param([object]$State)

    $project = Get-ProjectById -State $State -ProjectId $State.projectId
    return Get-ThreadById -Project $project -ThreadId $State.activeThreadId
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

function Initialize-StressGitRepo {
    param([string]$Path)

    New-Item -ItemType Directory -Path $Path -Force | Out-Null
    Invoke-Git -C $Path init | Out-Null
    Invoke-Git -C $Path config user.email "winmux-stress@example.com" | Out-Null
    Invoke-Git -C $Path config user.name "WinMux Stress" | Out-Null

    @(
        "alpha"
        "beta"
        "gamma"
    ) | Set-Content -Path (Join-Path $Path "notes.txt")

    @(
        "task one"
        "task two"
        "task three"
    ) | Set-Content -Path (Join-Path $Path "todo.md")

    Invoke-Git -C $Path add notes.txt todo.md | Out-Null
    Invoke-Git -C $Path commit -m "Initial stress snapshot" | Out-Null

    @(
        "alpha"
        "beta updated"
        "gamma"
        "delta"
    ) | Set-Content -Path (Join-Path $Path "notes.txt")

    @(
        "task one"
        "task two updated"
        "task three"
        "task four"
    ) | Set-Content -Path (Join-Path $Path "todo.md")
}

function Get-MetricSummary {
    param([System.Collections.Generic.List[object]]$Samples)

    return $Samples |
        Group-Object name |
        ForEach-Object {
            $values = @($_.Group.ms)
            [pscustomobject]@{
                name = $_.Name
                count = $values.Count
                avgMs = [math]::Round((($values | Measure-Object -Average).Average), 1)
                maxMs = ($values | Measure-Object -Maximum).Maximum
            }
        } |
        Sort-Object avgMs -Descending
}

try {
    $null = Wait-Until -FailureMessage "Automation health endpoint did not respond." -Condition {
        try {
            $health = Invoke-AutomationGet "/health"
            if ($health.ok -eq $true) {
                return $health
            }
        }
        catch {
        }

        return $null
    } -Attempts 50 -DelayMilliseconds 250

    $null = Invoke-AutomationPost "/events/clear" $null
    $initialState = Invoke-AutomationGet "/state"
    $initialProjectId = $initialState.projectId
    $initialThreadId = $initialState.activeThreadId

    Initialize-StressGitRepo -Path $tempProjectPath

    $newProject = Measure-Step -Name "setup:newProject" -Iteration 0 -Action {
        Invoke-AutomationPost "/action" @{
            action = "newProject"
            value = $tempProjectPath
        }
    }
    Assert-True ($newProject.ok -eq $true) "Could not create the temporary stress project."

    $state = Wait-Until -FailureMessage "Temporary stress project did not become active." -Condition {
        $latestState = Invoke-AutomationGet "/state"
        if ($latestState.projectPath -eq $tempProjectPath -and -not [string]::IsNullOrWhiteSpace($latestState.activeThreadId)) {
            return $latestState
        }

        return $null
    }
    $tempProjectId = $state.projectId

    $refresh = Measure-Step -Name "setup:refreshDiff" -Iteration 0 -Action {
        Invoke-AutomationPost "/action" @{ action = "refreshDiff" }
    }
    Assert-True ($refresh.ok -eq $true) "Could not refresh diff state for the stress project."

    $state = Wait-Until -FailureMessage "Stress project did not expose both changed files." -Condition {
        $latestState = Invoke-AutomationGet "/state"
        if ($latestState.projectPath -eq $tempProjectPath -and $latestState.changedFileCount -ge 2) {
            return $latestState
        }

        return $null
    }

    $newEditorPane = Measure-Step -Name "setup:newEditorPane" -Iteration 0 -Action {
        Invoke-AutomationPost "/action" @{ action = "newEditorPane" }
    }
    Assert-True ($newEditorPane.ok -eq $true) "Could not create an editor pane for stress."

    $newBrowserPane = Measure-Step -Name "setup:newBrowserPane" -Iteration 0 -Action {
        Invoke-AutomationPost "/action" @{ action = "newBrowserPane" }
    }
    Assert-True ($newBrowserPane.ok -eq $true) "Could not create a browser pane for stress."

    $state = Wait-Until -FailureMessage "Editor and browser panes did not appear on the active thread." -Condition {
        $latestState = Invoke-AutomationGet "/state"
        $thread = Get-ActiveThread -State $latestState
        $kinds = @($thread.tabs | ForEach-Object { $_.kind })
        if ($latestState.projectPath -eq $tempProjectPath -and
            $kinds -contains "editor" -and
            $kinds -contains "browser") {
            return $latestState
        }

        return $null
    }

    $openDiffPane = Measure-Step -Name "setup:selectDiffFile" -Iteration 0 -Action {
        Invoke-AutomationPost "/action" @{
            action = "selectDiffFile"
            value = "notes.txt"
        }
    }
    Assert-True ($openDiffPane.ok -eq $true) "Could not create the diff pane for stress."

    $state = Wait-Until -FailureMessage "Diff pane did not become available on the active thread." -Condition {
        $latestState = Invoke-AutomationGet "/state"
        $thread = Get-ActiveThread -State $latestState
        $kinds = @($thread.tabs | ForEach-Object { $_.kind })
        if ($latestState.projectPath -eq $tempProjectPath -and $kinds -contains "diff") {
            return $latestState
        }

        return $null
    }

    $browserReady = Wait-Until -FailureMessage "Browser pane did not initialize for the stress thread." -Condition {
        $browserState = Invoke-AutomationPost "/browser-state" @{}
        $browserPane = @($browserState.panes) | Where-Object { $_.initialized -eq $true } | Select-Object -First 1
        if ($null -ne $browserPane) {
            return $browserPane
        }

        return $null
    } -Attempts 60 -DelayMilliseconds 250

    function Get-StressTabs {
        $latestState = Invoke-AutomationGet "/state"
        $latestThread = Get-ActiveThread -State $latestState
        Assert-True ($null -ne $latestThread) "Stress thread is missing."

        $terminalTab = @($latestThread.tabs) | Where-Object { $_.kind -eq "terminal" } | Select-Object -First 1
        $editorTab = @($latestThread.tabs) | Where-Object { $_.kind -eq "editor" } | Select-Object -First 1
        $browserTab = @($latestThread.tabs) | Where-Object { $_.kind -eq "browser" } | Select-Object -First 1
        $diffTab = @($latestThread.tabs) | Where-Object { $_.kind -eq "diff" } | Select-Object -First 1

        Assert-True ($null -ne $terminalTab) "Stress thread is missing a terminal tab."
        Assert-True ($null -ne $editorTab) "Stress thread is missing an editor tab."
        Assert-True ($null -ne $browserTab) "Stress thread is missing a browser tab."
        Assert-True ($null -ne $diffTab) "Stress thread is missing a diff tab."

        return [pscustomobject]@{
            state = $latestState
            thread = $latestThread
            terminal = $terminalTab
            editor = $editorTab
            browser = $browserTab
            diff = $diffTab
        }
    }

    $layouts = @("solo", "dual", "triple", "quad")
    $diffTargets = @("notes.txt", "todo.md")

    for ($iteration = 1; $iteration -le $Iterations; $iteration++) {
        $layout = $layouts[($iteration - 1) % $layouts.Count]
        $diffTarget = $diffTargets[($iteration - 1) % $diffTargets.Count]
        $tabSnapshot = Get-StressTabs

        $null = Measure-Step -Name "showSettings" -Iteration $iteration -Action {
            $response = Invoke-AutomationPost "/action" @{ action = "showSettings" }
            Assert-True ($response.ok -eq $true) "showSettings failed."
            Wait-Until -FailureMessage "Settings view did not become active." -Condition {
                $latestState = Invoke-AutomationGet "/state"
                if ($latestState.activeView -eq "settings") {
                    return $latestState
                }

                return $null
            } -Attempts 25 -DelayMilliseconds 120
        }

        $null = Measure-Step -Name "showTerminal" -Iteration $iteration -Action {
            $response = Invoke-AutomationPost "/action" @{ action = "showTerminal" }
            Assert-True ($response.ok -eq $true) "showTerminal failed."
            Wait-Until -FailureMessage "Terminal view did not become active." -Condition {
                $latestState = Invoke-AutomationGet "/state"
                if ($latestState.activeView -eq "terminal") {
                    return $latestState
                }

                return $null
            } -Attempts 25 -DelayMilliseconds 120
        }

        $beforeState = Invoke-AutomationGet "/state"
        $null = Measure-Step -Name "toggleInspector:close" -Iteration $iteration -Action {
            $response = Invoke-AutomationPost "/action" @{ action = "toggleInspector" }
            Assert-True ($response.ok -eq $true) "toggleInspector close failed."
            Wait-Until -FailureMessage "Inspector did not close." -Condition {
                $latestState = Invoke-AutomationGet "/state"
                if ($latestState.inspectorOpen -eq $false) {
                    return $latestState
                }

                return $null
            } -Attempts 20 -DelayMilliseconds 120
        }

        $null = Measure-Step -Name "toggleInspector:open" -Iteration $iteration -Action {
            $response = Invoke-AutomationPost "/action" @{ action = "toggleInspector" }
            Assert-True ($response.ok -eq $true) "toggleInspector open failed."
            Wait-Until -FailureMessage "Inspector did not reopen." -Condition {
                $latestState = Invoke-AutomationGet "/state"
                if ($latestState.inspectorOpen -eq $true) {
                    return $latestState
                }

                return $null
            } -Attempts 20 -DelayMilliseconds 120
        }

        $null = Measure-Step -Name "togglePane:close" -Iteration $iteration -Action {
            $response = Invoke-AutomationPost "/action" @{ action = "togglePane" }
            Assert-True ($response.ok -eq $true) "togglePane close failed."
            Wait-Until -FailureMessage "Sidebar did not close." -Condition {
                $latestState = Invoke-AutomationGet "/state"
                if ($latestState.paneOpen -eq $false) {
                    return $latestState
                }

                return $null
            } -Attempts 20 -DelayMilliseconds 120
        }

        $null = Measure-Step -Name "togglePane:open" -Iteration $iteration -Action {
            $response = Invoke-AutomationPost "/action" @{ action = "togglePane" }
            Assert-True ($response.ok -eq $true) "togglePane open failed."
            Wait-Until -FailureMessage "Sidebar did not reopen." -Condition {
                $latestState = Invoke-AutomationGet "/state"
                if ($latestState.paneOpen -eq $true) {
                    return $latestState
                }

                return $null
            } -Attempts 20 -DelayMilliseconds 120
        }

        foreach ($tab in @($tabSnapshot.terminal, $tabSnapshot.editor, $tabSnapshot.browser, $tabSnapshot.diff)) {
            $tabName = "selectTab:$($tab.kind)"
            $null = Measure-Step -Name $tabName -Iteration $iteration -Action {
                $response = Invoke-AutomationPost "/action" @{
                    action = "selectTab"
                    tabId = $tab.id
                }
                Assert-True ($response.ok -eq $true) "selectTab failed for $($tab.kind)."
                Wait-Until -FailureMessage "Active tab did not switch to $($tab.kind)." -Condition {
                    $latestState = Invoke-AutomationGet "/state"
                    if ($latestState.activeTabId -eq $tab.id) {
                        return $latestState
                    }

                    return $null
                } -Attempts 20 -DelayMilliseconds 120
            }
        }

        $null = Measure-Step -Name "setLayout:$layout" -Iteration $iteration -Action {
            $response = Invoke-AutomationPost "/action" @{
                action = "setLayout"
                value = $layout
            }
            Assert-True ($response.ok -eq $true) "setLayout failed for $layout."
            Wait-Until -FailureMessage "Layout did not switch to $layout." -Condition {
                $latestState = Invoke-AutomationGet "/state"
                $threadState = Get-ActiveThread -State $latestState
                if ($threadState.layout -eq $layout) {
                    return $threadState
                }

                return $null
            } -Attempts 20 -DelayMilliseconds 120
        }

        $null = Measure-Step -Name "fitPanes" -Iteration $iteration -Action {
            $response = Invoke-AutomationPost "/action" @{ action = "fitPanes" }
            Assert-True ($response.ok -eq $true) "fitPanes failed."
        }

        $null = Measure-Step -Name "selectDiffFile" -Iteration $iteration -Action {
            $response = Invoke-AutomationPost "/action" @{
                action = "selectDiffFile"
                value = $diffTarget
            }
            Assert-True ($response.ok -eq $true) "selectDiffFile failed for $diffTarget."
            Wait-Until -FailureMessage "Diff pane did not render $diffTarget." -Condition {
                $latestState = Invoke-AutomationGet "/state"
                $diffState = Invoke-AutomationPost "/diff-state" @{
                    paneId = $latestState.activeTabId
                    maxLines = 30
                }
                $snapshot = @($diffState.panes) | Where-Object { $_.paneId -eq $latestState.activeTabId } | Select-Object -First 1
                if ($null -ne $snapshot -and $snapshot.path -eq $diffTarget -and $snapshot.hasDiff -eq $true -and $snapshot.lineCount -gt 0) {
                    return $snapshot
                }

                return $null
            } -Attempts 25 -DelayMilliseconds 150
        }

        $null = Measure-Step -Name "browserState" -Iteration $iteration -Action {
            $browserState = Invoke-AutomationPost "/browser-state" @{}
            Assert-True (@($browserState.panes | Where-Object { $_.initialized -eq $true }).Count -gt 0) "browser-state did not report an initialized browser pane."
        }

        if ($iteration % 2 -eq 0) {
            $null = Measure-Step -Name "uiTree" -Iteration $iteration -Action {
                $tree = Invoke-AutomationGet "/ui-tree"
                Assert-True (@($tree.interactiveNodes).Count -gt 0) "ui-tree returned no interactive nodes."
            }
        }
    }

    $health = Invoke-AutomationGet "/health"
    $events = Invoke-AutomationGet "/events"
    $summary = Get-MetricSummary -Samples $metrics

    [pscustomobject]@{
        ok = $true
        pid = $health.pid
        iterations = $Iterations
        tempProjectPath = $tempProjectPath
        summary = $summary
        metricCount = $metrics.Count
        eventCount = @($events.events).Count
    } | ConvertTo-Json -Depth 10
}
catch {
    [pscustomobject]@{
        ok = $false
        error = $_.Exception.Message
        tempProjectPath = $tempProjectPath
        summary = Get-MetricSummary -Samples $metrics
        metricCount = $metrics.Count
    } | ConvertTo-Json -Depth 10
    exit 1
}
finally {
    try {
        if (-not [string]::IsNullOrWhiteSpace($initialProjectId)) {
            $null = Invoke-AutomationPost "/action" @{
                action = "selectProject"
                projectId = $initialProjectId
            }
        }

        if (-not [string]::IsNullOrWhiteSpace($initialThreadId)) {
            $null = Invoke-AutomationPost "/action" @{
                action = "selectThread"
                threadId = $initialThreadId
            }
        }

        if (-not [string]::IsNullOrWhiteSpace($tempProjectId)) {
            $null = Invoke-AutomationPost "/action" @{
                action = "deleteProject"
                projectId = $tempProjectId
            }
        }
    }
    catch {
    }
}
