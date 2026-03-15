param(
    [int]$Port = 9331
)

$ErrorActionPreference = "Stop"
$baseUrl = "http://127.0.0.1:$Port"
$results = [System.Collections.Generic.List[object]]::new()

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

    Invoke-RestMethod -Uri "$baseUrl$Path" -TimeoutSec 10
}

function Invoke-AutomationPost {
    param(
        [string]$Path,
        [object]$Body
    )

    $json = if ($null -eq $Body) { "" } else { $Body | ConvertTo-Json -Depth 20 }
    Invoke-RestMethod -Method Post -Uri "$baseUrl$Path" -ContentType "application/json" -Body $json -TimeoutSec 10
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

function Get-TerminalSnapshot {
    param([string]$TabId)

    if ([string]::IsNullOrWhiteSpace($TabId)) {
        return $null
    }

    $terminalState = Invoke-AutomationPost "/terminal-state" @{ tabId = $TabId }
    @($terminalState.tabs) | Where-Object { $_.tabId -eq $TabId } | Select-Object -First 1
}

function Get-TextValue {
    param([object]$Value)

    if ($null -eq $Value) {
        return ""
    }

    return [string]$Value
}

function Send-TerminalInput {
    param([string]$Text)

    $response = Invoke-AutomationPost "/action" @{
        action = "input"
        value = $Text
    }

    Assert-True ($response.ok -eq $true) "Could not send terminal input."
}

function Send-TerminalLine {
    param([string]$Text)

    Send-TerminalInput ($Text + "`r")
}

try {
    $health = Invoke-AutomationGet "/health"
    Assert-True ($health.ok -eq $true) "Automation health check failed."
    Add-Check "health" "automation endpoint responded from pid $($health.pid)"

    $null = Invoke-AutomationPost "/events/clear" $null

    $newThread = Invoke-AutomationPost "/action" @{ action = "newThread" }
    Assert-True ($newThread.ok -eq $true) "Could not create agent smoke thread."
    $state = Invoke-AutomationGet "/state"
    $activeThread = @($state.threads) | Where-Object { $_.id -eq $state.activeThreadId } | Select-Object -First 1
    $terminalPane = @($activeThread.tabs) | Where-Object { $_.kind -eq "terminal" } | Select-Object -First 1
    Assert-True ($null -ne $terminalPane) "Agent smoke thread did not expose a terminal pane."

    $null = Wait-Until -FailureMessage "Selected terminal did not become ready." -Condition {
        $snapshot = Get-TerminalSnapshot -TabId $terminalPane.id
        if ($null -ne $snapshot -and $snapshot.rendererReady -eq $true -and $snapshot.started -eq $true) {
            return $snapshot
        }

        return $null
    }

    Send-TerminalLine "printenv WINMUX_BROWSER_PROFILE_MODE; printenv WINMUX_BROWSER_EVAL_URL; printenv WINMUX_BROWSER_STATE_URL; printenv WINMUX_BROWSER_BRIDGE"
    $envSnapshot = Wait-Until -FailureMessage "Terminal did not report WinMux browser environment variables." -Condition {
        $snapshot = Get-TerminalSnapshot -TabId $terminalPane.id
        $bufferTail = Get-TextValue $snapshot.bufferTail
        if ($null -ne $snapshot -and ($bufferTail -match 'shared' -and $bufferTail -match 'browser-eval' -and $bufferTail -match 'browser-state' -and $bufferTail -match 'winmux_browser_bridge.py')) {
            return $snapshot
        }

        return $null
    }
    Add-Check "terminal-env" "terminal exposes shared browser automation env vars"

    Send-TerminalLine "command -v claude >/dev/null 2>&1 && echo CLAUDE_FOUND || echo CLAUDE_MISSING"
    $claudeSnapshot = Wait-Until -FailureMessage "Terminal did not report Claude availability." -Condition {
        $snapshot = Get-TerminalSnapshot -TabId $terminalPane.id
        $bufferTail = Get-TextValue $snapshot.bufferTail
        if ($null -ne $snapshot -and ($bufferTail -match 'CLAUDE_(FOUND|MISSING)')) {
            return $snapshot
        }

        return $null
    }
    if ($claudeSnapshot.bufferTail -match 'CLAUDE_FOUND') {
        Add-Check "claude-command" "claude command is available in WSL terminal"
    }
    else {
        Add-Check "claude-command" "claude command is not installed in WSL terminal"
    }

    Send-TerminalLine "command -v codex >/dev/null 2>&1 && echo CODEX_FOUND || echo CODEX_MISSING"
    $codexSnapshot = Wait-Until -FailureMessage "Terminal did not report Codex availability." -Condition {
        $snapshot = Get-TerminalSnapshot -TabId $terminalPane.id
        $bufferTail = Get-TextValue $snapshot.bufferTail
        if ($null -ne $snapshot -and ($bufferTail -match 'CODEX_(FOUND|MISSING)')) {
            return $snapshot
        }

        return $null
    }
    if ($codexSnapshot.bufferTail -match 'CODEX_FOUND') {
        Add-Check "codex-command" "codex command is available in WSL terminal"
    }
    else {
        Add-Check "codex-command" "codex command is not installed in WSL terminal"
    }

    $newBrowserPane = Invoke-AutomationPost "/action" @{ action = "newBrowserPane" }
    Assert-True ($newBrowserPane.ok -eq $true) "Could not create browser pane for agent smoke."
    Add-Check "browser-pane" "created browser pane alongside terminal workspace"

    $selectTerminal = Invoke-AutomationPost "/action" @{
        action = "selectTab"
        tabId = $terminalPane.id
    }
    Assert-True ($selectTerminal.ok -eq $true) "Could not reselect terminal pane for browser bridge smoke."

    Send-TerminalLine "python3 `$WINMUX_BROWSER_BRIDGE state"
    $bridgeSnapshot = Wait-Until -FailureMessage "Terminal could not query WinMux browser state through the shared browser endpoint." -Condition {
        $snapshot = Get-TerminalSnapshot -TabId $terminalPane.id
        $bufferTail = Get-TextValue $snapshot.bufferTail
        $visibleText = Get-TextValue $snapshot.visibleText
        if ($null -ne $snapshot -and ($bufferTail -match 'selectedPaneId' -or $visibleText -match 'selectedPaneId')) {
            return $snapshot
        }

        return $null
    }
    Add-Check "browser-bridge" "terminal can query the live WinMux browser state over the shared automation endpoint"

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
