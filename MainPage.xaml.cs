// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.ObjectModel;

namespace SelfContainedDeployment
{
    public partial class MainPage : Page
    {
        public static MainPage Current;
        private readonly ObservableCollection<WorkspaceRecord> workspaces;

        public MainPage()
        {
            InitializeComponent();

            Current = this;

            workspaces = WorkspaceSeedBuilder.Create();
            WorkspaceList.ItemsSource = workspaces;

            if (workspaces.Count > 0)
            {
                WorkspaceList.SelectedIndex = 0;
            }
        }

        public void NotifyUser(string strMessage, InfoBarSeverity severity, bool isOpen = true)
        {
            if (DispatcherQueue.HasThreadAccess)
            {
                UpdateStatus(strMessage, severity, isOpen);
            }
            else
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    UpdateStatus(strMessage, severity, isOpen);
                });
            }
        }

        private void UpdateStatus(string strMessage, InfoBarSeverity severity, bool isOpen)
        {
            infoBar.Message = strMessage;
            infoBar.IsOpen = isOpen;
            infoBar.Severity = severity;
        }

        private void WorkspaceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var workspace = WorkspaceList.SelectedItem as WorkspaceRecord;
            if (workspace is null)
            {
                return;
            }

            WorkspaceNameBox.Text = workspace.Title;
            SessionList.ItemsSource = workspace.Sessions;
            SessionList.SelectedIndex = workspace.Sessions.Count > 0 ? 0 : -1;
            UpdateWorkspaceDetails(workspace);
            NotifyUser("Workspace loaded: " + workspace.Title, InfoBarSeverity.Informational);
        }

        private void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var session = SessionList.SelectedItem as SessionRecord;
            if (session is null)
            {
                SessionNameBox.Text = string.Empty;
                SessionKindText.Text = "No session selected";
                SessionMetaText.Text = "Pick a surface to inspect its WSL profile, assistant thread name, or browser attachment details.";
                LaunchCommandBox.Text = string.Empty;
                BehaviorNotesBox.Text = string.Empty;
                BrowserModeText.Text = string.Empty;
                return;
            }

            SessionNameBox.Text = session.DisplayName;
            UpdateSessionDetails(session);
        }

        private void RenameWorkspace_Click(object sender, RoutedEventArgs e)
        {
            var workspace = WorkspaceList.SelectedItem as WorkspaceRecord;
            var nextName = WorkspaceNameBox.Text?.Trim();
            if (workspace is null || string.IsNullOrWhiteSpace(nextName))
            {
                NotifyUser("Pick a workspace and enter a name first.", InfoBarSeverity.Warning);
                return;
            }

            workspace.Title = nextName;
            UpdateWorkspaceDetails(workspace);
            NotifyUser("Workspace renamed to " + workspace.Title, InfoBarSeverity.Success);
        }

        private void RenameSession_Click(object sender, RoutedEventArgs e)
        {
            var session = SessionList.SelectedItem as SessionRecord;
            var nextName = SessionNameBox.Text?.Trim();
            if (session is null || string.IsNullOrWhiteSpace(nextName))
            {
                NotifyUser("Pick a session and enter a name first.", InfoBarSeverity.Warning);
                return;
            }

            session.DisplayName = nextName;
            UpdateSessionDetails(session);
            NotifyUser("Session renamed to " + session.DisplayName, InfoBarSeverity.Success);
        }

        private void AddWslSession_Click(object sender, RoutedEventArgs e)
        {
            var workspace = RequireWorkspace();
            if (workspace is null)
            {
                return;
            }

            var session = new SessionRecord(
                "WSL / " + workspace.FolderName + " / shell-" + (workspace.Sessions.Count + 1),
                "WSL terminal surface",
                workspace.Distribution,
                "wsl.exe -d \"" + workspace.Distribution + "\" --cd \"" + workspace.RepoPath + "\"",
                "Primary terminal lane",
                "Launch directly into the selected repo so the terminal feels anchored to the project rather than to a generic tab.");

            AddSession(workspace, session, "WSL surface created for " + workspace.Title);
        }

        private void AddCodexSession_Click(object sender, RoutedEventArgs e)
        {
            var workspace = RequireWorkspace();
            if (workspace is null)
            {
                return;
            }

            var session = new SessionRecord(
                "Codex / " + workspace.FolderName + " / thread-" + (CountSessionsByKind(workspace, "Codex thread") + 1),
                "Codex thread",
                "codex",
                "codex --cwd \"" + workspace.RepoPath + "\"",
                "Assistant surface grouped with the project",
                "This is where project-folder style naming starts to matter. The thread name is meant to persist with the workspace instead of disappearing into a pile of terminals.");

            AddSession(workspace, session, "Codex thread added to " + workspace.Title);
        }

        private void AddClaudeSession_Click(object sender, RoutedEventArgs e)
        {
            var workspace = RequireWorkspace();
            if (workspace is null)
            {
                return;
            }

            var session = new SessionRecord(
                "Claude Code / " + workspace.FolderName + " / thread-" + (CountSessionsByKind(workspace, "Claude Code thread") + 1),
                "Claude Code thread",
                "claude-code",
                "claude-code --project \"" + workspace.RepoPath + "\"",
                "Assistant surface grouped with the project",
                "Claude Code threads should rename cleanly and live beside terminal sessions without pretending they are the same thing.");

            AddSession(workspace, session, "Claude Code thread added to " + workspace.Title);
        }

        private void AttachBrowserSession_Click(object sender, RoutedEventArgs e)
        {
            var workspace = RequireWorkspace();
            if (workspace is null)
            {
                return;
            }

            var session = new SessionRecord(
                "Browser / " + workspace.FolderName,
                "Browser attachment",
                "Chromium profile",
                "msedge.exe --profile-directory=Default",
                "Slide-over companion surface",
                "The browser belongs beside the workspace, not inside the terminal. Later this should slide in over the shell instead of constantly shrinking every terminal column.");

            AddSession(workspace, session, "Browser surface attached to " + workspace.Title);
        }

        private WorkspaceRecord RequireWorkspace()
        {
            var workspace = WorkspaceList.SelectedItem as WorkspaceRecord;
            if (workspace is null)
            {
                NotifyUser("Select a workspace first.", InfoBarSeverity.Warning);
            }

            return workspace;
        }

        private void AddSession(WorkspaceRecord workspace, SessionRecord session, string message)
        {
            workspace.Sessions.Add(session);
            workspace.RefreshSummary();
            WorkspaceList.UpdateLayout();
            SessionList.ItemsSource = workspace.Sessions;
            SessionList.SelectedItem = session;
            NotifyUser(message, InfoBarSeverity.Success);
        }

        private static int CountSessionsByKind(WorkspaceRecord workspace, string kindLabel)
        {
            var count = 0;
            foreach (var session in workspace.Sessions)
            {
                if (session.KindLabel == kindLabel)
                {
                    count++;
                }
            }

            return count;
        }

        private void UpdateWorkspaceDetails(WorkspaceRecord workspace)
        {
            DetailTitleText.Text = workspace.Title;
            WorkspaceMetaText.Text =
                "Folder: " + workspace.FolderName + Environment.NewLine +
                "Repo: " + workspace.RepoPath + Environment.NewLine +
                "Branch: " + workspace.Branch + Environment.NewLine +
                "WSL target: " + workspace.Distribution + Environment.NewLine +
                "Layout intent: " + workspace.LayoutIntent;

            DirectionText.Text =
                "Assistant threads stay attached to the active project workspace. " +
                "That gives Codex and Claude Code durable names and a place in the shell before we add true terminal multiplexing.";
        }

        private void UpdateSessionDetails(SessionRecord session)
        {
            SessionKindText.Text = session.DisplayName;
            SessionMetaText.Text =
                "Kind: " + session.KindLabel + Environment.NewLine +
                "Host: " + session.HostLabel + Environment.NewLine +
                "Attachment: " + session.AttachmentMode;
            LaunchCommandBox.Text = session.LaunchCommand;
            BehaviorNotesBox.Text = session.Notes;
            BrowserModeText.Text = "Browser strategy: " + session.AttachmentMode;
        }
    }
}
