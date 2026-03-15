using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SelfContainedDeployment.Automation;
using SelfContainedDeployment.Panes;
using SelfContainedDeployment.Shell;
using SelfContainedDeployment.Terminal;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Windows.Storage.Pickers;
using Windows.Foundation;
using WinRT.Interop;

namespace SelfContainedDeployment
{
    public partial class MainPage : Page
    {
        private const double PaneDividerThickness = 3;
        private const double MinPaneSplitRatio = 0.24;
        private const double MaxPaneSplitRatio = 0.76;
        private readonly List<WorkspaceProject> _projects = new();
        private readonly Dictionary<string, TabViewItem> _tabItemsById = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Border> _paneContainersById = new(StringComparer.Ordinal);
        private WorkspaceProject _activeProject;
        private WorkspaceThread _activeThread;
        private Border _activeSplitter;
        private string _activeSplitterDirection;
        private uint? _activeSplitterPointerId;
        private double _splitterDragOriginX;
        private double _splitterDragOriginY;
        private double _splitterStartPrimaryRatio;
        private double _splitterStartSecondaryRatio;
        private bool _showingSettings;
        private bool _suppressTabSelectionChanged;
        private bool _suppressThreadNameSync;
        private int _threadSequence = 1;

        public static MainPage Current;

        public MainPage()
        {
            InitializeComponent();
            Current = this;
            Loaded += OnLoaded;
            ActualThemeChanged += OnActualThemeChanged;
        }

        private static void LogAutomationEvent(string category, string name, string message = null, IReadOnlyDictionary<string, string> data = null)
        {
            NativeAutomationEventLog.Record(category, name, message, data);
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
            LogAutomationEvent("shell", "theme.changed", $"Theme set to {ResolveTheme(theme).ToString().ToLowerInvariant()}", new Dictionary<string, string>
            {
                ["theme"] = ResolveTheme(theme).ToString().ToLowerInvariant(),
            });
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

            LogAutomationEvent("shell", "profile.changed", $"Default shell profile set to {SampleConfig.DefaultShellProfileId}", new Dictionary<string, string>
            {
                ["profileId"] = SampleConfig.DefaultShellProfileId,
                ["projectId"] = _activeProject?.Id ?? string.Empty,
            });
        }

        public void ApplyPaneLimit(int paneLimit)
        {
            SampleConfig.MaxPaneCountPerThread = Math.Clamp(paneLimit, 2, 4);

            RefreshProjectTree();
            RefreshTabView();
            RenderPaneWorkspace();
            RequestLayoutForVisiblePanes();

            LogAutomationEvent("shell", "pane-limit.changed", $"Thread pane limit set to {SampleConfig.MaxPaneCountPerThread}", new Dictionary<string, string>
            {
                ["paneLimit"] = SampleConfig.MaxPaneCountPerThread.ToString(),
            });
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

                case "doubleclick":
                    DoubleClickUiElement(target);
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

                case "hover":
                    ApplyUiVisualState(target, "PointerOver");
                    break;

                case "press":
                    ApplyUiVisualState(target, "Pressed");
                    break;

                case "normalstate":
                    ApplyUiVisualState(target, "Normal");
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
            LogAutomationEvent("automation", "ui-action.executed", $"Executed ui action '{request.Action}'", new Dictionary<string, string>
            {
                ["action"] = request.Action ?? string.Empty,
                ["automationId"] = request.AutomationId ?? string.Empty,
                ["refLabel"] = request.RefLabel ?? string.Empty,
                ["elementId"] = request.ElementId ?? string.Empty,
                ["text"] = request.Text ?? string.Empty,
            });

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
            foreach ((WorkspaceProject project, WorkspaceThread thread, TerminalPaneRecord pane) in EnumerateTerminalRecords())
            {
                if (!string.IsNullOrWhiteSpace(request.TabId) && !string.Equals(pane.Id, request.TabId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                NativeAutomationTerminalSnapshot snapshot = await pane.Terminal.GetTerminalSnapshotAsync().ConfigureAwait(true);
                snapshot.TabId = pane.Id;
                snapshot.ThreadId = thread.Id;
                snapshot.ProjectId = project.Id;
                snapshots.Add(snapshot);
            }

            return new NativeAutomationTerminalStateResponse
            {
                SelectedTabId = _activeThread?.SelectedPaneId,
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
                ActiveTabId = _activeThread?.SelectedPaneId,
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
                        RequestLayoutForVisiblePanes();
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
                    case "newbrowserpane":
                        ShowTerminalShell();
                        AddBrowserPane(_activeProject, _activeThread, request.Value);
                        break;
                    case "neweditorpane":
                        ShowTerminalShell();
                        AddEditorPane(_activeProject, _activeThread);
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
                    case "movetabafter":
                        MoveTabAfter(request.TabId, request.TargetTabId);
                        ShowTerminalShell();
                        break;
                    case "setlayout":
                        SetThreadLayout(request.ThreadId, request.Value);
                        ShowTerminalShell();
                        break;
                    case "setpanesplit":
                        SetPaneSplit(request.ThreadId, request.Value);
                        ShowTerminalShell();
                        break;
                    case "closetab":
                        CloseTab(request.TabId);
                        break;
                    case "navigatebrowser":
                        NavigateSelectedBrowser(request.Value);
                        ShowTerminalShell();
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
                    case "deletethread":
                        DeleteThread(request.ThreadId);
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

                LogAutomationEvent("automation", "action.executed", $"Executed action '{request.Action}'", new Dictionary<string, string>
                {
                    ["action"] = request.Action ?? string.Empty,
                    ["projectId"] = request.ProjectId ?? string.Empty,
                    ["threadId"] = request.ThreadId ?? string.Empty,
                    ["tabId"] = request.TabId ?? string.Empty,
                    ["targetTabId"] = request.TargetTabId ?? string.Empty,
                    ["value"] = request.Value ?? string.Empty,
                });

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
            RequestLayoutForVisiblePanes();
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

        private async void OnThreadButtonDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string threadId)
            {
                e.Handled = true;
                await BeginRenameThreadAsync(threadId);
            }
        }

        private async void OnRenameThreadMenuClicked(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.Tag is string threadId)
            {
                await BeginRenameThreadAsync(threadId);
            }
        }

        private void OnDuplicateThreadMenuClicked(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.Tag is string threadId)
            {
                DuplicateThread(threadId);
            }
        }

        private void OnDeleteThreadMenuClicked(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.Tag is string threadId)
            {
                DeleteThread(threadId);
            }
        }

        private void OnEmptyStateNewThreadClicked(object sender, RoutedEventArgs e)
        {
            if (_activeProject is null)
            {
                return;
            }

            ActivateThread(CreateThread(_activeProject));
            ShowTerminalShell();
        }

