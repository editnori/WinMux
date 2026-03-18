function Get-WinMuxAutomationSessionFilePath {
    $localAppData = if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        $env:LOCALAPPDATA
    }
    else {
        [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    }

    return Join-Path $localAppData "WinMux\automation-session.json"
}

function Resolve-WinMuxAutomationClient {
    param(
        [int]$PreferredPort = 9331
    )

    $port = if ($env:NATIVE_TERMINAL_AUTOMATION_PORT) {
        [int]$env:NATIVE_TERMINAL_AUTOMATION_PORT
    }
    elseif ($env:WINMUX_AUTOMATION_PORT) {
        [int]$env:WINMUX_AUTOMATION_PORT
    }
    else {
        $PreferredPort
    }

    $token = if (-not [string]::IsNullOrWhiteSpace($env:NATIVE_TERMINAL_AUTOMATION_TOKEN)) {
        $env:NATIVE_TERMINAL_AUTOMATION_TOKEN.Trim()
    }
    elseif (-not [string]::IsNullOrWhiteSpace($env:WINMUX_AUTOMATION_TOKEN)) {
        $env:WINMUX_AUTOMATION_TOKEN.Trim()
    }
    else {
        ""
    }

    $sessionFilePath = Get-WinMuxAutomationSessionFilePath
    if (Test-Path $sessionFilePath) {
        try {
            $session = Get-Content -LiteralPath $sessionFilePath -Raw | ConvertFrom-Json
            if ($null -ne $session) {
                if (-not $env:NATIVE_TERMINAL_AUTOMATION_PORT -and -not $env:WINMUX_AUTOMATION_PORT -and $session.port) {
                    $port = [int]$session.port
                }

                if ([string]::IsNullOrWhiteSpace($token) -and -not [string]::IsNullOrWhiteSpace($session.token)) {
                    $token = $session.token.Trim()
                }
            }
        }
        catch {
        }
    }

    $headers = @{}
    if (-not [string]::IsNullOrWhiteSpace($token)) {
        $headers["X-WinMux-Automation-Token"] = $token
    }

    return [pscustomobject]@{
        Port = $port
        BaseUrl = "http://127.0.0.1:$port"
        Token = $token
        Headers = $headers
        SessionFilePath = $sessionFilePath
    }
}

function Initialize-WinMuxAutomationClient {
    param(
        [int]$Port = 9331
    )

    $script:WinMuxAutomationPreferredPort = $Port
    return Get-WinMuxAutomationClient -Port $Port
}

function Get-WinMuxAutomationClient {
    param(
        [int]$Port = 9331
    )

    $preferredPort = if ($script:WinMuxAutomationPreferredPort) { $script:WinMuxAutomationPreferredPort } else { $Port }
    $script:WinMuxAutomationClient = Resolve-WinMuxAutomationClient -PreferredPort $preferredPort
    return $script:WinMuxAutomationClient
}

function Invoke-AutomationGet {
    param(
        [string]$Path,
        [int]$TimeoutSec = 20
    )

    $client = Get-WinMuxAutomationClient
    return Invoke-RestMethod -Uri "$($client.BaseUrl)$Path" -Headers $client.Headers -TimeoutSec $TimeoutSec
}

function Invoke-AutomationPost {
    param(
        [string]$Path,
        [object]$Body,
        [int]$TimeoutSec = 25,
        [int]$JsonDepth = 20,
        [switch]$CompressJson
    )

    $client = Get-WinMuxAutomationClient
    $json = if ($null -eq $Body) {
        ""
    }
    elseif ($Body -is [string]) {
        $Body
    }
    elseif ($CompressJson) {
        $Body | ConvertTo-Json -Depth $JsonDepth -Compress
    }
    else {
        $Body | ConvertTo-Json -Depth $JsonDepth
    }

    return Invoke-RestMethod -Method Post -Uri "$($client.BaseUrl)$Path" -Headers $client.Headers -ContentType "application/json" -Body $json -TimeoutSec $TimeoutSec
}
