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
    $OutputDirectory = Join-Path $repoRoot "tmp/automation-captures/winmux-patch-review-$timestamp"
}

$tempProjectPath = Join-Path $env:TEMP ("winmux-patch-review-" + [Guid]::NewGuid().ToString("N"))
$screenshotPath = Join-Path $OutputDirectory "patch-review.png"
$recordingOutputDirectory = Join-Path $OutputDirectory "recording"
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $recordingOutputDirectory -Force | Out-Null

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
    return Invoke-RestMethod -Method Post -Uri "$baseUrl$Path" -ContentType "application/json" -Body $json -TimeoutSec 25
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

function Initialize-ReviewGitRepo {
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

    Initialize-ReviewGitRepo -Path $tempProjectPath

    $null = Invoke-AutomationPost "/action" @{
        action = "setTheme"
        value = "light"
    }
    $null = Invoke-AutomationPost "/action" @{
        action = "showTerminal"
    }

    $newProject = Invoke-AutomationPost "/action" @{
        action = "newProject"
        value = $tempProjectPath
    }
    Assert-True ($newProject.ok -eq $true) "Could not open the temporary patch-review project."

    $state = Wait-Until -FailureMessage "Temporary patch-review project did not become active." -Condition {
        $latestState = Invoke-AutomationGet "/state"
        if ($latestState.projectPath -eq $tempProjectPath -and -not [string]::IsNullOrWhiteSpace($latestState.activeThreadId)) {
            return $latestState
        }

        return $null
    }

    $refresh = Invoke-AutomationPost "/action" @{
        action = "refreshDiff"
    }
    Assert-True ($refresh.ok -eq $true) "Could not refresh the patch-review project's git state."

    $null = Wait-Until -FailureMessage "Temporary patch-review project did not report notes.txt as the selected diff." -Condition {
        $latestState = Invoke-AutomationGet "/state"
        if ($latestState.projectPath -eq $tempProjectPath -and
            $latestState.changedFileCount -ge 1 -and
            $latestState.selectedDiffPath -eq "notes.txt") {
            return $latestState
        }

        return $null
    }

    $recordingStart = Invoke-AutomationPost "/recording/start" @{
        fps = $Fps
        maxDurationMs = 4000
        jpegQuality = 80
        outputDirectory = $recordingOutputDirectory
        keepFrames = [bool]$KeepFrames
    }
    Assert-True ($recordingStart.recording -eq $true) "Native recording did not start."

    $openPatch = Invoke-AutomationPost "/action" @{
        action = "selectDiffFile"
        value = "notes.txt"
    }
    Assert-True ($openPatch.ok -eq $true) "Could not open notes.txt in the patch-review pane."

    $state = Wait-Until -FailureMessage "Patch-review pane did not become active." -Condition {
        $latestState = Invoke-AutomationGet "/state"
        $activeProject = @($latestState.projects) | Where-Object { $_.id -eq $latestState.projectId } | Select-Object -First 1
        $activeThread = @($activeProject.threads) | Where-Object { $_.id -eq $latestState.activeThreadId } | Select-Object -First 1
        $activePane = @($activeThread.panes) | Where-Object { $_.id -eq $latestState.activeTabId } | Select-Object -First 1
        if ($latestState.projectPath -eq $tempProjectPath -and
            $null -ne $activePane -and
            $activePane.kind -eq "diff" -and
            $activeThread.layout -eq "dual") {
            return $latestState
        }

        return $null
    }

    $diffState = Wait-Until -FailureMessage "Patch-review diff-state did not expose rendered lines for notes.txt." -Condition {
        $latestDiffState = Invoke-AutomationPost "/diff-state" @{
            paneId = $state.activeTabId
            maxLines = 40
        }
        $snapshot = @($latestDiffState.panes) | Where-Object { $_.paneId -eq $state.activeTabId } | Select-Object -First 1
        if ($null -eq $snapshot) {
            return $null
        }

        if ($snapshot.path -eq "notes.txt" -and
            $snapshot.hasDiff -eq $true -and
            @($snapshot.lines | Where-Object { $_.kind -eq "hunk" }).Count -gt 0) {
            return $snapshot
        }

        return $null
    }

    Start-Sleep -Milliseconds 600

    $screenshot = Invoke-AutomationPost "/screenshot" @{
        path = $screenshotPath
        annotated = $false
    }
    Assert-True ($screenshot.ok -eq $true) "Patch-review screenshot capture failed."

    $recordingStop = Invoke-AutomationPost "/recording/stop" $null
    Assert-True ($recordingStop.ok -eq $true) "Patch-review recording did not stop cleanly."

    [pscustomobject]@{
        ok = $true
        projectPath = $tempProjectPath
        outputDirectory = $OutputDirectory
        screenshotPath = $screenshot.path
        videoPath = $recordingStop.videoPath
        manifestPath = $recordingStop.manifestPath
        framesRetained = $recordingStop.framesRetained
        capturedFrames = $recordingStop.capturedFrames
        diffSnapshot = $diffState
    } | ConvertTo-Json -Depth 10
}
catch {
    [pscustomobject]@{
        ok = $false
        error = $_.Exception.Message
        outputDirectory = $OutputDirectory
    } | ConvertTo-Json -Depth 10
    exit 1
}
