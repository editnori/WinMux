using System.Collections.Generic;

namespace SelfContainedDeployment.Automation
{
    public sealed class NativeAutomationState
    {
        public string WindowTitle { get; set; }

        public string ProjectId { get; set; }

        public string ProjectName { get; set; }

        public string ProjectPath { get; set; }

        public string ActiveThreadId { get; set; }

        public string ActiveTabId { get; set; }

        public string ActiveView { get; set; }

        public string Theme { get; set; }

        public bool PaneOpen { get; set; }

        public string ShellProfileId { get; set; }

        public List<NativeAutomationProjectState> Projects { get; set; } = new();

        public List<NativeAutomationThreadState> Threads { get; set; } = new();
    }

    public sealed class NativeAutomationProjectState
    {
        public string Id { get; set; }

        public string Name { get; set; }

        public string RootPath { get; set; }

        public string DisplayPath { get; set; }

        public string ShellProfileId { get; set; }

        public string SelectedThreadId { get; set; }

        public List<NativeAutomationThreadState> Threads { get; set; } = new();
    }

    public sealed class NativeAutomationThreadState
    {
        public string Id { get; set; }

        public string Name { get; set; }

        public string SelectedTabId { get; set; }

        public int TabCount { get; set; }

        public List<NativeAutomationTabState> Tabs { get; set; } = new();
    }

    public sealed class NativeAutomationTabState
    {
        public string Id { get; set; }

        public string Title { get; set; }

        public bool Exited { get; set; }
    }

    public sealed class NativeAutomationActionRequest
    {
        public string Action { get; set; }

        public string ProjectId { get; set; }

        public string ThreadId { get; set; }

        public string TabId { get; set; }

        public string TargetTabId { get; set; }

        public string Value { get; set; }
    }

    public sealed class NativeAutomationActionResponse
    {
        public bool Ok { get; set; }

        public string Message { get; set; }

        public NativeAutomationState State { get; set; }
    }

    public sealed class NativeAutomationScreenshotRequest
    {
        public string Path { get; set; }

        public bool Annotated { get; set; }
    }

    public sealed class NativeAutomationScreenshotResponse
    {
        public bool Ok { get; set; }

        public string Path { get; set; }

        public int Width { get; set; }

        public int Height { get; set; }
    }

    public sealed class NativeAutomationUiTreeResponse
    {
        public string WindowTitle { get; set; }

        public string ActiveView { get; set; }

        public NativeAutomationUiNode Root { get; set; }

        public List<NativeAutomationUiNode> InteractiveNodes { get; set; } = new();
    }

    public sealed class NativeAutomationUiNode
    {
        public string ElementId { get; set; }

        public string RefLabel { get; set; }

        public string AutomationId { get; set; }

        public string Name { get; set; }

        public string ControlType { get; set; }

        public string Text { get; set; }

        public bool Visible { get; set; }

        public bool Enabled { get; set; }

        public bool Focused { get; set; }

        public bool Selected { get; set; }

        public bool Checked { get; set; }

        public bool Interactive { get; set; }

        public double X { get; set; }

        public double Y { get; set; }

        public double Width { get; set; }

        public double Height { get; set; }

        public string Margin { get; set; }

        public string Padding { get; set; }

        public string BorderThickness { get; set; }

        public string CornerRadius { get; set; }

        public string Background { get; set; }

        public string BorderBrush { get; set; }

        public string Foreground { get; set; }

        public double Opacity { get; set; }

        public double FontSize { get; set; }

        public string FontWeight { get; set; }

        public List<NativeAutomationUiNode> Children { get; set; } = new();
    }

    public sealed class NativeAutomationUiActionRequest
    {
        public string Action { get; set; }

        public string ElementId { get; set; }

        public string RefLabel { get; set; }

        public string AutomationId { get; set; }

        public string Name { get; set; }

        public string Text { get; set; }

        public string Value { get; set; }

        public string MenuItemText { get; set; }
    }

    public sealed class NativeAutomationUiActionResponse
    {
        public bool Ok { get; set; }

        public string Message { get; set; }

        public NativeAutomationUiNode Target { get; set; }
    }

    public sealed class NativeAutomationTerminalStateRequest
    {
        public string TabId { get; set; }
    }

    public sealed class NativeAutomationTerminalStateResponse
    {
        public string SelectedTabId { get; set; }

        public List<NativeAutomationTerminalSnapshot> Tabs { get; set; } = new();
    }

    public sealed class NativeAutomationTerminalSnapshot
    {
        public string TabId { get; set; }

        public string ThreadId { get; set; }

        public string ProjectId { get; set; }

        public string Title { get; set; }

        public string DisplayWorkingDirectory { get; set; }

        public string ShellCommand { get; set; }

        public bool RendererReady { get; set; }

        public bool Started { get; set; }

        public int Cols { get; set; }

        public int Rows { get; set; }

        public int CursorX { get; set; }

        public int CursorY { get; set; }

        public string Selection { get; set; }

        public string VisibleText { get; set; }

        public string BufferTail { get; set; }
    }

    public sealed class NativeAutomationEventsResponse
    {
        public long NextSequence { get; set; }

        public List<NativeAutomationEventEntry> Events { get; set; } = new();
    }

    public sealed class NativeAutomationEventEntry
    {
        public long Sequence { get; set; }

