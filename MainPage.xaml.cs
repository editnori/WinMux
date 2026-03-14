using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SelfContainedDeployment.Automation;
using SelfContainedDeployment.Shell;
using SelfContainedDeployment.Terminal;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SelfContainedDeployment
{
    public partial class MainPage : Page
    {
        private readonly WorkspaceProject _project;
        private WorkspaceThread _activeThread;
        private bool _showingSettings;
        private int _threadSequence = 1;
        private int _tabSequence = 1;

        public static MainPage Current;

        public MainPage()
        {
            InitializeComponent();
            Current = this;
            _project = new WorkspaceProject(Environment.CurrentDirectory);
            Loaded += OnLoaded;
            ActualThemeChanged += OnActualThemeChanged;
        }

        public void ApplyTheme(ElementTheme theme)
        {
            SampleConfig.CurrentTheme = theme;
            ShellRoot.RequestedTheme = theme;
            SettingsFrame.RequestedTheme = theme;

            RefreshThreadButtons();
            UpdateRailVisualState(showTerminal: !_showingSettings);
            ApplyThemeToAllTerminals(ResolveTheme(theme));
        }

        public NativeAutomationState GetAutomationState()
        {
            return new NativeAutomationState
            {
                WindowTitle = ((App)Application.Current).MainWindowInstance?.Title,
                ProjectId = _project.Id,
                ProjectName = _project.Name,
                ProjectPath = _project.RootPath,
                ActiveThreadId = _activeThread?.Id,
                ActiveTabId = _activeThread?.SelectedTabId,
                ActiveView = _showingSettings ? "settings" : "terminal",
                Theme = ResolveTheme(SampleConfig.CurrentTheme).ToString().ToLowerInvariant(),
                PaneOpen = ShellSplitView.IsPaneOpen,
                Threads = _project.Threads.Select(thread => new NativeAutomationThreadState
                {
                    Id = thread.Id,
                    Name = thread.Name,
                    SelectedTabId = thread.SelectedTabId,
                    TabCount = thread.Tabs.Count,
                    Tabs = thread.Tabs.Select(tab => new NativeAutomationTabState
                    {
                        Id = tab.Id,
                        Title = FormatTabHeader(tab.Header),
                        Exited = tab.IsExited,
                    }).ToList(),
                }).ToList(),
            };
        }

        public NativeAutomationActionResponse PerformAutomationAction(NativeAutomationActionRequest request)
        {
            request ??= new NativeAutomationActionRequest();

            try
            {
                string action = request.Action?.Trim().ToLowerInvariant();

                switch (action)
                {
                    case "togglepane":
                        ShellSplitView.IsPaneOpen = !ShellSplitView.IsPaneOpen;
                        UpdatePaneLayout();
                        break;
                    case "showterminal":
                        ShowTerminalShell();
                        break;
                    case "showsettings":
                        ShowSettings();
                        break;
                    case "newthread":
                        ActivateThread(CreateThread());
                        ShowTerminalShell();
                        break;
                    case "newtab":
                        ShowTerminalShell();
                        AddTerminalTab(_activeThread);
                        break;
                    case "selectthread":
                        ActivateThread(FindThread(request.ThreadId));
                        ShowTerminalShell();
                        break;
                    case "selecttab":
                        SelectTab(request.TabId);
                        ShowTerminalShell();
                        break;
                    case "closetab":
                        CloseTab(request.TabId);
                        break;
                    case "settheme":
                        ApplyTheme(ParseTheme(request.Value));
                        break;
                    default:
                        return new NativeAutomationActionResponse
                        {
                            Ok = false,
                            Message = $"Unknown action '{request.Action}'.",
                            State = GetAutomationState(),
                        };
                }

                return new NativeAutomationActionResponse
                {
                    Ok = true,
                    State = GetAutomationState(),
                };
            }
            catch (Exception ex)
            {
                return new NativeAutomationActionResponse
                {
                    Ok = false,
                    Message = ex.Message,
                    State = GetAutomationState(),
                };
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoaded;
            InitializeShellModel();
            ApplyTheme(SampleConfig.CurrentTheme);
            UpdatePaneLayout();
            ShowTerminalShell();
        }

        private void OnActualThemeChanged(FrameworkElement sender, object args)
        {
            if (SampleConfig.CurrentTheme == ElementTheme.Default)
            {
                RefreshThreadButtons();
                UpdateRailVisualState(showTerminal: !_showingSettings);
                ApplyThemeToAllTerminals(ResolveTheme(ElementTheme.Default));
            }
        }

        private void OnPaneToggleClicked(object sender, RoutedEventArgs e)
        {
            ShellSplitView.IsPaneOpen = !ShellSplitView.IsPaneOpen;
            UpdatePaneLayout();
        }

        private void OnTerminalNavClicked(object sender, RoutedEventArgs e)
        {
            ShowTerminalShell();
        }

        private void OnSettingsNavClicked(object sender, RoutedEventArgs e)
        {
            ShowSettings();
        }

        private void OnNewThreadClicked(object sender, RoutedEventArgs e)
        {
            ActivateThread(CreateThread());
            ShowTerminalShell();
        }

        private void OnNewTabClicked(object sender, RoutedEventArgs e)
        {
            ShowTerminalShell();
            AddTerminalTab(_activeThread);
        }

        private void OnThreadButtonClicked(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string threadId)
            {
                ActivateThread(FindThread(threadId));
                ShowTerminalShell();
            }
        }

        private void TerminalTabs_AddTabButtonClick(TabView sender, object args)
        {
            AddTerminalTab(_activeThread);
        }

        private void TerminalTabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
        {
            if (args.Item is TabViewItem item && item.Tag is TerminalTabRecord tab)
            {
                CloseTab(tab.Id);
            }
        }

        private void TerminalTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_activeThread is null)
            {
                return;
            }

            if (TerminalTabs.SelectedItem is TabViewItem item && item.Tag is TerminalTabRecord tab)
            {
                _activeThread.SelectedTabId = tab.Id;
            }

            FocusSelectedTerminal();
            RefreshThreadButtons();
            UpdateHeader();
        }

        private void InitializeShellModel()
        {
            ProjectNameText.Text = _project.Name;
            ProjectNamePaneText.Text = _project.Name;

            if (_project.Threads.Count == 0)
            {
                ActivateThread(CreateThread());
            }
            else
            {
                ActivateThread(_project.Threads[0]);
            }
        }

        private WorkspaceThread CreateThread()
        {
            var thread = new WorkspaceThread($"Thread {_threadSequence++}");
            _project.Threads.Add(thread);
            EnsureThreadHasTab(thread);
            RefreshThreadButtons();
            return thread;
        }

        private void EnsureThreadHasTab(WorkspaceThread thread)
        {
            if (thread.Tabs.Count == 0)
            {
                AddTerminalTab(thread);
            }
        }

        private TerminalTabRecord AddTerminalTab(WorkspaceThread thread)
        {
            thread ??= _activeThread ?? CreateThread();

            var terminal = new TerminalControl
            {
                InitialWorkingDirectory = _project.RootPath,
            };

            terminal.ApplyTheme(ResolveTheme(SampleConfig.CurrentTheme));

            var tab = new TerminalTabRecord($"Tab {_tabSequence++}", terminal);
            terminal.SessionTitleChanged += (_, title) =>
            {
                tab.Header = title;
                if (thread == _activeThread)
                {
                    RefreshTabView();
                }
            };
            terminal.SessionExited += (_, _) =>
            {
                tab.IsExited = true;
                if (thread == _activeThread)
                {
                    RefreshTabView();
                }

                RefreshThreadButtons();
            };

            thread.Tabs.Add(tab);
            thread.SelectedTabId = tab.Id;

            if (thread == _activeThread)
            {
                RefreshTabView();
            }

            RefreshThreadButtons();
            return tab;
        }

        private void CloseTab(string tabId)
        {
            if (string.IsNullOrWhiteSpace(tabId))
            {
                return;
            }

            foreach (WorkspaceThread thread in _project.Threads)
            {
                TerminalTabRecord tab = thread.Tabs.FirstOrDefault(candidate => candidate.Id == tabId);
                if (tab is null)
                {
                    continue;
                }

                tab.Terminal.DisposeTerminal();
                thread.Tabs.Remove(tab);

                if (thread.Tabs.Count == 0)
                {
                    AddTerminalTab(thread);
                }

                thread.SelectedTabId = thread.Tabs.FirstOrDefault()?.Id;

                if (thread == _activeThread)
                {
                    RefreshTabView();
                    FocusSelectedTerminal();
                }

                RefreshThreadButtons();
                UpdateHeader();
                return;
            }
        }

        private void SelectTab(string tabId)
        {
            if (string.IsNullOrWhiteSpace(tabId))
            {
                return;
            }

            foreach (WorkspaceThread thread in _project.Threads)
            {
                TerminalTabRecord tab = thread.Tabs.FirstOrDefault(candidate => candidate.Id == tabId);
                if (tab is null)
                {
                    continue;
                }

                ActivateThread(thread);
                thread.SelectedTabId = tab.Id;
                RefreshTabView();
                FocusSelectedTerminal();
                return;
            }

            throw new InvalidOperationException($"Unknown tab '{tabId}'.");
        }

        private void ActivateThread(WorkspaceThread thread)
        {
            _activeThread = thread ?? throw new ArgumentNullException(nameof(thread));
            EnsureThreadHasTab(thread);
            RefreshThreadButtons();
            RefreshTabView();
            UpdateHeader();
        }

        private void RefreshThreadButtons()
        {
            ThreadListPanel.Children.Clear();

            bool isOpen = ShellSplitView.IsPaneOpen;

            foreach (WorkspaceThread thread in _project.Threads)
            {
                var button = new Button
                {
                    Style = (Style)Application.Current.Resources["ShellNavButtonStyle"],
                    Tag = thread.Id,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                };
                AutomationProperties.SetAutomationId(button, $"shell-thread-{thread.Id}");
                button.Click += OnThreadButtonClicked;

                var layout = new Grid
                {
                    ColumnSpacing = 10,
                };
                layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                layout.Children.Add(new FontIcon
                {
                    FontSize = 12,
                    Glyph = "\uE8BD",
                    VerticalAlignment = VerticalAlignment.Center,
                });

                if (isOpen)
                {
                    var textStack = new StackPanel
                    {
                        Spacing = 0,
                    };
                    Grid.SetColumn(textStack, 1);

                    textStack.Children.Add(new TextBlock
                    {
                        Text = thread.Name,
                        FontSize = 12,
                    });
                    textStack.Children.Add(new TextBlock
                    {
                        Text = thread.TabSummary,
                        Style = (Style)Application.Current.Resources["ShellHintTextStyle"],
                    });
                    layout.Children.Add(textStack);
                }

                button.Content = layout;
                ApplyThreadButtonState(button, thread == _activeThread);
                ThreadListPanel.Children.Add(button);
            }

            ThreadSectionText.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RefreshTabView()
        {
            foreach (TabViewItem existingItem in TerminalTabs.TabItems.OfType<TabViewItem>())
            {
                existingItem.Content = null;
            }

            TerminalTabs.TabItems.Clear();

            if (_activeThread is null)
            {
                return;
            }

            foreach (TerminalTabRecord tab in _activeThread.Tabs)
            {
                var item = new TabViewItem
                {
                    Header = FormatTabHeader(tab.Header, tab.IsExited),
                    Content = tab.Terminal,
                    IsClosable = true,
                    Tag = tab,
                };

                TerminalTabs.TabItems.Add(item);
            }

            string selectedTabId = _activeThread.SelectedTabId ?? _activeThread.Tabs.FirstOrDefault()?.Id;
            var selectedItem = TerminalTabs.TabItems
                .OfType<TabViewItem>()
                .FirstOrDefault(item => (item.Tag as TerminalTabRecord)?.Id == selectedTabId)
                ?? TerminalTabs.TabItems.OfType<TabViewItem>().FirstOrDefault();

            TerminalTabs.SelectedItem = selectedItem;

            if (selectedItem?.Tag is TerminalTabRecord selectedTab)
            {
                _activeThread.SelectedTabId = selectedTab.Id;
            }
        }

        private void ShowSettings()
        {
            _showingSettings = true;
            SettingsFrame.Navigate(typeof(SettingsPage));
            SettingsFrame.Visibility = Visibility.Visible;
            TerminalTabs.Visibility = Visibility.Collapsed;
            UpdateRailVisualState(showTerminal: false);
            UpdateHeader();
        }

        private void ShowTerminalShell()
        {
            _showingSettings = false;
            SettingsFrame.Visibility = Visibility.Collapsed;
            TerminalTabs.Visibility = Visibility.Visible;
            UpdateRailVisualState(showTerminal: true);
            FocusSelectedTerminal();
            UpdateHeader();
        }

        private void FocusSelectedTerminal()
        {
            if (TerminalTabs.SelectedItem is TabViewItem item && item.Tag is TerminalTabRecord tab)
            {
                tab.Terminal.FocusTerminal();
            }
        }

        private void UpdateRailVisualState(bool showTerminal)
        {
            ApplyRailState(TerminalNavButton, TerminalNavText, showTerminal);
            ApplyRailState(SettingsNavButton, SettingsNavText, !showTerminal);
            ApplyRailState(NewThreadButton, NewThreadText, false);
            ApplyRailState(NewTabRailButton, NewTabText, false);
        }

        private void UpdatePaneLayout()
        {
            bool isOpen = ShellSplitView.IsPaneOpen;

            PaneBrandText.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
            ProjectNamePaneText.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
            TerminalNavText.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
            SettingsNavText.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
            NewThreadText.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
            NewTabText.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;

            ToolTipService.SetToolTip(PaneToggleButton, isOpen ? "Collapse sidebar" : "Expand sidebar");
            RefreshThreadButtons();
        }

        private void UpdateHeader()
        {
            ProjectNameText.Text = _project.Name;
            ProjectNamePaneText.Text = _project.RootPath;

            if (_showingSettings)
            {
                ActiveThreadText.Text = $"preferences  ·  {_project.RootPath}";
                return;
            }

            ActiveThreadText.Text = _activeThread is null
                ? _project.RootPath
                : $"{_activeThread.Name}  ·  {_project.RootPath}";
        }

        private void ApplyThemeToAllTerminals(ElementTheme resolvedTheme)
        {
            foreach (TerminalControl terminal in EnumerateTerminals())
            {
                terminal.ApplyTheme(resolvedTheme);
            }
        }

        private IEnumerable<TerminalControl> EnumerateTerminals()
        {
            return _project.Threads.SelectMany(thread => thread.Tabs).Select(tab => tab.Terminal);
        }

        private WorkspaceThread FindThread(string threadId)
        {
            return _project.Threads.FirstOrDefault(thread => thread.Id == threadId)
                ?? throw new InvalidOperationException($"Unknown thread '{threadId}'.");
        }

        private static ElementTheme ParseTheme(string value)
        {
            return value?.Trim().ToLowerInvariant() switch
            {
                "light" => ElementTheme.Light,
                "dark" => ElementTheme.Dark,
                "default" => ElementTheme.Default,
                _ => throw new InvalidOperationException($"Unknown theme '{value}'."),
            };
        }

        private ElementTheme ResolveTheme(ElementTheme requestedTheme)
        {
            if (requestedTheme == ElementTheme.Light || requestedTheme == ElementTheme.Dark)
            {
                return requestedTheme;
            }

            return ActualTheme == ElementTheme.Light ? ElementTheme.Light : ElementTheme.Dark;
        }

        private static Brush AppBrush(string key)
        {
            return (Brush)Application.Current.Resources[key];
        }

        private static void ApplyRailState(Button button, TextBlock label, bool active)
        {
            button.Background = active ? AppBrush("ShellNavActiveBrush") : null;
            button.BorderBrush = active ? AppBrush("ShellBorderBrush") : null;
            button.Foreground = active ? AppBrush("ShellTextPrimaryBrush") : AppBrush("ShellTextSecondaryBrush");
            label.Foreground = active ? AppBrush("ShellTextPrimaryBrush") : AppBrush("ShellTextSecondaryBrush");
        }

        private static void ApplyThreadButtonState(Button button, bool active)
        {
            button.Background = active ? AppBrush("ShellNavActiveBrush") : null;
            button.BorderBrush = active ? AppBrush("ShellBorderBrush") : null;
            button.Foreground = active ? AppBrush("ShellTextPrimaryBrush") : AppBrush("ShellTextSecondaryBrush");
        }

        private static string FormatTabHeader(string title, bool exited = false)
        {
            string nextTitle;
            if (string.IsNullOrWhiteSpace(title))
            {
                nextTitle = "shell";
            }
            else
            {
                string trimmed = title.Trim().TrimEnd('\\', '/');
                int slashIndex = Math.Max(trimmed.LastIndexOf('\\'), trimmed.LastIndexOf('/'));
                nextTitle = slashIndex >= 0 && slashIndex < trimmed.Length - 1
                    ? trimmed[(slashIndex + 1)..]
                    : trimmed;
            }

            if (nextTitle.Length > 28)
            {
                nextTitle = nextTitle[..28];
            }

            return exited ? $"{nextTitle} (ended)" : nextTitle;
        }
    }
}
