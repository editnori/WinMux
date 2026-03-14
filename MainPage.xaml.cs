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
        private readonly List<WorkspaceProject> _projects = new();
        private WorkspaceProject _activeProject;
        private WorkspaceThread _activeThread;
        private bool _showingSettings;
        private bool _suppressThreadNameSync;
        private int _threadSequence = 1;
        private int _tabSequence = 1;

        public static MainPage Current;

        public MainPage()
        {
            InitializeComponent();
            Current = this;
            Loaded += OnLoaded;
            ActualThemeChanged += OnActualThemeChanged;
        }

        public void ApplyTheme(ElementTheme theme)
        {
            SampleConfig.CurrentTheme = theme;
            ShellRoot.RequestedTheme = theme;
            SettingsFrame.RequestedTheme = theme;

            RefreshProjectTree();
            UpdateSidebarActions();
            UpdateHeader();
            ApplyThemeToAllTerminals(ResolveTheme(theme));
        }

        public void ApplyShellProfile(string profileId)
        {
            SampleConfig.DefaultShellProfileId = ShellProfiles.Resolve(profileId).Id;

            if (_activeProject is not null)
            {
                _activeProject.ShellProfileId = SampleConfig.DefaultShellProfileId;
                RefreshProjectTree();
                UpdateHeader();
            }
        }

        public NativeAutomationState GetAutomationState()
        {
            List<NativeAutomationProjectState> projects = _projects.Select(project => new NativeAutomationProjectState
            {
                Id = project.Id,
                Name = project.Name,
                RootPath = project.RootPath,
                DisplayPath = FormatProjectPath(project),
                ShellProfileId = project.ShellProfileId,
                SelectedThreadId = project.SelectedThreadId,
                Threads = project.Threads.Select(BuildThreadState).ToList(),
            }).ToList();

            return new NativeAutomationState
            {
                WindowTitle = ((App)Application.Current).MainWindowInstance?.Title,
                ProjectId = _activeProject?.Id,
                ProjectName = _activeProject?.Name,
                ProjectPath = _activeProject?.RootPath,
                ActiveThreadId = _activeThread?.Id,
                ActiveTabId = _activeThread?.SelectedTabId,
                ActiveView = _showingSettings ? "settings" : "terminal",
                Theme = ResolveTheme(SampleConfig.CurrentTheme).ToString().ToLowerInvariant(),
                PaneOpen = ShellSplitView.IsPaneOpen,
                ShellProfileId = _activeProject?.ShellProfileId,
                Projects = projects,
                Threads = projects.SelectMany(project => project.Threads).ToList(),
            };
        }

        public NativeAutomationActionResponse PerformAutomationAction(NativeAutomationActionRequest request)
        {
            request ??= new NativeAutomationActionRequest();

            try
            {
                switch (request.Action?.Trim().ToLowerInvariant())
                {
                    case "togglepane":
                        ShellSplitView.IsPaneOpen = !ShellSplitView.IsPaneOpen;
                        UpdatePaneLayout();
                        RequestFitForSelectedTerminal();
                        break;
                    case "showterminal":
                        ShowTerminalShell();
                        break;
                    case "showsettings":
                        ShowSettings();
                        break;
                    case "newproject":
                        ActivateThread(CreateThread(GetOrCreateProject(ResolveRequestedPath(request.Value), null, SampleConfig.DefaultShellProfileId)));
                        ShowTerminalShell();
                        break;
                    case "newthread":
                        ActivateThread(CreateThread(ResolveActionProject(request), ResolveThreadName(request.Value)));
                        ShowTerminalShell();
                        break;
                    case "newtab":
                        ShowTerminalShell();
                        AddTerminalTab(_activeProject, _activeThread);
                        break;
                    case "selectproject":
                        ActivateProject(FindProject(request.ProjectId));
                        ShowTerminalShell();
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
                    case "setprofile":
                        ApplyShellProfile(request.Value);
                        break;
                    case "renamethread":
                        RenameThread(request.ThreadId, request.Value);
                        break;
                    case "input":
                        SendInputToSelectedTerminal(request.Value);
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
                RefreshProjectTree();
                UpdateSidebarActions();
                UpdateHeader();
                ApplyThemeToAllTerminals(ResolveTheme(ElementTheme.Default));
            }
        }

        private void OnPaneToggleClicked(object sender, RoutedEventArgs e)
        {
            ShellSplitView.IsPaneOpen = !ShellSplitView.IsPaneOpen;
            UpdatePaneLayout();
            RequestFitForSelectedTerminal();
        }

        private void OnSettingsNavClicked(object sender, RoutedEventArgs e)
        {
            ShowSettings();
        }

        private async void OnNewThreadClicked(object sender, RoutedEventArgs e)
        {
            ThreadDraft draft = await PromptForThreadAsync();
            if (draft is null)
            {
                return;
            }

            WorkspaceProject project = GetOrCreateProject(draft.ProjectPath, null, draft.ShellProfileId);
            ActivateThread(CreateThread(project, draft.ThreadName));
            ShowTerminalShell();
        }

        private void OnProjectButtonClicked(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string projectId)
            {
                ActivateProject(FindProject(projectId));
                ShowTerminalShell();
            }
        }

        private void OnThreadButtonClicked(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string threadId)
            {
                ActivateThread(FindThread(threadId));
                ShowTerminalShell();
            }
        }

        private void OnThreadNameTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressThreadNameSync || _showingSettings || _activeThread is null)
            {
                return;
            }

            _activeThread.Name = string.IsNullOrWhiteSpace(ThreadNameBox.Text) ? "Untitled thread" : ThreadNameBox.Text.Trim();
            RefreshProjectTree();
        }

        private void TerminalTabs_AddTabButtonClick(TabView sender, object args)
        {
            AddTerminalTab(_activeProject, _activeThread);
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
            RequestFitForSelectedTerminal();
            RefreshProjectTree();
            UpdateHeader();
        }

        private void InitializeShellModel()
        {
            WorkspaceProject project = GetOrCreateProject(Environment.CurrentDirectory, null, SampleConfig.DefaultShellProfileId);
            if (project.Threads.Count == 0)
            {
                CreateThread(project);
            }

            ActivateProject(project);
        }

        private WorkspaceProject GetOrCreateProject(string rootPath, string name = null, string shellProfileId = null)
        {
            string normalizedPath = ResolveRequestedPath(rootPath);
            WorkspaceProject existing = _projects.FirstOrDefault(candidate => string.Equals(candidate.RootPath, normalizedPath, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                if (!string.IsNullOrWhiteSpace(shellProfileId))
                {
                    existing.ShellProfileId = ShellProfiles.Resolve(shellProfileId).Id;
                }

                return existing;
            }

            WorkspaceProject project = new(normalizedPath, ShellProfiles.Resolve(shellProfileId ?? SampleConfig.DefaultShellProfileId).Id, name);
            _projects.Add(project);
            RefreshProjectTree();
            return project;
        }

        private WorkspaceThread CreateThread(WorkspaceProject project, string threadName = null)
        {
            project ??= _activeProject ?? GetOrCreateProject(Environment.CurrentDirectory);

            WorkspaceThread thread = new(project, string.IsNullOrWhiteSpace(threadName) ? $"Thread {_threadSequence++}" : threadName.Trim());

            project.Threads.Add(thread);
            project.SelectedThreadId = thread.Id;
            EnsureThreadHasTab(project, thread);
            RefreshProjectTree();
            return thread;
        }

        private void EnsureThreadHasTab(WorkspaceProject project, WorkspaceThread thread)
        {
            if (thread.Tabs.Count == 0)
            {
                AddTerminalTab(project, thread);
            }
        }

        private TerminalTabRecord AddTerminalTab(WorkspaceProject project, WorkspaceThread thread)
        {
            project ??= _activeProject ?? GetOrCreateProject(Environment.CurrentDirectory);
            thread ??= _activeThread ?? CreateThread(project);

            TerminalControl terminal = new()
            {
                DisplayWorkingDirectory = FormatProjectPath(project),
                InitialWorkingDirectory = FormatProjectPath(project),
                ProcessWorkingDirectory = ShellProfiles.ResolveProcessWorkingDirectory(project.RootPath),
                ShellCommand = ShellProfiles.BuildLaunchCommand(project.ShellProfileId, project.RootPath),
            };

            terminal.ApplyTheme(ResolveTheme(SampleConfig.CurrentTheme));

            TerminalTabRecord tab = new($"Tab {_tabSequence++}", terminal);
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

                RefreshProjectTree();
            };

            thread.Tabs.Add(tab);
            thread.SelectedTabId = tab.Id;
            project.SelectedThreadId = thread.Id;

            if (thread == _activeThread)
            {
                RefreshTabView();
                RequestFitForSelectedTerminal();
            }

            RefreshProjectTree();
            return tab;
        }

        private void CloseTab(string tabId)
        {
            if (string.IsNullOrWhiteSpace(tabId))
            {
                return;
            }

            foreach (WorkspaceProject project in _projects)
            {
                foreach (WorkspaceThread thread in project.Threads)
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
                        AddTerminalTab(project, thread);
                    }

                    thread.SelectedTabId = thread.Tabs.FirstOrDefault()?.Id;

                    if (thread == _activeThread)
                    {
                        RefreshTabView();
                        FocusSelectedTerminal();
                        RequestFitForSelectedTerminal();
                    }

                    RefreshProjectTree();
                    UpdateHeader();
                    return;
                }
            }
        }

        private void SelectTab(string tabId)
        {
            if (string.IsNullOrWhiteSpace(tabId))
            {
                return;
            }

            foreach (WorkspaceProject project in _projects)
            {
                foreach (WorkspaceThread thread in project.Threads)
                {
                    TerminalTabRecord tab = thread.Tabs.FirstOrDefault(candidate => candidate.Id == tabId);
                    if (tab is null)
                    {
                        continue;
                    }

                    ActivateThread(thread);
                    thread.SelectedTabId = tab.Id;
                    project.SelectedThreadId = thread.Id;
                    RefreshTabView();
                    FocusSelectedTerminal();
                    RequestFitForSelectedTerminal();
                    return;
                }
            }

            throw new InvalidOperationException($"Unknown tab '{tabId}'.");
        }

        private void ActivateProject(WorkspaceProject project)
        {
            _activeProject = project ?? throw new ArgumentNullException(nameof(project));

            WorkspaceThread thread = project.Threads.FirstOrDefault(candidate => candidate.Id == project.SelectedThreadId)
                ?? project.Threads.FirstOrDefault()
                ?? CreateThread(project);

            ActivateThread(thread);
        }

        private void ActivateThread(WorkspaceThread thread)
        {
            _activeThread = thread ?? throw new ArgumentNullException(nameof(thread));
            _activeProject = FindProjectForThread(thread);
            _activeProject.SelectedThreadId = thread.Id;
            EnsureThreadHasTab(_activeProject, thread);
            RefreshProjectTree();
            RefreshTabView();
            UpdateHeader();
            RequestFitForSelectedTerminal();
        }

        private void RenameThread(string threadId, string nextName)
        {
            WorkspaceThread thread = string.IsNullOrWhiteSpace(threadId) ? _activeThread : FindThread(threadId);
            if (thread is null)
            {
                return;
            }

            thread.Name = string.IsNullOrWhiteSpace(nextName) ? thread.Name : nextName.Trim();
            RefreshProjectTree();
            UpdateHeader();
        }

        private void RefreshProjectTree()
        {
            ProjectListPanel.Children.Clear();
            bool isOpen = ShellSplitView.IsPaneOpen;

            foreach (WorkspaceProject project in _projects)
            {
                StackPanel group = new()
                {
                    Spacing = 2,
                };

                Button projectButton = new()
                {
                    Style = (Style)Application.Current.Resources["ShellNavButtonStyle"],
                    Tag = project.Id,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                };
                AutomationProperties.SetAutomationId(projectButton, $"shell-project-{project.Id}");
                projectButton.Click += OnProjectButtonClicked;

                Grid projectLayout = new()
                {
                    ColumnSpacing = 10,
                };
                projectLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                projectLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                projectLayout.Children.Add(new FontIcon
                {
                    FontSize = 12,
                    Glyph = "\uE8B7",
                    VerticalAlignment = VerticalAlignment.Center,
                });

                if (isOpen)
                {
                    StackPanel textStack = new()
                    {
                        Spacing = 0,
                    };
                    Grid.SetColumn(textStack, 1);
                    textStack.Children.Add(new TextBlock
                    {
                        Text = project.Name,
                        FontSize = 12,
                    });
                    textStack.Children.Add(new TextBlock
                    {
                        Text = FormatProjectPath(project),
                        Style = (Style)Application.Current.Resources["ShellHintTextStyle"],
                    });
                    projectLayout.Children.Add(textStack);
                }

                projectButton.Content = projectLayout;
                ApplyProjectButtonState(projectButton, project == _activeProject && !_showingSettings);
                group.Children.Add(projectButton);

                if (isOpen)
                {
                    StackPanel threadStack = new()
                    {
                        Spacing = 2,
                        Margin = new Thickness(20, 0, 0, 0),
                    };

                    foreach (WorkspaceThread thread in project.Threads)
                    {
                        Button threadButton = new()
                        {
                            Style = (Style)Application.Current.Resources["ShellSidebarThreadButtonStyle"],
                            Tag = thread.Id,
                            HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        };
                        AutomationProperties.SetAutomationId(threadButton, $"shell-thread-{thread.Id}");
                        threadButton.Click += OnThreadButtonClicked;

                        Grid threadLayout = new()
                        {
                            ColumnSpacing = 8,
                        };
                        threadLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        threadLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                        threadLayout.Children.Add(new FontIcon
                        {
                            FontSize = 10,
                            Glyph = "\uE8BD",
                            VerticalAlignment = VerticalAlignment.Center,
                        });

                        StackPanel threadText = new()
                        {
                            Spacing = 0,
                        };
                        Grid.SetColumn(threadText, 1);
                        threadText.Children.Add(new TextBlock
                        {
                            Text = thread.Name,
                            FontSize = 12,
                        });
                        threadText.Children.Add(new TextBlock
                        {
                            Text = thread.TabSummary,
                            Style = (Style)Application.Current.Resources["ShellHintTextStyle"],
                        });

                        threadLayout.Children.Add(threadText);
                        threadButton.Content = threadLayout;
                        ApplyThreadButtonState(threadButton, thread == _activeThread && !_showingSettings);
                        threadStack.Children.Add(threadButton);
                    }

                    group.Children.Add(threadStack);
                }

                ProjectListPanel.Children.Add(group);
            }
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
                TabViewItem item = new()
                {
                    Header = FormatTabHeader(tab.Header, tab.IsExited),
                    Content = tab.Terminal,
                    IsClosable = true,
                    Tag = tab,
                };

                TerminalTabs.TabItems.Add(item);
            }

            string selectedTabId = _activeThread.SelectedTabId ?? _activeThread.Tabs.FirstOrDefault()?.Id;
            TabViewItem selectedItem = TerminalTabs.TabItems
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
            UpdateSidebarActions();
            UpdateHeader();
        }

        private void ShowTerminalShell()
        {
            _showingSettings = false;
            SettingsFrame.Visibility = Visibility.Collapsed;
            TerminalTabs.Visibility = Visibility.Visible;
            UpdateSidebarActions();
            FocusSelectedTerminal();
            RequestFitForSelectedTerminal();
            UpdateHeader();
        }

        private void FocusSelectedTerminal()
        {
            if (TerminalTabs.SelectedItem is TabViewItem item && item.Tag is TerminalTabRecord tab)
            {
                tab.Terminal.FocusTerminal();
            }
        }

        private void RequestFitForSelectedTerminal()
        {
            if (TerminalTabs.SelectedItem is TabViewItem item && item.Tag is TerminalTabRecord tab)
            {
                tab.Terminal.RequestFit();
            }
        }

        private void SendInputToSelectedTerminal(string text)
        {
            if (TerminalTabs.SelectedItem is TabViewItem item && item.Tag is TerminalTabRecord tab)
            {
                tab.Terminal.SendInput(text);
            }
        }

        private void UpdateSidebarActions()
        {
            ApplyActionButtonState(SettingsNavButton, SettingsNavText, _showingSettings);
            ApplyActionButtonState(NewThreadButton, NewThreadText, false);
        }

        private void UpdatePaneLayout()
        {
            bool isOpen = ShellSplitView.IsPaneOpen;

            PaneBrandText.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
            NewThreadText.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
            SettingsNavText.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;

            ToolTipService.SetToolTip(PaneToggleButton, isOpen ? "Collapse sidebar" : "Expand sidebar");
            RefreshProjectTree();
        }

        private void UpdateHeader()
        {
            _suppressThreadNameSync = true;

            try
            {
                if (_showingSettings)
                {
                    ThreadNameBox.Text = "Preferences";
                    ThreadNameBox.IsReadOnly = true;
                    ActiveDirectoryText.Visibility = Visibility.Visible;
                    ActiveDirectoryText.Text = "Theme, shell profile, and launch defaults";
                    return;
                }

                ThreadNameBox.IsReadOnly = false;
                ThreadNameBox.Text = _activeThread?.Name ?? "Thread";
                ActiveDirectoryText.Visibility = Visibility.Visible;
                ActiveDirectoryText.Text = _activeProject is null ? string.Empty : FormatProjectPath(_activeProject);
            }
            finally
            {
                _suppressThreadNameSync = false;
            }
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
            return _projects.SelectMany(project => project.Threads).SelectMany(thread => thread.Tabs).Select(tab => tab.Terminal);
        }

        private WorkspaceProject ResolveActionProject(NativeAutomationActionRequest request)
        {
            if (!string.IsNullOrWhiteSpace(request.ProjectId))
            {
                return FindProject(request.ProjectId);
            }

            if (LooksLikePath(request.Value))
            {
                return GetOrCreateProject(request.Value);
            }

            return _activeProject ?? GetOrCreateProject(Environment.CurrentDirectory);
        }

        private static string ResolveThreadName(string value)
        {
            return LooksLikePath(value) ? null : value;
        }

        private WorkspaceProject FindProject(string projectId)
        {
            return _projects.FirstOrDefault(project => project.Id == projectId)
                ?? throw new InvalidOperationException($"Unknown project '{projectId}'.");
        }

        private WorkspaceThread FindThread(string threadId)
        {
            return _projects.SelectMany(project => project.Threads).FirstOrDefault(thread => thread.Id == threadId)
                ?? throw new InvalidOperationException($"Unknown thread '{threadId}'.");
        }

        private WorkspaceProject FindProjectForThread(WorkspaceThread thread)
        {
            return _projects.FirstOrDefault(project => ReferenceEquals(project, thread.Project) || project.Threads.Contains(thread))
                ?? throw new InvalidOperationException($"Unknown project for thread '{thread.Id}'.");
        }

        private static NativeAutomationThreadState BuildThreadState(WorkspaceThread thread)
        {
            return new NativeAutomationThreadState
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
            };
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

        private static void ApplyActionButtonState(Button button, TextBlock label, bool active)
        {
            button.Background = active ? AppBrush("ShellNavActiveBrush") : null;
            button.BorderBrush = active ? AppBrush("ShellBorderBrush") : null;
            button.Foreground = active ? AppBrush("ShellTextPrimaryBrush") : AppBrush("ShellTextSecondaryBrush");
            label.Foreground = active ? AppBrush("ShellTextPrimaryBrush") : AppBrush("ShellTextSecondaryBrush");
        }

        private static void ApplyProjectButtonState(Button button, bool active)
        {
            button.Background = active ? AppBrush("ShellNavActiveBrush") : null;
            button.BorderBrush = active ? AppBrush("ShellBorderBrush") : null;
            button.Foreground = active ? AppBrush("ShellTextPrimaryBrush") : AppBrush("ShellTextSecondaryBrush");
        }

        private static void ApplyThreadButtonState(Button button, bool active)
        {
            button.Background = active ? AppBrush("ShellMutedSurfaceBrush") : null;
            button.BorderBrush = active ? AppBrush("ShellBorderBrush") : null;
            button.Foreground = active ? AppBrush("ShellTextPrimaryBrush") : AppBrush("ShellTextSecondaryBrush");
        }

        private static string ResolveRequestedPath(string rootPath)
        {
            return ShellProfiles.NormalizeProjectPath(rootPath);
        }

        private static bool LooksLikePath(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && (value.Contains('\\') || value.Contains('/') || value.Contains(':'));
        }

        private static string FormatProjectPath(WorkspaceProject project)
        {
            return project.DisplayPath;
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

        private async System.Threading.Tasks.Task<ThreadDraft> PromptForThreadAsync()
        {
            TextBox pathBox = new()
            {
                Header = "Project directory",
                Text = _activeProject?.RootPath ?? Environment.CurrentDirectory,
            };

            TextBox threadBox = new()
            {
                Header = "Thread name",
                Text = $"Thread {_threadSequence}",
            };

            ComboBox profileBox = new()
            {
                Header = "Shell profile",
                DisplayMemberPath = nameof(ShellProfileDefinition.Name),
                SelectedValuePath = nameof(ShellProfileDefinition.Id),
                ItemsSource = ShellProfiles.All,
                SelectedValue = _activeProject?.ShellProfileId ?? SampleConfig.DefaultShellProfileId,
            };

            StackPanel body = new()
            {
                Spacing = 12,
            };
            body.Children.Add(pathBox);
            body.Children.Add(threadBox);
            body.Children.Add(profileBox);

            ContentDialog dialog = new()
            {
                XamlRoot = XamlRoot,
                Title = "New thread",
                PrimaryButtonText = "Create",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                Content = body,
            };

            ContentDialogResult result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                return null;
            }

            string projectPath = string.IsNullOrWhiteSpace(pathBox.Text) ? Environment.CurrentDirectory : pathBox.Text.Trim();
            string threadName = string.IsNullOrWhiteSpace(threadBox.Text) ? $"Thread {_threadSequence}" : threadBox.Text.Trim();
            string profileId = profileBox.SelectedValue as string ?? SampleConfig.DefaultShellProfileId;

            return new ThreadDraft(projectPath, threadName, profileId);
        }

        private sealed record ThreadDraft(string ProjectPath, string ThreadName, string ShellProfileId);
    }
}
