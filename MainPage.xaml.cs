using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Dispatching;
using SelfContainedDeployment.Automation;
using SelfContainedDeployment.Browser;
using SelfContainedDeployment.Git;
using SelfContainedDeployment.Panes;
using SelfContainedDeployment.Persistence;
using SelfContainedDeployment.Shell;
using SelfContainedDeployment.Terminal;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Windows.Storage.Pickers;
using Windows.Foundation;
using WinRT.Interop;

namespace SelfContainedDeployment
{
    public partial class MainPage : Page
    {
        private const double PaneDividerThickness = 4;
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
        private bool _refreshingTabView;
        private bool _suppressPaneInteractionRequests;
        private bool _suppressThreadNameSync;
        private string _inlineRenamingPaneId;
        private bool _restoringSession;
        private bool _inspectorOpen = true;
        private int _threadSequence = 1;
        private readonly DispatcherQueueTimer _sessionSaveTimer;
        private readonly DispatcherQueueTimer _projectTreeRefreshTimer;
        private readonly DispatcherQueueTimer _gitRefreshTimer;
        private GitThreadSnapshot _activeGitSnapshot;
        private int _latestGitRefreshRequestId;
        private string _pendingGitSelectedPath;
        private bool _pendingGitPreserveSelection;
        private string _lastProjectTreeRenderKey;
        private string _lastPaneWorkspaceRenderKey;
        private bool _projectTreeRefreshEnqueued;

        public static MainPage Current;