        private void OnThreadNameTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressThreadNameSync || _showingSettings || _activeThread is null)
            {
                return;
            }

            _activeThread.Name = string.IsNullOrWhiteSpace(ThreadNameBox.Text) ? "Untitled thread" : ThreadNameBox.Text.Trim();
            RefreshProjectTree();
            LogAutomationEvent("shell", "thread.header_edited", $"Header edited for {_activeThread.Name}", new Dictionary<string, string>
            {
                ["threadId"] = _activeThread.Id,
                ["projectId"] = _activeProject?.Id ?? string.Empty,
                ["threadName"] = _activeThread.Name,
            });
        }

        private void TerminalTabs_AddTabButtonClick(TabView sender, object args)
        {
            if (_activeProject is not null && _activeThread is null)
            {
                ActivateThread(CreateThread(_activeProject));
            }

            AddTerminalTab(_activeProject, _activeThread);
        }

        private void OnAddBrowserPaneClicked(object sender, RoutedEventArgs e)
        {
            if (_activeProject is not null && _activeThread is null)
            {
                ActivateThread(CreateThread(_activeProject));
            }

            AddBrowserPane(_activeProject, _activeThread);
        }

        private void OnAddEditorPaneClicked(object sender, RoutedEventArgs e)
        {
            if (_activeProject is not null && _activeThread is null)
            {
                ActivateThread(CreateThread(_activeProject));
            }

            AddEditorPane(_activeProject, _activeThread);
        }

        private void TerminalTabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
        {
            if (args.Item is TabViewItem item && item.Tag is WorkspacePaneRecord pane)
            {
                CloseTab(pane.Id);
            }
        }

        private void TerminalTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_activeThread is null || _suppressTabSelectionChanged)
            {
                return;
            }

            if (TerminalTabs.SelectedItem is TabViewItem item && item.Tag is WorkspacePaneRecord pane)
            {
                _activeThread.SelectedPaneId = pane.Id;
                LogAutomationEvent("shell", "pane.selected", $"Selected pane {pane.Id}", new Dictionary<string, string>
                {
                    ["paneId"] = pane.Id,
                    ["paneKind"] = pane.Kind.ToString().ToLowerInvariant(),
                    ["threadId"] = _activeThread.Id,
                    ["projectId"] = _activeProject?.Id ?? string.Empty,
                });
            }

            FocusSelectedPane();
            RequestLayoutForVisiblePanes();
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
            LogAutomationEvent("shell", "workspace.initialized", "Initialized workspace model", new Dictionary<string, string>
            {
                ["projectId"] = project.Id,
                ["projectPath"] = project.RootPath,
            });
        }

        private WorkspaceProject GetOrCreateProject(string rootPath, string name = null, string shellProfileId = null)
        {
            string normalizedPath = ResolveRequestedPath(rootPath);
            ShellProfiles.EnsureProjectDirectory(normalizedPath, out _);
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
            LogAutomationEvent("shell", "project.created", $"Created project {project.Name}", new Dictionary<string, string>
            {
                ["projectId"] = project.Id,
                ["projectPath"] = project.RootPath,
                ["shellProfileId"] = project.ShellProfileId,
            });
            return project;
        }

        private WorkspaceThread CreateThread(WorkspaceProject project, string threadName = null, bool ensureInitialPane = true)
        {
            project ??= _activeProject ?? GetOrCreateProject(Environment.CurrentDirectory);

            WorkspaceThread thread = new(project, string.IsNullOrWhiteSpace(threadName) ? $"Thread {_threadSequence++}" : threadName.Trim());

            project.Threads.Add(thread);
            project.SelectedThreadId = thread.Id;
            if (ensureInitialPane)
            {
                EnsureThreadHasTab(project, thread);
            }
            RefreshProjectTree();
            LogAutomationEvent("shell", "thread.created", $"Created thread {thread.Name}", new Dictionary<string, string>
            {
                ["threadId"] = thread.Id,
                ["projectId"] = project.Id,
                ["threadName"] = thread.Name,
            });
            return thread;
        }

        private void EnsureThreadHasTab(WorkspaceProject project, WorkspaceThread thread)
        {
            if (thread.Panes.Count == 0)
            {
                AddTerminalTab(project, thread);
            }
        }

        private WorkspaceThread ResolveTargetThreadForNewPane(WorkspaceProject project, WorkspaceThread thread, WorkspacePaneKind paneKind)
        {
            project ??= _activeProject ?? GetOrCreateProject(Environment.CurrentDirectory);
            thread ??= _activeThread ?? CreateThread(project);

            if (thread.Panes.Count < thread.PaneLimit)
            {
                return thread;
            }

            WorkspaceThread overflowThread = CreateThread(project, ensureInitialPane: false);
            LogAutomationEvent("shell", "thread.overflow_created", $"Created overflow thread {overflowThread.Name} for {paneKind.ToString().ToLowerInvariant()} pane", new Dictionary<string, string>
            {
                ["projectId"] = project.Id,
                ["sourceThreadId"] = thread.Id,
                ["overflowThreadId"] = overflowThread.Id,
                ["paneKind"] = paneKind.ToString().ToLowerInvariant(),
                ["paneLimit"] = overflowThread.PaneLimit.ToString(),
            });
            return overflowThread;
        }

        private TerminalPaneRecord AddTerminalTab(WorkspaceProject project, WorkspaceThread thread)
        {
            project ??= _activeProject ?? GetOrCreateProject(Environment.CurrentDirectory);
            thread = ResolveTargetThreadForNewPane(project, thread, WorkspacePaneKind.Terminal);

            TerminalPaneRecord pane = CreateTerminalPane(project, thread, WorkspacePaneKind.Terminal, startupInput: null, initialTitle: FormatProjectPath(project));
            thread.Panes.Add(pane);
            thread.SelectedPaneId = pane.Id;
            project.SelectedThreadId = thread.Id;
            PromoteLayoutForPaneCount(thread);

            if (thread == _activeThread)
            {
                RefreshTabView();
                RequestLayoutForVisiblePanes();
            }
            else if (thread.Panes.Count == 1)
            {
                ActivateThread(thread);
            }

            RefreshProjectTree();
            LogAutomationEvent("shell", "pane.created", $"Created terminal pane {pane.Id}", new Dictionary<string, string>
            {
                ["paneId"] = pane.Id,
                ["paneKind"] = pane.Kind.ToString().ToLowerInvariant(),
                ["threadId"] = thread.Id,
                ["projectId"] = project.Id,
                ["shellCommand"] = pane.Terminal.ShellCommand ?? string.Empty,
            });
            return pane;
        }

        private BrowserPaneRecord AddBrowserPane(WorkspaceProject project, WorkspaceThread thread, string initialUri = null)
        {
            project ??= _activeProject ?? GetOrCreateProject(Environment.CurrentDirectory);
            thread = ResolveTargetThreadForNewPane(project, thread, WorkspacePaneKind.Browser);

            BrowserPaneControl browser = new()
            {
                ProjectId = project.Id,
                ProjectName = project.Name,
                ProjectPath = FormatProjectPath(project),
                ProjectRootPath = project.RootPath,
                InitialUri = initialUri,
            };
            browser.ApplyTheme(ResolveTheme(SampleConfig.CurrentTheme));

            BrowserPaneRecord pane = new("Preview", browser);
            browser.TitleChanged += (_, title) =>
            {
                pane.Title = string.IsNullOrWhiteSpace(title) ? "Preview" : title;
                LogAutomationEvent("browser", "title.changed", $"Browser title changed to {pane.Title}", new Dictionary<string, string>
                {
                    ["paneId"] = pane.Id,
                    ["threadId"] = thread.Id,
                    ["projectId"] = project.Id,
                    ["title"] = pane.Title,
                });
                if (thread == _activeThread)
                {
                    UpdateTabViewItem(pane);
                }
            };

            thread.Panes.Add(pane);
            thread.SelectedPaneId = pane.Id;
            project.SelectedThreadId = thread.Id;
            PromoteLayoutForPaneCount(thread);

            if (thread == _activeThread)
            {
                RefreshTabView();
            }
            else if (thread.Panes.Count == 1)
            {
                ActivateThread(thread);
            }

            RefreshProjectTree();
            LogAutomationEvent("shell", "pane.created", $"Created browser pane {pane.Id}", new Dictionary<string, string>
            {
                ["paneId"] = pane.Id,
                ["paneKind"] = pane.Kind.ToString().ToLowerInvariant(),
                ["threadId"] = thread.Id,
                ["projectId"] = project.Id,
                ["initialUri"] = initialUri ?? string.Empty,
            });
            return pane;
        }

        private TerminalPaneRecord AddEditorPane(WorkspaceProject project, WorkspaceThread thread)
        {
            project ??= _activeProject ?? GetOrCreateProject(Environment.CurrentDirectory);
            thread = ResolveTargetThreadForNewPane(project, thread, WorkspacePaneKind.Editor);

            string startupInput = "nvim .\r";
            TerminalPaneRecord pane = CreateTerminalPane(project, thread, WorkspacePaneKind.Editor, startupInput, "Editor");
            thread.Panes.Add(pane);
            thread.SelectedPaneId = pane.Id;
            project.SelectedThreadId = thread.Id;
            PromoteLayoutForPaneCount(thread);

            if (thread == _activeThread)
            {
                RefreshTabView();
                RequestLayoutForVisiblePanes();
            }
            else if (thread.Panes.Count == 1)
            {
                ActivateThread(thread);
            }

            RefreshProjectTree();
            LogAutomationEvent("shell", "pane.created", $"Created editor pane {pane.Id}", new Dictionary<string, string>
            {
                ["paneId"] = pane.Id,
                ["paneKind"] = pane.Kind.ToString().ToLowerInvariant(),
                ["threadId"] = thread.Id,
                ["projectId"] = project.Id,
                ["startupInput"] = startupInput.Trim(),
            });
            return pane;
        }

        private TerminalPaneRecord CreateTerminalPane(WorkspaceProject project, WorkspaceThread thread, WorkspacePaneKind kind, string startupInput, string initialTitle)
        {
            TerminalControl terminal = new()
            {
                DisplayWorkingDirectory = FormatProjectPath(project),
                InitialWorkingDirectory = FormatProjectPath(project),
                ProcessWorkingDirectory = ShellProfiles.ResolveProcessWorkingDirectory(project.RootPath),
                ShellCommand = ShellProfiles.BuildLaunchCommand(project.ShellProfileId, project.RootPath),
                StartupInput = startupInput,
            };

            terminal.ApplyTheme(ResolveTheme(SampleConfig.CurrentTheme));

            TerminalPaneRecord pane = new(initialTitle, terminal, kind);
            terminal.SessionTitleChanged += (_, title) =>
            {
                pane.Title = string.IsNullOrWhiteSpace(title) ? initialTitle : title;
                LogAutomationEvent("terminal", "title.changed", $"Terminal title changed to {pane.Title}", new Dictionary<string, string>
                {
                    ["paneId"] = pane.Id,
                    ["threadId"] = thread.Id,
                    ["projectId"] = project.Id,
                    ["title"] = pane.Title ?? string.Empty,
                });
                if (thread == _activeThread)
                {
                    UpdateTabViewItem(pane);
                }
            };
            terminal.SessionExited += (_, _) =>
            {
                pane.MarkExited();
                LogAutomationEvent("terminal", "session.exited", $"Terminal exited for pane {pane.Id}", new Dictionary<string, string>
                {
                    ["paneId"] = pane.Id,
                    ["threadId"] = thread.Id,
                    ["projectId"] = project.Id,
                    ["paneKind"] = pane.Kind.ToString().ToLowerInvariant(),
                });
                if (thread == _activeThread)
                {
                    UpdateTabViewItem(pane);
                }

                RefreshProjectTree();
            };

            return pane;
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
                    WorkspacePaneRecord pane = thread.Panes.FirstOrDefault(candidate => candidate.Id == tabId);
                    if (pane is null)
                    {
                        continue;
                    }

                    RemoveTabViewItem(pane.Id);
                    pane.DisposePane();
                    thread.Panes.Remove(pane);

                    if (thread.Panes.Count == 0)
                    {
                        AddTerminalTab(project, thread);
                    }

                    thread.SelectedPaneId = thread.Panes.FirstOrDefault()?.Id;

                    if (thread == _activeThread)
                    {
                        RefreshTabView();
                        FocusSelectedPane();
                        RequestLayoutForVisiblePanes();
                    }

                    RefreshProjectTree();
                    UpdateHeader();
                    LogAutomationEvent("shell", "pane.closed", $"Closed pane {pane.Id}", new Dictionary<string, string>
                    {
                        ["paneId"] = pane.Id,
                        ["paneKind"] = pane.Kind.ToString().ToLowerInvariant(),
                        ["threadId"] = thread.Id,
                        ["projectId"] = project.Id,
                    });
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
                    WorkspacePaneRecord pane = thread.Panes.FirstOrDefault(candidate => candidate.Id == tabId);
                    if (pane is null)
                    {
                        continue;
                    }

                    ActivateThread(thread);
                    thread.SelectedPaneId = pane.Id;
                    project.SelectedThreadId = thread.Id;
                    RefreshTabView();
                    FocusSelectedPane();
                    RequestLayoutForVisiblePanes();
                    return;
                }
            }

            throw new InvalidOperationException($"Unknown tab '{tabId}'.");
        }

        private void MoveTabAfter(string tabId, string targetTabId)
        {
            if (string.IsNullOrWhiteSpace(tabId) || string.IsNullOrWhiteSpace(targetTabId) || string.Equals(tabId, targetTabId, StringComparison.Ordinal))
            {
                return;
            }

            foreach (WorkspaceProject project in _projects)
            {
                foreach (WorkspaceThread thread in project.Threads)
                {
                    int sourceIndex = thread.Panes.FindIndex(candidate => candidate.Id == tabId);
                    int targetIndex = thread.Panes.FindIndex(candidate => candidate.Id == targetTabId);
                    if (sourceIndex < 0 || targetIndex < 0)
                    {
                        continue;
                    }

                    WorkspacePaneRecord source = thread.Panes[sourceIndex];
                    thread.Panes.RemoveAt(sourceIndex);
                    if (sourceIndex < targetIndex)
                    {
                        targetIndex--;
                    }

                    thread.Panes.Insert(targetIndex + 1, source);

                    if (thread == _activeThread)
                    {
                        RefreshTabView();
                    }

                    LogAutomationEvent("shell", "pane.moved", $"Moved pane {tabId} after {targetTabId}", new Dictionary<string, string>
                    {
                        ["paneId"] = tabId,
                        ["targetPaneId"] = targetTabId,
                        ["threadId"] = thread.Id,
                        ["projectId"] = project.Id,
                    });
                    return;
                }
            }

            throw new InvalidOperationException($"Could not move tab '{tabId}' after '{targetTabId}'.");
        }

        private static void PromoteLayoutForPaneCount(WorkspaceThread thread)
        {
            if (thread is null)
            {
                return;
            }

            int targetCount = Math.Min(thread.Panes.Count, thread.PaneLimit);
            thread.LayoutPreset = targetCount switch
            {
                <= 1 => WorkspaceLayoutPreset.Solo,
                2 => WorkspaceLayoutPreset.Dual,
                3 => WorkspaceLayoutPreset.Triple,
                _ => WorkspaceLayoutPreset.Quad,
            };
        }

        private void SetThreadLayout(string threadId, string value)
        {
            WorkspaceThread thread = string.IsNullOrWhiteSpace(threadId) ? _activeThread : FindThread(threadId);
            if (thread is null)
            {
                return;
            }

            thread.LayoutPreset = ParseLayoutPreset(value);
            if (ReferenceEquals(thread, _activeThread))
            {
                RefreshTabView();
                RequestLayoutForVisiblePanes();
            }

            RefreshProjectTree();
            LogAutomationEvent("shell", "thread.layout_changed", $"Thread layout changed to {thread.LayoutPreset}", new Dictionary<string, string>
            {
                ["threadId"] = thread.Id,
                ["projectId"] = thread.Project.Id,
                ["layout"] = thread.LayoutPreset.ToString().ToLowerInvariant(),
            });
        }

        private void NavigateSelectedBrowser(string value)
        {
            if (TerminalTabs.SelectedItem is not TabViewItem item || item.Tag is not BrowserPaneRecord pane)
            {
                return;
            }

            pane.Browser.Navigate(value);
            LogAutomationEvent("browser", "navigate.requested", $"Browser navigate requested for pane {pane.Id}", new Dictionary<string, string>
            {
                ["paneId"] = pane.Id,
                ["threadId"] = _activeThread?.Id ?? string.Empty,
                ["projectId"] = _activeProject?.Id ?? string.Empty,
                ["value"] = value ?? string.Empty,
            });
        }

        private static WorkspaceLayoutPreset ParseLayoutPreset(string value)
        {
            return value?.Trim().ToLowerInvariant() switch
            {
                "1" or "solo" or "single" => WorkspaceLayoutPreset.Solo,
                "2" or "dual" or "split" => WorkspaceLayoutPreset.Dual,
                "3" or "triple" => WorkspaceLayoutPreset.Triple,
                "4" or "quad" or "grid" => WorkspaceLayoutPreset.Quad,
                _ => WorkspaceLayoutPreset.Quad,
            };
        }

        private void ActivateProject(WorkspaceProject project)
        {
            _activeProject = project ?? throw new ArgumentNullException(nameof(project));

            WorkspaceThread thread = project.Threads.FirstOrDefault(candidate => candidate.Id == project.SelectedThreadId)
                ?? project.Threads.FirstOrDefault();

            if (thread is null)
            {
                _activeThread = null;
                RefreshProjectTree();
                RefreshTabView();
                UpdateWorkspaceVisibility();
                UpdateHeader();
                LogAutomationEvent("shell", "project.selected", $"Selected project {project.Name} with no active thread", new Dictionary<string, string>
                {
                    ["projectId"] = project.Id,
                    ["projectPath"] = project.RootPath,
                });
                return;
            }

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
            UpdateWorkspaceVisibility();
            UpdateHeader();
            RequestLayoutForVisiblePanes();
            LogAutomationEvent("shell", "thread.selected", $"Selected thread {thread.Name}", new Dictionary<string, string>
            {
                ["threadId"] = thread.Id,
                ["projectId"] = _activeProject.Id,
                ["threadName"] = thread.Name,
            });
        }

        private async System.Threading.Tasks.Task BeginRenameThreadAsync(string threadId)
        {
            WorkspaceThread thread = FindThread(threadId);
            LogAutomationEvent("shell", "thread.rename_requested", $"Opening rename dialog for {thread.Name}", new Dictionary<string, string>
            {
                ["threadId"] = thread.Id,
                ["projectId"] = thread.Project.Id,
            });

            string nextName = await PromptForThreadNameAsync("Rename thread", thread.Name);
            if (!string.IsNullOrWhiteSpace(nextName))
            {
                RenameThread(threadId, nextName);
            }
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
            LogAutomationEvent("shell", "thread.renamed", $"Renamed thread to {thread.Name}", new Dictionary<string, string>
            {
                ["threadId"] = thread.Id,
                ["projectId"] = thread.Project.Id,
                ["threadName"] = thread.Name,
            });
        }

        private void DuplicateThread(string threadId)
        {
            WorkspaceThread source = FindThread(threadId);
            WorkspaceProject project = FindProjectForThread(source);
            WorkspaceThread duplicate = CreateThread(project, $"Copy of {source.Name}");

            duplicate.LayoutPreset = source.LayoutPreset;
            ClearThreadPanes(project, duplicate);

            foreach (WorkspacePaneRecord pane in source.Panes)
            {
                switch (pane.Kind)
                {
                    case WorkspacePaneKind.Browser:
                        string currentUri = (pane as BrowserPaneRecord)?.Browser.CurrentUri;
                        AddBrowserPane(project, duplicate, currentUri);
                        break;
                    case WorkspacePaneKind.Editor:
                        AddEditorPane(project, duplicate);
                        break;
                    default:
                        AddTerminalTab(project, duplicate);
                        break;
                }
            }

            ActivateThread(duplicate);
            ShowTerminalShell();
            LogAutomationEvent("shell", "thread.duplicated", $"Duplicated thread {source.Name}", new Dictionary<string, string>
            {
                ["sourceThreadId"] = source.Id,
                ["threadId"] = duplicate.Id,
                ["projectId"] = project.Id,
            });
        }

        private void DeleteThread(string threadId)
        {
            WorkspaceThread thread = FindThread(threadId);
            WorkspaceProject project = FindProjectForThread(thread);
            bool wasActive = ReferenceEquals(thread, _activeThread);

            ClearThreadPanes(project, thread);

            project.Threads.Remove(thread);
            if (string.Equals(project.SelectedThreadId, thread.Id, StringComparison.Ordinal))
            {
                project.SelectedThreadId = project.Threads.FirstOrDefault()?.Id;
            }

            if (wasActive)
            {
                WorkspaceThread nextThread = project.Threads.FirstOrDefault(candidate => candidate.Id == project.SelectedThreadId)
                    ?? project.Threads.FirstOrDefault();

                if (nextThread is not null)
                {
                    ActivateThread(nextThread);
                }
                else
                {
                    _activeProject = project;
                    _activeThread = null;
                    RefreshProjectTree();
                    RefreshTabView();
                    UpdateWorkspaceVisibility();
                    UpdateHeader();
                }
            }
            else
            {
                RefreshProjectTree();
                UpdateWorkspaceVisibility();
                UpdateHeader();
            }

            ShowTerminalShell();
            LogAutomationEvent("shell", "thread.deleted", $"Deleted thread {thread.Name}", new Dictionary<string, string>
            {
                ["threadId"] = thread.Id,
                ["projectId"] = project.Id,
                ["remainingThreadCount"] = project.Threads.Count.ToString(),
            });
        }

        private void SetPaneSplit(string threadId, string value)
        {
            WorkspaceThread thread = string.IsNullOrWhiteSpace(threadId) ? _activeThread : FindThread(threadId);
            if (thread is null || string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            bool updated = false;
            foreach (string token in value.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string[] parts = token.Split(new[] { '=', ':' }, 2, StringSplitOptions.TrimEntries);
                if (parts.Length == 1 && double.TryParse(parts[0], out double primaryOnly))
                {
                    thread.PrimarySplitRatio = ClampPaneSplitRatio(primaryOnly);
                    updated = true;
                    continue;
                }

                if (parts.Length != 2 || !double.TryParse(parts[1], out double ratio))
                {
                    continue;
                }

                switch (parts[0].Trim().ToLowerInvariant())
                {
                    case "primary":
                    case "x":
                    case "vertical":
                        thread.PrimarySplitRatio = ClampPaneSplitRatio(ratio);
                        updated = true;
                        break;
                    case "secondary":
                    case "y":
                    case "horizontal":
                        thread.SecondarySplitRatio = ClampPaneSplitRatio(ratio);
                        updated = true;
                        break;
                }
            }

            if (!updated)
            {
                return;
            }

            if (ReferenceEquals(thread, _activeThread))
            {
                RenderPaneWorkspace();
                RequestLayoutForVisiblePanes();
            }

            RefreshProjectTree();
            LogAutomationEvent("render", "pane.split_set", "Applied pane split ratios", new Dictionary<string, string>
            {
                ["threadId"] = thread.Id,
                ["projectId"] = FindProjectForThread(thread)?.Id ?? string.Empty,
                ["primarySplitRatio"] = thread.PrimarySplitRatio.ToString("0.000"),
                ["secondarySplitRatio"] = thread.SecondarySplitRatio.ToString("0.000"),
            });
        }

        private void ClearThreadPanes(WorkspaceProject project, WorkspaceThread thread)
        {
            foreach (WorkspacePaneRecord pane in thread.Panes.ToList())
            {
                RemoveTabViewItem(pane.Id);
                pane.DisposePane();
            }

            thread.Panes.Clear();
            thread.SelectedPaneId = null;

            if (ReferenceEquals(thread, _activeThread))
            {
                RefreshTabView();
            }

            LogAutomationEvent("shell", "thread.panes_cleared", $"Cleared panes for thread {thread.Name}", new Dictionary<string, string>
            {
                ["threadId"] = thread.Id,
                ["projectId"] = project?.Id ?? string.Empty,
            });
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
                    threadButton.DoubleTapped += OnThreadButtonDoubleTapped;

                    MenuFlyout threadMenu = new();
                    MenuFlyoutItem renameItem = new()
                    {
                        Text = "Rename",
                        Tag = thread.Id,
                    };
                    AutomationProperties.SetAutomationId(renameItem, $"shell-thread-rename-{thread.Id}");
                    renameItem.Click += OnRenameThreadMenuClicked;
                    MenuFlyoutItem duplicateItem = new()
                    {
                        Text = "Duplicate",
                        Tag = thread.Id,
                    };
                    AutomationProperties.SetAutomationId(duplicateItem, $"shell-thread-duplicate-{thread.Id}");
                    duplicateItem.Click += OnDuplicateThreadMenuClicked;
                    MenuFlyoutItem deleteItem = new()
                    {
                        Text = "Delete",
                        Tag = thread.Id,
                    };
                    AutomationProperties.SetAutomationId(deleteItem, $"shell-thread-delete-{thread.Id}");
                    deleteItem.Click += OnDeleteThreadMenuClicked;
                    threadMenu.Items.Add(renameItem);
                    threadMenu.Items.Add(duplicateItem);
                    threadMenu.Items.Add(deleteItem);
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

                if (project.Threads.Count == 0)
                {
                    threadStack.Children.Add(new TextBlock
                    {
                        Text = "No threads yet",
                        Style = (Style)Application.Current.Resources["ShellHintTextStyle"],
                    });
                }

                group.Children.Add(threadStack);
                ProjectListPanel.Children.Add(group);
            }
        }

        private void RefreshTabView()
        {
            if (_activeThread is null)
            {
                if (TerminalTabs.TabItems.Count > 0)
                {
                    TerminalTabs.TabItems.Clear();
                }

                UpdateWorkspaceVisibility();
                return;
            }

            List<TabViewItem> desiredItems = _activeThread.Panes
                .Select(GetOrCreateTabViewItem)
                .ToList();

            foreach (TabViewItem existingItem in TerminalTabs.TabItems.OfType<TabViewItem>().ToList())
            {
                if (!desiredItems.Contains(existingItem))
                {
                    TerminalTabs.TabItems.Remove(existingItem);
                }
            }

            for (int index = 0; index < desiredItems.Count; index++)
            {
                TabViewItem desiredItem = desiredItems[index];
                int existingIndex = FindTabViewIndex(desiredItem);
                if (existingIndex == index)
                {
                    continue;
                }

                if (existingIndex >= 0)
                {
                    TerminalTabs.TabItems.RemoveAt(existingIndex);
                }

                TerminalTabs.TabItems.Insert(index, desiredItem);
            }

            string selectedTabId = _activeThread.SelectedPaneId ?? _activeThread.Panes.FirstOrDefault()?.Id;
            TabViewItem selectedItem = desiredItems
                .FirstOrDefault(item => (item.Tag as WorkspacePaneRecord)?.Id == selectedTabId)
                ?? desiredItems.FirstOrDefault();

            if (!ReferenceEquals(TerminalTabs.SelectedItem, selectedItem))
            {
                _suppressTabSelectionChanged = true;
                try
                {
                    TerminalTabs.SelectedItem = selectedItem;
                }
                finally
                {
                    _suppressTabSelectionChanged = false;
                }
            }

            if (selectedItem?.Tag is WorkspacePaneRecord selectedPane)
            {
                _activeThread.SelectedPaneId = selectedPane.Id;
            }

            RenderPaneWorkspace();
            UpdateWorkspaceVisibility();
            LogAutomationEvent("render", "pane-strip.refreshed", $"Pane strip refreshed for thread {_activeThread.Name}", new Dictionary<string, string>
            {
                ["threadId"] = _activeThread.Id,
                ["projectId"] = _activeProject?.Id ?? string.Empty,
                ["paneCount"] = _activeThread.Panes.Count.ToString(),
            });
        }

        private TabViewItem GetOrCreateTabViewItem(WorkspacePaneRecord pane)
        {
            if (!_tabItemsById.TryGetValue(pane.Id, out TabViewItem item))
            {
                item = new TabViewItem
                {
                    Content = null,
                    IsClosable = true,
                    Tag = pane,
                };
                AutomationProperties.SetAutomationId(item, $"shell-tab-{pane.Id}");
                _tabItemsById[pane.Id] = item;
            }

            item.Tag = pane;
            object nextHeader = FormatTabHeader(pane.Title, pane.Kind, pane.IsExited);
            if (!Equals(item.Header, nextHeader))
            {
                item.Header = nextHeader;
            }
            item.IsClosable = true;
            return item;
        }

        private void UpdateTabViewItem(WorkspacePaneRecord pane)
        {
            if (pane is null)
            {
                return;
            }

            TabViewItem item = GetOrCreateTabViewItem(pane);
            object nextHeader = FormatTabHeader(pane.Title, pane.Kind, pane.IsExited);
            if (!Equals(item.Header, nextHeader))
            {
                item.Header = nextHeader;
            }
        }

        private void RemoveTabViewItem(string tabId)
        {
            if (string.IsNullOrWhiteSpace(tabId) || !_tabItemsById.TryGetValue(tabId, out TabViewItem item))
            {
                return;
            }

            TerminalTabs.TabItems.Remove(item);
            item.Content = null;
            _tabItemsById.Remove(tabId);
            RemovePaneContainer(tabId);
        }

        private int FindTabViewIndex(TabViewItem target)
        {
            for (int index = 0; index < TerminalTabs.TabItems.Count; index++)
            {
                if (ReferenceEquals(TerminalTabs.TabItems[index], target))
                {
                    return index;
                }
            }

            return -1;
        }

        private void RemovePaneContainer(string paneId)
        {
            if (string.IsNullOrWhiteSpace(paneId) || !_paneContainersById.TryGetValue(paneId, out Border container))
            {
                return;
            }

            PaneWorkspaceGrid.Children.Remove(container);
            container.Child = null;
            _paneContainersById.Remove(paneId);
        }

        private void RenderPaneWorkspace()
        {
            PaneWorkspaceGrid.Children.Clear();
            PaneWorkspaceGrid.RowDefinitions.Clear();
            PaneWorkspaceGrid.ColumnDefinitions.Clear();

            if (_activeThread is null || _showingSettings)
            {
                foreach (Border container in _paneContainersById.Values)
                {
                    container.Visibility = Visibility.Collapsed;
                }

                return;
            }

            List<WorkspacePaneRecord> visiblePanes = GetVisiblePanes(_activeThread).ToList();
            if (visiblePanes.Count == 0)
            {
                foreach (Border container in _paneContainersById.Values)
                {
                    container.Visibility = Visibility.Collapsed;
                }

                return;
            }

            foreach (Border container in _paneContainersById.Values)
            {
                container.Visibility = Visibility.Collapsed;
            }

            switch (visiblePanes.Count)
            {
                case 1:
                    PaneWorkspaceGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    PaneWorkspaceGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    AddPaneCell(visiblePanes[0], 0, 0);
                    break;
                case 2:
                    PaneWorkspaceGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    ConfigureSplitColumns(_activeThread.PrimarySplitRatio);
                    AddPaneCell(visiblePanes[0], 0, 0);
                    AddVerticalSplitter(0, 1);
                    AddPaneCell(visiblePanes[1], 0, 2);
                    break;
                case 3:
                    ConfigureSplitRows(_activeThread.SecondarySplitRatio);
                    ConfigureSplitColumns(_activeThread.PrimarySplitRatio);
                    AddPaneCell(visiblePanes[0], 0, 0, rowSpan: 3);
                    AddVerticalSplitter(0, 1, rowSpan: 3);
                    AddPaneCell(visiblePanes[1], 0, 2);
                    AddHorizontalSplitter(1, 2);
                    AddPaneCell(visiblePanes[2], 2, 2);
                    break;
                default:
                    ConfigureSplitRows(_activeThread.SecondarySplitRatio);
                    ConfigureSplitColumns(_activeThread.PrimarySplitRatio);
                    AddPaneCell(visiblePanes[0], 0, 0);
                    AddVerticalSplitter(0, 1, rowSpan: 3);
                    AddPaneCell(visiblePanes[1], 0, 2);
                    AddHorizontalSplitter(1, 0, columnSpan: 3);
                    AddPaneCell(visiblePanes[2], 2, 0);
                    AddPaneCell(visiblePanes[3], 2, 2);
                    break;
            }

            UpdatePaneSelectionChrome();
        }

        private void AddPaneCell(WorkspacePaneRecord pane, int row, int column, int rowSpan = 1, int columnSpan = 1)
        {
            if (!_paneContainersById.TryGetValue(pane.Id, out Border border))
            {
                border = new Border
                {
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Background = (Brush)Application.Current.Resources["ShellSurfaceBackgroundBrush"],
                    Child = pane.View,
                    Margin = new Thickness(1),
                    Tag = pane,
                };
                AutomationProperties.SetAutomationId(border, $"shell-pane-{pane.Id}");
                border.PointerPressed += OnPaneContainerPointerPressed;
                _paneContainersById[pane.Id] = border;
            }

            border.Visibility = Visibility.Visible;
            Grid.SetRow(border, row);
            Grid.SetColumn(border, column);
            Grid.SetRowSpan(border, rowSpan);
            Grid.SetColumnSpan(border, columnSpan);
            PaneWorkspaceGrid.Children.Add(border);

            if (pane is BrowserPaneRecord browserPane)
            {
                _ = browserPane.Browser.EnsureInitializedAsync();
            }
        }

        private void ConfigureSplitColumns(double primaryRatio)
        {
            double clampedRatio = ClampPaneSplitRatio(primaryRatio);
            PaneWorkspaceGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(clampedRatio, GridUnitType.Star) });
            PaneWorkspaceGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(PaneDividerThickness) });
            PaneWorkspaceGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - clampedRatio, GridUnitType.Star) });
        }

        private void ConfigureSplitRows(double primaryRatio)
        {
            double clampedRatio = ClampPaneSplitRatio(primaryRatio);
            PaneWorkspaceGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(clampedRatio, GridUnitType.Star) });
            PaneWorkspaceGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(PaneDividerThickness) });
            PaneWorkspaceGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1 - clampedRatio, GridUnitType.Star) });
        }

        private void AddVerticalSplitter(int row, int column, int rowSpan = 1)
        {
            Border splitter = new()
            {
                Width = PaneDividerThickness,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(1, 0, 0, 0)),
                Tag = "vertical",
            };
            AutomationProperties.SetAutomationId(splitter, $"shell-pane-splitter-vertical-{row}-{column}");
            splitter.PointerPressed += OnPaneSplitterPointerPressed;
            splitter.PointerMoved += OnPaneSplitterPointerMoved;
            splitter.PointerReleased += OnPaneSplitterPointerReleased;
            splitter.PointerCanceled += OnPaneSplitterPointerCanceled;
            Grid.SetRow(splitter, row);
            Grid.SetColumn(splitter, column);
            Grid.SetRowSpan(splitter, rowSpan);
            PaneWorkspaceGrid.Children.Add(splitter);
        }

        private void AddHorizontalSplitter(int row, int column, int columnSpan = 1)
        {
            Border splitter = new()
            {
                Height = PaneDividerThickness,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(1, 0, 0, 0)),
                Tag = "horizontal",
            };
            AutomationProperties.SetAutomationId(splitter, $"shell-pane-splitter-horizontal-{row}-{column}");
            splitter.PointerPressed += OnPaneSplitterPointerPressed;
            splitter.PointerMoved += OnPaneSplitterPointerMoved;
            splitter.PointerReleased += OnPaneSplitterPointerReleased;
            splitter.PointerCanceled += OnPaneSplitterPointerCanceled;
            Grid.SetRow(splitter, row);
            Grid.SetColumn(splitter, column);
            Grid.SetColumnSpan(splitter, columnSpan);
            PaneWorkspaceGrid.Children.Add(splitter);
        }

        private void OnPaneSplitterPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_activeThread is null || sender is not Border splitter || splitter.Tag is not string direction)
            {
                return;
            }

            _activeSplitter = splitter;
            _activeSplitterDirection = direction;
            _activeSplitterPointerId = e.Pointer.PointerId;
            Point point = e.GetCurrentPoint(PaneWorkspaceGrid).Position;
            _splitterDragOriginX = point.X;
            _splitterDragOriginY = point.Y;
            _splitterStartPrimaryRatio = _activeThread.PrimarySplitRatio;
            _splitterStartSecondaryRatio = _activeThread.SecondarySplitRatio;
            splitter.CapturePointer(e.Pointer);
            e.Handled = true;
        }

        private void OnPaneSplitterPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_activeThread is null || sender is not Border splitter || !ReferenceEquals(splitter, _activeSplitter) || _activeSplitterPointerId != e.Pointer.PointerId)
            {
                return;
            }

            Point point = e.GetCurrentPoint(PaneWorkspaceGrid).Position;

            if (string.Equals(_activeSplitterDirection, "vertical", StringComparison.Ordinal) && PaneWorkspaceGrid.ColumnDefinitions.Count >= 3)
            {
                double leftWidth = PaneWorkspaceGrid.ColumnDefinitions[0].ActualWidth;
                double rightWidth = PaneWorkspaceGrid.ColumnDefinitions[2].ActualWidth;
                double totalWidth = leftWidth + rightWidth;
                if (totalWidth <= 0)
                {
                    return;
                }

                double nextLeftWidth = Math.Clamp((totalWidth * _splitterStartPrimaryRatio) + (point.X - _splitterDragOriginX), totalWidth * MinPaneSplitRatio, totalWidth * MaxPaneSplitRatio);
                _activeThread.PrimarySplitRatio = ClampPaneSplitRatio(nextLeftWidth / totalWidth);
                PaneWorkspaceGrid.ColumnDefinitions[0].Width = new GridLength(_activeThread.PrimarySplitRatio, GridUnitType.Star);
                PaneWorkspaceGrid.ColumnDefinitions[2].Width = new GridLength(1 - _activeThread.PrimarySplitRatio, GridUnitType.Star);
                e.Handled = true;
                return;
            }

            if (string.Equals(_activeSplitterDirection, "horizontal", StringComparison.Ordinal) && PaneWorkspaceGrid.RowDefinitions.Count >= 3)
            {
                double topHeight = PaneWorkspaceGrid.RowDefinitions[0].ActualHeight;
                double bottomHeight = PaneWorkspaceGrid.RowDefinitions[2].ActualHeight;
                double totalHeight = topHeight + bottomHeight;
                if (totalHeight <= 0)
                {
                    return;
                }

                double nextTopHeight = Math.Clamp((totalHeight * _splitterStartSecondaryRatio) + (point.Y - _splitterDragOriginY), totalHeight * MinPaneSplitRatio, totalHeight * MaxPaneSplitRatio);
                _activeThread.SecondarySplitRatio = ClampPaneSplitRatio(nextTopHeight / totalHeight);
                PaneWorkspaceGrid.RowDefinitions[0].Height = new GridLength(_activeThread.SecondarySplitRatio, GridUnitType.Star);
                PaneWorkspaceGrid.RowDefinitions[2].Height = new GridLength(1 - _activeThread.SecondarySplitRatio, GridUnitType.Star);
                e.Handled = true;
            }
        }

        private void OnPaneSplitterPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border splitter && ReferenceEquals(splitter, _activeSplitter) && _activeSplitterPointerId == e.Pointer.PointerId)
            {
                splitter.ReleasePointerCapture(e.Pointer);
                ClearActiveSplitterTracking();
                e.Handled = true;
            }

            PersistActiveThreadSplitRatios();
        }

        private void OnPaneSplitterPointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border splitter && ReferenceEquals(splitter, _activeSplitter) && _activeSplitterPointerId == e.Pointer.PointerId)
            {
                splitter.ReleasePointerCaptures();
                ClearActiveSplitterTracking();
                e.Handled = true;
            }
        }

        private void ClearActiveSplitterTracking()
        {
            _activeSplitter = null;
            _activeSplitterDirection = null;
            _activeSplitterPointerId = null;
        }

        private void PersistActiveThreadSplitRatios()
        {
            if (_activeThread is null)
            {
                return;
            }

            if (PaneWorkspaceGrid.ColumnDefinitions.Count >= 3)
            {
                double leftWidth = PaneWorkspaceGrid.ColumnDefinitions[0].ActualWidth;
                double rightWidth = PaneWorkspaceGrid.ColumnDefinitions[2].ActualWidth;
                double totalWidth = leftWidth + rightWidth;
                if (totalWidth > 0)
                {
                    _activeThread.PrimarySplitRatio = ClampPaneSplitRatio(leftWidth / totalWidth);
                }
            }

            if (PaneWorkspaceGrid.RowDefinitions.Count >= 3)
            {
                double topHeight = PaneWorkspaceGrid.RowDefinitions[0].ActualHeight;
                double bottomHeight = PaneWorkspaceGrid.RowDefinitions[2].ActualHeight;
                double totalHeight = topHeight + bottomHeight;
                if (totalHeight > 0)
                {
                    _activeThread.SecondarySplitRatio = ClampPaneSplitRatio(topHeight / totalHeight);
                }
            }

            RefreshProjectTree();
            LogAutomationEvent("render", "pane.split_resized", "Updated pane split ratios", new Dictionary<string, string>
            {
                ["threadId"] = _activeThread.Id,
                ["projectId"] = _activeProject?.Id ?? string.Empty,
                ["primarySplitRatio"] = _activeThread.PrimarySplitRatio.ToString("0.000"),
                ["secondarySplitRatio"] = _activeThread.SecondarySplitRatio.ToString("0.000"),
            });
        }

        private void OnPaneContainerPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border border && border.Tag is WorkspacePaneRecord pane)
            {
                SelectPane(pane);
            }
        }

        private void SelectPane(WorkspacePaneRecord pane)
        {
            if (_activeThread is null || pane is null)
            {
                return;
            }

            _activeThread.SelectedPaneId = pane.Id;
            if (_tabItemsById.TryGetValue(pane.Id, out TabViewItem item) && !ReferenceEquals(TerminalTabs.SelectedItem, item))
            {
                _suppressTabSelectionChanged = true;
                TerminalTabs.SelectedItem = item;
                _suppressTabSelectionChanged = false;
            }

            UpdatePaneSelectionChrome();
            FocusSelectedPane();
            RequestLayoutForVisiblePanes();
            LogAutomationEvent("shell", "pane.selected", $"Selected pane {pane.Id}", new Dictionary<string, string>
            {
                ["paneId"] = pane.Id,
                ["paneKind"] = pane.Kind.ToString().ToLowerInvariant(),
                ["threadId"] = _activeThread.Id,
                ["projectId"] = _activeProject?.Id ?? string.Empty,
            });
        }

        private void UpdatePaneSelectionChrome()
        {
            WorkspacePaneRecord selectedPane = GetSelectedPane(_activeThread);
            foreach ((string paneId, Border border) in _paneContainersById)
            {
                bool isSelected = string.Equals(selectedPane?.Id, paneId, StringComparison.Ordinal);
                border.BorderBrush = (Brush)Application.Current.Resources[isSelected
                    ? "ShellPaneActiveBorderBrush"
                    : "ShellBorderBrush"];
                border.BorderThickness = isSelected ? new Thickness(1.5) : new Thickness(1);
            }
        }

        private static double ClampPaneSplitRatio(double ratio)
        {
            return Math.Clamp(ratio, MinPaneSplitRatio, MaxPaneSplitRatio);
        }

        private IEnumerable<WorkspacePaneRecord> GetVisiblePanes(WorkspaceThread thread)
        {
            if (thread is null || thread.Panes.Count == 0)
            {
                return Enumerable.Empty<WorkspacePaneRecord>();
            }

            int capacity = Math.Min(thread.VisiblePaneCapacity, thread.Panes.Count);
            if (capacity <= 0)
            {
                capacity = 1;
            }

            int selectedIndex = thread.Panes.FindIndex(candidate => candidate.Id == thread.SelectedPaneId);
            if (selectedIndex < 0)
            {
                selectedIndex = 0;
            }

            int start = Math.Max(0, selectedIndex - (capacity - 1));
            if (start + capacity > thread.Panes.Count)
            {
                start = Math.Max(0, thread.Panes.Count - capacity);
            }

            return thread.Panes.Skip(start).Take(capacity);
        }

        private static WorkspacePaneRecord GetSelectedPane(WorkspaceThread thread)
        {
            return thread?.Panes.FirstOrDefault(candidate => candidate.Id == thread.SelectedPaneId)
                ?? thread?.Panes.FirstOrDefault();
        }

        private void ShowSettings()
        {
            _showingSettings = true;
            SettingsFrame.Navigate(typeof(SettingsPage));
            UpdateWorkspaceVisibility();
            UpdateSidebarActions();
            UpdateHeader();
            LogAutomationEvent("shell", "view.settings", "Opened preferences");
        }

        private void ShowTerminalShell()
        {
            _showingSettings = false;
            UpdateWorkspaceVisibility();
            UpdateSidebarActions();
            FocusSelectedPane();
            RequestLayoutForVisiblePanes();
            UpdateHeader();
            LogAutomationEvent("shell", "view.terminal", _activeThread is null ? "Showing empty project state" : "Showing pane workspace", new Dictionary<string, string>
            {
                ["projectId"] = _activeProject?.Id ?? string.Empty,
                ["threadId"] = _activeThread?.Id ?? string.Empty,
            });
        }

        private void FocusSelectedPane()
        {
            if (TerminalTabs.SelectedItem is TabViewItem item && item.Tag is WorkspacePaneRecord pane)
            {
                pane.FocusPane();
            }
        }

        private void RequestLayoutForVisiblePanes()
        {
            foreach (WorkspacePaneRecord pane in GetVisiblePanes(_activeThread))
            {
                pane.RequestLayout();
                LogAutomationEvent("render", "pane.layout_requested", $"Requested layout for pane {pane.Id}", new Dictionary<string, string>
                {
                    ["paneId"] = pane.Id,
                    ["paneKind"] = pane.Kind.ToString().ToLowerInvariant(),
                    ["threadId"] = _activeThread?.Id ?? string.Empty,
                    ["projectId"] = _activeProject?.Id ?? string.Empty,
                });
            }
        }

        private void SendInputToSelectedTerminal(string text)
        {
            if (TerminalTabs.SelectedItem is TabViewItem item && item.Tag is TerminalPaneRecord pane)
            {
                pane.Terminal.SendInput(text);
                LogAutomationEvent("terminal", "input.sent", "Sent input to selected terminal", new Dictionary<string, string>
                {
                    ["paneId"] = pane.Id,
                    ["threadId"] = _activeThread?.Id ?? string.Empty,
                    ["projectId"] = _activeProject?.Id ?? string.Empty,
                    ["length"] = (text?.Length ?? 0).ToString(),
                });
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
            LogAutomationEvent("render", "pane.layout", isOpen ? "Sidebar expanded" : "Sidebar collapsed", new Dictionary<string, string>
            {
                ["paneOpen"] = isOpen.ToString(),
            });
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

                ThreadNameBox.IsReadOnly = _activeThread is null;
                ThreadNameBox.Text = _activeThread?.Name ?? "No thread selected";
                ActiveDirectoryText.Visibility = Visibility.Visible;
                ActiveDirectoryText.Text = _activeProject is null ? string.Empty : FormatProjectPath(_activeProject);
            }
            finally
            {
                _suppressThreadNameSync = false;
            }
        }

        private void UpdateWorkspaceVisibility()
        {
            if (_showingSettings)
            {
                SettingsFrame.Visibility = Visibility.Visible;
                PaneWorkspaceShell.Visibility = Visibility.Collapsed;
                EmptyThreadStatePanel.Visibility = Visibility.Collapsed;
                return;
            }

            SettingsFrame.Visibility = Visibility.Collapsed;

            bool showEmptyState = _activeProject is not null && _activeThread is null;
            PaneWorkspaceShell.Visibility = showEmptyState ? Visibility.Collapsed : Visibility.Visible;
            EmptyThreadStatePanel.Visibility = showEmptyState ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ApplyThemeToAllTerminals(ElementTheme resolvedTheme)
        {
            foreach (WorkspacePaneRecord pane in EnumeratePaneRecords())
            {
                pane.ApplyTheme(resolvedTheme);
            }
        }

        private IEnumerable<TerminalControl> EnumerateTerminals()
        {
            return EnumerateTerminalRecords().Select(record => record.Pane.Terminal);
        }

        private IEnumerable<WorkspacePaneRecord> EnumeratePaneRecords()
        {
            return _projects.SelectMany(project => project.Threads.SelectMany(thread => thread.Panes));
        }

        private IEnumerable<(WorkspaceProject Project, WorkspaceThread Thread, TerminalPaneRecord Pane)> EnumerateTerminalRecords()
        {
            return _projects.SelectMany(project => project.Threads.SelectMany(thread => thread.Panes.OfType<TerminalPaneRecord>().Select(pane => (project, thread, pane))));
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
                Margin = SerializeThickness(TryGetThickness(node, "Margin")),
                Padding = SerializeThickness(TryGetThickness(node, "Padding")),
                BorderThickness = SerializeThickness(TryGetThickness(node, "BorderThickness")),
                CornerRadius = SerializeCornerRadius(TryGetCornerRadius(node, "CornerRadius")),
                Background = SerializeBrush(TryGetBrush(node, "Background")),
                BorderBrush = SerializeBrush(TryGetBrush(node, "BorderBrush")),
                Foreground = SerializeBrush(TryGetBrush(node, "Foreground")),
                Opacity = element?.Opacity ?? 1,
                FontSize = TryGetDouble(node, "FontSize"),
                FontWeight = TryGetFontWeight(node, "FontWeight"),
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
            return node is FrameworkElement or TextBlock or FontIcon or TabViewItem or Thumb;
        }

        private static bool IsInteractiveUiNode(DependencyObject node, string automationId)
        {
            return node is Button
                or HyperlinkButton
                or ToggleButton
                or RadioButton
                or CheckBox
                or ComboBox
                or TextBox
                or TabView
                or TabViewItem
                or TerminalControl
                or Microsoft.UI.Xaml.Controls.WebView2
                or MenuFlyoutItem
                || (node is Border && automationId?.StartsWith("shell-pane-splitter-", StringComparison.Ordinal) == true);
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

        private static Thickness? TryGetThickness(object instance, string propertyName)
        {
            var property = instance?.GetType().GetProperty(propertyName);
            if (property is null)
            {
                return null;
            }

            object value = property.GetValue(instance);
            return value is Thickness thickness ? thickness : null;
        }

        private static CornerRadius? TryGetCornerRadius(object instance, string propertyName)
        {
            var property = instance?.GetType().GetProperty(propertyName);
            if (property is null)
            {
                return null;
            }

            object value = property.GetValue(instance);
            return value is CornerRadius cornerRadius ? cornerRadius : null;
        }

        private static Brush TryGetBrush(object instance, string propertyName)
        {
            var property = instance?.GetType().GetProperty(propertyName);
            return property?.GetValue(instance) as Brush;
        }

        private static double TryGetDouble(object instance, string propertyName)
        {
            var property = instance?.GetType().GetProperty(propertyName);
            object value = property?.GetValue(instance);
            return value is double number ? number : 0;
        }

        private static string TryGetFontWeight(object instance, string propertyName)
        {
            var property = instance?.GetType().GetProperty(propertyName);
            object value = property?.GetValue(instance);
            return value is Windows.UI.Text.FontWeight fontWeight ? fontWeight.Weight.ToString() : null;
        }

        private static string SerializeThickness(Thickness? thickness)
        {
            if (thickness is null)
            {
                return null;
            }

            Thickness value = thickness.Value;
            return $"{value.Left:0.##},{value.Top:0.##},{value.Right:0.##},{value.Bottom:0.##}";
        }

        private static string SerializeCornerRadius(CornerRadius? cornerRadius)
        {
            if (cornerRadius is null)
            {
                return null;
            }

            CornerRadius value = cornerRadius.Value;
            return $"{value.TopLeft:0.##},{value.TopRight:0.##},{value.BottomRight:0.##},{value.BottomLeft:0.##}";
        }

        private static string SerializeBrush(Brush brush)
        {
            return brush switch
            {
                null => null,
                SolidColorBrush solid => $"#{solid.Color.A:X2}{solid.Color.R:X2}{solid.Color.G:X2}{solid.Color.B:X2}",
                _ => brush.GetType().Name,
            };
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

        private void DoubleClickUiElement(DependencyObject node)
        {
            if (node is FrameworkElement element && TryInvokeKnownDoubleClickAction(element))
            {
                return;
            }

            InvokeUiElement(node);
            InvokeUiElement(node);
        }

        private bool TryInvokeKnownDoubleClickAction(FrameworkElement element)
        {
            string automationId = AutomationProperties.GetAutomationId(element);
            if (string.IsNullOrWhiteSpace(automationId))
            {
                return false;
            }

            if (automationId.StartsWith("shell-thread-", StringComparison.Ordinal))
            {
                _ = BeginRenameThreadAsync(automationId["shell-thread-".Length..]);
                return true;
            }

            return false;
        }

        private static void ApplyUiVisualState(DependencyObject node, string stateName)
        {
            if (node is Control control)
            {
                if (VisualStateManager.GoToState(control, stateName, false))
                {
                    return;
                }
            }

            if (node is TabViewItem tabItem && VisualStateManager.GoToState(tabItem, stateName, false))
            {
                return;
            }

            throw new InvalidOperationException($"Element '{node.GetType().Name}' does not expose visual state '{stateName}'.");
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
                LogAutomationEvent("automation", "context-menu.invoked", $"Invoked context menu item {menuItemText}", new Dictionary<string, string>
                {
                    ["menuItemText"] = menuItemText ?? string.Empty,
                    ["targetAutomationId"] = AutomationProperties.GetAutomationId(element) ?? string.Empty,
                });
                return;
            }

            if (item is MenuFlyoutItem directMenuItem && TryInvokeContextMenuItemDirect(directMenuItem))
            {
                LogAutomationEvent("automation", "context-menu.invoked", $"Invoked context menu item {menuItemText}", new Dictionary<string, string>
                {
                    ["menuItemText"] = menuItemText ?? string.Empty,
                    ["targetAutomationId"] = AutomationProperties.GetAutomationId(element) ?? string.Empty,
                    ["directDispatch"] = bool.TrueString,
                });
                return;
            }

            throw new InvalidOperationException($"Context menu item '{menuItemText}' could not be invoked.");
        }

        private bool TryInvokeContextMenuItemDirect(MenuFlyoutItem menuItem)
        {
            if (menuItem?.Tag is not string threadId)
            {
                return false;
            }

            string action = menuItem.Text?.Trim().ToLowerInvariant();
            switch (action)
            {
                case "rename":
                    _ = BeginRenameThreadAsync(threadId);
                    return true;
                case "duplicate":
                    DuplicateThread(threadId);
                    return true;
                case "delete":
                    DeleteThread(threadId);
                    return true;
                default:
                    return false;
            }
        }

        private static NativeAutomationThreadState BuildThreadState(WorkspaceThread thread)
        {
            return new NativeAutomationThreadState
            {
                Id = thread.Id,
                Name = thread.Name,
                SelectedTabId = thread.SelectedPaneId,
                TabCount = thread.Panes.Count,
                PaneCount = thread.Panes.Count,
                Layout = thread.LayoutPreset.ToString().ToLowerInvariant(),
                PaneLimit = thread.PaneLimit,
                VisiblePaneCapacity = thread.VisiblePaneCapacity,
                PrimarySplitRatio = thread.PrimarySplitRatio,
                SecondarySplitRatio = thread.SecondarySplitRatio,
                Tabs = thread.Panes.Select(tab => new NativeAutomationTabState
                {
                    Id = tab.Id,
                    Kind = tab.Kind.ToString().ToLowerInvariant(),
                    Title = FormatTabHeader(tab.Title, tab.Kind),
                    Exited = tab.IsExited,
                }).ToList(),
                Panes = thread.Panes.Select(tab => new NativeAutomationTabState
                {
                    Id = tab.Id,
                    Kind = tab.Kind.ToString().ToLowerInvariant(),
                    Title = FormatTabHeader(tab.Title, tab.Kind),
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

        private static string FormatTabHeader(string title, WorkspacePaneKind kind = WorkspacePaneKind.Terminal, bool exited = false)
        {
            string nextTitle;
            if (string.IsNullOrWhiteSpace(title))
            {
                nextTitle = kind switch
                {
                    WorkspacePaneKind.Browser => "preview",
                    WorkspacePaneKind.Editor => "editor",
                    _ => "shell",
                };
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

            string prefix = kind switch
            {
                WorkspacePaneKind.Browser => "Web",
                WorkspacePaneKind.Editor => "Edit",
                _ => string.Empty,
            };

            string formatted = string.IsNullOrWhiteSpace(prefix) ? nextTitle : $"{prefix} {nextTitle}";
            return exited ? $"{formatted} (ended)" : formatted;
        }

        private async System.Threading.Tasks.Task<ProjectDraft> PromptForProjectAsync()
        {
            TextBox pathBox = new()
            {
                Text = _activeProject?.RootPath ?? Environment.CurrentDirectory,
                Style = (Style)Application.Current.Resources["ShellInlineTextBoxStyle"],
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinWidth = 0,
            };
            AutomationProperties.SetAutomationId(pathBox, "dialog-project-path");

            Button browseButton = new()
            {
                Content = new TextBlock
                {
                    Text = "Browse",
                    FontSize = 11,
                    TextWrapping = TextWrapping.NoWrap,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                Height = 28,
                Width = 84,
                Padding = new Thickness(6, 0, 6, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            AutomationProperties.SetAutomationId(browseButton, "dialog-project-browse");
            AutomationProperties.SetName(browseButton, "Browse for folder");
            ToolTipService.SetToolTip(browseButton, "Browse for folder");

            ComboBox profileBox = new()
            {
                DisplayMemberPath = nameof(ShellProfileDefinition.Name),
                SelectedValuePath = nameof(ShellProfileDefinition.Id),
                ItemsSource = ShellProfiles.All,
                SelectedValue = _activeProject?.ShellProfileId ?? SampleConfig.DefaultShellProfileId,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            AutomationProperties.SetAutomationId(profileBox, "dialog-project-shell-profile");

            TextBlock previewValue = new()
            {
                Style = (Style)Application.Current.Resources["ShellBodyTextStyle"],
                TextWrapping = TextWrapping.WrapWholeWords,
            };
            AutomationProperties.SetAutomationId(previewValue, "dialog-project-preview");

            TextBlock helperText = new()
            {
                Style = (Style)Application.Current.Resources["ShellHintTextStyle"],
                TextWrapping = TextWrapping.WrapWholeWords,
            };
            AutomationProperties.SetAutomationId(helperText, "dialog-project-helper");

            TextBlock projectDirectoryLabel = new()
            {
                Text = "Project directory",
                Style = (Style)Application.Current.Resources["ShellHintTextStyle"],
            };
            Grid projectDirectoryRow = new()
            {
                ColumnSpacing = 10,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            projectDirectoryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            projectDirectoryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            projectDirectoryRow.Children.Add(pathBox);
            Grid.SetColumn(browseButton, 1);
            projectDirectoryRow.Children.Add(browseButton);

            TextBlock shellProfileLabel = new()
            {
                Text = "Shell profile",
                Style = (Style)Application.Current.Resources["ShellHintTextStyle"],
            };
            TextBlock terminalPathLabel = new()
            {
                Text = "Terminal path",
                Style = (Style)Application.Current.Resources["ShellHintTextStyle"],
            };

            Grid body = new()
            {
                Width = 480,
                RowSpacing = 14,
            };
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AutomationProperties.SetAutomationId(body, "dialog-project-body");

            body.Children.Add(projectDirectoryLabel);
            Grid.SetRow(projectDirectoryRow, 1);
            body.Children.Add(projectDirectoryRow);
            Grid.SetRow(shellProfileLabel, 2);
            body.Children.Add(shellProfileLabel);
            Grid.SetRow(profileBox, 3);
            body.Children.Add(profileBox);
            Grid.SetRow(terminalPathLabel, 4);
            body.Children.Add(terminalPathLabel);
            Grid.SetRow(previewValue, 5);
            body.Children.Add(previewValue);
            Grid.SetRow(helperText, 6);
            body.Children.Add(helperText);

            ContentDialog dialog = new()
            {
                XamlRoot = XamlRoot,
                Title = "New project",
                PrimaryButtonText = "Add project",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                Content = body,
                MinWidth = 540,
            };

            string selectedPath = null;
            string selectedProfileId = null;

            void RefreshDraftPreview()
            {
                ProjectDraftPreview preview = BuildProjectDraftPreview(pathBox.Text, profileBox.SelectedValue as string ?? SampleConfig.DefaultShellProfileId);
                previewValue.Text = preview.DisplayPath;
                helperText.Text = preview.HelperText;
                helperText.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[preview.CanSubmit
                    ? "ShellTextTertiaryBrush"
                    : "SystemFillColorCriticalBrush"];
                dialog.IsPrimaryButtonEnabled = preview.CanSubmit;
            }

            browseButton.Click += async (_, _) =>
            {
                string selectedFolder = await BrowseForFolderAsync(pathBox.Text);
                if (!string.IsNullOrWhiteSpace(selectedFolder))
                {
                    pathBox.Text = selectedFolder;
                }

                RefreshDraftPreview();
            };

            pathBox.TextChanged += (_, _) => RefreshDraftPreview();
            profileBox.SelectionChanged += (_, _) => RefreshDraftPreview();
            dialog.PrimaryButtonClick += (_, args) =>
            {
                ProjectDraftPreview preview = BuildProjectDraftPreview(pathBox.Text, profileBox.SelectedValue as string ?? SampleConfig.DefaultShellProfileId);
                previewValue.Text = preview.DisplayPath;
                helperText.Text = preview.HelperText;
                helperText.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[preview.CanSubmit
                    ? "ShellTextTertiaryBrush"
                    : "SystemFillColorCriticalBrush"];
                dialog.IsPrimaryButtonEnabled = preview.CanSubmit;

                if (!preview.CanSubmit)
                {
                    args.Cancel = true;
                    return;
                }

                selectedPath = preview.NormalizedPath;
                selectedProfileId = preview.ShellProfileId;

                if (!string.IsNullOrWhiteSpace(preview.LocalStoragePath))
                {
                    try
                    {
                        Directory.CreateDirectory(preview.LocalStoragePath);
                    }
                    catch (Exception ex)
                    {
                        helperText.Text = $"Could not prepare the project folder: {ex.Message}";
                        helperText.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
                        args.Cancel = true;
                    }
                }
            };

            RefreshDraftPreview();
            ContentDialogResult result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                return null;
            }

            return new ProjectDraft(
                selectedPath ?? ResolveRequestedPath(pathBox.Text),
                selectedProfileId ?? profileBox.SelectedValue as string ?? SampleConfig.DefaultShellProfileId);
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

        private sealed record ProjectDraftPreview(
            string NormalizedPath,
            string ShellProfileId,
            string DisplayPath,
            string HelperText,
            bool CanSubmit,
            string LocalStoragePath);

        private static ProjectDraftPreview BuildProjectDraftPreview(string rawPath, string shellProfileId)
        {
            string profileId = ShellProfiles.Resolve(shellProfileId).Id;
            string normalizedPath = ResolveRequestedPath(rawPath);
            string displayPath = ShellProfiles.ResolveDisplayPath(normalizedPath, profileId);

            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                return new ProjectDraftPreview(
                    Environment.CurrentDirectory,
                    profileId,
                    ShellProfiles.ResolveDisplayPath(Environment.CurrentDirectory, profileId),
                    "Enter a project directory before adding the project.",
                    CanSubmit: false,
                    LocalStoragePath: null);
            }

            if (ShellProfiles.TryResolveLocalStoragePath(normalizedPath, out string localStoragePath))
            {
                bool exists = Directory.Exists(localStoragePath);
                return new ProjectDraftPreview(
                    normalizedPath,
                    profileId,
                    displayPath,
                    exists
                        ? "WinMux will open the project in this existing folder."
                        : "This folder will be created when you add the project.",
                    CanSubmit: true,
                    LocalStoragePath: localStoragePath);
            }

            return new ProjectDraftPreview(
                normalizedPath,
                profileId,
                displayPath,
                "WSL-native paths outside /mnt/... are used as-is. Make sure the folder already exists before you add the project.",
                CanSubmit: true,
                LocalStoragePath: null);
        }
    }
}
