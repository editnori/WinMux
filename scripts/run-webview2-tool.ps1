param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Tool,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ForwardArgs
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot

$toolMap = @{
    "targets" = "tools\webview2-targets.mjs"
    "screenshot" = "tools\webview2-screenshot.mjs"
    "eval" = "tools\webview2-eval.mjs"
}

if (-not $toolMap.ContainsKey($Tool)) {
    throw "Unknown tool '$Tool'. Expected one of: $($toolMap.Keys -join ', ')"
}

$toolPath = Join-Path $repoRoot $toolMap[$Tool]

Push-Location $repoRoot
try {
    & node $toolPath @ForwardArgs
}
finally {
    Pop-Location
}
