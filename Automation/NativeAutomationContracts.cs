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
    }

    public sealed class NativeAutomationScreenshotResponse
    {
        public bool Ok { get; set; }

        public string Path { get; set; }

        public int Width { get; set; }

        public int Height { get; set; }
    }
}
