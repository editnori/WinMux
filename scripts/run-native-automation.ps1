param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Tool,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ForwardArgs
)

$ErrorActionPreference = "Stop"

$port = if ($env:NATIVE_TERMINAL_AUTOMATION_PORT) { [int]$env:NATIVE_TERMINAL_AUTOMATION_PORT } else { 9331 }
$baseUrl = "http://127.0.0.1:$port"

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
    "events" {
        Invoke-RestMethod -Uri "$baseUrl/events" | ConvertTo-Json -Depth 20
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
    "terminal-state" {
        $tabId = if ($ForwardArgs.Count -gt 0) { $ForwardArgs[0] } else { "" }
        $body = @{ tabId = $tabId } | ConvertTo-Json
        Invoke-RestMethod -Method Post -Uri "$baseUrl/terminal-state" -ContentType "application/json" -Body $body | ConvertTo-Json -Depth 20
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
        throw "Unknown automation tool '$Tool'. Expected one of: health, state, ui-tree, ui-refs, desktop-windows, events, events-clear, action, ui-action, desktop-action, terminal-state, render-trace, screenshot"
    }
}
