param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Tool,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ForwardArgs
)

$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "native-automation-client.ps1")
$client = Initialize-WinMuxAutomationClient -Port 9331
$desktopUiaScript = Join-Path $PSScriptRoot "run-desktop-uia.ps1"

switch ($Tool) {
    "health" {
        Invoke-AutomationGet "/health" | ConvertTo-Json -Depth 10
        break
    }
    "state" {
        Invoke-AutomationGet "/state" | ConvertTo-Json -Depth 10
        break
    }
    "ui-tree" {
        Invoke-AutomationGet "/ui-tree" | ConvertTo-Json -Depth 30
        break
    }
    "desktop-windows" {
        Invoke-AutomationGet "/desktop-windows" | ConvertTo-Json -Depth 20
        break
    }
    "desktop-uia-tree" {
        $body = if ($ForwardArgs.Count -gt 0) { $ForwardArgs -join " " } else { @{ titleContains = "WinMux"; maxDepth = 4 } | ConvertTo-Json }
        & $desktopUiaScript tree $body
        break
    }
    "events" {
        Invoke-AutomationGet "/events" | ConvertTo-Json -Depth 20
        break
    }
    "perf-snapshot" {
        Invoke-AutomationGet "/perf-snapshot" | ConvertTo-Json -Depth 30
        break
    }
    "doctor" {
        Invoke-AutomationGet "/doctor" | ConvertTo-Json -Depth 40
        break
    }
    "recording-status" {
        Invoke-AutomationGet "/recording-status" | ConvertTo-Json -Depth 20
        break
    }
    "events-clear" {
        Invoke-AutomationPost "/events/clear" $null | ConvertTo-Json -Depth 10
        break
    }
    "ui-refs" {
        $response = Invoke-AutomationGet "/ui-tree"
        $response.interactiveNodes |
            Select-Object refLabel, automationId, controlType, name, text, x, y, width, height, background, foreground, margin, padding, elementId |
            ConvertTo-Json -Depth 10
        break
    }
    "action" {
        if (-not $ForwardArgs -or $ForwardArgs.Count -eq 0) {
            throw "Provide a JSON payload, for example: bun run native:action -- '{`"action`":`"togglePane`"}'"
        }

        $body = $ForwardArgs -join " "
        Invoke-AutomationPost "/action" $body | ConvertTo-Json -Depth 10
        break
    }
    "ui-action" {
        if (-not $ForwardArgs -or $ForwardArgs.Count -eq 0) {
            throw "Provide a JSON payload, for example: bun run native:ui-action -- '{`"action`":`"click`",`"automationId`":`"shell-pane-toggle`"}'"
        }

        $body = $ForwardArgs -join " "
        Invoke-AutomationPost "/ui-action" $body | ConvertTo-Json -Depth 20
        break
    }
    "desktop-action" {
        if (-not $ForwardArgs -or $ForwardArgs.Count -eq 0) {
            throw "Provide a JSON payload, for example: bun run native:desktop-action -- '{`"action`":`"focusWindow`",`"titleContains`":`"Browse`"}'"
        }

        $body = $ForwardArgs -join " "
        Invoke-AutomationPost "/desktop-action" $body | ConvertTo-Json -Depth 20
        break
    }
    "desktop-uia-action" {
        if (-not $ForwardArgs -or $ForwardArgs.Count -eq 0) {
            throw "Provide a JSON payload, for example: bun run native:desktop-uia-action -- '{`"action`":`"invoke`",`"titleContains`":`"Browse for Folder`",`"name`":`"OK`"}'"
        }

        $body = $ForwardArgs -join " "
        & $desktopUiaScript action $body
        break
    }
    "terminal-state" {
        $tabId = if ($ForwardArgs.Count -gt 0) { $ForwardArgs[0] } else { "" }
        $body = @{ tabId = $tabId } | ConvertTo-Json
        Invoke-AutomationPost "/terminal-state" $body | ConvertTo-Json -Depth 20
        break
    }
    "browser-state" {
        $paneId = if ($ForwardArgs.Count -gt 0) { $ForwardArgs[0] } else { "" }
        $body = @{ paneId = $paneId } | ConvertTo-Json
        Invoke-AutomationPost "/browser-state" $body | ConvertTo-Json -Depth 20
        break
    }
    "diff-state" {
        $paneId = if ($ForwardArgs.Count -gt 0) { $ForwardArgs[0] } else { "" }
        $maxLines = if ($ForwardArgs.Count -gt 1) { [int]$ForwardArgs[1] } else { 0 }
        $body = @{ paneId = $paneId; maxLines = $maxLines } | ConvertTo-Json -Depth 10
        Invoke-AutomationPost "/diff-state" $body | ConvertTo-Json -Depth 30
        break
    }
    "editor-state" {
        $paneId = if ($ForwardArgs.Count -gt 0) { $ForwardArgs[0] } else { "" }
        $maxChars = if ($ForwardArgs.Count -gt 1) { [int]$ForwardArgs[1] } else { 0 }
        $maxFiles = if ($ForwardArgs.Count -gt 2) { [int]$ForwardArgs[2] } else { 0 }
        $body = @{ paneId = $paneId; maxChars = $maxChars; maxFiles = $maxFiles } | ConvertTo-Json -Depth 10
        Invoke-AutomationPost "/editor-state" $body | ConvertTo-Json -Depth 30
        break
    }
    "browser-eval" {
        $paneId = ""
        $script = "document.title"
        if ($ForwardArgs.Count -eq 1) {
            $script = $ForwardArgs[0]
        }
        elseif ($ForwardArgs.Count -gt 1) {
            $paneId = $ForwardArgs[0]
            $script = ($ForwardArgs[1..($ForwardArgs.Count - 1)] -join " ")
        }
        $body = @{ paneId = $paneId; script = $script } | ConvertTo-Json -Depth 10
        Invoke-AutomationPost "/browser-eval" $body | ConvertTo-Json -Depth 20
        break
    }
    "browser-screenshot" {
        $paneId = if ($ForwardArgs.Count -gt 0) { $ForwardArgs[0] } else { "" }
        $path = if ($ForwardArgs.Count -gt 1) { $ForwardArgs[1] } else { "" }
        $body = @{ paneId = $paneId; path = $path } | ConvertTo-Json -Depth 10
        Invoke-AutomationPost "/browser-screenshot" $body | ConvertTo-Json -Depth 20
        break
    }
    "recording-start" {
        $body = if ($ForwardArgs.Count -gt 0) { $ForwardArgs -join " " } else { @{ fps = 24; maxDurationMs = 5000; jpegQuality = 82 } | ConvertTo-Json }
        Invoke-AutomationPost "/recording/start" $body | ConvertTo-Json -Depth 20
        break
    }
    "recording-stop" {
        Invoke-AutomationPost "/recording/stop" "" | ConvertTo-Json -Depth 20
        break
    }
    "render-trace" {
        $body = if ($ForwardArgs.Count -gt 0) { $ForwardArgs -join " " } else { @{ frames = 8; captureScreenshots = $true; annotated = $false } | ConvertTo-Json }
        Invoke-AutomationPost "/render-trace" $body | ConvertTo-Json -Depth 30
        break
    }
    "screenshot" {
        $path = if ($ForwardArgs.Count -gt 0) { $ForwardArgs[0] } else { "" }
        $annotated = $false
        if ($ForwardArgs.Count -gt 1) {
            $annotated = [System.Convert]::ToBoolean($ForwardArgs[1])
        }
        $body = @{ path = $path; annotated = $annotated } | ConvertTo-Json
        Invoke-AutomationPost "/screenshot" $body | ConvertTo-Json -Depth 10
        break
    }
    default {
        throw "Unknown automation tool '$Tool'. Expected one of: health, state, ui-tree, ui-refs, desktop-windows, desktop-uia-tree, events, perf-snapshot, doctor, events-clear, recording-status, action, ui-action, desktop-action, desktop-uia-action, terminal-state, browser-state, diff-state, editor-state, browser-eval, browser-screenshot, recording-start, recording-stop, render-trace, screenshot"
    }
}
