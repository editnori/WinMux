// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.ObjectModel;

namespace SelfContainedDeployment
{
    public partial class MainPage : Page
    {
        public static MainPage Current;
        private readonly ObservableCollection<SidebarSession> sessions;
        private readonly TerminalTabsController tabsController = new();
        private readonly TerminalTabsStrip tabsStrip;
        private readonly TerminalSurfaceControl terminalSurface;
        private int wslCount = 2;
        private int powerShellCount = 2;
        private int codexCount = 2;
        private int claudeCount = 2;

        public MainPage()
        {
            InitializeComponent();
            Current = this;
            sessions = SidebarSessionSeedBuilder.Create();
            SessionList.ItemsSource = sessions;
            tabsStrip = new TerminalTabsStrip();
            tabsStrip.SetModel(tabsController);
            tabsStrip.TabAdded += TabsStrip_TabAdded;
            tabsStrip.TabClosed += TabsStrip_TabClosed;
            tabsStrip.TabRenamedRequested += TabsStrip_TabRenamedRequested;
            tabsStrip.ActiveTabChanged += TabsStrip_ActiveTabChanged;
            TabsHostPresenter.Content = tabsStrip;

            terminalSurface = new TerminalSurfaceControl();
            TerminalHostPresenter.Content = terminalSurface;

            foreach (var session in sessions)
            {
                AttachTabForSession(session);
            }

            if (sessions.Count > 0)
            {
                SessionList.SelectedIndex = 0;
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

        private void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var session = SessionList.SelectedItem as SidebarSession;
            if (session is null)
            {
                SelectedSessionTitleText.Text = "No session selected";
                SelectedSessionMetaText.Text = "Pick a session from the sidebar to target the terminal host.";
                SessionNameBox.Text = string.Empty;
                ShellContractText.Text = "The shell contract becomes active once a session is selected.";
                terminalSurface.AttachSession(null);
                return;
            }

            SessionNameBox.Text = session.DisplayName;
            SelectedSessionTitleText.Text = session.DisplayName;
            SelectedSessionMetaText.Text = session.KindLabel + " • " + session.StatusText;
            ShellContractText.Text =
                "The selected sidebar session owns a real ConPTY-backed host. " +
                "Tabs stay synchronized with the same session object, and the embedded surface is attached directly to that host.";
            if (session.Tab is not null && !ReferenceEquals(tabsController.SelectedTab, session.Tab))
            {
                tabsController.SelectedTab = session.Tab;
                tabsStrip.SetModel(tabsController);
            }

            terminalSurface.AttachSession(session);
        }

        private void RenameSession_Click(object sender, RoutedEventArgs e)
        {
            var session = SessionList.SelectedItem as SidebarSession;
            var nextName = SessionNameBox.Text.Trim();
            if (session is null || string.IsNullOrWhiteSpace(nextName))
            {
                NotifyUser("Select a session and enter a name first.", InfoBarSeverity.Warning);
                return;
            }

            session.DisplayName = nextName;
            if (session.Tab is not null)
            {
                tabsController.RenameTab(session.Tab, nextName);
                tabsStrip.SetModel(tabsController);
            }

            SelectedSessionTitleText.Text = session.DisplayName;
            SessionList.UpdateLayout();
            NotifyUser("Renamed session to " + session.DisplayName, InfoBarSeverity.Success);
        }

        private void AddWslSession_Click(object sender, RoutedEventArgs e)
        {
            AddSession(SidebarSessionSeedBuilder.CreateWslSession("WSL " + wslCount++));
        }

        private void AddPowerShellSession_Click(object sender, RoutedEventArgs e)
        {
            AddSession(SidebarSessionSeedBuilder.CreatePowerShellSession("PowerShell " + powerShellCount++));
        }

        private void AddCodexSession_Click(object sender, RoutedEventArgs e)
        {
            AddSession(SidebarSessionSeedBuilder.CreateCodexSession("Codex Thread " + codexCount++));
        }

        private void AddClaudeSession_Click(object sender, RoutedEventArgs e)
        {
            AddSession(SidebarSessionSeedBuilder.CreateClaudeSession("Claude Thread " + claudeCount++));
        }

        private void AddSession(SidebarSession session)
        {
            AttachTabForSession(session);
            sessions.Add(session);
            SessionList.SelectedItem = session;
            NotifyUser("Added " + session.DisplayName, InfoBarSeverity.Success);
        }

        private void AttachTabForSession(SidebarSession session)
        {
            if (session.Tab is not null)
            {
                return;
            }

            var tab = tabsController.AddNewTab(session.DisplayName, session.KindLabel);
            session.Tab = tab;
        }

        private void TabsStrip_ActiveTabChanged(object sender, TerminalTabEventArgs e)
        {
            if (e.Tab is null)
            {
                return;
            }

            foreach (var session in sessions)
            {
                if (ReferenceEquals(session.Tab, e.Tab))
                {
                    if (!ReferenceEquals(SessionList.SelectedItem, session))
                    {
                        SessionList.SelectedItem = session;
                    }

                    return;
                }
            }
        }

        private void TabsStrip_TabAdded(object sender, TerminalTabEventArgs e)
        {
            var session = SidebarSessionSeedBuilder.CreatePowerShellSession(e.Tab.Title);
            session.Tab = e.Tab;
            sessions.Add(session);
            SessionList.SelectedItem = session;
            NotifyUser("Added " + session.DisplayName, InfoBarSeverity.Success);
        }

        private async void TabsStrip_TabClosed(object sender, TerminalTabEventArgs e)
        {
            SidebarSession sessionToRemove = null;
            foreach (var session in sessions)
            {
                if (ReferenceEquals(session.Tab, e.Tab))
                {
                    sessionToRemove = session;
                    break;
                }
            }

            if (sessionToRemove is null)
            {
                return;
            }

            await sessionToRemove.Host.StopAsync();
            sessions.Remove(sessionToRemove);

            if (sessions.Count > 0)
            {
                SessionList.SelectedIndex = 0;
            }
        }

        private void TabsStrip_TabRenamedRequested(object sender, TerminalTabEventArgs e)
        {
            SidebarSession sessionToRename = null;
            foreach (var session in sessions)
            {
                if (ReferenceEquals(session.Tab, e.Tab))
                {
                    sessionToRename = session;
                    break;
                }
            }

            if (sessionToRename is null)
            {
                return;
            }

            SessionList.SelectedItem = sessionToRename;
            SessionNameBox.Focus(FocusState.Programmatic);
            NotifyUser("Edit the session name box, then click Apply Rename.", InfoBarSeverity.Informational);
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            var selected = SessionList.SelectedItem as SidebarSession;
            if (selected is null && sessions.Count > 0)
            {
                SessionList.SelectedIndex = 0;
                selected = SessionList.SelectedItem as SidebarSession;
            }

            if (selected is not null)
            {
                terminalSurface.AttachSession(selected);
            }
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            foreach (var session in sessions)
            {
                session.Host.Dispose();
            }
        }
    }
}
