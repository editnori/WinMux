param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Tool,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ForwardArgs
)

$ErrorActionPreference = "Stop"

$port = if ($env:NATIVE_TERMINAL_AUTOMATION_PORT) { [int]$env:NATIVE_TERMINAL_AUTOMATION_PORT } else { 9331 }
$baseUrl = "http://127.0.0.1:$port"
$desktopUiaScript = Join-Path $PSScriptRoot "run-desktop-uia.ps1"

switch ($Tool) {
    "health" {
        Invoke-RestMethod -Uri "$baseUrl/health" | ConvertTo-Json -Depth 10
        break
    }
    "state" {
        Invoke-RestMethod -Uri "$baseUrl/state" | ConvertTo-Json -Depth 10
        break
    }
    "ui-tree" {
        Invoke-RestMethod -Uri "$baseUrl/ui-tree" | ConvertTo-Json -Depth 30
        break
    }
    "desktop-windows" {
        Invoke-RestMethod -Uri "$baseUrl/desktop-windows" | ConvertTo-Json -Depth 20
        break
    }
    "desktop-uia-tree" {
        $body = if ($ForwardArgs.Count -gt 0) { $ForwardArgs -join " " } else { @{ titleContains = "WinMux"; maxDepth = 4 } | ConvertTo-Json }
        & $desktopUiaScript tree $body
        break
    }
    "events" {
        Invoke-RestMethod -Uri "$baseUrl/events" | ConvertTo-Json -Depth 20
        break
    }
    "recording-status" {
        Invoke-RestMethod -Uri "$baseUrl/recording-status" | ConvertTo-Json -Depth 20
        break
    }
    "events-clear" {
        Invoke-RestMethod -Method Post -Uri "$baseUrl/events/clear" | ConvertTo-Json -Depth 10
        break
    }
    "ui-refs" {
        $response = Invoke-RestMethod -Uri "$baseUrl/ui-tree"
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
        Invoke-RestMethod -Method Post -Uri "$baseUrl/action" -ContentType "application/json" -Body $body | ConvertTo-Json -Depth 10
        break
    }
    "ui-action" {
        if (-not $ForwardArgs -or $ForwardArgs.Count -eq 0) {
            throw "Provide a JSON payload, for example: bun run native:ui-action -- '{`"action`":`"click`",`"automationId`":`"shell-pane-toggle`"}'"
        }

        $body = $ForwardArgs -join " "
        Invoke-RestMethod -Method Post -Uri "$baseUrl/ui-action" -ContentType "application/json" -Body $body | ConvertTo-Json -Depth 20
        break
    }
    "desktop-action" {
        if (-not $ForwardArgs -or $ForwardArgs.Count -eq 0) {
            throw "Provide a JSON payload, for example: bun run native:desktop-action -- '{`"action`":`"focusWindow`",`"titleContains`":`"Browse`"}'"
        }

        $body = $ForwardArgs -join " "
        Invoke-RestMethod -Method Post -Uri "$baseUrl/desktop-action" -ContentType "application/json" -Body $body | ConvertTo-Json -Depth 20
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
        Invoke-RestMethod -Method Post -Uri "$baseUrl/terminal-state" -ContentType "application/json" -Body $body | ConvertTo-Json -Depth 20
        break
    }
    "browser-state" {
        $paneId = if ($ForwardArgs.Count -gt 0) { $ForwardArgs[0] } else { "" }
        $body = @{ paneId = $paneId } | ConvertTo-Json
        Invoke-RestMethod -Method Post -Uri "$baseUrl/browser-state" -ContentType "application/json" -Body $body | ConvertTo-Json -Depth 20
        break
    }
    "diff-state" {
        $paneId = if ($ForwardArgs.Count -gt 0) { $ForwardArgs[0] } else { "" }
        $maxLines = if ($ForwardArgs.Count -gt 1) { [int]$ForwardArgs[1] } else { 0 }
        $body = @{ paneId = $paneId; maxLines = $maxLines } | ConvertTo-Json -Depth 10
        Invoke-RestMethod -Method Post -Uri "$baseUrl/diff-state" -ContentType "application/json" -Body $body | ConvertTo-Json -Depth 30
        break
    }
    "editor-state" {
        $paneId = if ($ForwardArgs.Count -gt 0) { $ForwardArgs[0] } else { "" }
        $maxChars = if ($ForwardArgs.Count -gt 1) { [int]$ForwardArgs[1] } else { 0 }
        $maxFiles = if ($ForwardArgs.Count -gt 2) { [int]$ForwardArgs[2] } else { 0 }
        $body = @{ paneId = $paneId; maxChars = $maxChars; maxFiles = $maxFiles } | ConvertTo-Json -Depth 10
        Invoke-RestMethod -Method Post -Uri "$baseUrl/editor-state" -ContentType "application/json" -Body $body | ConvertTo-Json -Depth 30
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
        Invoke-RestMethod -Method Post -Uri "$baseUrl/browser-eval" -ContentType "application/json" -Body $body | ConvertTo-Json -Depth 20
        break
    }
    "browser-screenshot" {
        $paneId = if ($ForwardArgs.Count -gt 0) { $ForwardArgs[0] } else { "" }
        $path = if ($ForwardArgs.Count -gt 1) { $ForwardArgs[1] } else { "" }
        $body = @{ paneId = $paneId; path = $path } | ConvertTo-Json -Depth 10
        Invoke-RestMethod -Method Post -Uri "$baseUrl/browser-screenshot" -ContentType "application/json" -Body $body | ConvertTo-Json -Depth 20
        break
    }
    "recording-start" {
        $body = if ($ForwardArgs.Count -gt 0) { $ForwardArgs -join " " } else { @{ fps = 24; maxDurationMs = 5000; jpegQuality = 82 } | ConvertTo-Json }
        Invoke-RestMethod -Method Post -Uri "$baseUrl/recording/start" -ContentType "application/json" -Body $body | ConvertTo-Json -Depth 20
        break
    }
    "recording-stop" {
        Invoke-RestMethod -Method Post -Uri "$baseUrl/recording/stop" -ContentType "application/json" -Body "" | ConvertTo-Json -Depth 20
        break
    }
    "render-trace" {
        $body = if ($ForwardArgs.Count -gt 0) { $ForwardArgs -join " " } else { @{ frames = 8; captureScreenshots = $true; annotated = $false } | ConvertTo-Json }
        Invoke-RestMethod -Method Post -Uri "$baseUrl/render-trace" -ContentType "application/json" -Body $body | ConvertTo-Json -Depth 30
        break
    }
    "screenshot" {
        $path = if ($ForwardArgs.Count -gt 0) { $ForwardArgs[0] } else { "" }
        $annotated = $false
        if ($ForwardArgs.Count -gt 1) {
            $annotated = [System.Convert]::ToBoolean($ForwardArgs[1])
        }
        $body = @{ path = $path; annotated = $annotated } | ConvertTo-Json
        Invoke-RestMethod -Method Post -Uri "$baseUrl/screenshot" -ContentType "application/json" -Body $body | ConvertTo-Json -Depth 10
        break
    }
    default {
        throw "Unknown automation tool '$Tool'. Expected one of: health, state, ui-tree, ui-refs, desktop-windows, desktop-uia-tree, events, events-clear, recording-status, action, ui-action, desktop-action, desktop-uia-action, terminal-state, browser-state, diff-state, editor-state, browser-eval, browser-screenshot, recording-start, recording-stop, render-trace, screenshot"
    }
}
