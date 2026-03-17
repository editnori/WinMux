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

        public bool InspectorOpen { get; set; }

        public string ShellProfileId { get; set; }

        public string GitBranch { get; set; }

        public string WorktreePath { get; set; }

        public int ChangedFileCount { get; set; }

        public string SelectedDiffPath { get; set; }

        public string DiffReviewSource { get; set; }

        public string SelectedCheckpointId { get; set; }

        public int CheckpointCount { get; set; }

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

        public List<NativeAutomationThreadNoteState> Notes { get; set; } = new();
    }

    public sealed class NativeAutomationThreadState
    {
        public string Id { get; set; }

        public string Name { get; set; }

        public string WorktreePath { get; set; }

        public string BranchName { get; set; }

        public string SelectedDiffPath { get; set; }

        public string SelectedTabId { get; set; }

        public int TabCount { get; set; }

        public int PaneCount { get; set; }

        public string Layout { get; set; }

        public bool AutoFitPaneContentLocked { get; set; }

        public string ZoomedPaneId { get; set; }

        public int PaneLimit { get; set; }

        public int VisiblePaneCapacity { get; set; }

        public double PrimarySplitRatio { get; set; }

        public double SecondarySplitRatio { get; set; }

        public int ChangedFileCount { get; set; }

        public bool HasNotes { get; set; }

        public int NoteCount { get; set; }

        public string SelectedNoteId { get; set; }

        public string NotePreview { get; set; }

        public string DiffReviewSource { get; set; }

        public string SelectedCheckpointId { get; set; }

        public int CheckpointCount { get; set; }

        public List<NativeAutomationTabState> Tabs { get; set; } = new();

        public List<NativeAutomationTabState> Panes { get; set; } = new();

        public List<NativeAutomationThreadNoteState> Notes { get; set; } = new();
    }

    public sealed class NativeAutomationThreadNoteState
    {
        public string Id { get; set; }

        public string Title { get; set; }

        public string Text { get; set; }

        public string Preview { get; set; }

        public string ProjectId { get; set; }

        public string ProjectName { get; set; }

        public string ThreadId { get; set; }

        public string ThreadName { get; set; }

        public string PaneId { get; set; }

        public string PaneTitle { get; set; }

        public bool Selected { get; set; }

        public bool Archived { get; set; }

        public string UpdatedAt { get; set; }

        public string ArchivedAt { get; set; }
    }

    public sealed class NativeAutomationTabState
    {
        public string Id { get; set; }

        public string Title { get; set; }

        public string Kind { get; set; }

        public bool Exited { get; set; }
    }

    public sealed class NativeAutomationActionRequest
    {
        public string Action { get; set; }

        public string ProjectId { get; set; }

        public string ThreadId { get; set; }

        public string TabId { get; set; }

        public string TargetTabId { get; set; }

        public string NoteId { get; set; }

        public string Title { get; set; }

        public string Value { get; set; }
    }

    public sealed class NativeAutomationActionResponse
    {
        public bool Ok { get; set; }

        public string Message { get; set; }

        public string CorrelationId { get; set; }

        public double DurationMs { get; set; }

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

        public string CorrelationId { get; set; }

        public double DurationMs { get; set; }

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

        public bool Exited { get; set; }

        public bool AutoStartSession { get; set; }

        public bool ReplayRestorePending { get; set; }

        public bool ReplayRestoreFailed { get; set; }

        public bool StartupVisible { get; set; }

        public bool StatusVisible { get; set; }

        public bool HasDisplayOutput { get; set; }

        public string StatusText { get; set; }

        public double? WebViewInitializedMs { get; set; }

        public double? NavigationCompletedMs { get; set; }

        public double? RendererReadyMs { get; set; }

        public double? SessionStartedMs { get; set; }

        public double? FirstDisplayOutputMs { get; set; }

        public int Cols { get; set; }

        public int Rows { get; set; }

        public int CursorX { get; set; }

        public int CursorY { get; set; }

        public int ViewportY { get; set; }

        public int BufferLength { get; set; }

        public string ReplayTool { get; set; }

        public string ReplaySessionId { get; set; }

        public string ReplayCommand { get; set; }

        public string ActiveToolSession { get; set; }

        public bool ToolSurfaceVisible { get; set; }

        public string Selection { get; set; }

        public string VisibleText { get; set; }

        public string BufferTail { get; set; }
    }

    public sealed class NativeAutomationBrowserStateRequest
    {
        public string PaneId { get; set; }
    }

    public sealed class NativeAutomationBrowserStateResponse
    {
        public string SelectedPaneId { get; set; }

        public List<NativeAutomationBrowserSnapshot> Panes { get; set; } = new();
    }

    public sealed class NativeAutomationBrowserSnapshot
    {
        public string PaneId { get; set; }

        public string ThreadId { get; set; }

        public string ProjectId { get; set; }

        public string Title { get; set; }

        public string Uri { get; set; }

        public string AddressText { get; set; }

        public bool Initialized { get; set; }

        public string SelectedTabId { get; set; }

        public int TabCount { get; set; }

        public string ProfileSeedStatus { get; set; }

        public string ExtensionImportStatus { get; set; }

        public string CredentialAutofillStatus { get; set; }

        public List<string> InstalledExtensions { get; set; } = new();

        public List<NativeAutomationBrowserTabSnapshot> Tabs { get; set; } = new();
    }

    public sealed class NativeAutomationBrowserTabSnapshot
    {
        public string Id { get; set; }

        public string Title { get; set; }

        public string Uri { get; set; }
    }

    public sealed class NativeAutomationBrowserEvalRequest
    {
        public string PaneId { get; set; }

        public string Script { get; set; }
    }

    public sealed class NativeAutomationBrowserEvalResponse
    {
        public bool Ok { get; set; }

        public string Message { get; set; }

        public string PaneId { get; set; }

        public string Result { get; set; }
    }

    public sealed class NativeAutomationBrowserScreenshotRequest
    {
        public string PaneId { get; set; }

        public string Path { get; set; }
    }

    public sealed class NativeAutomationBrowserScreenshotResponse
    {
        public bool Ok { get; set; }

        public string Message { get; set; }

        public string PaneId { get; set; }

        public string Path { get; set; }

        public int Width { get; set; }

        public int Height { get; set; }
    }

    public sealed class NativeAutomationDiffStateRequest
    {
        public string PaneId { get; set; }

        public int MaxLines { get; set; }
    }

    public sealed class NativeAutomationDiffStateResponse
    {
        public string SelectedPaneId { get; set; }

        public List<NativeAutomationDiffSnapshot> Panes { get; set; } = new();
    }

    public sealed class NativeAutomationDiffSnapshot
    {
        public string PaneId { get; set; }

        public string ThreadId { get; set; }

        public string ProjectId { get; set; }

        public string Title { get; set; }

        public string Path { get; set; }

        public string Summary { get; set; }

        public string RawText { get; set; }

        public bool HasDiff { get; set; }

        public int LineCount { get; set; }

        public List<NativeAutomationDiffLine> Lines { get; set; } = new();
    }

    public sealed class NativeAutomationDiffLine
    {
        public int Index { get; set; }

        public string Kind { get; set; }

        public string Text { get; set; }

        public string Foreground { get; set; }
    }

    public sealed class NativeAutomationEditorStateRequest
    {
        public string PaneId { get; set; }

        public int MaxChars { get; set; }

        public int MaxFiles { get; set; }
    }

    public sealed class NativeAutomationEditorStateResponse
    {
        public string SelectedPaneId { get; set; }

        public List<NativeAutomationEditorSnapshot> Panes { get; set; } = new();
    }

    public sealed class NativeAutomationEditorSnapshot
    {
        public string PaneId { get; set; }

        public string ThreadId { get; set; }

        public string ProjectId { get; set; }

        public string Title { get; set; }

        public string SelectedPath { get; set; }

        public string Status { get; set; }

        public bool Dirty { get; set; }

        public bool ReadOnly { get; set; }

        public int FileCount { get; set; }

        public List<string> Files { get; set; } = new();

        public string Text { get; set; }
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

        public double? Width { get; set; }

        public double? Height { get; set; }

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

        public List<string> StateChanges { get; set; } = new();

        public List<string> InteractiveChanges { get; set; } = new();

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

    public sealed class NativeAutomationPerfSnapshot
    {
        public string Timestamp { get; set; }

        public string LastUiHeartbeat { get; set; }

        public bool UiResponsive { get; set; }

        public string ActiveCorrelationId { get; set; }

        public string ActiveAction { get; set; }

        public Dictionary<string, long> Counters { get; set; } = new();

        public Dictionary<string, double> LastDurationsMs { get; set; } = new();

        public NativeAutomationActionProfile LastAction { get; set; }

        public List<NativeAutomationPerfOperation> RecentOperations { get; set; } = new();
    }

    public sealed class NativeAutomationActionProfile
    {
        public string CorrelationId { get; set; }

        public string Kind { get; set; }

        public string Name { get; set; }

        public string StartedAt { get; set; }

        public string CompletedAt { get; set; }

        public double TotalMs { get; set; }

        public double UiThreadWorkMs { get; set; }

        public double AsyncBackgroundMs { get; set; }

        public double FirstRenderCompleteMs { get; set; }

        public double PaneLayoutMs { get; set; }

        public double ProjectRailRefreshMs { get; set; }

        public double InspectorRebuildMs { get; set; }

        public double GitRefreshMs { get; set; }

        public string Error { get; set; }

        public Dictionary<string, string> Data { get; set; } = new();

        public Dictionary<string, double> OperationTotalsMs { get; set; } = new();

        public Dictionary<string, long> CounterDeltas { get; set; } = new();
    }

    public sealed class NativeAutomationPerfOperation
    {
        public string Timestamp { get; set; }

        public string CorrelationId { get; set; }

        public string Name { get; set; }

        public double DurationMs { get; set; }

        public string ThreadKind { get; set; }

        public bool Background { get; set; }

        public Dictionary<string, string> Data { get; set; } = new();
    }

    public sealed class NativeAutomationDoctorResponse
    {
        public bool Ok { get; set; }

        public string Timestamp { get; set; }

        public int ProcessId { get; set; }

        public string WindowTitle { get; set; }

        public string AutomationLogPath { get; set; }

        public string AutomationLogTail { get; set; }

        public string StartupErrorLogPath { get; set; }

        public string StartupErrorLogTail { get; set; }

        public string LastUnhandledExceptionMessage { get; set; }

        public string LastUnhandledExceptionDetails { get; set; }

        public bool UiResponsive { get; set; }

        public NativeAutomationState State { get; set; }

        public NativeAutomationPerfSnapshot Perf { get; set; }

        public NativeAutomationEventsResponse Events { get; set; }

        public NativeAutomationRecordingStatusResponse RecordingStatus { get; set; }

        public NativeAutomationHangDump LastHangDump { get; set; }
    }

    public sealed class NativeAutomationHangDump
    {
        public string Timestamp { get; set; }

        public string CorrelationId { get; set; }

        public string Action { get; set; }

        public string ScreenshotPath { get; set; }

        public string EventsPath { get; set; }

        public string Message { get; set; }
    }
}
