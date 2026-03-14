param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet("tree", "action")]
    [string]$Tool,

    [Parameter(Position = 1)]
    [string]$JsonBody = "{}"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName UIAutomationProvider

function Get-SafeValue {
    param([scriptblock]$Getter)

    try {
        return & $Getter
    }
    catch {
        return $null
    }
}

function Resolve-RootElement {
    param([object]$Request)

    if ($Request.handle) {
        $handle = if ($Request.handle.StartsWith("0x")) {
            [IntPtr]::new([Convert]::ToInt64($Request.handle.Substring(2), 16))
        }
        else {
            [IntPtr]::new([Convert]::ToInt64($Request.handle))
        }

        return [System.Windows.Automation.AutomationElement]::FromHandle($handle)
    }

    $children = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Children,
        [System.Windows.Automation.Condition]::TrueCondition)

    foreach ($child in $children) {
        $name = Get-SafeValue { $child.Current.Name }
        $className = Get-SafeValue { $child.Current.ClassName }

        $titleMatches = (-not $Request.titleContains) -or ($name -and $name.IndexOf($Request.titleContains, [System.StringComparison]::OrdinalIgnoreCase) -ge 0)
        $classMatches = (-not $Request.className) -or ($className -and $className -ieq $Request.className)

        if ($titleMatches -and $classMatches) {
            return $child
        }
    }

    throw "No matching desktop UIA root was found."
}

function Get-Children {
    param([System.Windows.Automation.AutomationElement]$Element)

    $result = @()
    try {
        $collection = $Element.FindAll(
            [System.Windows.Automation.TreeScope]::Children,
            [System.Windows.Automation.Condition]::TrueCondition)

        foreach ($child in $collection) {
            $result += $child
        }
    }
    catch {
    }

    return $result
}

function Try-GetPattern {
    param(
        [System.Windows.Automation.AutomationElement]$Element,
        [System.Windows.Automation.AutomationPattern]$Pattern
    )

    $raw = $null
    if ($Element.TryGetCurrentPattern($Pattern, [ref]$raw)) {
        return $raw
    }

    return $null
}

function Get-ElementText {
    param([System.Windows.Automation.AutomationElement]$Element)

    $valuePattern = Try-GetPattern $Element ([System.Windows.Automation.ValuePattern]::Pattern)
    if ($valuePattern) {
        return Get-SafeValue { $valuePattern.Current.Value }
    }

    return Get-SafeValue { $Element.Current.Name }
}

