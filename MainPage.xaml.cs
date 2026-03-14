using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using SelfContainedDeployment.Automation;
using SelfContainedDeployment.Shell;
using SelfContainedDeployment.Terminal;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Windows.Storage.Pickers;
using Windows.Foundation;
using WinRT.Interop;

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

        public NativeAutomationUiTreeResponse GetAutomationUiTree()
        {
            int interactiveIndex = 0;
            List<NativeAutomationUiNode> children = new();
            List<DependencyObject> automationRoots = GetAutomationRoots();
            for (int index = 0; index < automationRoots.Count; index++)
            {
                NativeAutomationUiNode child = BuildUiNodeTree(automationRoots[index], $"root/{index}", ref interactiveIndex);
                if (child is not null)
                {
                    children.Add(child);
                }
            }

            NativeAutomationUiNode root = new()
            {
                ElementId = "root",
                ControlType = "AutomationRoot",
                Visible = true,
                Enabled = true,
                Children = children,
            };

            List<NativeAutomationUiNode> interactiveNodes = FlattenUiNodes(root)
                .Where(node => node.Interactive && node.Visible)
                .ToList();

            return new NativeAutomationUiTreeResponse
            {
                WindowTitle = ((App)Application.Current).MainWindowInstance?.Title,
                ActiveView = _showingSettings ? "settings" : "terminal",
                Root = root,
                InteractiveNodes = interactiveNodes,
            };
        }

        public NativeAutomationUiActionResponse PerformAutomationUiAction(NativeAutomationUiActionRequest request)
        {
            request ??= new NativeAutomationUiActionRequest();

            DependencyObject target = FindUiElement(request);
            if (target is null)
            {
                return new NativeAutomationUiActionResponse
                {
                    Ok = false,
                    Message = "No matching UI element was found.",
                };
            }

            string action = request.Action?.Trim().ToLowerInvariant();

            switch (action)
            {
                case "focus":
                    if (target is FrameworkElement focusable)
                    {
                        focusable.Focus(FocusState.Programmatic);
                    }
                    break;

                case "click":
                case "invoke":
                    InvokeUiElement(target);
                    break;

                case "rightclick":
                    ShowContextFlyout(target);
                    break;

                case "settext":
                    SetUiElementText(target, request.Value);
                    break;

                case "select":
                    SelectUiElement(target);
                    break;

                case "toggle":
                    ToggleUiElement(target);
                    break;

                case "invokemenuitem":
                    InvokeContextMenuItem(target, request.MenuItemText ?? request.Value);
                    break;

                default:
                    return new NativeAutomationUiActionResponse
                    {
                        Ok = false,
                        Message = $"Unknown ui action '{request.Action}'.",
                    };
            }

            int interactiveIndex = 0;
            NativeAutomationUiNode snapshot = BuildUiNodeTree(target, "target", ref interactiveIndex);

            return new NativeAutomationUiActionResponse
            {
                Ok = true,
                Target = snapshot,
            };
        }

        public async System.Threading.Tasks.Task<NativeAutomationTerminalStateResponse> GetTerminalStateAsync(NativeAutomationTerminalStateRequest request)
        {
            request ??= new NativeAutomationTerminalStateRequest();

            List<NativeAutomationTerminalSnapshot> snapshots = new();
            foreach ((WorkspaceProject project, WorkspaceThread thread, TerminalTabRecord tab) in EnumerateTerminalRecords())
            {
                if (!string.IsNullOrWhiteSpace(request.TabId) && !string.Equals(tab.Id, request.TabId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                NativeAutomationTerminalSnapshot snapshot = await tab.Terminal.GetTerminalSnapshotAsync().ConfigureAwait(true);
                snapshot.TabId = tab.Id;
                snapshot.ThreadId = thread.Id;
                snapshot.ProjectId = project.Id;
                snapshots.Add(snapshot);
            }

            return new NativeAutomationTerminalStateResponse
            {
                SelectedTabId = _activeThread?.SelectedTabId,
                Tabs = snapshots,
            };
        }

        public void ShowAutomationOverlay()
        {
            NativeAutomationUiTreeResponse snapshot = GetAutomationUiTree();
            AutomationOverlayCanvas.Children.Clear();
            AutomationOverlayCanvas.Visibility = Visibility.Visible;

            foreach (NativeAutomationUiNode node in snapshot.InteractiveNodes)
            {
                Border label = new()
                {
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Black) { Opacity = 0.88 },
                    BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Yellow),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(4, 1, 4, 1),
                    Child = new TextBlock
                    {
                        Text = node.RefLabel,
                        FontSize = 11,
                        Foreground = new SolidColorBrush(Microsoft.UI.Colors.Yellow),
                    },
                };

                Canvas.SetLeft(label, Math.Max(0, node.X));
                Canvas.SetTop(label, Math.Max(0, node.Y));
                AutomationOverlayCanvas.Children.Add(label);
            }
        }

        public void HideAutomationOverlay()
        {
            AutomationOverlayCanvas.Children.Clear();
            AutomationOverlayCanvas.Visibility = Visibility.Collapsed;
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
                        OpenProject(GetOrCreateProject(ResolveRequestedPath(request.Value), null, SampleConfig.DefaultShellProfileId));
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
                    case "duplicatethread":
                        DuplicateThread(request.ThreadId);
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

        private async void OnNewProjectClicked(object sender, RoutedEventArgs e)
        {
            ProjectDraft draft = await PromptForProjectAsync();
            if (draft is null)
            {
                return;
            }

            WorkspaceProject project = GetOrCreateProject(draft.ProjectPath, null, draft.ShellProfileId);
            OpenProject(project);
            ShowTerminalShell();
        }

        private void OnProjectAddThreadClicked(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string projectId)
            {
                ActivateThread(CreateThread(FindProject(projectId)));
                ShowTerminalShell();
            }
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

        private async void OnRenameThreadMenuClicked(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.Tag is string threadId)
            {
                WorkspaceThread thread = FindThread(threadId);
                string nextName = await PromptForThreadNameAsync("Rename thread", thread.Name);
                if (!string.IsNullOrWhiteSpace(nextName))
                {
                    RenameThread(threadId, nextName);
                }
            }
        }

        private void OnDuplicateThreadMenuClicked(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.Tag is string threadId)
            {
                DuplicateThread(threadId);
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

        private void OpenProject(WorkspaceProject project)
        {
            if (project.Threads.Count == 0)
            {
                ActivateThread(CreateThread(project));
                return;
            }

            ActivateProject(project);
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

        private void DuplicateThread(string threadId)
        {
            WorkspaceThread source = FindThread(threadId);
            WorkspaceProject project = FindProjectForThread(source);
            WorkspaceThread duplicate = CreateThread(project, $"Copy of {source.Name}");

            for (int i = 1; i < source.Tabs.Count; i++)
            {
                AddTerminalTab(project, duplicate);
            }

            ActivateThread(duplicate);
            ShowTerminalShell();
        }

        private void RefreshProjectTree()
        {
            ProjectListPanel.Children.Clear();
            bool isOpen = ShellSplitView.IsPaneOpen;

            foreach (WorkspaceProject project in _projects)
            {
                StackPanel group = new()
                {
                    Spacing = 4,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
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
                    ColumnSpacing = 8,
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
                if (!isOpen)
                {
                    group.Children.Add(projectButton);
                    ProjectListPanel.Children.Add(group);
                    continue;
                }

                Button addThreadButton = new()
                {
                    Style = (Style)Application.Current.Resources["ShellChromeButtonStyle"],
                    Tag = project.Id,
                    HorizontalAlignment = HorizontalAlignment.Right,
                };
                AutomationProperties.SetAutomationId(addThreadButton, $"shell-project-add-thread-{project.Id}");
                addThreadButton.Click += OnProjectAddThreadClicked;
                ToolTipService.SetToolTip(addThreadButton, "Add thread");
                addThreadButton.Content = new FontIcon
                {
                    FontSize = 11,
                    Glyph = "\uE710",
                };

                Grid projectHeader = new()
                {
                    ColumnDefinitions =
                    {
                        new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                        new ColumnDefinition { Width = GridLength.Auto },
                    },
                    ColumnSpacing = 6,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };
                projectHeader.Children.Add(projectButton);
                Grid.SetColumn(addThreadButton, 1);
                projectHeader.Children.Add(addThreadButton);
                group.Children.Add(projectHeader);

                StackPanel threadStack = new()
                {
                    Spacing = 2,
                    Margin = new Thickness(18, 0, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };

                foreach (WorkspaceThread thread in project.Threads)
                {
                    Button threadButton = new()
                    {
                        Style = (Style)Application.Current.Resources["ShellSidebarThreadButtonStyle"],
                        Tag = thread.Id,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                    };
                    AutomationProperties.SetAutomationId(threadButton, $"shell-thread-{thread.Id}");
                    threadButton.Click += OnThreadButtonClicked;

                    MenuFlyout threadMenu = new();
                    MenuFlyoutItem renameItem = new()
                    {
                        Text = "Rename",
                        Tag = thread.Id,
                    };
                    renameItem.Click += OnRenameThreadMenuClicked;
                    MenuFlyoutItem duplicateItem = new()
                    {
                        Text = "Duplicate",
                        Tag = thread.Id,
                    };
                    duplicateItem.Click += OnDuplicateThreadMenuClicked;
                    threadMenu.Items.Add(renameItem);
                    threadMenu.Items.Add(duplicateItem);
                    threadButton.ContextFlyout = threadMenu;

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
            ApplyActionButtonState(NewProjectButton, NewProjectText, false);
        }

        private void UpdatePaneLayout()
        {
            bool isOpen = ShellSplitView.IsPaneOpen;

            PaneBrandText.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
            ProjectSectionText.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
            NewProjectText.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
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
            return EnumerateTerminalRecords().Select(record => record.Tab.Terminal);
        }

        private IEnumerable<(WorkspaceProject Project, WorkspaceThread Thread, TerminalTabRecord Tab)> EnumerateTerminalRecords()
        {
            return _projects.SelectMany(project => project.Threads.SelectMany(thread => thread.Tabs.Select(tab => (project, thread, tab))));
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

        private NativeAutomationUiNode BuildUiNodeTree(DependencyObject node, string path, ref int interactiveIndex)
        {
            if (node is null || ReferenceEquals(node, AutomationOverlayCanvas))
            {
                return null;
            }

            List<NativeAutomationUiNode> children = new();
            if (node is not Microsoft.UI.Xaml.Controls.WebView2)
            {
                int childCount = VisualTreeHelper.GetChildrenCount(node);
                for (int index = 0; index < childCount; index++)
                {
                    NativeAutomationUiNode childNode = BuildUiNodeTree(VisualTreeHelper.GetChild(node, index), $"{path}/{index}", ref interactiveIndex);
                    if (childNode is not null)
                    {
                        children.Add(childNode);
                    }
                }
            }

            if (!ShouldTrackUiNode(node) && children.Count == 0)
            {
                return null;
            }

            FrameworkElement element = node as FrameworkElement;
            string automationId = element is not null ? AutomationProperties.GetAutomationId(element) : null;
            string name = element is not null ? AutomationProperties.GetName(element) : null;
            string text = ExtractNodeText(node);
            bool interactive = IsInteractiveUiNode(node, automationId);
            bool visible = IsNodeVisible(node);
            bool enabled = node is Control control ? control.IsEnabled : true;
            bool focused = element is Control controlForFocus
                ? controlForFocus.FocusState != FocusState.Unfocused
                : false;
            bool selected = IsNodeSelected(node, automationId);
            bool isChecked = IsNodeChecked(node);
            Rect bounds = GetNodeBounds(node);

            NativeAutomationUiNode result = new()
            {
                ElementId = path,
                AutomationId = automationId,
                Name = name,
                ControlType = node.GetType().Name,
                Text = text,
                Visible = visible,
                Enabled = enabled,
                Focused = focused,
                Selected = selected,
                Checked = isChecked,
                Interactive = interactive,
                X = bounds.X,
                Y = bounds.Y,
                Width = bounds.Width,
                Height = bounds.Height,
                Children = children,
            };

            if (result.Interactive && result.Visible)
            {
                interactiveIndex++;
                result.RefLabel = $"e{interactiveIndex}";
            }

            return result;
        }

        private static IEnumerable<NativeAutomationUiNode> FlattenUiNodes(NativeAutomationUiNode root)
        {
            if (root is null)
            {
                yield break;
            }

            yield return root;
            foreach (NativeAutomationUiNode child in root.Children)
            {
                foreach (NativeAutomationUiNode descendant in FlattenUiNodes(child))
                {
                    yield return descendant;
                }
            }
        }

        private bool ShouldTrackUiNode(DependencyObject node)
        {
            return node is FrameworkElement or TextBlock or FontIcon or TabViewItem;
        }

        private static bool IsInteractiveUiNode(DependencyObject node, string automationId)
        {
            if (!string.IsNullOrWhiteSpace(automationId))
            {
                return true;
            }

            return node is Button
                or ToggleButton
                or RadioButton
                or CheckBox
                or ComboBox
                or TextBox
                or TabView
                or TabViewItem
                or TerminalControl
                or Microsoft.UI.Xaml.Controls.WebView2
                or MenuFlyoutItem;
        }

        private string ExtractNodeText(DependencyObject node)
        {
            if (node is TabViewItem tabViewItem)
            {
                return ExtractContentText(tabViewItem.Header);
            }

            switch (node)
            {
                case TextBlock textBlock:
                    return textBlock.Text;
                case TextBox textBox:
                    return textBox.Text;
                case MenuFlyoutItem menuItem:
                    return menuItem.Text;
                case ContentControl contentControl:
                    return ExtractContentText(contentControl.Content);
                default:
                    return TryExtractPropertyText(node, "Header")
                        ?? TryExtractPropertyText(node, "Text")
                        ?? ExtractTextFromVisual(node);
            }
        }

        private static string ExtractContentText(object content)
        {
            return content switch
            {
                null => null,
                string text => text,
                TextBlock textBlock => textBlock.Text,
                FontIcon fontIcon => fontIcon.Glyph,
                FrameworkElement element => ExtractTextFromVisual(element),
                _ => content.ToString(),
            };
        }

        private static string TryExtractPropertyText(object instance, string propertyName)
        {
            if (instance is null || string.IsNullOrWhiteSpace(propertyName))
            {
                return null;
            }

            var property = instance?.GetType().GetProperty(propertyName);
            return property is null ? null : ExtractContentText(property.GetValue(instance));
        }

        private static string ExtractTextFromVisual(DependencyObject node)
        {
            if (node is null)
            {
                return null;
            }

            List<string> parts = new();
            CollectVisualText(node, parts);

            string[] filtered = parts
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .Select(part => part.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            return filtered.Length == 0 ? null : string.Join(" ", filtered);
        }

        private static void CollectVisualText(DependencyObject node, List<string> parts)
        {
            switch (node)
            {
                case TextBlock textBlock when !string.IsNullOrWhiteSpace(textBlock.Text):
                    parts.Add(textBlock.Text);
                    break;
                case MenuFlyoutItem menuItem when !string.IsNullOrWhiteSpace(menuItem.Text):
                    parts.Add(menuItem.Text);
                    break;
                case TextBox textBox when !string.IsNullOrWhiteSpace(textBox.Text):
                    parts.Add(textBox.Text);
                    break;
            }

            if (node is Microsoft.UI.Xaml.Controls.WebView2)
            {
                return;
            }

            int childCount = VisualTreeHelper.GetChildrenCount(node);
            for (int index = 0; index < childCount; index++)
            {
                CollectVisualText(VisualTreeHelper.GetChild(node, index), parts);
            }
        }

        private bool IsNodeSelected(DependencyObject node, string automationId)
        {
            if (node is TabViewItem item)
            {
                return ReferenceEquals(TerminalTabs.SelectedItem, item);
            }

            if (!string.IsNullOrWhiteSpace(automationId))
            {
                if (automationId.StartsWith("shell-thread-", StringComparison.Ordinal) && _activeThread is not null)
                {
                    return automationId.EndsWith(_activeThread.Id, StringComparison.Ordinal);
                }

                if (automationId.StartsWith("shell-project-", StringComparison.Ordinal) && _activeProject is not null && !automationId.Contains("-add-thread-", StringComparison.Ordinal))
                {
                    return automationId.EndsWith(_activeProject.Id, StringComparison.Ordinal);
                }
            }

            return false;
        }

        private static bool IsNodeChecked(DependencyObject node)
        {
            return node switch
            {
                ToggleButton toggle => toggle.IsChecked == true,
                _ => false,
            };
        }

        private Rect GetNodeBounds(DependencyObject node)
        {
            if (node is not FrameworkElement element || ShellRoot is null)
            {
                return default;
            }

            try
            {
                GeneralTransform transform = element.TransformToVisual(ShellRoot);
                Point origin = transform.TransformPoint(new Point(0, 0));
                return new Rect(origin.X, origin.Y, element.ActualWidth, element.ActualHeight);
            }
            catch
            {
                return default;
            }
        }

        private static bool IsNodeVisible(DependencyObject node)
        {
            if (node is not FrameworkElement element)
            {
                return true;
            }

            if (element.Visibility == Visibility.Collapsed)
            {
                return false;
            }

            if (node is not Page && element.ActualWidth <= 0 && element.ActualHeight <= 0)
            {
                return false;
            }

            DependencyObject current = node;
            while (current is not null)
            {
                if (current is FrameworkElement ancestor && ancestor.Visibility == Visibility.Collapsed)
                {
                    return false;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return true;
        }

        private DependencyObject FindUiElement(NativeAutomationUiActionRequest request)
        {
            if (!string.IsNullOrWhiteSpace(request.AutomationId))
            {
                return FindFirstElementAcrossRoots(candidate =>
                    candidate is FrameworkElement element &&
                    IsNodeVisible(candidate) &&
                    string.Equals(AutomationProperties.GetAutomationId(element), request.AutomationId, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(request.RefLabel))
            {
                NativeAutomationUiNode match = GetAutomationUiTree().InteractiveNodes
                    .FirstOrDefault(node => string.Equals(node.RefLabel, request.RefLabel, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    return FindElementByPath(match.ElementId);
                }
            }

            if (!string.IsNullOrWhiteSpace(request.ElementId))
            {
                return FindElementByPath(request.ElementId);
            }

            if (!string.IsNullOrWhiteSpace(request.Name))
            {
                return FindFirstElementAcrossRoots(candidate =>
                    candidate is FrameworkElement element &&
                    IsNodeVisible(candidate) &&
                    string.Equals(AutomationProperties.GetName(element), request.Name, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(request.Text))
            {
                NativeAutomationUiNode match = GetAutomationUiTree().InteractiveNodes
                    .FirstOrDefault(node => string.Equals(node.Text, request.Text, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    return FindElementByPath(match.ElementId);
                }
            }

            return null;
        }

        private List<DependencyObject> GetAutomationRoots()
        {
            List<DependencyObject> roots = new();
            if (ShellRoot is not null)
            {
                roots.Add(ShellRoot);
            }

            if (ShellRoot?.XamlRoot is not null)
            {
                foreach (Popup popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(ShellRoot.XamlRoot))
                {
                    if (popup?.Child is not null && !ReferenceEquals(popup.Child, AutomationOverlayCanvas))
                    {
                        roots.Add(popup.Child);
                    }
                }
            }

            return roots;
        }

        private DependencyObject FindFirstElementAcrossRoots(Func<DependencyObject, bool> predicate)
        {
            foreach (DependencyObject root in GetAutomationRoots())
            {
                DependencyObject match = FindFirstElement(root, predicate);
                if (match is not null)
                {
                    return match;
                }
            }

            return null;
        }

        private DependencyObject FindFirstElement(DependencyObject root, Func<DependencyObject, bool> predicate)
        {
            if (root is null)
            {
                return null;
            }

            if (predicate(root))
            {
                return root;
            }

            if (root is Microsoft.UI.Xaml.Controls.WebView2)
            {
                return null;
            }

            int childCount = VisualTreeHelper.GetChildrenCount(root);
            for (int index = 0; index < childCount; index++)
            {
                DependencyObject match = FindFirstElement(VisualTreeHelper.GetChild(root, index), predicate);
                if (match is not null)
                {
                    return match;
                }
            }

            return null;
        }

        private DependencyObject FindElementByPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || !string.Equals(parts[0], "root", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            List<DependencyObject> roots = GetAutomationRoots();
            if (parts.Length == 1)
            {
                return ShellRoot;
            }

            if (!int.TryParse(parts[1], out int rootIndex) || rootIndex < 0 || rootIndex >= roots.Count)
            {
                return null;
            }

            DependencyObject current = roots[rootIndex];
            for (int i = 2; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i], out int childIndex))
                {
                    return null;
                }

                if (childIndex < 0 || childIndex >= VisualTreeHelper.GetChildrenCount(current))
                {
                    return null;
                }

                current = VisualTreeHelper.GetChild(current, childIndex);
            }

            return current;
        }

        private void InvokeUiElement(DependencyObject node)
        {
            if (node is ButtonBase buttonBase &&
                buttonBase is Button &&
                TryInvokeKnownShellAction(buttonBase))
            {
                return;
            }

            if (node is TabViewItem tabViewItem)
            {
                TerminalTabs.SelectedItem = tabViewItem;
                return;
            }

            if (node is FrameworkElement element && TryInvokeViaAutomationPeer(element))
            {
                return;
            }

            throw new InvalidOperationException($"Element '{node.GetType().Name}' could not be invoked.");
        }

        private bool TryInvokeKnownShellAction(DependencyObject node)
        {
            if (node is not FrameworkElement element)
            {
                return false;
            }

            string automationId = AutomationProperties.GetAutomationId(element);
            if (string.IsNullOrWhiteSpace(automationId))
            {
                return false;
            }

            if (string.Equals(automationId, "shell-pane-toggle", StringComparison.Ordinal))
            {
                ShellSplitView.IsPaneOpen = !ShellSplitView.IsPaneOpen;
                UpdatePaneLayout();
                return true;
            }

            if (string.Equals(automationId, "shell-nav-settings", StringComparison.Ordinal))
            {
                ShowSettings();
                return true;
            }

            if (string.Equals(automationId, "shell-new-project", StringComparison.Ordinal))
            {
                return false;
            }

            if (automationId.StartsWith("shell-project-add-thread-", StringComparison.Ordinal))
            {
                ActivateThread(CreateThread(FindProject(automationId["shell-project-add-thread-".Length..])));
                ShowTerminalShell();
                return true;
            }

            if (automationId.StartsWith("shell-project-", StringComparison.Ordinal) && !automationId.Contains("-add-thread-", StringComparison.Ordinal))
            {
                ActivateProject(FindProject(automationId["shell-project-".Length..]));
                ShowTerminalShell();
                return true;
            }

            if (automationId.StartsWith("shell-thread-", StringComparison.Ordinal))
            {
                ActivateThread(FindThread(automationId["shell-thread-".Length..]));
                ShowTerminalShell();
                return true;
            }

            return false;
        }

        private static bool TryInvokeViaAutomationPeer(FrameworkElement element)
        {
            AutomationPeer peer = FrameworkElementAutomationPeer.FromElement(element) ?? FrameworkElementAutomationPeer.CreatePeerForElement(element);
            if (peer is null)
            {
                return false;
            }

            if (peer.GetPattern(PatternInterface.Invoke) is IInvokeProvider invokeProvider)
            {
                invokeProvider.Invoke();
                return true;
            }

            if (peer.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider selectionItemProvider)
            {
                selectionItemProvider.Select();
                return true;
            }

            if (peer.GetPattern(PatternInterface.Toggle) is IToggleProvider toggleProvider)
            {
                toggleProvider.Toggle();
                return true;
            }

            return false;
        }

        private static void SetUiElementText(DependencyObject node, string value)
        {
            switch (node)
            {
                case TextBox textBox:
                    textBox.Text = value ?? string.Empty;
                    break;
                case ComboBox comboBox:
                    SelectComboBoxValue(comboBox, value);
                    break;
                default:
                    if (node is FrameworkElement element)
                    {
                        AutomationPeer peer = FrameworkElementAutomationPeer.FromElement(element) ?? FrameworkElementAutomationPeer.CreatePeerForElement(element);
                        if (peer?.GetPattern(PatternInterface.Value) is IValueProvider valueProvider)
                        {
                            valueProvider.SetValue(value ?? string.Empty);
                            return;
                        }
                    }

                    throw new InvalidOperationException($"Element '{node.GetType().Name}' does not support text input.");
            }
        }

        private static void SelectComboBoxValue(ComboBox comboBox, string value)
        {
            string desired = value?.Trim() ?? string.Empty;
            IEnumerable items = comboBox.ItemsSource as IEnumerable ?? comboBox.Items.Cast<object>();

            foreach (object item in items)
            {
                if (item is null)
                {
                    continue;
                }

                string selectedValue = TryExtractPropertyText(item, comboBox.SelectedValuePath);
                string displayValue = TryExtractPropertyText(item, comboBox.DisplayMemberPath) ?? ExtractContentText(item);
                if (string.Equals(selectedValue, desired, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(displayValue, desired, StringComparison.OrdinalIgnoreCase))
                {
                    comboBox.SelectedItem = item;
                    return;
                }
            }

            throw new InvalidOperationException($"ComboBox does not contain '{value}'.");
        }

        private void SelectUiElement(DependencyObject node)
        {
            switch (node)
            {
                case TabViewItem item:
                    TerminalTabs.SelectedItem = item;
                    break;
                case FrameworkElement element when TryInvokeViaAutomationPeer(element):
                    break;
                default:
                    throw new InvalidOperationException($"Element '{node.GetType().Name}' does not support selection.");
            }
        }

        private static void ToggleUiElement(DependencyObject node)
        {
            switch (node)
            {
                case ToggleButton toggle:
                    toggle.IsChecked = !(toggle.IsChecked ?? false);
                    break;
                case FrameworkElement element:
                    AutomationPeer peer = FrameworkElementAutomationPeer.FromElement(element) ?? FrameworkElementAutomationPeer.CreatePeerForElement(element);
                    if (peer?.GetPattern(PatternInterface.Toggle) is IToggleProvider toggleProvider)
                    {
                        toggleProvider.Toggle();
                        return;
                    }

                    throw new InvalidOperationException($"Element '{node.GetType().Name}' does not support toggle.");
                default:
                    throw new InvalidOperationException($"Element '{node.GetType().Name}' does not support toggle.");
            }
        }

        private static void ShowContextFlyout(DependencyObject node)
        {
            if (node is FrameworkElement element && element.ContextFlyout is not null)
            {
                element.ContextFlyout.ShowAt(element);
                return;
            }

            throw new InvalidOperationException("Element does not expose a context flyout.");
        }

        private void InvokeContextMenuItem(DependencyObject node, string menuItemText)
        {
            if (node is not FrameworkElement element || element.ContextFlyout is not MenuFlyout menu)
            {
                throw new InvalidOperationException("Element does not expose a context menu.");
            }

            MenuFlyoutItemBase item = menu.Items
                .OfType<MenuFlyoutItemBase>()
                .FirstOrDefault(candidate => string.Equals((candidate as MenuFlyoutItem)?.Text, menuItemText, StringComparison.OrdinalIgnoreCase));

            if (item is MenuFlyoutItem menuItem && TryInvokeViaAutomationPeer(menuItem))
            {
                return;
            }

            throw new InvalidOperationException($"Context menu item '{menuItemText}' could not be invoked.");
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

        private async System.Threading.Tasks.Task<ProjectDraft> PromptForProjectAsync()
        {
            TextBox pathBox = new()
            {
                Header = "Project directory",
                Text = _activeProject?.RootPath ?? Environment.CurrentDirectory,
                Style = (Style)Application.Current.Resources["ShellInlineTextBoxStyle"],
            };
            AutomationProperties.SetAutomationId(pathBox, "dialog-project-path");

            Button browseButton = new()
            {
                Content = "Browse…",
                HorizontalAlignment = HorizontalAlignment.Left,
                MinWidth = 96,
            };
            AutomationProperties.SetAutomationId(browseButton, "dialog-project-browse");
            browseButton.Click += async (_, _) =>
            {
                string selectedPath = await BrowseForFolderAsync(pathBox.Text);
                if (!string.IsNullOrWhiteSpace(selectedPath))
                {
                    pathBox.Text = selectedPath;
                }
            };

            ComboBox profileBox = new()
            {
                Header = "Shell profile",
                DisplayMemberPath = nameof(ShellProfileDefinition.Name),
                SelectedValuePath = nameof(ShellProfileDefinition.Id),
                ItemsSource = ShellProfiles.All,
                SelectedValue = _activeProject?.ShellProfileId ?? SampleConfig.DefaultShellProfileId,
            };
            AutomationProperties.SetAutomationId(profileBox, "dialog-project-shell-profile");

            StackPanel body = new()
            {
                Spacing = 12,
            };
            AutomationProperties.SetAutomationId(body, "dialog-project-body");
            body.Children.Add(pathBox);
            body.Children.Add(browseButton);
            body.Children.Add(profileBox);

            ContentDialog dialog = new()
            {
                XamlRoot = XamlRoot,
                Title = "New project",
                PrimaryButtonText = "Add project",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                Content = body,
            };

            ContentDialogResult result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                return null;
            }

            return new ProjectDraft(
                string.IsNullOrWhiteSpace(pathBox.Text) ? Environment.CurrentDirectory : pathBox.Text.Trim(),
                profileBox.SelectedValue as string ?? SampleConfig.DefaultShellProfileId);
        }

        private async System.Threading.Tasks.Task<string> PromptForThreadNameAsync(string title, string initialValue)
        {
            TextBox nameBox = new()
            {
                Text = initialValue,
                Style = (Style)Application.Current.Resources["ShellInlineTextBoxStyle"],
            };
            AutomationProperties.SetAutomationId(nameBox, "dialog-thread-name");

            ContentDialog dialog = new()
            {
                XamlRoot = XamlRoot,
                Title = title,
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                Content = nameBox,
            };

            ContentDialogResult result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary ? nameBox.Text?.Trim() : null;
        }

        private async System.Threading.Tasks.Task<string> BrowseForFolderAsync(string initialPath)
        {
            FolderPicker picker = new();
            picker.FileTypeFilter.Add("*");

            IntPtr hwnd = WindowNative.GetWindowHandle(((App)Application.Current).MainWindowInstance);
            InitializeWithWindow.Initialize(picker, hwnd);

            try
            {
                if (!string.IsNullOrWhiteSpace(initialPath) && !initialPath.StartsWith("/", StringComparison.Ordinal))
                {
                    picker.SuggestedStartLocation = PickerLocationId.Desktop;
                }
            }
            catch
            {
            }

            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }

        private sealed record ProjectDraft(string ProjectPath, string ShellProfileId);
    }
}
