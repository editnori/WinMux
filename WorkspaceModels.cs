using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SelfContainedDeployment
{
    internal static class WorkspaceSeedBuilder
    {
        public static ObservableCollection<WorkspaceRecord> Create()
        {
            return new ObservableCollection<WorkspaceRecord>
            {
                new WorkspaceRecord(
                    "Core Shell",
                    "native-terminal-starter",
                    @"C:\Users\lqassem\native-terminal-starter",
                    "main",
                    "Ubuntu-24.04",
                    "Browser slides over the main shell",
                    new SessionRecord(
                        "WSL / starter / bootstrap",
                        "WSL terminal surface",
                        "Ubuntu-24.04",
                        "wsl.exe -d \"Ubuntu-24.04\" --cd \"C:\\Users\\lqassem\\native-terminal-starter\"",
                        "Primary terminal lane",
                        "This is the main shell surface. Later it should become a real ConPTY terminal with restoreable workspace context."),
                    new SessionRecord(
                        "Codex / shell foundation",
                        "Codex thread",
                        "codex",
                        "codex --cwd \"C:\\Users\\lqassem\\native-terminal-starter\"",
                        "Assistant surface grouped with the project",
                        "Codex threads should read like project folders: obvious names, durable grouping, and one-click return to the right workspace."),
                    new SessionRecord(
                        "Browser / docs companion",
                        "Browser attachment",
                        "Chromium profile",
                        "msedge.exe --profile-directory=Default",
                        "Slide-over companion surface",
                        "Documentation, GitHub, and auth-heavy flows should open beside the shell, not inside the terminal itself.")),
                new WorkspaceRecord(
                    "WSL Automation",
                    "wsl-automation",
                    @"C:\Users\lqassem\source\wsl-automation",
                    "feature/session-launch",
                    "Debian",
                    "Terminal stays stable while tools slide over it",
                    new SessionRecord(
                        "WSL / distro orchestration",
                        "WSL terminal surface",
                        "Debian",
                        "wsl.exe -d \"Debian\" --cd \"C:\\Users\\lqassem\\source\\wsl-automation\"",
                        "Primary terminal lane",
                        "Pinned distro selection is part of the workspace identity, not an afterthought in a command palette."),
                    new SessionRecord(
                        "Claude Code / launch stories",
                        "Claude Code thread",
                        "claude-code",
                        "claude-code --project \"C:\\Users\\lqassem\\source\\wsl-automation\"",
                        "Assistant surface grouped with the project",
                        "Claude Code threads should be renameable and visually grouped with the same project metadata as terminals."),
                    new SessionRecord(
                        "Browser / distro docs",
                        "Browser attachment",
                        "Chromium profile",
                        "msedge.exe --profile-directory=Default https://learn.microsoft.com/windows/wsl/",
                        "Slide-over companion surface",
                        "The browser surface follows the workspace so docs and terminal context stay together.")),
                new WorkspaceRecord(
                    "Agent Studio",
                    "agent-studio",
                    @"C:\Users\lqassem\source\agent-studio",
                    "design/native-shell",
                    "Ubuntu-24.04",
                    "Workspace cards map to project folders first",
                    new SessionRecord(
                        "Codex / ui pass",
                        "Codex thread",
                        "codex",
                        "codex --cwd \"C:\\Users\\lqassem\\source\\agent-studio\"",
                        "Assistant surface grouped with the project",
                        "Use this surface for product implementation threads that need persistent naming."),
                    new SessionRecord(
                        "Claude Code / review pass",
                        "Claude Code thread",
                        "claude-code",
                        "claude-code --project \"C:\\Users\\lqassem\\source\\agent-studio\"",
                        "Assistant surface grouped with the project",
                        "Review and planning threads should look like first-class workspace children rather than floating utility tabs."))
            };
        }
    }

    public sealed class WorkspaceRecord : ObservableObject
    {
        private string title;
        private string summary;

        public WorkspaceRecord(
            string title,
            string folderName,
            string repoPath,
            string branch,
            string distribution,
            string layoutIntent,
            params SessionRecord[] sessions)
        {
            this.title = title;
            FolderName = folderName;
            RepoPath = repoPath;
            Branch = branch;
            Distribution = distribution;
            LayoutIntent = layoutIntent;
            Sessions = new ObservableCollection<SessionRecord>(sessions);
            summary = string.Empty;
            RefreshSummary();
        }

        public string Title
        {
            get => title;
            set => SetProperty(ref title, value);
        }

        public string FolderName { get; }

        public string RepoPath { get; }

        public string Branch { get; }

        public string Distribution { get; }

        public string LayoutIntent { get; }

        public ObservableCollection<SessionRecord> Sessions { get; }

        public string Summary
        {
            get => summary;
            private set => SetProperty(ref summary, value);
        }

        public void RefreshSummary()
        {
            Summary = Sessions.Count + " surfaces • " + Distribution + " • " + LayoutIntent;
        }
    }

    public sealed class SessionRecord : ObservableObject
    {
        private string displayName;

        public SessionRecord(
            string displayName,
            string kindLabel,
            string hostLabel,
            string launchCommand,
            string attachmentMode,
            string notes)
        {
            this.displayName = displayName;
            KindLabel = kindLabel;
            HostLabel = hostLabel;
            LaunchCommand = launchCommand;
            AttachmentMode = attachmentMode;
            Notes = notes;
        }

        public string DisplayName
        {
            get => displayName;
            set => SetProperty(ref displayName, value);
        }

        public string KindLabel { get; }

        public string HostLabel { get; }

        public string LaunchCommand { get; }

        public string AttachmentMode { get; }

        public string Notes { get; }
    }

    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string propertyName = null)
        {
            if (Equals(storage, value))
            {
                return false;
            }

            storage = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }
    }
}