function Get-BoolPatternState {
    param(
        [System.Windows.Automation.AutomationElement]$Element,
        [string]$Kind
    )

    switch ($Kind) {
        "selected" {
            $pattern = Try-GetPattern $Element ([System.Windows.Automation.SelectionItemPattern]::Pattern)
            if ($pattern) {
                return [bool](Get-SafeValue { $pattern.Current.IsSelected })
            }
            return $false
        }
        "expanded" {
            $pattern = Try-GetPattern $Element ([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
            if ($pattern) {
                return (Get-SafeValue { $pattern.Current.ExpandCollapseState }) -eq [System.Windows.Automation.ExpandCollapseState]::Expanded
            }
            return $false
        }
        "checked" {
            $pattern = Try-GetPattern $Element ([System.Windows.Automation.TogglePattern]::Pattern)
            if ($pattern) {
                return (Get-SafeValue { $pattern.Current.ToggleState }) -eq [System.Windows.Automation.ToggleState]::On
            }
            return $false
        }
        default {
            return $false
        }
    }
}

function Test-IsInteractive {
    param([System.Windows.Automation.AutomationElement]$Element)

    return $null -ne (Try-GetPattern $Element ([System.Windows.Automation.InvokePattern]::Pattern) `
        ) -or $null -ne (Try-GetPattern $Element ([System.Windows.Automation.ValuePattern]::Pattern)) `
        -or $null -ne (Try-GetPattern $Element ([System.Windows.Automation.SelectionItemPattern]::Pattern)) `
        -or $null -ne (Try-GetPattern $Element ([System.Windows.Automation.ExpandCollapsePattern]::Pattern)) `
        -or $null -ne (Try-GetPattern $Element ([System.Windows.Automation.TogglePattern]::Pattern))
}

function New-UiaNode {
    param(
        [System.Windows.Automation.AutomationElement]$Element,
        [string]$Path,
        [int]$Depth,
        [int]$MaxDepth,
        [ref]$InteractiveIndex
    )

    if (-not $Element) {
        return $null
    }

    $children = @()
    if ($Depth -lt $MaxDepth) {
        $index = 0
        foreach ($child in (Get-Children $Element)) {
            $childNode = New-UiaNode -Element $child -Path "$Path/$index" -Depth ($Depth + 1) -MaxDepth $MaxDepth -InteractiveIndex $InteractiveIndex
            if ($childNode) {
                $children += $childNode
            }
            $index++
        }
    }

    $rect = Get-SafeValue { $Element.Current.BoundingRectangle }
    $interactive = Test-IsInteractive $Element
    $visible = -not [bool](Get-SafeValue { $Element.Current.IsOffscreen })

    $node = [pscustomobject]@{
        elementId = $Path
        handle = (Get-SafeValue {
                $hwnd = $Element.Current.NativeWindowHandle
                if ($hwnd -gt 0) { "0x{0:X}" -f $hwnd } else { $null }
            })
        automationId = (Get-SafeValue { $Element.Current.AutomationId })
        name = (Get-SafeValue { $Element.Current.Name })
        className = (Get-SafeValue { $Element.Current.ClassName })
        controlType = (Get-SafeValue { $Element.Current.ControlType.ProgrammaticName })
        text = (Get-ElementText $Element)
        visible = $visible
        enabled = [bool](Get-SafeValue { $Element.Current.IsEnabled })
        focused = [bool](Get-SafeValue { $Element.Current.HasKeyboardFocus })
        selected = (Get-BoolPatternState $Element "selected")
        expanded = (Get-BoolPatternState $Element "expanded")
        checked = (Get-BoolPatternState $Element "checked")
        interactive = $interactive
        refLabel = $null
        x = $(if ($rect) { $rect.X } else { 0 })
        y = $(if ($rect) { $rect.Y } else { 0 })
        width = $(if ($rect) { $rect.Width } else { 0 })
        height = $(if ($rect) { $rect.Height } else { 0 })
        children = $children
    }

    if ($interactive -and $visible) {
        $InteractiveIndex.Value++
        $node.refLabel = "u$($InteractiveIndex.Value)"
    }

    return $node
}

function Get-FlatNodes {
    param([object]$Node)

    $nodes = @()
    if (-not $Node) {
        return $nodes
    }

    $nodes += $Node
    foreach ($child in @($Node.children)) {
        $nodes += Get-FlatNodes $child
    }

    return $nodes
}

function Find-UiaElement {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [object]$Request
    )

    if ($Request.elementId) {
        $parts = @($Request.elementId -split "/" | Where-Object { $_ })
        if ($parts.Length -gt 0 -and $parts[0] -ieq "root") {
            $current = $Root
            foreach ($part in $parts[1..($parts.Length - 1)]) {
                $parsedIndex = 0
                if (-not [int]::TryParse($part, [ref]$parsedIndex)) {
                    return $null
                }

                $index = $parsedIndex
                $children = @(Get-Children $current)
                if ($index -lt 0 -or $index -ge $children.Count) {
                    return $null
                }

                $current = $children[$index]
            }

            return $current
        }
    }

    $queue = New-Object System.Collections.Generic.Queue[System.Windows.Automation.AutomationElement]
    $queue.Enqueue($Root)
    while ($queue.Count -gt 0) {
        $current = $queue.Dequeue()
        $automationId = Get-SafeValue { $current.Current.AutomationId }
        $name = Get-SafeValue { $current.Current.Name }
        $text = Get-ElementText $current

        if (($Request.automationId -and $automationId -ieq $Request.automationId) -or
            ($Request.name -and $name -ieq $Request.name) -or
            ($Request.text -and $text -ieq $Request.text)) {
            return $current
        }

        foreach ($child in (Get-Children $current)) {
            $queue.Enqueue($child)
        }
    }

    return $null
}

function Coalesce {
    param($Value, $Fallback)

    if ($null -eq $Value) {
        return $Fallback
    }

    return $Value
}

$request = if ([string]::IsNullOrWhiteSpace($JsonBody)) { @{} } else { $JsonBody | ConvertFrom-Json }

if ($Tool -eq "tree") {
    $root = Resolve-RootElement $request
    $interactiveIndex = 0
    $maxDepth = [Math]::Max(1, [Math]::Min(8, [int](Coalesce $request.maxDepth 4)))
    $node = New-UiaNode -Element $root -Path "root" -Depth 0 -MaxDepth $maxDepth -InteractiveIndex ([ref]$interactiveIndex)
    $interactiveNodes = @(Get-FlatNodes $node | Where-Object { $_.interactive -and $_.visible })
    [pscustomobject]@{
        root = $node
        interactiveNodes = $interactiveNodes
    } | ConvertTo-Json -Depth 30
    exit 0
}

if ($Tool -eq "action") {
    $root = Resolve-RootElement $request
    $target = Find-UiaElement -Root $root -Request $request
    if (-not $target) {
        throw "No matching desktop UIA element was found."
    }

    switch ((Coalesce $request.action "").ToLowerInvariant()) {
        "focus" {
            $target.SetFocus()
        }
        "invoke" {
            $pattern = Try-GetPattern $target ([System.Windows.Automation.InvokePattern]::Pattern)
            if (-not $pattern) { throw "Element does not support invoke." }
            $pattern.Invoke()
        }
        "click" {
            $pattern = Try-GetPattern $target ([System.Windows.Automation.InvokePattern]::Pattern)
            if (-not $pattern) { throw "Element does not support invoke." }
            $pattern.Invoke()
        }
        "setvalue" {
            $pattern = Try-GetPattern $target ([System.Windows.Automation.ValuePattern]::Pattern)
            if (-not $pattern) { throw "Element does not support value." }
            $pattern.SetValue((Coalesce $request.value ""))
        }
        "settext" {
            $pattern = Try-GetPattern $target ([System.Windows.Automation.ValuePattern]::Pattern)
            if (-not $pattern) { throw "Element does not support value." }
            $pattern.SetValue((Coalesce $request.value ""))
        }
        "select" {
            $pattern = Try-GetPattern $target ([System.Windows.Automation.SelectionItemPattern]::Pattern)
            if (-not $pattern) { throw "Element does not support selection." }
            $pattern.Select()
        }
        "toggle" {
            $pattern = Try-GetPattern $target ([System.Windows.Automation.TogglePattern]::Pattern)
            if (-not $pattern) { throw "Element does not support toggle." }
            $pattern.Toggle()
        }
        "expand" {
            $pattern = Try-GetPattern $target ([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
            if (-not $pattern) { throw "Element does not support expand." }
            $pattern.Expand()
        }
        "collapse" {
            $pattern = Try-GetPattern $target ([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
            if (-not $pattern) { throw "Element does not support collapse." }
            $pattern.Collapse()
        }
        default {
            throw "Unknown desktop UIA action '$($request.action)'."
        }
    }

    $interactiveIndex = 0
    $targetNode = New-UiaNode -Element $target -Path "target" -Depth 0 -MaxDepth 1 -InteractiveIndex ([ref]$interactiveIndex)
    [pscustomobject]@{
        ok = $true
        target = $targetNode
    } | ConvertTo-Json -Depth 20
    exit 0
}