        public string Timestamp { get; set; }

        public string Category { get; set; }

        public string Name { get; set; }

        public string Message { get; set; }

        public Dictionary<string, string> Data { get; set; } = new();
    }

    public sealed class NativeAutomationDesktopWindowsResponse
    {
        public List<NativeAutomationDesktopWindowNode> Windows { get; set; } = new();
    }

    public sealed class NativeAutomationDesktopWindowNode
    {
        public string Handle { get; set; }

        public string Title { get; set; }

        public string ClassName { get; set; }

        public bool Visible { get; set; }

        public bool Enabled { get; set; }

        public bool Focused { get; set; }

        public double X { get; set; }

        public double Y { get; set; }

        public double Width { get; set; }

        public double Height { get; set; }

        public List<NativeAutomationDesktopWindowNode> Children { get; set; } = new();
    }

    public sealed class NativeAutomationDesktopActionRequest
    {
        public string Action { get; set; }

        public string Handle { get; set; }

        public string TitleContains { get; set; }

        public string ClassName { get; set; }

        public double? X { get; set; }

        public double? Y { get; set; }

        public double? EndX { get; set; }

        public double? EndY { get; set; }

        public int DurationMs { get; set; }

        public string Value { get; set; }
    }

    public sealed class NativeAutomationDesktopActionResponse
    {
        public bool Ok { get; set; }

        public string Message { get; set; }

        public NativeAutomationDesktopWindowNode Target { get; set; }
    }

    public sealed class NativeAutomationRenderTraceRequest
    {
        public int Frames { get; set; }

        public bool CaptureScreenshots { get; set; }

        public bool Annotated { get; set; }

        public NativeAutomationActionRequest Action { get; set; }

        public NativeAutomationUiActionRequest UiAction { get; set; }
    }

    public sealed class NativeAutomationRenderTraceResponse
    {
        public bool Ok { get; set; }

        public string Message { get; set; }

        public NativeAutomationState State { get; set; }

        public List<NativeAutomationRenderFrame> Frames { get; set; } = new();
    }

    public sealed class NativeAutomationRenderFrame
    {
        public int Index { get; set; }

        public string Timestamp { get; set; }

        public string ScreenshotPath { get; set; }

        public NativeAutomationState State { get; set; }

        public List<NativeAutomationUiNode> InteractiveNodes { get; set; } = new();
    }

    public sealed class NativeAutomationRecordingRequest
    {
        public int Fps { get; set; }

        public int MaxDurationMs { get; set; }

        public int JpegQuality { get; set; }

        public string OutputDirectory { get; set; }

        public bool KeepFrames { get; set; }
    }

    public sealed class NativeAutomationRecordingStatusResponse
    {
        public bool Recording { get; set; }

        public string RecordingId { get; set; }

        public string OutputDirectory { get; set; }

        public int TargetFps { get; set; }

        public int CapturedFrames { get; set; }

        public string VideoPath { get; set; }

        public string ManifestPath { get; set; }

        public bool KeepFrames { get; set; }

        public bool FramesRetained { get; set; }
    }

    public sealed class NativeAutomationRecordingStopResponse
    {
        public bool Ok { get; set; }

        public string RecordingId { get; set; }

        public string OutputDirectory { get; set; }

        public int CapturedFrames { get; set; }

        public string VideoPath { get; set; }

        public string ManifestPath { get; set; }

        public bool KeepFrames { get; set; }

        public bool FramesRetained { get; set; }
    }

    public sealed class NativeAutomationDesktopUiaTreeRequest
    {
        public string Handle { get; set; }

        public string TitleContains { get; set; }

        public string ClassName { get; set; }

        public int MaxDepth { get; set; }
    }

    public sealed class NativeAutomationDesktopUiaTreeResponse
    {
        public NativeAutomationExternalUiNode Root { get; set; }

        public List<NativeAutomationExternalUiNode> InteractiveNodes { get; set; } = new();
    }

    public sealed class NativeAutomationExternalUiNode
    {
        public string ElementId { get; set; }

        public string Handle { get; set; }

        public string AutomationId { get; set; }

        public string Name { get; set; }

        public string ClassName { get; set; }

        public string ControlType { get; set; }

        public string Text { get; set; }

        public bool Visible { get; set; }

        public bool Enabled { get; set; }

        public bool Focused { get; set; }

        public bool Selected { get; set; }

        public bool Expanded { get; set; }

        public bool Checked { get; set; }

        public bool Interactive { get; set; }

        public string RefLabel { get; set; }

        public double X { get; set; }

        public double Y { get; set; }

        public double Width { get; set; }

        public double Height { get; set; }

        public List<NativeAutomationExternalUiNode> Children { get; set; } = new();
    }

    public sealed class NativeAutomationDesktopUiaActionRequest
    {
        public string Action { get; set; }

        public string Handle { get; set; }

        public string TitleContains { get; set; }

        public string ClassName { get; set; }

        public string ElementId { get; set; }

        public string AutomationId { get; set; }

        public string Name { get; set; }

        public string Text { get; set; }

        public string Value { get; set; }
    }

    public sealed class NativeAutomationDesktopUiaActionResponse
    {
        public bool Ok { get; set; }

        public string Message { get; set; }

        public NativeAutomationExternalUiNode Target { get; set; }
    }
}