        public MainPage()
        {
            InitializeComponent();
            Current = this;
            Loaded += OnLoaded;
            ActualThemeChanged += OnActualThemeChanged;
            _sessionSaveTimer = DispatcherQueue.CreateTimer();
            _sessionSaveTimer.IsRepeating = false;
            _sessionSaveTimer.Interval = TimeSpan.FromMilliseconds(450);
            _sessionSaveTimer.Tick += OnSessionSaveTimerTick;
            _projectTreeRefreshTimer = DispatcherQueue.CreateTimer();
            _projectTreeRefreshTimer.IsRepeating = false;
            _projectTreeRefreshTimer.Interval = TimeSpan.FromMilliseconds(70);
            _projectTreeRefreshTimer.Tick += OnProjectTreeRefreshTimerTick;
            _gitRefreshTimer = DispatcherQueue.CreateTimer();
            _gitRefreshTimer.IsRepeating = false;
            _gitRefreshTimer.Interval = TimeSpan.FromMilliseconds(120);
            _gitRefreshTimer.Tick += OnGitRefreshTimerTick;
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
            ApplyGitSnapshotToUi();
            _lastPaneWorkspaceRenderKey = null;
            RenderPaneWorkspace();
            PaneWorkspaceGrid.Background = AppBrush(PaneWorkspaceGrid, "ShellPaneDividerBrush");
            LogAutomationEvent("shell", "theme.changed", $"Theme set to {ResolveTheme(theme).ToString().ToLowerInvariant()}", new Dictionary<string, string>
            {
                ["theme"] = ResolveTheme(theme).ToString().ToLowerInvariant(),
            });
            QueueSessionSave();
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
            QueueSessionSave();
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
            QueueSessionSave();
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
                if (TryPerformKnownUiActionWithoutElement(request))
                {
                    return new NativeAutomationUiActionResponse
                    {
                        Ok = true,
                    };
                }

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

            NativeAutomationUiNode snapshot = null;
            try
            {
                int interactiveIndex = 0;
                snapshot = BuildUiNodeTree(target, "target", ref interactiveIndex);
            }
            catch
            {
            }
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

        private bool TryPerformKnownUiActionWithoutElement(NativeAutomationUiActionRequest request)
        {
            string automationId = request?.AutomationId?.Trim();
            if (string.IsNullOrWhiteSpace(automationId))
            {
                return false;
            }

            string action = request.Action?.Trim().ToLowerInvariant();
            if (automationId.StartsWith("shell-thread-", StringComparison.Ordinal))
            {
                string threadId = automationId["shell-thread-".Length..];
                switch (action)
                {
                    case "doubleclick":
                        _ = BeginRenameThreadAsync(threadId);
                        return true;
                    case "invokemenuitem":
                        return TryInvokeThreadMenuAction(threadId, request.MenuItemText ?? request.Value);
                    default:
                        return false;
                }
            }

            if (automationId.StartsWith("shell-project-", StringComparison.Ordinal) &&
                !automationId.Contains("-add-thread-", StringComparison.Ordinal) &&
                string.Equals(action, "invokemenuitem", StringComparison.Ordinal))
            {
                return TryInvokeProjectMenuAction(automationId["shell-project-".Length..], request.MenuItemText ?? request.Value);
            }

            return false;
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

        public NativeAutomationBrowserStateResponse GetBrowserState(NativeAutomationBrowserStateRequest request)
        {
            request ??= new NativeAutomationBrowserStateRequest();

            List<NativeAutomationBrowserSnapshot> snapshots = new();
            foreach ((WorkspaceProject project, WorkspaceThread thread, BrowserPaneRecord pane) in EnumerateBrowserRecords())
            {
                if (!string.IsNullOrWhiteSpace(request.PaneId) && !string.Equals(pane.Id, request.PaneId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                snapshots.Add(new NativeAutomationBrowserSnapshot
                {
                    PaneId = pane.Id,
                    ThreadId = thread.Id,
                    ProjectId = project.Id,
                    Title = pane.Browser.CurrentTitle,
                    Uri = pane.Browser.CurrentUri,
                    AddressText = pane.Browser.AddressText,
                    Initialized = pane.Browser.IsInitialized,
                    ProfileSeedStatus = pane.Browser.ProfileSeedStatus,
                    ExtensionImportStatus = pane.Browser.ExtensionImportStatus,
                    CredentialAutofillStatus = pane.Browser.CredentialAutofillStatus,
                    InstalledExtensions = pane.Browser.InstalledExtensionNames.ToList(),
                });
            }

            return new NativeAutomationBrowserStateResponse
            {
                SelectedPaneId = _activeThread?.SelectedPaneId,
                Panes = snapshots,
            };
        }

        public NativeAutomationDiffStateResponse GetDiffState(NativeAutomationDiffStateRequest request)
        {
            request ??= new NativeAutomationDiffStateRequest();

            List<NativeAutomationDiffSnapshot> snapshots = new();
            foreach ((WorkspaceProject project, WorkspaceThread thread, DiffPaneRecord pane) in EnumerateDiffRecords())
            {
                if (!string.IsNullOrWhiteSpace(request.PaneId) && !string.Equals(pane.Id, request.PaneId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                DiffPaneRenderSnapshot snapshot = pane.DiffPane.GetRenderSnapshot(request.MaxLines);
                snapshots.Add(new NativeAutomationDiffSnapshot
                {
                    PaneId = pane.Id,
                    ThreadId = thread.Id,
                    ProjectId = project.Id,
                    Title = pane.Title,
                    Path = snapshot.Path,
                    Summary = snapshot.Summary,
                    RawText = snapshot.RawText,
                    HasDiff = !string.IsNullOrWhiteSpace(snapshot.RawText),
                    LineCount = snapshot.LineCount,
                    Lines = snapshot.Lines.Select(line => new NativeAutomationDiffLine
                    {
                        Index = line.Index,
                        Kind = line.Kind,
                        Text = line.Text,
                        Foreground = line.Foreground,
                    }).ToList(),
                });
            }

            return new NativeAutomationDiffStateResponse
            {
                SelectedPaneId = _activeThread?.SelectedPaneId,
                Panes = snapshots,
            };
        }

        public async System.Threading.Tasks.Task<NativeAutomationBrowserEvalResponse> EvaluateBrowserAsync(NativeAutomationBrowserEvalRequest request)
        {
            request ??= new NativeAutomationBrowserEvalRequest();
            BrowserPaneRecord pane = ResolveBrowserPane(request.PaneId);
            if (pane is null)
            {
                return new NativeAutomationBrowserEvalResponse
                {
                    Ok = false,
                    Message = "No browser pane was available for evaluation.",
                };
            }

            try
            {
                string result = await pane.Browser.ExecuteBrowserScriptAsync(request.Script).ConfigureAwait(true);
                return new NativeAutomationBrowserEvalResponse
                {
                    Ok = true,
                    PaneId = pane.Id,
                    Result = result,
                };
            }
            catch (Exception ex)
            {
                return new NativeAutomationBrowserEvalResponse
                {
                    Ok = false,
                    PaneId = pane.Id,
                    Message = ex.Message,
                };
            }
        }

        public async System.Threading.Tasks.Task<NativeAutomationBrowserScreenshotResponse> CaptureBrowserScreenshotAsync(NativeAutomationBrowserScreenshotRequest request)
        {
            request ??= new NativeAutomationBrowserScreenshotRequest();
            BrowserPaneRecord pane = ResolveBrowserPane(request.PaneId);
            if (pane is null)
            {
                return new NativeAutomationBrowserScreenshotResponse
                {
                    Ok = false,
                    Message = "No browser pane was available for capture.",
                };
            }

            try
            {
                (string path, int width, int height) = await pane.Browser.CaptureBrowserPreviewAsync(request.Path).ConfigureAwait(true);
                return new NativeAutomationBrowserScreenshotResponse
                {
                    Ok = true,
                    PaneId = pane.Id,
                    Path = path,
                    Width = width,
                    Height = height,
                };
            }
            catch (Exception ex)
            {
                return new NativeAutomationBrowserScreenshotResponse
                {
                    Ok = false,
                    PaneId = pane.Id,
                    Message = ex.Message,
                };
            }
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
                InspectorOpen = _inspectorOpen,
                ShellProfileId = _activeProject?.ShellProfileId,
                GitBranch = _activeThread?.BranchName,
                WorktreePath = _activeThread?.WorktreePath,
                ChangedFileCount = _activeThread?.ChangedFileCount ?? 0,
                SelectedDiffPath = _activeGitSnapshot?.SelectedPath ?? _activeThread?.SelectedDiffPath,
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
                    case "toggleinspector":
                        ToggleInspector();
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
                    case "setthreadworktree":
                        SetThreadWorktree(request.ThreadId, request.Value);
                        ShowTerminalShell();
                        break;
                    case "refreshdiff":
                        ShowTerminalShell();
                        QueueActiveThreadGitRefresh(preserveSelection: true);
                        break;
                    case "selectdifffile":
                        if (_activeGitSnapshot is not null && !string.IsNullOrWhiteSpace(request.Value))
                        {
                            _activeGitSnapshot.SelectedPath = request.Value;
                            UpdateDiffFileSelection();
                        }

                        if (_activeThread is not null && !string.IsNullOrWhiteSpace(request.Value))
                        {
                            _activeThread.SelectedDiffPath = request.Value;
                        }

                        if (!string.IsNullOrWhiteSpace(request.Value))
                        {
                            AddOrSelectDiffPane(_activeProject, _activeThread, request.Value, null);
                        }

                        ShowTerminalShell();
                        QueueActiveThreadGitRefresh(request.Value, preserveSelection: string.IsNullOrWhiteSpace(request.Value));
                        break;
                    case "closetab":
                        CloseTab(request.TabId);
                        break;
                    case "navigatebrowser":
                        NavigateSelectedBrowser(request.Value);
                        ShowTerminalShell();
                        break;
                    case "importbrowserpasswordscsv":
                        ImportBrowserPasswordsCsv(request.Value);
                        break;
                    case "deletebrowsercredential":
                        DeleteBrowserCredential(request.Value);
                        break;
                    case "clearbrowsercredentials":
                        ClearBrowserCredentials();
                        break;
                    case "autofillbrowser":
                        _ = ManualAutofillSelectedBrowserAsync();
                        break;
                    case "settheme":
                        ApplyTheme(ParseTheme(request.Value));
                        break;
                    case "setprofile":
                        ApplyShellProfile(request.Value);
                        break;
                    case "savesession":
                        PersistSessionState();
                        break;
                    case "renamethread":
                        RenameThread(request.ThreadId, request.Value);
                        break;
                    case "renamepane":
                        RenamePane(request.TabId, request.Value);
                        break;
                    case "duplicatethread":
                        DuplicateThread(request.ThreadId);
                        break;
                    case "deletethread":
                        DeleteThread(request.ThreadId);
                        break;
                    case "deleteproject":
                        DeleteProject(request.ProjectId);
                        break;
                    case "clearprojectthreads":
                        ClearProjectThreads(request.ProjectId);
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
            UpdateInspectorVisibility();
            if (_showingSettings)
            {
                ShowSettings();
            }
            else
            {
                ShowTerminalShell();
            }
        }

        private void OnSessionSaveTimerTick(DispatcherQueueTimer sender, object args)
        {
            _sessionSaveTimer.Stop();
            PersistSessionState();
        }

        private void OnProjectTreeRefreshTimerTick(DispatcherQueueTimer sender, object args)
        {
            _projectTreeRefreshTimer.Stop();
            _projectTreeRefreshEnqueued = false;
            RefreshProjectTree();
        }

        private async void OnGitRefreshTimerTick(DispatcherQueueTimer sender, object args)
        {
            _gitRefreshTimer.Stop();
            await RefreshActiveThreadGitStateAsync(_pendingGitSelectedPath, _pendingGitPreserveSelection).ConfigureAwait(true);
        }

        private void OnActualThemeChanged(FrameworkElement sender, object args)
        {
            if (SampleConfig.CurrentTheme == ElementTheme.Default)
            {
                QueueProjectTreeRefresh();
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
            QueueSessionSave();
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

        private void OnProjectNewThreadMenuClicked(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.Tag is string projectId)
            {
                ActivateThread(CreateThread(FindProject(projectId)));
                ShowTerminalShell();
            }
        }

        private void OnClearProjectThreadsMenuClicked(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.Tag is string projectId)
            {
                ClearProjectThreads(projectId);
            }
        }

        private void OnDeleteProjectMenuClicked(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.Tag is string projectId)
            {
                DeleteProject(projectId);
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

        private void OnPaneTabDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (sender is TabViewItem item && item.Tag is WorkspacePaneRecord pane)
            {
                e.Handled = true;
                BeginInlinePaneRename(pane.Id);
            }
        }

        private async void OnRenameThreadMenuClicked(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.Tag is string threadId)
            {
                await BeginRenameThreadAsync(threadId);
            }
        }

        private void OnRenamePaneMenuClicked(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.Tag is string paneId)
            {
                BeginInlinePaneRename(paneId);
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
            QueueSessionSave();
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

        private void OnToggleInspectorClicked(object sender, RoutedEventArgs e)
        {
            ToggleInspector();
        }

        private void OnRefreshDiffClicked(object sender, RoutedEventArgs e)
        {
            QueueActiveThreadGitRefresh(preserveSelection: true);
        }

        private void OnDiffFileButtonClicked(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button)
            {
                return;
            }

            GitChangedFile changedFile = ResolveSelectedDiffFile(button);
            if (_activeGitSnapshot is null || changedFile is null)
            {
                return;
            }

            _activeGitSnapshot.SelectedPath = changedFile.Path;
            _activeThread.SelectedDiffPath = changedFile.Path;
            UpdateDiffFileSelection();
            AddOrSelectDiffPane(_activeProject, _activeThread, changedFile.Path, null);
            QueueActiveThreadGitRefresh(changedFile.Path);
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
            if (_activeThread is null || _suppressTabSelectionChanged || _refreshingTabView)
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
            QueueProjectTreeRefresh();
            UpdateHeader();
            QueueSessionSave();
        }

        private void InitializeShellModel()
        {
            if (TryRestoreSession())
            {
                LogAutomationEvent("shell", "workspace.restored", "Restored previous WinMux session", new Dictionary<string, string>
                {
                    ["projectCount"] = _projects.Count.ToString(),
                    ["sessionPath"] = WorkspaceSessionStore.GetSessionPath(),
                });
                return;
            }

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

        internal void PersistSessionState()
        {
            if (_restoringSession)
            {
                return;
            }

            WorkspaceSessionSnapshot snapshot = BuildSessionSnapshot();
            WorkspaceSessionStore.Save(snapshot);
            LogAutomationEvent("shell", "workspace.saved", "Saved WinMux workspace session", new Dictionary<string, string>
            {
                ["projectCount"] = snapshot.Projects.Count.ToString(),
                ["sessionPath"] = WorkspaceSessionStore.GetSessionPath(),
            });
        }

        private void QueueSessionSave()
        {
            if (_restoringSession)
            {
                return;
            }

            _sessionSaveTimer.Stop();
            _sessionSaveTimer.Start();
        }

        private void QueueProjectTreeRefresh(bool immediate = false)
        {
            _projectTreeRefreshTimer.Stop();
            if (immediate)
            {
                if (_projectTreeRefreshEnqueued)
                {
                    return;
                }

                _projectTreeRefreshEnqueued = true;
                DispatcherQueue.TryEnqueue(() =>
                {
                    _projectTreeRefreshEnqueued = false;
                    RefreshProjectTree();
                });
                return;
            }

            _projectTreeRefreshTimer.Start();
        }

        private void QueueActiveThreadGitRefresh(string selectedPath = null, bool preserveSelection = false)
        {
            if (_activeThread is null)
            {
                _activeGitSnapshot = null;
                ApplyGitSnapshotToUi();
                return;
            }

            _pendingGitSelectedPath = ResolveSelectedDiffPathForRefresh(_activeThread, selectedPath, preserveSelection);
            _pendingGitPreserveSelection = preserveSelection;
            _gitRefreshTimer.Stop();
            _gitRefreshTimer.Start();

            DiffBranchText.Text = string.IsNullOrWhiteSpace(_activeThread.BranchName)
                ? (_activeGitSnapshot?.BranchName ?? "No git context")
                : _activeThread.BranchName;
            DiffWorktreeText.Text = _activeThread.WorktreePath ?? _activeProject?.RootPath ?? string.Empty;
        }

        private WorkspaceSessionSnapshot BuildSessionSnapshot()
        {
            return new WorkspaceSessionSnapshot
            {
                SavedAt = DateTimeOffset.UtcNow.ToString("O"),
                Theme = WorkspaceSessionStore.FormatTheme(SampleConfig.CurrentTheme),
                DefaultShellProfileId = SampleConfig.DefaultShellProfileId,
                MaxPaneCountPerThread = SampleConfig.MaxPaneCountPerThread,
                PaneOpen = ShellSplitView.IsPaneOpen,
                InspectorOpen = _inspectorOpen,
                ActiveView = _showingSettings ? "settings" : "terminal",
                ActiveProjectId = _activeProject?.Id,
                ActiveThreadId = _activeThread?.Id,
                ThreadSequence = _threadSequence,
                Projects = _projects
                    .Where(ShouldPersistProject)
                    .Select(project => new ProjectSessionSnapshot
                {
                    Id = project.Id,
                    Name = project.Name,
                    RootPath = project.RootPath,
                    ShellProfileId = project.ShellProfileId,
                    SelectedThreadId = project.SelectedThreadId,
                    Threads = project.Threads.Select(thread => new ThreadSessionSnapshot
                    {
                        Id = thread.Id,
                        Name = thread.Name,
                        WorktreePath = thread.WorktreePath,
                        BranchName = thread.BranchName,
                        SelectedDiffPath = thread.SelectedDiffPath,
                        SelectedPaneId = thread.SelectedPaneId,
                        Layout = WorkspaceSessionStore.FormatLayout(thread.LayoutPreset),
                        PrimarySplitRatio = thread.PrimarySplitRatio,
                        SecondarySplitRatio = thread.SecondarySplitRatio,
                        Panes = thread.Panes
                            .Where(pane => !pane.IsExited)
                            .Select(pane => new PaneSessionSnapshot
                            {
                                Id = pane.Id,
                                Kind = pane.Kind.ToString().ToLowerInvariant(),
                                Title = pane.Title,
                                HasCustomTitle = pane.HasCustomTitle,
                                BrowserUri = pane is BrowserPaneRecord browserPane ? browserPane.Browser.CurrentUri : null,
                                DiffPath = pane is DiffPaneRecord diffPane ? diffPane.DiffPath : null,
                                ReplayTool = pane.ReplayTool,
                                ReplaySessionId = pane.ReplaySessionId,
                                ReplayCommand = pane.ReplayCommand,
                            })
                            .ToList(),
                    }).ToList(),
                }).ToList(),
            };
        }

        private bool TryRestoreSession()
        {
            WorkspaceSessionSnapshot snapshot = WorkspaceSessionStore.Load(out string loadError);
            if (snapshot?.Projects is null || snapshot.Projects.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(loadError))
                {
                    LogAutomationEvent("shell", "workspace.restore_failed", $"Could not load saved workspace session: {loadError}", new Dictionary<string, string>
                    {
                        ["sessionPath"] = WorkspaceSessionStore.GetSessionPath(),
                    });
                }

                return false;
            }

            _restoringSession = true;
            try
            {
                _projects.Clear();
                _tabItemsById.Clear();
                foreach (Border container in _paneContainersById.Values)
                {
                    container.Child = null;
                }
                _paneContainersById.Clear();

                SampleConfig.CurrentTheme = WorkspaceSessionStore.ParseTheme(snapshot.Theme);
                SampleConfig.DefaultShellProfileId = ShellProfiles.Resolve(snapshot.DefaultShellProfileId).Id;
                SampleConfig.MaxPaneCountPerThread = Math.Clamp(snapshot.MaxPaneCountPerThread, 2, 4);
                _threadSequence = Math.Max(1, snapshot.ThreadSequence);

                foreach (ProjectSessionSnapshot projectSnapshot in snapshot.Projects)
                {
                    if (!ShouldPersistProjectPath(projectSnapshot.RootPath))
                    {
                        continue;
                    }

                    WorkspaceProject project = new(projectSnapshot.RootPath, projectSnapshot.ShellProfileId, projectSnapshot.Name, projectSnapshot.Id);
                    ShellProfiles.EnsureProjectDirectory(project.RootPath, out _);
                    _projects.Add(project);

                    foreach (ThreadSessionSnapshot threadSnapshot in projectSnapshot.Threads ?? new List<ThreadSessionSnapshot>())
                    {
                        WorkspaceThread thread = new(project, string.IsNullOrWhiteSpace(threadSnapshot.Name) ? $"Thread {_threadSequence++}" : threadSnapshot.Name, threadSnapshot.Id)
                        {
                            WorktreePath = string.IsNullOrWhiteSpace(threadSnapshot.WorktreePath) ? project.RootPath : threadSnapshot.WorktreePath,
                            BranchName = threadSnapshot.BranchName,
                            SelectedDiffPath = threadSnapshot.SelectedDiffPath,
                            LayoutPreset = WorkspaceSessionStore.ParseLayout(threadSnapshot.Layout),
                            PrimarySplitRatio = ClampPaneSplitRatio(threadSnapshot.PrimarySplitRatio <= 0 ? 0.58 : threadSnapshot.PrimarySplitRatio),
                            SecondarySplitRatio = ClampPaneSplitRatio(threadSnapshot.SecondarySplitRatio <= 0 ? 0.5 : threadSnapshot.SecondarySplitRatio),
                        };
                        project.Threads.Add(thread);

                        foreach (PaneSessionSnapshot paneSnapshot in threadSnapshot.Panes ?? new List<PaneSessionSnapshot>())
                        {
                            WorkspacePaneRecord pane = RestorePaneFromSnapshot(project, thread, paneSnapshot);
                            if (pane is not null)
                            {
                                thread.Panes.Add(pane);
                            }
                        }

                        if (thread.Panes.Count == 0)
                        {
                            EnsureThreadHasTab(project, thread);
                        }

                        EnsureThreadHasSelectedDiffPane(project, thread);

                        thread.SelectedPaneId = thread.Panes.Any(candidate => string.Equals(candidate.Id, threadSnapshot.SelectedPaneId, StringComparison.Ordinal))
                            ? threadSnapshot.SelectedPaneId
                            : thread.Panes.FirstOrDefault()?.Id;
                    }

                    if (project.Threads.Count == 0)
                    {
                        CreateThread(project);
                    }

                    project.SelectedThreadId = project.Threads.Any(candidate => string.Equals(candidate.Id, projectSnapshot.SelectedThreadId, StringComparison.Ordinal))
                        ? projectSnapshot.SelectedThreadId
                        : project.Threads.FirstOrDefault()?.Id;
                }

                _activeProject = _projects.FirstOrDefault(project => string.Equals(project.Id, snapshot.ActiveProjectId, StringComparison.Ordinal))
                    ?? _projects.FirstOrDefault();
                _activeThread = _projects
                    .SelectMany(project => project.Threads)
                    .FirstOrDefault(thread => string.Equals(thread.Id, snapshot.ActiveThreadId, StringComparison.Ordinal))
                    ?? _activeProject?.Threads.FirstOrDefault(thread => string.Equals(thread.Id, _activeProject.SelectedThreadId, StringComparison.Ordinal))
                    ?? _activeProject?.Threads.FirstOrDefault();

                if (_activeProject is not null && _activeThread is not null)
                {
                    _activeProject.SelectedThreadId = _activeThread.Id;
                }

                ShellSplitView.IsPaneOpen = snapshot.PaneOpen;
                _inspectorOpen = snapshot.InspectorOpen;
                _showingSettings = string.Equals(snapshot.ActiveView, "settings", StringComparison.OrdinalIgnoreCase);
                RefreshProjectTree();
                RefreshTabView();
                UpdateInspectorVisibility();
                return true;
            }
            catch (Exception ex)
            {
                LogAutomationEvent("shell", "workspace.restore_failed", $"Could not restore saved workspace session: {ex.Message}", new Dictionary<string, string>
                {
                    ["sessionPath"] = WorkspaceSessionStore.GetSessionPath(),
                });
                _projects.Clear();
                _tabItemsById.Clear();
                _paneContainersById.Clear();
                _activeProject = null;
                _activeThread = null;
                return false;
            }
            finally
            {
                _restoringSession = false;
            }
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
            QueueSessionSave();
            return project;
        }

        private WorkspaceThread CreateThread(WorkspaceProject project, string threadName = null, bool ensureInitialPane = true)
        {
            project ??= _activeProject ?? GetOrCreateProject(Environment.CurrentDirectory);

            WorkspaceThread thread = new(project, string.IsNullOrWhiteSpace(threadName) ? $"Thread {_threadSequence++}" : threadName.Trim());
            if (TryResolveInheritedWorktreePath(project, out string inheritedWorktreePath))
            {
                thread.WorktreePath = inheritedWorktreePath;
            }

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
            QueueSessionSave();
            return thread;
        }

        private bool TryResolveInheritedWorktreePath(WorkspaceProject project, out string worktreePath)
        {
            worktreePath = null;
            if (project is null)
            {
                return false;
            }

            WorkspaceThread sourceThread = null;
            if (ReferenceEquals(project, _activeProject) && _activeThread is not null)
            {
                sourceThread = _activeThread;
            }
            else if (!string.IsNullOrWhiteSpace(project.SelectedThreadId))
            {
                sourceThread = project.Threads.FirstOrDefault(thread => string.Equals(thread.Id, project.SelectedThreadId, StringComparison.Ordinal));
            }

            string candidatePath = sourceThread?.WorktreePath;
            if (string.IsNullOrWhiteSpace(candidatePath) || string.Equals(candidatePath, project.RootPath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            worktreePath = candidatePath;
            return true;
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

            if (paneKind == WorkspacePaneKind.Diff)
            {
                return thread;
            }

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

            TerminalPaneRecord pane = CreateTerminalPane(project, thread, WorkspacePaneKind.Terminal, startupInput: null, initialTitle: FormatThreadPath(project, thread));
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
            QueueSessionSave();
            return pane;
        }

        private BrowserPaneRecord AddBrowserPane(WorkspaceProject project, WorkspaceThread thread, string initialUri = null)
        {
            project ??= _activeProject ?? GetOrCreateProject(Environment.CurrentDirectory);
            thread = ResolveTargetThreadForNewPane(project, thread, WorkspacePaneKind.Browser);
            BrowserPaneRecord pane = CreateBrowserPane(project, thread, initialUri, "Preview");

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
            QueueSessionSave();
            return pane;
        }

        private DiffPaneRecord AddOrSelectDiffPane(WorkspaceProject project, WorkspaceThread thread, string diffPath, string diffText)
        {
            project ??= _activeProject ?? GetOrCreateProject(Environment.CurrentDirectory);
            thread = ResolveTargetThreadForNewPane(project, thread, WorkspacePaneKind.Diff);
            thread.SelectedDiffPath = diffPath;

            DiffPaneRecord pane = thread.Panes.OfType<DiffPaneRecord>().FirstOrDefault();
            bool created = false;
            if (pane is null)
            {
                pane = CreateDiffPane(project, thread, diffPath, diffText, BuildDiffPaneTitle(diffPath));
                thread.Panes.Add(pane);
                PromoteLayoutForPaneCount(thread);
                created = true;
            }
            else
            {
                UpdateDiffPane(pane, diffPath, diffText);
            }

            thread.SelectedPaneId = pane.Id;
            project.SelectedThreadId = thread.Id;

            thread.LayoutPreset = WorkspaceLayoutPreset.Dual;

            if (thread == _activeThread)
            {
                RefreshTabView();
                RequestLayoutForVisiblePanes();
            }
            else
            {
                ActivateThread(thread);
            }

            RefreshProjectTree();
            if (created)
            {
                LogAutomationEvent("shell", "pane.created", $"Created diff pane {pane.Id}", new Dictionary<string, string>
                {
                    ["paneId"] = pane.Id,
                    ["paneKind"] = pane.Kind.ToString().ToLowerInvariant(),
                    ["threadId"] = thread.Id,
                    ["projectId"] = project.Id,
                    ["diffPath"] = diffPath ?? string.Empty,
                });
            }
            else
            {
                LogAutomationEvent("shell", "pane.updated", $"Updated diff pane {pane.Id}", new Dictionary<string, string>
                {
                    ["paneId"] = pane.Id,
                    ["paneKind"] = pane.Kind.ToString().ToLowerInvariant(),
                    ["threadId"] = thread.Id,
                    ["projectId"] = project.Id,
                    ["diffPath"] = diffPath ?? string.Empty,
                });
            }

            QueueSessionSave();
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
            QueueSessionSave();
            return pane;
        }

        private BrowserPaneRecord CreateBrowserPane(WorkspaceProject project, WorkspaceThread thread, string initialUri, string initialTitle, string paneId = null)
        {
            string threadRootPath = ResolveThreadRootPath(project, thread);
            BrowserPaneControl browser = new()
            {
                ProjectId = project.Id,
                ProjectName = project.Name,
                ProjectPath = FormatThreadPath(project, thread),
                ProjectRootPath = threadRootPath,
                InitialUri = initialUri,
            };
            browser.ApplyTheme(ResolveTheme(SampleConfig.CurrentTheme));

            BrowserPaneRecord pane = new(initialTitle, browser, paneId);
            AttachPaneInteraction(project, thread, pane);
            browser.OpenPaneRequested += (_, uri) =>
            {
                BrowserPaneRecord siblingPane = AddBrowserPane(project, thread, uri);
                SelectPane(siblingPane);
                ShowTerminalShell();
                LogAutomationEvent("browser", "pane.spawned", "Opened a related browser pane", new Dictionary<string, string>
                {
                    ["sourcePaneId"] = pane.Id,
                    ["paneId"] = siblingPane.Id,
                    ["threadId"] = thread.Id,
                    ["projectId"] = project.Id,
                    ["initialUri"] = uri ?? string.Empty,
                });
                QueueSessionSave();
            };
            browser.TitleChanged += (_, title) =>
            {
                if (!pane.HasCustomTitle)
                {
                    pane.Title = string.IsNullOrWhiteSpace(title) ? initialTitle : title;
                }

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

                QueueSessionSave();
            };
            browser.StateChanged += (_, _) => QueueSessionSave();
            return pane;
        }

        private DiffPaneRecord CreateDiffPane(WorkspaceProject project, WorkspaceThread thread, string diffPath, string diffText, string initialTitle, string paneId = null)
        {
            DiffPaneControl diffPaneControl = new();
            diffPaneControl.ApplyTheme(ResolveTheme(SampleConfig.CurrentTheme));

            DiffPaneRecord pane = new(initialTitle, diffPaneControl, diffPath, paneId);
            diffPaneControl.ApplyAutomationIdentity(pane.Id);
            AttachPaneInteraction(project, thread, pane);
            UpdateDiffPane(pane, diffPath, diffText);
            return pane;
        }

        private void UpdateDiffPane(DiffPaneRecord pane, string diffPath, string diffText)
        {
            if (pane is null)
            {
                return;
            }

            pane.DiffPath = diffPath;
            if (!pane.HasCustomTitle)
            {
                pane.Title = BuildDiffPaneTitle(diffPath);
            }

            pane.DiffPane.SetDiff(diffPath, diffText);
            if (ReferenceEquals(FindThreadForPane(pane.Id), _activeThread))
            {
                UpdateTabViewItem(pane);
            }
        }

        private TerminalPaneRecord CreateTerminalPane(WorkspaceProject project, WorkspaceThread thread, WorkspacePaneKind kind, string startupInput, string initialTitle, string paneId = null)
        {
            TerminalControl terminal = new()
            {
                DisplayWorkingDirectory = FormatThreadPath(project, thread),
                InitialWorkingDirectory = FormatThreadPath(project, thread),
                ProcessWorkingDirectory = ShellProfiles.ResolveProcessWorkingDirectory(ResolveThreadRootPath(project, thread)),
                ShellCommand = ShellProfiles.BuildLaunchCommand(project.ShellProfileId, ResolveThreadRootPath(project, thread)),
                StartupInput = startupInput,
            };

            terminal.ApplyTheme(ResolveTheme(SampleConfig.CurrentTheme));

            TerminalPaneRecord pane = new(initialTitle, terminal, kind, paneId);
            AttachPaneInteraction(project, thread, pane);
            terminal.SessionTitleChanged += (_, title) =>
            {
                if (!pane.HasCustomTitle)
                {
                    pane.Title = string.IsNullOrWhiteSpace(title) ? initialTitle : title;
                }
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

                QueueSessionSave();
            };
            terminal.ReplayStateChanged += (_, _) =>
            {
                pane.ReplayTool = terminal.ReplayTool;
                pane.ReplaySessionId = terminal.ReplaySessionId;
                pane.ReplayCommand = terminal.ReplayCommand;
                QueueSessionSave();
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
                QueueSessionSave();
            };

            return pane;
        }

        private WorkspaceThread FindThreadForPane(string paneId)
        {
            if (string.IsNullOrWhiteSpace(paneId))
            {
                return null;
            }

            return _projects
                .SelectMany(project => project.Threads)
                .FirstOrDefault(thread => thread.Panes.Any(candidate => string.Equals(candidate.Id, paneId, StringComparison.Ordinal)));
        }

        private WorkspacePaneRecord RestorePaneFromSnapshot(WorkspaceProject project, WorkspaceThread thread, PaneSessionSnapshot snapshot)
        {
            if (snapshot is null)
            {
                return null;
            }

            WorkspacePaneRecord pane = snapshot.Kind?.Trim().ToLowerInvariant() switch
            {
                "browser" => CreateBrowserPane(project, thread, snapshot.BrowserUri, string.IsNullOrWhiteSpace(snapshot.Title) ? "Preview" : snapshot.Title, snapshot.Id),
                "diff" => CreateDiffPane(project, thread, snapshot.DiffPath, diffText: null, string.IsNullOrWhiteSpace(snapshot.Title) ? BuildDiffPaneTitle(snapshot.DiffPath) : snapshot.Title, snapshot.Id),
                "editor" => CreateTerminalPane(project, thread, WorkspacePaneKind.Editor, "nvim .\r", string.IsNullOrWhiteSpace(snapshot.Title) ? "Editor" : snapshot.Title, snapshot.Id),
                _ => CreateTerminalPane(project, thread, WorkspacePaneKind.Terminal, BuildReplayStartupInput(snapshot), string.IsNullOrWhiteSpace(snapshot.Title) ? FormatThreadPath(project, thread) : snapshot.Title, snapshot.Id),
            };

            pane.HasCustomTitle = snapshot.HasCustomTitle;
            pane.ReplayTool = snapshot.ReplayTool;
            pane.ReplaySessionId = snapshot.ReplaySessionId;
            pane.ReplayCommand = snapshot.ReplayCommand;
            return pane;
        }

        private void EnsureThreadHasSelectedDiffPane(WorkspaceProject project, WorkspaceThread thread)
        {
            if (thread is null || string.IsNullOrWhiteSpace(thread.SelectedDiffPath))
            {
                return;
            }

            if (thread.Panes.OfType<DiffPaneRecord>().Any())
            {
                return;
            }

            DiffPaneRecord pane = CreateDiffPane(project, thread, thread.SelectedDiffPath, diffText: null, BuildDiffPaneTitle(thread.SelectedDiffPath));
            thread.Panes.Add(pane);
            PromoteLayoutForPaneCount(thread);
        }

        private static string BuildReplayStartupInput(PaneSessionSnapshot snapshot)
        {
            if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.ReplayCommand))
            {
                return null;
            }

            return snapshot.ReplayCommand.EndsWith("\r", StringComparison.Ordinal)
                ? snapshot.ReplayCommand
                : snapshot.ReplayCommand + "\r";
        }

        private void AttachPaneInteraction(WorkspaceProject project, WorkspaceThread thread, WorkspacePaneRecord pane)
        {
            void ActivatePaneFromInteraction()
            {
                if (_suppressPaneInteractionRequests || _refreshingTabView)
                {
                    return;
                }

                if (!ReferenceEquals(_activeThread, thread))
                {
                    ActivateThread(thread);
                }

                if (!string.Equals(thread.SelectedPaneId, pane.Id, StringComparison.Ordinal))
                {
                    SelectPane(pane, focusPane: false);
                }
                else
                {
                    UpdatePaneSelectionChrome();
                }
            }

            switch (pane)
            {
                case TerminalPaneRecord terminalPane:
                    terminalPane.Terminal.InteractionRequested += (_, _) => ActivatePaneFromInteraction();
                    break;
                case BrowserPaneRecord browserPane:
                    browserPane.Browser.InteractionRequested += (_, _) => ActivatePaneFromInteraction();
                    break;
            }
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

                    if (pane is DiffPaneRecord)
                    {
                        thread.SelectedDiffPath = null;
                        if (thread == _activeThread && _activeGitSnapshot is not null)
                        {
                            _activeGitSnapshot.SelectedPath = null;
                            _activeGitSnapshot.SelectedDiff = null;
                        }
                    }

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
                    QueueSessionSave();
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
                    QueueSessionSave();
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

                    QueueSessionSave();
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
            QueueSessionSave();
            LogAutomationEvent("shell", "thread.layout_changed", $"Thread layout changed to {thread.LayoutPreset}", new Dictionary<string, string>
            {
                ["threadId"] = thread.Id,
                ["projectId"] = thread.Project.Id,
                ["layout"] = thread.LayoutPreset.ToString().ToLowerInvariant(),
            });
        }

        private void NavigateSelectedBrowser(string value)
        {
            BrowserPaneRecord pane = ResolveBrowserPane();

            if (pane is null)
            {
                return;
            }

            if (!string.Equals(_activeThread?.SelectedPaneId, pane.Id, StringComparison.Ordinal))
            {
                SelectPane(pane);
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

        private BrowserPaneRecord ResolveBrowserPane(string paneId = null)
        {
            if (!string.IsNullOrWhiteSpace(paneId))
            {
                return EnumerateBrowserRecords()
                    .Select(record => record.Pane)
                    .FirstOrDefault(candidate => string.Equals(candidate.Id, paneId, StringComparison.OrdinalIgnoreCase));
            }

            return (TerminalTabs.SelectedItem as TabViewItem)?.Tag as BrowserPaneRecord
                ?? _activeThread?.Panes.OfType<BrowserPaneRecord>().FirstOrDefault(candidate => string.Equals(candidate.Id, _activeThread.SelectedPaneId, StringComparison.Ordinal))
                ?? _activeThread?.Panes.OfType<BrowserPaneRecord>().FirstOrDefault();
        }

        private void ImportBrowserPasswordsCsv(string csvPath)
        {
            BrowserCredentialImportResult result = BrowserCredentialStore.ImportGooglePasswordsCsv(csvPath);
            if (!result.Ok)
            {
                throw new InvalidOperationException(result.Message);
            }

            RefreshBrowserCredentialState(reloadCurrentPages: true);

            LogAutomationEvent("browser", "credentials.imported", result.Message, new Dictionary<string, string>
            {
                ["path"] = csvPath ?? string.Empty,
                ["importedCount"] = result.ImportedCount.ToString(),
            });
        }

        private void DeleteBrowserCredential(string id)
        {
            if (!BrowserCredentialStore.DeleteCredential(id))
            {
                throw new InvalidOperationException("The requested browser credential could not be found.");
            }

            RefreshBrowserCredentialState(reloadCurrentPages: false);
            LogAutomationEvent("browser", "credentials.deleted", "Deleted a browser credential from the WinMux vault", new Dictionary<string, string>
            {
                ["credentialId"] = id ?? string.Empty,
            });
        }

        private void ClearBrowserCredentials()
        {
            int removed = BrowserCredentialStore.ClearCredentials();
            RefreshBrowserCredentialState(reloadCurrentPages: false);
            LogAutomationEvent("browser", "credentials.cleared", "Cleared the WinMux browser credential vault", new Dictionary<string, string>
            {
                ["removedCount"] = removed.ToString(),
            });
        }

        private void RefreshBrowserCredentialState(bool reloadCurrentPages)
        {
            foreach ((_, _, BrowserPaneRecord pane) in EnumerateBrowserRecords())
            {
                pane.Browser.RefreshCredentialAutofillState();
                if (reloadCurrentPages && !string.IsNullOrWhiteSpace(pane.Browser.CurrentUri))
                {
                    pane.Browser.Navigate(pane.Browser.CurrentUri);
                }
            }
        }

        internal IReadOnlyList<BrowserCredentialSummary> GetBrowserCredentialSummaries()
        {
            return BrowserCredentialStore.GetCredentialSummaries();
        }

        internal string ImportBrowserPasswordsCsvFromPath(string csvPath)
        {
            ImportBrowserPasswordsCsv(csvPath);
            return $"Imported {BrowserCredentialStore.GetCredentialCount()} encrypted browser credential{(BrowserCredentialStore.GetCredentialCount() == 1 ? string.Empty : "s")} into WinMux.";
        }

        internal bool DeleteBrowserCredentialFromSettings(string id, out string message)
        {
            try
            {
                DeleteBrowserCredential(id);
                message = "Removed credential from the WinMux vault.";
                return true;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return false;
            }
        }

        internal string ClearBrowserCredentialsFromSettings()
        {
            int removed = BrowserCredentialStore.GetCredentialCount();
            ClearBrowserCredentials();
            return removed == 0
                ? "The WinMux credential vault was already empty."
                : $"Cleared {removed} browser credential{(removed == 1 ? string.Empty : "s")} from the WinMux vault.";
        }

        internal async System.Threading.Tasks.Task<string> ManualAutofillSelectedBrowserAsync()
        {
            BrowserPaneRecord pane = ResolveBrowserPane();
            if (pane is null)
            {
                throw new InvalidOperationException("No browser pane is selected.");
            }

            await pane.Browser.ManualAutofillCurrentPageAsync().ConfigureAwait(true);
            return pane.Browser.CredentialAutofillStatus;
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
                _lastProjectTreeRenderKey = null;
                QueueProjectTreeRefresh(immediate: true);
                RefreshTabView();
                UpdateWorkspaceVisibility();
                UpdateHeader();
                QueueSessionSave();
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
            WorkspaceThread previousThread = _activeThread;
            _activeThread = thread ?? throw new ArgumentNullException(nameof(thread));
            _activeProject = FindProjectForThread(thread);
            _activeProject.SelectedThreadId = thread.Id;
            EnsureThreadHasTab(_activeProject, thread);
            EnsureThreadHasSelectedDiffPane(_activeProject, thread);
            if (!ReferenceEquals(previousThread, thread))
            {
                _activeGitSnapshot = null;
                ApplyGitSnapshotToUi();
            }
            QueueProjectTreeRefresh();
            RefreshTabView();
            UpdateWorkspaceVisibility();
            UpdateHeader();
            RequestLayoutForVisiblePanes();
            FocusSelectedPane();
            QueueActiveThreadGitRefresh(thread.SelectedDiffPath, preserveSelection: true);
            QueueSessionSave();
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

        private void BeginInlinePaneRename(string paneId)
        {
            WorkspacePaneRecord pane = EnumeratePaneRecords().FirstOrDefault(candidate => string.Equals(candidate.Id, paneId, StringComparison.Ordinal));
            if (pane is null)
            {
                return;
            }

            WorkspaceThread thread = FindThreadForPane(paneId);
            LogAutomationEvent("shell", "pane.rename_requested", $"Opening rename dialog for {pane.Title}", new Dictionary<string, string>
            {
                ["paneId"] = pane.Id,
                ["threadId"] = thread?.Id ?? string.Empty,
                ["projectId"] = _activeProject?.Id ?? string.Empty,
                ["paneKind"] = pane.Kind.ToString().ToLowerInvariant(),
            });

            _inlineRenamingPaneId = pane.Id;
            UpdateTabViewItem(pane);
            if (_tabItemsById.TryGetValue(pane.Id, out TabViewItem item))
            {
                TerminalTabs.SelectedItem = item;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (item.Header is TextBox editor)
                    {
                        editor.Focus(FocusState.Programmatic);
                        editor.SelectAll();
                    }
                });
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
            QueueSessionSave();
            LogAutomationEvent("shell", "thread.renamed", $"Renamed thread to {thread.Name}", new Dictionary<string, string>
            {
                ["threadId"] = thread.Id,
                ["projectId"] = thread.Project.Id,
                ["threadName"] = thread.Name,
            });
        }

        private void RenamePane(string paneId, string nextName)
        {
            WorkspacePaneRecord pane = string.IsNullOrWhiteSpace(paneId)
                ? GetSelectedPane(_activeThread)
                : EnumeratePaneRecords().FirstOrDefault(candidate => string.Equals(candidate.Id, paneId, StringComparison.Ordinal));
            if (pane is null)
            {
                return;
            }

            string trimmed = nextName?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return;
            }

            pane.Title = trimmed;
            pane.HasCustomTitle = true;
            _inlineRenamingPaneId = null;
            UpdateTabViewItem(pane);
            RenderPaneWorkspace();
            UpdateHeader();
            QueueSessionSave();

            WorkspaceThread thread = FindThreadForPane(pane.Id);
            LogAutomationEvent("shell", "pane.renamed", $"Renamed pane to {pane.Title}", new Dictionary<string, string>
            {
                ["paneId"] = pane.Id,
                ["threadId"] = thread?.Id ?? string.Empty,
                ["projectId"] = _activeProject?.Id ?? string.Empty,
                ["paneKind"] = pane.Kind.ToString().ToLowerInvariant(),
                ["paneTitle"] = pane.Title,
            });
        }

        private void CancelInlinePaneRename(string paneId)
        {
            if (string.IsNullOrWhiteSpace(paneId) || !string.Equals(_inlineRenamingPaneId, paneId, StringComparison.Ordinal))
            {
                return;
            }

            _inlineRenamingPaneId = null;
            WorkspacePaneRecord pane = EnumeratePaneRecords().FirstOrDefault(candidate => string.Equals(candidate.Id, paneId, StringComparison.Ordinal));
            if (pane is not null)
            {
                UpdateTabViewItem(pane);
            }
        }

        private void CommitInlinePaneRename(string paneId, string nextName)
        {
            WorkspacePaneRecord pane = EnumeratePaneRecords().FirstOrDefault(candidate => string.Equals(candidate.Id, paneId, StringComparison.Ordinal));
            _inlineRenamingPaneId = null;
            if (pane is null)
            {
                return;
            }

            string trimmed = nextName?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || string.Equals(trimmed, pane.Title, StringComparison.Ordinal))
            {
                UpdateTabViewItem(pane);
                return;
            }

            RenamePane(paneId, trimmed);
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
                    case WorkspacePaneKind.Diff:
                        AddOrSelectDiffPane(project, duplicate, (pane as DiffPaneRecord)?.DiffPath, diffText: null);
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
            QueueSessionSave();
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
                    _activeGitSnapshot = null;
                    _lastProjectTreeRenderKey = null;
                    _lastPaneWorkspaceRenderKey = null;
                    QueueProjectTreeRefresh(immediate: true);
                    RefreshTabView();
                    UpdateWorkspaceVisibility();
                    UpdateSidebarActions();
                    UpdateInspectorVisibility();
                    UpdateHeader();
                }
            }
            else
            {
                _lastProjectTreeRenderKey = null;
                QueueProjectTreeRefresh(immediate: true);
                UpdateWorkspaceVisibility();
                UpdateHeader();
            }

            ShowTerminalShell();
            QueueSessionSave();
            LogAutomationEvent("shell", "thread.deleted", $"Deleted thread {thread.Name}", new Dictionary<string, string>
            {
                ["threadId"] = thread.Id,
                ["projectId"] = project.Id,
                ["remainingThreadCount"] = project.Threads.Count.ToString(),
            });
        }

        private void ClearProjectThreads(string projectId)
        {
            WorkspaceProject project = string.IsNullOrWhiteSpace(projectId)
                ? _activeProject
                : FindProject(projectId);
            if (project is null)
            {
                return;
            }

            foreach (WorkspaceThread thread in project.Threads.ToList())
            {
                ClearThreadPanes(project, thread);
            }

            project.Threads.Clear();
            project.SelectedThreadId = null;

            if (ReferenceEquals(project, _activeProject))
            {
                _activeThread = null;
                _activeGitSnapshot = null;
                _showingSettings = false;
                _lastProjectTreeRenderKey = null;
                _lastPaneWorkspaceRenderKey = null;
                RefreshTabView();
                QueueProjectTreeRefresh(immediate: true);
                UpdateWorkspaceVisibility();
                UpdateSidebarActions();
                UpdateInspectorVisibility();
                UpdateHeader();
            }
            else
            {
                _lastProjectTreeRenderKey = null;
                QueueProjectTreeRefresh(immediate: true);
            }

            QueueSessionSave();
            LogAutomationEvent("shell", "project.threads_cleared", $"Cleared all threads for {project.Name}", new Dictionary<string, string>
            {
                ["projectId"] = project.Id,
            });
        }

        private void DeleteProject(string projectId)
        {
            WorkspaceProject project = string.IsNullOrWhiteSpace(projectId)
                ? _activeProject
                : _projects.FirstOrDefault(candidate => string.Equals(candidate.Id, projectId, StringComparison.Ordinal));
            if (project is null)
            {
                return;
            }

            bool wasActive = ReferenceEquals(project, _activeProject);
            foreach (WorkspaceThread thread in project.Threads.ToList())
            {
                ClearThreadPanes(project, thread);
            }

            _projects.Remove(project);

            if (_projects.Count == 0)
            {
                WorkspaceProject fallbackProject = GetOrCreateProject(Environment.CurrentDirectory, null, SampleConfig.DefaultShellProfileId);
                OpenProject(fallbackProject);
            }
            else if (wasActive)
            {
                OpenProject(_projects[0]);
            }
            else
            {
                QueueProjectTreeRefresh();
                UpdateWorkspaceVisibility();
                UpdateHeader();
            }

            ShowTerminalShell();
            QueueSessionSave();
            LogAutomationEvent("shell", "project.deleted", $"Deleted project {project.Name}", new Dictionary<string, string>
            {
                ["projectId"] = project.Id,
                ["remainingProjectCount"] = _projects.Count.ToString(),
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
            QueueSessionSave();
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
            thread.SelectedDiffPath = null;

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
            string renderKey = BuildProjectTreeRenderKey();
            if (string.Equals(renderKey, _lastProjectTreeRenderKey, StringComparison.Ordinal))
            {
                return;
            }

            _lastProjectTreeRenderKey = renderKey;
            ProjectListPanel.Children.Clear();
            bool isOpen = ShellSplitView.IsPaneOpen;

            foreach (WorkspaceProject project in _projects)
            {
                bool showProjectThreads = isOpen && ReferenceEquals(project, _activeProject);
                StackPanel group = new()
                {
                    Spacing = 4,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };
                AutomationProperties.SetAutomationId(group, $"shell-project-group-{project.Id}");

                Button projectButton = new()
                {
                    Style = (Style)Application.Current.Resources["ShellNavButtonStyle"],
                    Tag = project.Id,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                };
                AutomationProperties.SetAutomationId(projectButton, $"shell-project-{project.Id}");
                AutomationProperties.SetName(projectButton, project.Name);
                projectButton.Click += OnProjectButtonClicked;

                Grid projectLayout = new()
                {
                    ColumnSpacing = 8,
                };
                AutomationProperties.SetAutomationId(projectLayout, $"shell-project-layout-{project.Id}");
                projectLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                projectLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                FontIcon projectIcon = new()
                {
                    FontSize = 12,
                    Glyph = "\uE8B7",
                    VerticalAlignment = VerticalAlignment.Center,
                };
                AutomationProperties.SetAutomationId(projectIcon, $"shell-project-icon-{project.Id}");
                projectLayout.Children.Add(projectIcon);

                if (isOpen)
                {
                    StackPanel textStack = new()
                    {
                        Spacing = 0,
                    };
                    AutomationProperties.SetAutomationId(textStack, $"shell-project-text-{project.Id}");
                    Grid.SetColumn(textStack, 1);
                    TextBlock projectTitle = new()
                    {
                        Text = project.Name,
                        FontSize = 12,
                        TextWrapping = TextWrapping.NoWrap,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    };
                    AutomationProperties.SetAutomationId(projectTitle, $"shell-project-title-{project.Id}");
                    textStack.Children.Add(projectTitle);
                    TextBlock projectMeta = new()
                    {
                        Text = $"{FormatProjectPath(project)} · {project.Threads.Count} thread{(project.Threads.Count == 1 ? string.Empty : "s")}",
                        Style = (Style)Application.Current.Resources["ShellHintTextStyle"],
                        TextWrapping = TextWrapping.NoWrap,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    };
                    AutomationProperties.SetAutomationId(projectMeta, $"shell-project-meta-{project.Id}");
                    textStack.Children.Add(projectMeta);
                    projectLayout.Children.Add(textStack);
                }

                projectButton.Content = projectLayout;
                ApplyProjectButtonState(projectButton, project == _activeProject && !_showingSettings);
                MenuFlyout projectMenu = new();
                MenuFlyoutItem projectNewThreadItem = new()
                {
                    Text = "New thread",
                    Tag = project.Id,
                };
                projectNewThreadItem.Click += OnProjectNewThreadMenuClicked;
                projectMenu.Items.Add(projectNewThreadItem);
                MenuFlyoutItem clearThreadsItem = new()
                {
                    Text = "Clear all threads",
                    Tag = project.Id,
                    IsEnabled = project.Threads.Count > 0,
                };
                clearThreadsItem.Click += OnClearProjectThreadsMenuClicked;
                projectMenu.Items.Add(clearThreadsItem);
                MenuFlyoutItem deleteProjectItem = new()
                {
                    Text = "Remove project",
                    Tag = project.Id,
                };
                deleteProjectItem.Click += OnDeleteProjectMenuClicked;
                projectMenu.Items.Add(deleteProjectItem);
                projectButton.ContextFlyout = projectMenu;
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
                AutomationProperties.SetName(addThreadButton, $"Add thread to {project.Name}");
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
                AutomationProperties.SetAutomationId(projectHeader, $"shell-project-header-{project.Id}");
                projectHeader.Children.Add(projectButton);
                Grid.SetColumn(addThreadButton, 1);
                projectHeader.Children.Add(addThreadButton);
                group.Children.Add(projectHeader);

                if (showProjectThreads)
                {
                    StackPanel threadStack = new()
                    {
                        Spacing = 2,
                        Margin = new Thickness(18, 0, 0, 0),
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                    };
                    AutomationProperties.SetAutomationId(threadStack, $"shell-thread-list-{project.Id}");

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
                        string threadLocation = string.IsNullOrWhiteSpace(thread.BranchName)
                            ? ShellProfiles.DeriveName(thread.WorktreePath ?? project.RootPath)
                            : $"{thread.BranchName} · {ShellProfiles.DeriveName(thread.WorktreePath ?? project.RootPath)}";
                        AutomationProperties.SetName(threadButton, $"{thread.Name} {threadLocation}");
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
                            Text = "Clear thread",
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
                        AutomationProperties.SetAutomationId(threadLayout, $"shell-thread-layout-{thread.Id}");
                        threadLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        threadLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                        FontIcon threadIcon = new()
                        {
                            FontSize = 10,
                            Glyph = "\uE8BD",
                            VerticalAlignment = VerticalAlignment.Center,
                        };
                        AutomationProperties.SetAutomationId(threadIcon, $"shell-thread-icon-{thread.Id}");
                        threadLayout.Children.Add(threadIcon);

                        StackPanel threadText = new()
                        {
                            Spacing = 0,
                        };
                        AutomationProperties.SetAutomationId(threadText, $"shell-thread-text-{thread.Id}");
                        Grid.SetColumn(threadText, 1);
                        TextBlock threadTitle = new()
                        {
                            Text = thread.Name,
                            FontSize = 12,
                            TextWrapping = TextWrapping.NoWrap,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                        };
                        AutomationProperties.SetAutomationId(threadTitle, $"shell-thread-title-{thread.Id}");
                        threadText.Children.Add(threadTitle);
                        TextBlock threadMeta = new()
                        {
                            Text = $"{threadLocation} · {thread.TabSummary}",
                            Style = (Style)Application.Current.Resources["ShellHintTextStyle"],
                            TextWrapping = TextWrapping.NoWrap,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                        };
                        AutomationProperties.SetAutomationId(threadMeta, $"shell-thread-meta-{thread.Id}");
                        threadText.Children.Add(threadMeta);

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
                }
                ProjectListPanel.Children.Add(group);
            }
        }

        private void RefreshTabView()
        {
            if (_activeThread is null)
            {
                bool previousSuppressSelection = _suppressTabSelectionChanged;
                _refreshingTabView = true;
                _suppressTabSelectionChanged = true;
                try
                {
                    if (TerminalTabs.TabItems.Count > 0)
                    {
                        TerminalTabs.TabItems.Clear();
                    }
                }
                finally
                {
                    _suppressTabSelectionChanged = previousSuppressSelection;
                    _refreshingTabView = false;
                }

                UpdateWorkspaceVisibility();
                return;
            }

            List<TabViewItem> desiredItems = _activeThread.Panes
                .Select(GetOrCreateTabViewItem)
                .ToList();

            bool previousSuppression = _suppressTabSelectionChanged;
            _refreshingTabView = true;
            _suppressTabSelectionChanged = true;
            try
            {
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
                    TerminalTabs.SelectedItem = selectedItem;
                }

                if (selectedItem?.Tag is WorkspacePaneRecord selectedPane)
                {
                    _activeThread.SelectedPaneId = selectedPane.Id;
                }
            }
            finally
            {
                _suppressTabSelectionChanged = previousSuppression;
                _refreshingTabView = false;
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
                item.DoubleTapped += OnPaneTabDoubleTapped;
                _tabItemsById[pane.Id] = item;
            }

            item.Tag = pane;
            AutomationProperties.SetName(item, FormatTabHeader(pane.Title, pane.Kind, pane.IsExited));
            item.ContextFlyout = BuildPaneContextMenu(pane);
            object nextHeader = BuildPaneTabHeader(pane);
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
            item.ContextFlyout = BuildPaneContextMenu(pane);
            object nextHeader = BuildPaneTabHeader(pane);
            if (!Equals(item.Header, nextHeader))
            {
                item.Header = nextHeader;
            }
        }

        private MenuFlyout BuildPaneContextMenu(WorkspacePaneRecord pane)
        {
            MenuFlyout menu = new();
            MenuFlyoutItem renameItem = new()
            {
                Text = "Rename",
                Tag = pane.Id,
            };
            AutomationProperties.SetAutomationId(renameItem, $"shell-tab-rename-{pane.Id}");
            renameItem.Click += OnRenamePaneMenuClicked;
            menu.Items.Add(renameItem);
            return menu;
        }

        private object BuildPaneTabHeader(WorkspacePaneRecord pane)
        {
            if (pane is null)
            {
                return string.Empty;
            }

            if (!string.Equals(_inlineRenamingPaneId, pane.Id, StringComparison.Ordinal))
            {
                string title = FormatTabHeader(pane.Title, pane.Kind, pane.IsExited);
                Grid header = new()
                {
                    ColumnSpacing = 6,
                };
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                AutomationProperties.SetAutomationId(header, $"shell-tab-header-{pane.Id}");

                TextBlock kindText = new()
                {
                    Text = pane.Kind switch
                    {
                        WorkspacePaneKind.Browser => "Web",
                        WorkspacePaneKind.Editor => "Edit",
                        WorkspacePaneKind.Diff => "Diff",
                        _ => "Term",
                    },
                    FontSize = 10,
                    Opacity = 0.72,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                AutomationProperties.SetAutomationId(kindText, $"shell-tab-kind-{pane.Id}");
                header.Children.Add(kindText);

                TextBlock titleText = new()
                {
                    Text = title,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                AutomationProperties.SetAutomationId(titleText, $"shell-tab-title-{pane.Id}");
                Grid.SetColumn(titleText, 1);
                header.Children.Add(titleText);
                return header;
            }

            TextBox editor = new()
            {
                Text = pane.Title,
                MinWidth = 132,
                MaxWidth = 220,
                Padding = new Thickness(8, 2, 8, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)Application.Current.Resources["ShellInlineTextBoxStyle"],
            };
            AutomationProperties.SetAutomationId(editor, $"shell-tab-rename-editor-{pane.Id}");
            editor.KeyDown += (_, args) =>
            {
                switch (args.Key)
                {
                    case Windows.System.VirtualKey.Enter:
                        args.Handled = true;
                        CommitInlinePaneRename(pane.Id, editor.Text);
                        break;
                    case Windows.System.VirtualKey.Escape:
                        args.Handled = true;
                        CancelInlinePaneRename(pane.Id);
                        break;
                }
            };
            editor.LostFocus += (_, _) => CommitInlinePaneRename(pane.Id, editor.Text);
            return editor;
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
            string renderKey = BuildPaneWorkspaceRenderKey();
            if (string.Equals(renderKey, _lastPaneWorkspaceRenderKey, StringComparison.Ordinal))
            {
                UpdatePaneSelectionChrome();
                return;
            }

            _lastPaneWorkspaceRenderKey = renderKey;
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
                    Child = pane.View,
                    Margin = new Thickness(1),
                    Tag = pane,
                };
                AutomationProperties.SetAutomationId(border, $"shell-pane-{pane.Id}");
                border.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPaneContainerPointerPressed), true);
                _paneContainersById[pane.Id] = border;
            }

            border.Background = AppBrush(border, "ShellSurfaceBackgroundBrush");
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
                Background = AppBrush(PaneWorkspaceGrid, "ShellPaneDividerBrush"),
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
                Background = AppBrush(PaneWorkspaceGrid, "ShellPaneDividerBrush"),
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

            QueueProjectTreeRefresh();
            QueueSessionSave();
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
                bool focusPane = !ShouldDeferPaneFocus(e.OriginalSource as DependencyObject);
                SelectPane(pane, focusPane);
            }
        }

        private static bool ShouldDeferPaneFocus(DependencyObject source)
        {
            DependencyObject current = source;
            while (current is not null)
            {
                switch (current)
                {
                    case Button:
                    case HyperlinkButton:
                    case TextBox:
                    case AutoSuggestBox:
                    case Microsoft.UI.Xaml.Controls.WebView2:
                        return true;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return false;
        }

        private void SelectPane(WorkspacePaneRecord pane, bool focusPane = true)
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
            if (focusPane)
            {
                FocusSelectedPane();
            }
            RequestLayoutForVisiblePanes();
            LogAutomationEvent("shell", "pane.selected", $"Selected pane {pane.Id}", new Dictionary<string, string>
            {
                ["paneId"] = pane.Id,
                ["paneKind"] = pane.Kind.ToString().ToLowerInvariant(),
                ["threadId"] = _activeThread.Id,
                ["projectId"] = _activeProject?.Id ?? string.Empty,
                ["focusPane"] = focusPane.ToString(),
            });
        }

        private void UpdatePaneSelectionChrome()
        {
            WorkspacePaneRecord selectedPane = GetSelectedPane(_activeThread);
            foreach ((string paneId, Border border) in _paneContainersById)
            {
                bool isSelected = string.Equals(selectedPane?.Id, paneId, StringComparison.Ordinal);
                border.Background = AppBrush(border, "ShellSurfaceBackgroundBrush");
                border.BorderBrush = AppBrush(border, isSelected
                    ? "ShellPaneActiveBorderBrush"
                    : "ShellBorderBrush");
                border.BorderThickness = isSelected ? new Thickness(2) : new Thickness(1);
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
            QueueSessionSave();
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
            QueueActiveThreadGitRefresh(_activeThread?.SelectedDiffPath ?? _activeGitSnapshot?.SelectedPath, preserveSelection: true);
            QueueSessionSave();
            LogAutomationEvent("shell", "view.terminal", _activeThread is null ? "Showing empty project state" : "Showing pane workspace", new Dictionary<string, string>
            {
                ["projectId"] = _activeProject?.Id ?? string.Empty,
                ["threadId"] = _activeThread?.Id ?? string.Empty,
            });
        }

        private async System.Threading.Tasks.Task RefreshActiveThreadGitStateAsync(string selectedPath = null, bool preserveSelection = false)
        {
            if (_activeThread is null)
            {
                _activeGitSnapshot = null;
                ApplyGitSnapshotToUi();
                return;
            }

            WorkspaceThread thread = _activeThread;
            WorkspaceProject project = _activeProject;
            string targetPath = ResolveSelectedDiffPathForRefresh(thread, selectedPath, preserveSelection);
            int requestId = ++_latestGitRefreshRequestId;
            string worktreePath = thread.WorktreePath ?? project?.RootPath;

            GitThreadSnapshot snapshot = await System.Threading.Tasks.Task
                .Run(() => GitStatusService.Capture(worktreePath, targetPath))
                .ConfigureAwait(true);

            if (requestId != _latestGitRefreshRequestId ||
                !ReferenceEquals(thread, _activeThread) ||
                !ReferenceEquals(project, _activeProject))
            {
                return;
            }

            ApplyActiveGitSnapshot(snapshot);
        }

        private void ApplyActiveGitSnapshot(GitThreadSnapshot snapshot)
        {
            _activeGitSnapshot = snapshot;
            _activeThread.BranchName = snapshot.BranchName;
            _activeThread.WorktreePath = string.IsNullOrWhiteSpace(_activeThread.WorktreePath) ? snapshot.WorktreePath : _activeThread.WorktreePath;
            _activeThread.ChangedFileCount = snapshot.ChangedFiles.Count;
            _activeThread.SelectedDiffPath = snapshot.SelectedPath;
            ApplyGitSnapshotToUi();
            QueueProjectTreeRefresh();
            LogAutomationEvent("git", "thread.snapshot_refreshed", string.IsNullOrWhiteSpace(snapshot.Error) ? "Refreshed thread git snapshot" : snapshot.Error, new Dictionary<string, string>
            {
                ["threadId"] = _activeThread.Id,
                ["projectId"] = _activeProject?.Id ?? string.Empty,
                ["branch"] = snapshot.BranchName ?? string.Empty,
                ["worktreePath"] = snapshot.WorktreePath ?? string.Empty,
                ["changedFileCount"] = snapshot.ChangedFiles.Count.ToString(),
                ["selectedPath"] = snapshot.SelectedPath ?? string.Empty,
            });
        }

        private void ApplyGitSnapshotToUi()
        {
            if (_activeGitSnapshot is null)
            {
                DiffBranchText.Text = "No git context";
                DiffWorktreeText.Text = string.Empty;
                DiffSummaryText.Text = "No working tree changes";
                PopulateDiffFileList(null, null);
                if (_activeThread?.Panes.OfType<DiffPaneRecord>().FirstOrDefault() is DiffPaneRecord emptyDiffPane)
                {
                    UpdateDiffPane(emptyDiffPane, emptyDiffPane.DiffPath, null);
                }
                return;
            }

            int totalAddedLines = _activeGitSnapshot.ChangedFiles.Sum(file => file.AddedLines);
            int totalRemovedLines = _activeGitSnapshot.ChangedFiles.Sum(file => file.RemovedLines);
            DiffBranchText.Text = string.IsNullOrWhiteSpace(_activeGitSnapshot.BranchName)
                ? "Git metadata unavailable"
                : _activeGitSnapshot.BranchName;
            DiffWorktreeText.Text = _activeGitSnapshot.WorktreePath ?? string.Empty;
            DiffSummaryText.Text = string.IsNullOrWhiteSpace(_activeGitSnapshot.Error)
                ? FormatGitSummary(_activeGitSnapshot.StatusSummary, totalAddedLines, totalRemovedLines)
                : _activeGitSnapshot.Error;
            PopulateDiffFileList(_activeGitSnapshot.ChangedFiles, _activeGitSnapshot.SelectedPath);

            if (_activeThread?.Panes.OfType<DiffPaneRecord>().FirstOrDefault() is DiffPaneRecord diffPane)
            {
                bool hasLiveSelectedDiff = !string.IsNullOrWhiteSpace(_activeGitSnapshot.SelectedPath) &&
                    _activeGitSnapshot.ChangedFiles.Any(file => string.Equals(file.Path, _activeGitSnapshot.SelectedPath, StringComparison.Ordinal));
                UpdateDiffPane(diffPane, hasLiveSelectedDiff ? _activeGitSnapshot.SelectedPath : null, hasLiveSelectedDiff ? _activeGitSnapshot.SelectedDiff : null);
            }
        }

        private void PopulateDiffFileList(IReadOnlyList<GitChangedFile> changedFiles, string selectedPath)
        {
            DiffFileListPanel.Children.Clear();
            IReadOnlyList<GitChangedFile> files = changedFiles ?? Array.Empty<GitChangedFile>();
            DiffEmptyText.Visibility = files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            foreach (GitChangedFile changedFile in files)
            {
                DiffFileListPanel.Children.Add(BuildDiffFileButton(changedFile));
            }

            if (_activeGitSnapshot is not null)
            {
                _activeGitSnapshot.SelectedPath = selectedPath;
            }

            if (_activeThread is not null)
            {
                _activeThread.SelectedDiffPath = selectedPath;
            }

            UpdateDiffFileSelection();
        }

        private string ResolveSelectedDiffPathForRefresh(WorkspaceThread thread, string selectedPath = null, bool preserveSelection = false)
        {
            if (!preserveSelection)
            {
                return selectedPath;
            }

            if (!string.IsNullOrWhiteSpace(selectedPath))
            {
                return selectedPath;
            }

            if (ReferenceEquals(thread, _activeThread) && !string.IsNullOrWhiteSpace(_activeGitSnapshot?.SelectedPath))
            {
                return _activeGitSnapshot.SelectedPath;
            }

            return thread?.SelectedDiffPath;
        }

        private Button BuildDiffFileButton(GitChangedFile changedFile)
        {
            Button button = new()
            {
                Style = (Style)Application.Current.Resources["ShellButtonBaseStyle"],
                Tag = changedFile,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(8, 4, 8, 4),
                MinHeight = 34,
            };
            button.Click += OnDiffFileButtonClicked;
            AutomationProperties.SetAutomationId(button, $"shell-diff-file-{BuildAutomationKey(changedFile.Path)}");
            AutomationProperties.SetName(button, changedFile.DisplayName);

            Grid layout = new()
            {
                ColumnSpacing = 8,
            };
            AutomationProperties.SetAutomationId(layout, $"shell-diff-file-layout-{BuildAutomationKey(changedFile.Path)}");
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            TextBlock statusText = new()
            {
                Text = ResolveGitStatusSymbol(changedFile.Status),
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Width = 14,
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalTextAlignment = TextAlignment.Center,
                Foreground = AppBrush(button, ResolveGitStatusBrushKey(changedFile.Status)),
            };
            AutomationProperties.SetAutomationId(statusText, $"shell-diff-file-status-{BuildAutomationKey(changedFile.Path)}");
            layout.Children.Add(statusText);

            StackPanel textStack = new()
            {
                Spacing = 0,
            };
            AutomationProperties.SetAutomationId(textStack, $"shell-diff-file-text-{BuildAutomationKey(changedFile.Path)}");
            Grid.SetColumn(textStack, 1);

            TextBlock fileNameText = new()
            {
                Text = changedFile.DisplayName,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            AutomationProperties.SetAutomationId(fileNameText, $"shell-diff-file-name-{BuildAutomationKey(changedFile.Path)}");
            textStack.Children.Add(fileNameText);

            TextBlock fileMetaText = new()
            {
                Text = ResolveGitStatusDescription(changedFile.Status),
                FontSize = 11,
                Opacity = 0.74,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            AutomationProperties.SetAutomationId(fileMetaText, $"shell-diff-file-meta-{BuildAutomationKey(changedFile.Path)}");
            textStack.Children.Add(fileMetaText);

            layout.Children.Add(textStack);

            StackPanel metricsStack = new()
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Top,
            };
            AutomationProperties.SetAutomationId(metricsStack, $"shell-diff-file-metrics-{BuildAutomationKey(changedFile.Path)}");
            Grid.SetColumn(metricsStack, 2);

            if (changedFile.AddedLines > 0)
            {
                metricsStack.Children.Add(new TextBlock
                {
                    Text = $"+{changedFile.AddedLines}",
                    FontSize = 11,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = AppBrush(button, "ShellSuccessBrush"),
                });
            }

            if (changedFile.RemovedLines > 0)
            {
                metricsStack.Children.Add(new TextBlock
                {
                    Text = $"-{changedFile.RemovedLines}",
                    FontSize = 11,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = AppBrush(button, "ShellDangerBrush"),
                });
            }

            if (metricsStack.Children.Count > 0)
            {
                layout.Children.Add(metricsStack);
            }

            button.Content = layout;
            return button;
        }

        private void UpdateDiffFileSelection()
        {
            string selectedPath = _activeGitSnapshot?.SelectedPath;
            foreach (Button button in DiffFileListPanel.Children.OfType<Button>())
            {
                GitChangedFile changedFile = ResolveSelectedDiffFile(button);
                ApplyDiffFileButtonState(button, string.Equals(changedFile?.Path, selectedPath, StringComparison.Ordinal));
            }
        }

        private static string FormatGitSummary(string statusSummary, int totalAddedLines, int totalRemovedLines)
        {
            List<string> parts = new()
            {
                string.IsNullOrWhiteSpace(statusSummary) ? "No working tree changes" : statusSummary,
            };

            if (totalAddedLines > 0)
            {
                parts.Add($"+{totalAddedLines}");
            }

            if (totalRemovedLines > 0)
            {
                parts.Add($"-{totalRemovedLines}");
            }

            return string.Join(" · ", parts);
        }

        private static string BuildAutomationKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "empty";
            }

            StringBuilder builder = new();
            foreach (char character in value.Trim().ToLowerInvariant())
            {
                if ((character >= 'a' && character <= 'z') || (character >= '0' && character <= '9'))
                {
                    builder.Append(character);
                }
                else if (builder.Length == 0 || builder[^1] != '-')
                {
                    builder.Append('-');
                }
            }

            string result = builder.ToString().Trim('-');
            if (string.IsNullOrWhiteSpace(result))
            {
                return "item";
            }

            return result.Length > 48 ? result[..48] : result;
        }

        private void FocusSelectedPane()
        {
            if (TerminalTabs.SelectedItem is TabViewItem item && item.Tag is WorkspacePaneRecord pane)
            {
                bool previousSuppression = _suppressPaneInteractionRequests;
                _suppressPaneInteractionRequests = true;
                try
                {
                    pane.FocusPane();
                }
                finally
                {
                    _suppressPaneInteractionRequests = previousSuppression;
                }
            }
        }

        private void RequestLayoutForVisiblePanes()
        {
            foreach (WorkspacePaneRecord pane in GetVisiblePanes(_activeThread))
            {
                pane.RequestLayout();
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

        private void ToggleInspector()
        {
            _inspectorOpen = !_inspectorOpen;
            UpdateInspectorVisibility();
            _lastPaneWorkspaceRenderKey = null;
            QueueSessionSave();
            LogAutomationEvent("shell", "inspector.toggled", _inspectorOpen ? "Inspector opened" : "Inspector collapsed", new Dictionary<string, string>
            {
                ["inspectorOpen"] = _inspectorOpen.ToString(),
            });
        }

        private void UpdateInspectorVisibility()
        {
            if (InspectorColumn is null || InspectorSidebar is null || ToggleInspectorButton is null)
            {
                return;
            }

            bool showInspector = _inspectorOpen && !_showingSettings && _activeThread is not null;

            InspectorColumn.Width = showInspector
                ? new GridLength(320)
                : new GridLength(0);
            InspectorSidebar.Visibility = showInspector ? Visibility.Visible : Visibility.Collapsed;

            ToolTipService.SetToolTip(ToggleInspectorButton, _inspectorOpen ? "Hide inspector" : "Show inspector");
            if (ToggleInspectorButton.Content is FontIcon icon)
            {
                icon.Glyph = _inspectorOpen ? "\uE7F8" : "\uE7F7";
            }
        }

        private void UpdatePaneLayout()
        {
            bool isOpen = ShellSplitView.IsPaneOpen;

            PaneBrandText.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
            ProjectSectionText.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
            NewProjectText.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
            SettingsNavText.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
            _lastProjectTreeRenderKey = null;

            ToolTipService.SetToolTip(PaneToggleButton, isOpen ? "Collapse sidebar" : "Expand sidebar");
            QueueProjectTreeRefresh();
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
                ActiveDirectoryText.Text = _activeProject is null ? string.Empty : FormatThreadPath(_activeProject, _activeThread);
            }
            finally
            {
                _suppressThreadNameSync = false;
            }
        }

        private void SetThreadWorktree(string threadId, string requestedPath)
        {
            WorkspaceThread thread = FindThread(threadId) ?? _activeThread;
            if (thread is null)
            {
                return;
            }

            string normalizedPath = string.IsNullOrWhiteSpace(requestedPath)
                ? thread.Project.RootPath
                : ResolveRequestedPath(requestedPath);

            thread.WorktreePath = normalizedPath;
            thread.BranchName = null;
            thread.ChangedFileCount = 0;
            thread.SelectedDiffPath = null;

            if (thread == _activeThread)
            {
                UpdateHeader();
                QueueActiveThreadGitRefresh();
            }

            QueueProjectTreeRefresh();
            QueueSessionSave();
            LogAutomationEvent("shell", "thread.worktree.changed", $"Set thread worktree to {normalizedPath}", new Dictionary<string, string>
            {
                ["threadId"] = thread.Id,
                ["projectId"] = thread.Project.Id,
                ["worktreePath"] = normalizedPath,
            });
        }

        private void UpdateWorkspaceVisibility()
        {
            if (_showingSettings)
            {
                SettingsFrame.Visibility = Visibility.Visible;
                PaneWorkspaceShell.Visibility = Visibility.Collapsed;
                EmptyThreadStatePanel.Visibility = Visibility.Collapsed;
                UpdateInspectorVisibility();
                return;
            }

            SettingsFrame.Visibility = Visibility.Collapsed;

            bool showEmptyState = _activeProject is not null && _activeThread is null;
            PaneWorkspaceShell.Visibility = showEmptyState ? Visibility.Collapsed : Visibility.Visible;
            EmptyThreadStatePanel.Visibility = showEmptyState ? Visibility.Visible : Visibility.Collapsed;
            UpdateInspectorVisibility();
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

        private IEnumerable<(WorkspaceProject Project, WorkspaceThread Thread, BrowserPaneRecord Pane)> EnumerateBrowserRecords()
        {
            return _projects.SelectMany(project => project.Threads.SelectMany(thread => thread.Panes.OfType<BrowserPaneRecord>().Select(pane => (project, thread, pane))));
        }

        private IEnumerable<(WorkspaceProject Project, WorkspaceThread Thread, DiffPaneRecord Pane)> EnumerateDiffRecords()
        {
            return _projects.SelectMany(project => project.Threads.SelectMany(thread => thread.Panes.OfType<DiffPaneRecord>().Select(pane => (project, thread, pane))));
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
            if (ShouldTraverseUiNodeChildren(node))
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

        private static bool ShouldTraverseUiNodeChildren(DependencyObject node)
        {
            return node switch
            {
                Microsoft.UI.Xaml.Controls.WebView2 => false,
                TextBox => false,
                PasswordBox => false,
                RichEditBox => false,
                _ => true,
            };
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

            if (automationId.StartsWith("shell-tab-", StringComparison.Ordinal))
            {
                BeginInlinePaneRename(automationId["shell-tab-".Length..]);
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

            if (string.Equals(automationId, "shell-toggle-inspector", StringComparison.Ordinal))
            {
                ToggleInspector();
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
            if (menuItem?.Tag is not string targetId)
            {
                return false;
            }

            string automationId = AutomationProperties.GetAutomationId(menuItem) ?? string.Empty;
            string action = menuItem.Text?.Trim().ToLowerInvariant();
            if (automationId.StartsWith("shell-tab-rename-", StringComparison.Ordinal))
            {
                switch (action)
                {
                    case "rename":
                        BeginInlinePaneRename(targetId);
                        return true;
                    default:
                        return false;
                }
            }

            switch (action)
            {
                case "rename":
                    _ = BeginRenameThreadAsync(targetId);
                    return true;
                case "duplicate":
                    DuplicateThread(targetId);
                    return true;
                case "clear thread":
                case "delete":
                    DeleteThread(targetId);
                    return true;
                default:
                    return false;
            }
        }

        private bool TryInvokeThreadMenuAction(string threadId, string menuItemText)
        {
            if (string.IsNullOrWhiteSpace(threadId))
            {
                return false;
            }

            string action = menuItemText?.Trim().ToLowerInvariant();
            switch (action)
            {
                case "rename":
                    _ = BeginRenameThreadAsync(threadId);
                    return true;
                case "duplicate":
                    DuplicateThread(threadId);
                    return true;
                case "clear thread":
                case "delete":
                    DeleteThread(threadId);
                    return true;
                default:
                    return false;
            }
        }

        private bool TryInvokeProjectMenuAction(string projectId, string menuItemText)
        {
            if (string.IsNullOrWhiteSpace(projectId))
            {
                return false;
            }

            string action = menuItemText?.Trim().ToLowerInvariant();
            switch (action)
            {
                case "new thread":
                    ActivateThread(CreateThread(FindProject(projectId)));
                    ShowTerminalShell();
                    return true;
                case "clear all threads":
                    ClearProjectThreads(projectId);
                    ShowTerminalShell();
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
                WorktreePath = thread.WorktreePath,
                BranchName = thread.BranchName,
                SelectedDiffPath = thread.SelectedDiffPath,
                SelectedTabId = thread.SelectedPaneId,
                TabCount = thread.Panes.Count,
                PaneCount = thread.Panes.Count,
                Layout = thread.LayoutPreset.ToString().ToLowerInvariant(),
                PaneLimit = thread.PaneLimit,
                VisiblePaneCapacity = thread.VisiblePaneCapacity,
                PrimarySplitRatio = thread.PrimarySplitRatio,
                SecondarySplitRatio = thread.SecondarySplitRatio,
                ChangedFileCount = thread.ChangedFileCount,
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

        private static Brush AppBrush(FrameworkElement element, string key)
        {
            ElementTheme effectiveTheme = SampleConfig.CurrentTheme == ElementTheme.Default
                ? (Current?.ActualTheme == ElementTheme.Light ? ElementTheme.Light : ElementTheme.Dark)
                : SampleConfig.CurrentTheme;

            Windows.UI.Color color = (effectiveTheme, key) switch
            {
                (ElementTheme.Light, "ShellPageBackgroundBrush") => Windows.UI.Color.FromArgb(0xFF, 0xFA, 0xFA, 0xFA),
                (ElementTheme.Light, "ShellPaneBackgroundBrush") => Windows.UI.Color.FromArgb(0xFF, 0xF4, 0xF4, 0xF5),
                (ElementTheme.Light, "ShellSurfaceBackgroundBrush") => Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
                (ElementTheme.Light, "ShellPaneDividerBrush") => Windows.UI.Color.FromArgb(0xFF, 0xE5, 0xE7, 0xEB),
                (ElementTheme.Light, "ShellNavActiveBrush") => Windows.UI.Color.FromArgb(0xFF, 0xE7, 0xE7, 0xEB),
                (ElementTheme.Light, "ShellBorderBrush") => Windows.UI.Color.FromArgb(0xFF, 0xE4, 0xE4, 0xE7),
                (ElementTheme.Light, "ShellPaneActiveBorderBrush") => Windows.UI.Color.FromArgb(0xFF, 0x25, 0x63, 0xEB),
                (ElementTheme.Light, "ShellTextPrimaryBrush") => Windows.UI.Color.FromArgb(0xFF, 0x18, 0x18, 0x1B),
                (ElementTheme.Light, "ShellTextSecondaryBrush") => Windows.UI.Color.FromArgb(0xFF, 0x52, 0x52, 0x5B),
                (ElementTheme.Light, "ShellTextTertiaryBrush") => Windows.UI.Color.FromArgb(0xFF, 0x71, 0x71, 0x7A),
                (ElementTheme.Light, "ShellSuccessBrush") => Windows.UI.Color.FromArgb(0xFF, 0x16, 0xA3, 0x4A),
                (ElementTheme.Light, "ShellWarningBrush") => Windows.UI.Color.FromArgb(0xFF, 0xCA, 0x8A, 0x04),
                (ElementTheme.Light, "ShellDangerBrush") => Windows.UI.Color.FromArgb(0xFF, 0xDC, 0x26, 0x26),
                (ElementTheme.Light, "ShellInfoBrush") => Windows.UI.Color.FromArgb(0xFF, 0x25, 0x63, 0xEB),
                (ElementTheme.Dark, "ShellPageBackgroundBrush") => Windows.UI.Color.FromArgb(0xFF, 0x09, 0x09, 0x0B),
                (ElementTheme.Dark, "ShellPaneBackgroundBrush") => Windows.UI.Color.FromArgb(0xFF, 0x0E, 0x10, 0x13),
                (ElementTheme.Dark, "ShellSurfaceBackgroundBrush") => Windows.UI.Color.FromArgb(0xFF, 0x11, 0x12, 0x14),
                (ElementTheme.Dark, "ShellPaneDividerBrush") => Windows.UI.Color.FromArgb(0xFF, 0x20, 0x23, 0x2A),
                (ElementTheme.Dark, "ShellNavActiveBrush") => Windows.UI.Color.FromArgb(0xFF, 0x1F, 0x22, 0x28),
                (ElementTheme.Dark, "ShellBorderBrush") => Windows.UI.Color.FromArgb(0xFF, 0x23, 0x25, 0x2B),
                (ElementTheme.Dark, "ShellPaneActiveBorderBrush") => Windows.UI.Color.FromArgb(0xFF, 0x60, 0xA5, 0xFA),
                (ElementTheme.Dark, "ShellTextPrimaryBrush") => Windows.UI.Color.FromArgb(0xFF, 0xFA, 0xFA, 0xFA),
                (ElementTheme.Dark, "ShellTextSecondaryBrush") => Windows.UI.Color.FromArgb(0xFF, 0xA1, 0xA1, 0xAA),
                (ElementTheme.Dark, "ShellTextTertiaryBrush") => Windows.UI.Color.FromArgb(0xFF, 0x71, 0x71, 0x7A),
                (ElementTheme.Dark, "ShellSuccessBrush") => Windows.UI.Color.FromArgb(0xFF, 0x4A, 0xDE, 0x80),
                (ElementTheme.Dark, "ShellWarningBrush") => Windows.UI.Color.FromArgb(0xFF, 0xFB, 0xBF, 0x24),
                (ElementTheme.Dark, "ShellDangerBrush") => Windows.UI.Color.FromArgb(0xFF, 0xF8, 0x71, 0x71),
                (ElementTheme.Dark, "ShellInfoBrush") => Windows.UI.Color.FromArgb(0xFF, 0x60, 0xA5, 0xFA),
                _ => default,
            };

            if (color != default)
            {
                return new SolidColorBrush(color);
            }

            return (Brush)Application.Current.Resources[key];
        }

        private static GitChangedFile ResolveSelectedDiffFile(object selectedItem)
        {
            return selectedItem switch
            {
                GitChangedFile changedFile => changedFile,
                Button button when button.Tag is GitChangedFile changedFile => changedFile,
                ListViewItem item when item.Tag is GitChangedFile changedFile => changedFile,
                _ => null,
            };
        }

        private static void ApplyDiffFileButtonState(Button button, bool active)
        {
            button.Background = active ? AppBrush(button, "ShellNavActiveBrush") : null;
            button.BorderBrush = active ? AppBrush(button, "ShellBorderBrush") : null;
            button.Foreground = active ? AppBrush(button, "ShellTextPrimaryBrush") : AppBrush(button, "ShellTextSecondaryBrush");
        }

        private static string ResolveGitStatusSymbol(string status)
        {
            status = status?.Trim() ?? string.Empty;
            if (status.IndexOf('A') >= 0 || status == "??")
            {
                return "+";
            }

            if (status.IndexOf('D') >= 0)
            {
                return "-";
            }

            if (status.IndexOf('R') >= 0)
            {
                return "R";
            }

            return "~";
        }

        private static string ResolveGitStatusDescription(string status)
        {
            status = status?.Trim() ?? string.Empty;
            if (status.IndexOf('A') >= 0 || status == "??")
            {
                return "Added";
            }

            if (status.IndexOf('D') >= 0)
            {
                return "Deleted";
            }

            if (status.IndexOf('R') >= 0)
            {
                return "Renamed";
            }

            return "Modified";
        }

        private static string ResolveGitStatusBrushKey(string status)
        {
            status = status?.Trim() ?? string.Empty;
            if (status.IndexOf('A') >= 0 || status == "??")
            {
                return "ShellSuccessBrush";
            }

            if (status.IndexOf('D') >= 0)
            {
                return "ShellDangerBrush";
            }

            if (status.IndexOf('R') >= 0)
            {
                return "ShellInfoBrush";
            }

            return "ShellWarningBrush";
        }

        private static void ApplyActionButtonState(Button button, TextBlock label, bool active)
        {
            button.Background = active ? AppBrush(button, "ShellNavActiveBrush") : null;
            button.BorderBrush = active ? AppBrush(button, "ShellBorderBrush") : null;
            button.Foreground = active ? AppBrush(button, "ShellTextPrimaryBrush") : AppBrush(button, "ShellTextSecondaryBrush");
            label.Foreground = active ? AppBrush(label, "ShellTextPrimaryBrush") : AppBrush(label, "ShellTextSecondaryBrush");
        }

        private static void ApplyProjectButtonState(Button button, bool active)
        {
            button.Background = active ? AppBrush(button, "ShellNavActiveBrush") : null;
            button.BorderBrush = active ? AppBrush(button, "ShellBorderBrush") : null;
            button.Foreground = active ? AppBrush(button, "ShellTextPrimaryBrush") : AppBrush(button, "ShellTextSecondaryBrush");
        }

        private static void ApplyThreadButtonState(Button button, bool active)
        {
            button.Background = active ? AppBrush(button, "ShellNavActiveBrush") : null;
            button.BorderBrush = active ? AppBrush(button, "ShellBorderBrush") : null;
            button.Foreground = active ? AppBrush(button, "ShellTextPrimaryBrush") : AppBrush(button, "ShellTextSecondaryBrush");
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

        private string BuildProjectTreeRenderKey()
        {
            StringBuilder builder = new();
            builder.Append(ShellSplitView?.IsPaneOpen == true ? '1' : '0')
                .Append('|')
                .Append(_showingSettings ? '1' : '0')
                .Append('|')
                .Append(_activeProject?.Id)
                .Append('|')
                .Append(_activeThread?.Id)
                .Append('|');

            foreach (WorkspaceProject project in _projects)
            {
                builder.Append(project.Id)
                    .Append(':')
                    .Append(project.Name)
                    .Append(':')
                    .Append(project.SelectedThreadId)
                    .Append(':')
                    .Append(project.Threads.Count)
                    .Append(':')
                    .Append(project.RootPath)
                    .Append('|');

                if (!ReferenceEquals(project, _activeProject))
                {
                    continue;
                }

                foreach (WorkspaceThread thread in project.Threads)
                {
                    builder.Append(thread.Id)
                        .Append(':')
                        .Append(thread.Name)
                        .Append(':')
                        .Append(thread.TabSummary)
                        .Append(':')
                        .Append(thread.BranchName)
                        .Append(':')
                        .Append(thread.WorktreePath)
                        .Append('|');
                }
            }

            return builder.ToString();
        }

        private string BuildPaneWorkspaceRenderKey()
        {
            StringBuilder builder = new();
            builder.Append(_showingSettings ? '1' : '0')
                .Append('|')
                .Append(_inspectorOpen ? '1' : '0')
                .Append('|')
                .Append(_activeThread?.Id)
                .Append('|');

            if (_activeThread is null)
            {
                return builder.ToString();
            }

            builder.Append(_activeThread.SelectedPaneId)
                .Append('|')
                .Append(_activeThread.LayoutPreset)
                .Append('|')
                .Append(_activeThread.PrimarySplitRatio.ToString("0.000"))
                .Append('|')
                .Append(_activeThread.SecondarySplitRatio.ToString("0.000"))
                .Append('|');

            foreach (WorkspacePaneRecord pane in GetVisiblePanes(_activeThread))
            {
                builder.Append(pane.Id)
                    .Append(':')
                    .Append(pane.Title)
                    .Append(':')
                    .Append(pane.Kind)
                    .Append(':')
                    .Append(pane.IsExited ? '1' : '0')
                    .Append('|');
            }

            return builder.ToString();
        }

        private static bool ShouldPersistProject(WorkspaceProject project)
        {
            return project is not null && ShouldPersistProjectPath(project.RootPath);
        }

        private static bool ShouldPersistProjectPath(string rootPath)
        {
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                return false;
            }

            string normalizedRoot = ShellProfiles.NormalizeProjectPath(rootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string leafName = Path.GetFileName(normalizedRoot);
            return !leafName.StartsWith("winmux-smoke-", StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolveThreadRootPath(WorkspaceProject project, WorkspaceThread thread)
        {
            string rootPath = thread?.WorktreePath;
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                rootPath = project?.RootPath;
            }

            return ShellProfiles.NormalizeProjectPath(rootPath ?? Environment.CurrentDirectory);
        }

        private static string FormatThreadPath(WorkspaceProject project, WorkspaceThread thread)
        {
            string rootPath = ResolveThreadRootPath(project, thread);
            string profileId = project?.ShellProfileId ?? SampleConfig.DefaultShellProfileId;
            return ShellProfiles.ResolveDisplayPath(rootPath, profileId);
        }

        private static string BuildDiffPaneTitle(string diffPath)
        {
            if (string.IsNullOrWhiteSpace(diffPath))
            {
                return "Patch";
            }

            string trimmed = diffPath.Replace('\\', '/').TrimEnd('/');
            int slashIndex = trimmed.LastIndexOf('/');
            string fileName = slashIndex >= 0 && slashIndex < trimmed.Length - 1
                ? trimmed[(slashIndex + 1)..]
                : trimmed;
            return fileName;
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
                    WorkspacePaneKind.Diff => "diff",
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
                WorkspacePaneKind.Diff => "Diff",
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
