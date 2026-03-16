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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Windows.Storage.Pickers;
using Windows.Foundation;
using WinRT.Interop;

namespace SelfContainedDeployment
{
    public sealed class InspectorDirectoryTreeItem
    {
        public string Name { get; init; }

        public string RelativePath { get; init; }

        public bool IsDirectory { get; init; }

        public string IconGlyph { get; init; }

        public Brush IconBrush { get; init; }

        public string KindText { get; init; }

        public Brush KindBrush { get; init; }

        public string StatusText { get; init; }

        public Brush StatusBrush { get; init; }

        public Visibility KindVisibility => string.IsNullOrWhiteSpace(KindText) ? Visibility.Collapsed : Visibility.Visible;

        public Visibility StatusVisibility => string.IsNullOrWhiteSpace(StatusText) ? Visibility.Collapsed : Visibility.Visible;
    }

    public partial class MainPage : Page
    {
        private const double PaneDividerThickness = 4;
        private const double MinPaneSplitRatio = 0.24;
        private const double MaxPaneSplitRatio = 0.76;
        private const int AutomationUiTreeMaxDepth = 28;
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
        private bool _showingOverview;
        private bool _suppressTabSelectionChanged;
        private bool _refreshingTabView;
        private int _tabSelectionChangeGeneration;
        private bool _suppressPaneInteractionRequests;
        private bool _suppressThreadNameSync;
        private string _inlineRenamingPaneId;
        private bool _restoringSession;
        private bool _inspectorOpen = true;
        private int _threadSequence = 1;
        private readonly DispatcherQueueTimer _sessionSaveTimer;
        private readonly DispatcherQueueTimer _projectTreeRefreshTimer;
        private readonly DispatcherQueueTimer _gitRefreshTimer;
        private readonly DispatcherQueueTimer _paneLayoutTimer;
        private readonly HashSet<string> _baselineCaptureInFlightThreadIds = new(StringComparer.Ordinal);
        private GitThreadSnapshot _activeGitSnapshot;
        private int _latestGitRefreshRequestId;
        private string _pendingGitSelectedPath;
        private bool _pendingGitPreserveSelection;
        private string _lastProjectTreeRenderKey;
        private string _lastPaneWorkspaceRenderKey;
        private string _lastThreadOverviewRenderKey;
        private bool _projectTreeRefreshEnqueued;
        private bool _suppressDiffReviewSourceSelectionChanged;
        private bool _capturingDiffCheckpoint;
        private InspectorSection _activeInspectorSection = InspectorSection.Review;
        private string _lastInspectorDirectoryRootPath;
        private readonly Dictionary<string, TreeViewNode> _inspectorDirectoryNodesByPath = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<TreeViewNode, InspectorDirectoryTreeItem> _inspectorDirectoryItemsByNode = new();

        private sealed class DiffReviewSourceOption
        {
            public DiffReviewSourceKind Kind { get; init; }

            public string CheckpointId { get; init; }

            public string Label { get; init; }
        }

        private sealed class InspectorDirectoryDecoration
        {
            public GitChangedFile File { get; init; }

            public bool HasChangedDescendant { get; init; }
        }

        private sealed class InspectorDirectoryNodeModel
        {
            public string Name { get; init; }

            public string RelativePath { get; init; }

            public bool IsDirectory { get; init; }

            public InspectorDirectoryDecoration Decoration { get; init; }

            public Dictionary<string, InspectorDirectoryNodeModel> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class ThreadActivitySummary
        {
            public string Label { get; init; }

            public string ToolTip { get; init; }

            public bool IsRunning { get; init; }

            public bool RequiresAttention { get; init; }
        }

        private enum InspectorSection
        {
            Review,
            Files,
        }

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
            _paneLayoutTimer = DispatcherQueue.CreateTimer();
            _paneLayoutTimer.IsRepeating = false;
            _paneLayoutTimer.Interval = TimeSpan.FromMilliseconds(30);
            _paneLayoutTimer.Tick += OnPaneLayoutTimerTick;
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
            ((App)Application.Current).MainWindowInstance?.ApplyChromeTheme(ResolveTheme(theme));

            RefreshProjectTree();
            UpdateSidebarActions();
            UpdateHeader();
            ApplyThemeToAllTerminals(ResolveTheme(theme));
            ApplyGitSnapshotToUi();
            UpdateInspectorSectionChrome();
            RefreshInspectorFileBrowser(forceRebuild: true);
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
            Stopwatch stopwatch = Stopwatch.StartNew();
            int interactiveIndex = 0;
            List<NativeAutomationUiNode> children = new();
            List<DependencyObject> automationRoots = GetAutomationRoots();
            for (int index = 0; index < automationRoots.Count; index++)
            {
                NativeAutomationUiNode child = BuildUiNodeTree(automationRoots[index], $"root/{index}", ref interactiveIndex, depth: 0);
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

            if (stopwatch.ElapsedMilliseconds >= 250)
            {
                LogAutomationEvent("performance", "ui-tree.snapshot_ready", "Built automation UI tree snapshot", new Dictionary<string, string>
                {
                    ["durationMs"] = stopwatch.ElapsedMilliseconds.ToString(),
                    ["interactiveCount"] = interactiveNodes.Count.ToString(),
                    ["rootCount"] = children.Count.ToString(),
                });
            }

            return new NativeAutomationUiTreeResponse
            {
                WindowTitle = ((App)Application.Current).MainWindowInstance?.Title,
                ActiveView = ResolveActiveViewName(),
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
                snapshot = BuildUiNodeTree(target, "target", ref interactiveIndex, depth: 0);
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
                    SelectedTabId = pane.Browser.SelectedTabId,
                    TabCount = pane.Browser.TabCount,
                    ProfileSeedStatus = pane.Browser.ProfileSeedStatus,
                    ExtensionImportStatus = pane.Browser.ExtensionImportStatus,
                    CredentialAutofillStatus = pane.Browser.CredentialAutofillStatus,
                    InstalledExtensions = pane.Browser.InstalledExtensionNames.ToList(),
                    Tabs = pane.Browser.Tabs.Select(tab => new NativeAutomationBrowserTabSnapshot
                    {
                        Id = tab.Id,
                        Title = tab.Title,
                        Uri = tab.Uri,
                    }).ToList(),
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

        public NativeAutomationEditorStateResponse GetEditorState(NativeAutomationEditorStateRequest request)
        {
            request ??= new NativeAutomationEditorStateRequest();

            List<NativeAutomationEditorSnapshot> snapshots = new();
            foreach ((WorkspaceProject project, WorkspaceThread thread, EditorPaneRecord pane) in EnumerateEditorRecords())
            {
                if (!string.IsNullOrWhiteSpace(request.PaneId) && !string.Equals(pane.Id, request.PaneId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                EditorPaneRenderSnapshot snapshot = pane.Editor.GetRenderSnapshot(request.MaxChars, request.MaxFiles);
                snapshots.Add(new NativeAutomationEditorSnapshot
                {
                    PaneId = pane.Id,
                    ThreadId = thread.Id,
                    ProjectId = project.Id,
                    Title = pane.Title,
                    SelectedPath = snapshot.SelectedPath,
                    Status = snapshot.Status,
                    Dirty = snapshot.Dirty,
                    ReadOnly = snapshot.ReadOnly,
                    FileCount = snapshot.FileCount,
                    Files = snapshot.Files,
                    Text = snapshot.Text,
                });
            }

            return new NativeAutomationEditorStateResponse
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
            GitThreadSnapshot displayedSnapshot = ResolveDisplayedGitSnapshot();
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
                ActiveView = ResolveActiveViewName(),
                Theme = ResolveTheme(SampleConfig.CurrentTheme).ToString().ToLowerInvariant(),
                PaneOpen = ShellSplitView.IsPaneOpen,
                InspectorOpen = _inspectorOpen,
                ShellProfileId = _activeProject?.ShellProfileId,
                GitBranch = displayedSnapshot?.BranchName ?? _activeThread?.BranchName,
                WorktreePath = displayedSnapshot?.WorktreePath ?? _activeThread?.WorktreePath,
                ChangedFileCount = displayedSnapshot?.ChangedFiles.Count ?? _activeThread?.ChangedFileCount ?? 0,
                SelectedDiffPath = displayedSnapshot?.SelectedPath ?? _activeThread?.SelectedDiffPath,
                DiffReviewSource = _activeThread is null ? "live" : FormatDiffReviewSource(_activeThread.DiffReviewSource),
                SelectedCheckpointId = _activeThread?.SelectedCheckpointId,
                CheckpointCount = _activeThread?.DiffCheckpoints.Count ?? 0,
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
                        ShowTerminalShell(queueGitRefresh: false);
                        break;
                    case "showoverview":
                        ShowThreadOverview();
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
                        ShowTerminalShell(queueGitRefresh: false);
                        AddTerminalTab(_activeProject, _activeThread);
                        break;
                    case "newbrowserpane":
                        ShowTerminalShell(queueGitRefresh: false);
                        AddBrowserPane(_activeProject, _activeThread, request.Value);
                        break;
                    case "neweditorpane":
                        ShowTerminalShell(queueGitRefresh: false);
                        AddEditorPane(_activeProject, _activeThread, request.Value);
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
                        ShowTerminalShell(queueGitRefresh: false);
                        break;
                    case "movetabafter":
                        MoveTabAfter(request.TabId, request.TargetTabId);
                        ShowTerminalShell(queueGitRefresh: false);
                        break;
                    case "setlayout":
                        SetThreadLayout(request.ThreadId, request.Value);
                        ShowTerminalShell(queueGitRefresh: false);
                        break;
                    case "setpanesplit":
                        SetPaneSplit(request.ThreadId, request.Value);
                        ShowTerminalShell(queueGitRefresh: false);
                        break;
                    case "fitpanes":
                    case "fitvisiblepanes":
                        EqualizeVisiblePaneSplits(ResolveActionThread(request), equalizePrimary: true, equalizeSecondary: true, reason: "automation");
                        ApplyFitToVisiblePanes(ResolveActionThread(request), persistLockState: false, autoLock: ResolveActionThread(request)?.AutoFitPaneContentLocked == true, reason: "automation");
                        ShowTerminalShell(queueGitRefresh: false);
                        break;
                    case "togglefitpaneslock":
                    case "togglefitvisiblepaneslock":
                        WorkspaceThread fitThread = ResolveActionThread(request);
                        if (fitThread is not null)
                        {
                            bool nextAutoLock = !fitThread.AutoFitPaneContentLocked;
                            ApplyFitToVisiblePanes(fitThread, persistLockState: true, autoLock: nextAutoLock, reason: "automation");
                        }

                        ShowTerminalShell(queueGitRefresh: false);
                        break;
                    case "togglepanezoom":
                    case "focuspane":
                        WorkspaceThread zoomThread = ResolveActionThread(request) ?? _activeThread;
                        WorkspacePaneRecord zoomPane = !string.IsNullOrWhiteSpace(request.TabId)
                            ? zoomThread?.Panes.FirstOrDefault(candidate => string.Equals(candidate.Id, request.TabId, StringComparison.Ordinal))
                            : GetSelectedPane(zoomThread);
                        if (ReferenceEquals(zoomThread, _activeThread) && zoomPane is not null)
                        {
                            TogglePaneZoom(zoomPane);
                        }
                        else if (zoomThread is not null && zoomPane is not null)
                        {
                            ActivateThread(zoomThread);
                            TogglePaneZoom(zoomPane);
                        }

                        ShowTerminalShell(queueGitRefresh: false);
                        break;
                    case "setthreadworktree":
                        SetThreadWorktree(request.ThreadId, request.Value);
                        ShowTerminalShell();
                        break;
                    case "refreshdiff":
                        ShowTerminalShell(queueGitRefresh: false);
                        QueueActiveThreadGitRefresh(preserveSelection: true);
                        break;
                    case "capturecheckpoint":
                        _ = CaptureDiffCheckpointAsync(request.Value);
                        ShowTerminalShell(queueGitRefresh: false);
                        break;
                    case "openfullpatch":
                    case "openfullpatchreview":
                        _ = OpenFullPatchReviewAsync();
                        ShowTerminalShell(queueGitRefresh: false);
                        break;
                    case "selectreviewsource":
                        SelectDiffReviewSource(request.Value);
                        ShowTerminalShell(queueGitRefresh: false);
                        break;
                    case "selectdifffile":
                        if (!string.IsNullOrWhiteSpace(request.Value))
                        {
                            SelectDiffPathInCurrentReview(request.Value);
                        }

                        ShowTerminalShell(queueGitRefresh: false);
                        if (string.IsNullOrWhiteSpace(request.Value))
                        {
                            QueueActiveThreadGitRefresh(request.Value, preserveSelection: true);
                        }
                        break;
                    case "closetab":
                        CloseTab(request.TabId);
                        break;
                    case "navigatebrowser":
                        NavigateSelectedBrowser(request.Value);
                        ShowTerminalShell(queueGitRefresh: false);
                        break;
                    case "newbrowsertab":
                        _ = ResolveBrowserPane()?.Browser.AddTabAsync(request.Value);
                        ShowTerminalShell(queueGitRefresh: false);
                        break;
                    case "selectbrowsertab":
                        _ = ResolveBrowserPane()?.Browser.SelectTabAsync(request.Value);
                        ShowTerminalShell(queueGitRefresh: false);
                        break;
                    case "closebrowsertab":
                        _ = ResolveBrowserPane()?.Browser.CloseTabAsync(request.Value);
                        ShowTerminalShell(queueGitRefresh: false);
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
                ((App)Application.Current).MainWindowInstance?.ApplyChromeTheme(ResolveTheme(ElementTheme.Default));
                QueueProjectTreeRefresh();
                UpdateSidebarActions();
                UpdateHeader();
                ApplyThemeToAllTerminals(ResolveTheme(ElementTheme.Default));
                ApplyGitSnapshotToUi();
                UpdateInspectorSectionChrome();
                RefreshInspectorFileBrowser(forceRebuild: true);
                _lastPaneWorkspaceRenderKey = null;
                RenderPaneWorkspace();
                PaneWorkspaceGrid.Background = AppBrush(PaneWorkspaceGrid, "ShellPaneDividerBrush");
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

        private void OnShowOverviewClicked(object sender, RoutedEventArgs e)
        {
            if (_showingOverview)
            {
                ShowTerminalShell(queueGitRefresh: false);
                return;
            }

            ShowThreadOverview();
        }

        private void OnCloseOverviewClicked(object sender, RoutedEventArgs e)
        {
            ShowTerminalShell(queueGitRefresh: false);
        }

        private void OnOverviewNewThreadClicked(object sender, RoutedEventArgs e)
        {
            if (_activeProject is null)
            {
                return;
            }

            ActivateThread(CreateThread(_activeProject));
            ShowThreadOverview();
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

        private void OnProjectOverviewMenuClicked(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuFlyoutItem item || item.Tag is not string projectId)
            {
                return;
            }

            WorkspaceProject project = FindProject(projectId);
            if (project is null)
            {
                return;
            }

            ActivateProject(project);
            ShowThreadOverview();
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

        private void OnOverviewThreadClicked(object sender, RoutedEventArgs e)
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

        private void OnFitPanesClicked(object sender, RoutedEventArgs e)
        {
            EqualizeVisiblePaneSplits(_activeThread, equalizePrimary: true, equalizeSecondary: true, reason: "button");
            ApplyFitToVisiblePanes(_activeThread, persistLockState: false, autoLock: _activeThread?.AutoFitPaneContentLocked == true, reason: "button");
        }

        private void OnFitPanesDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (_activeThread is null)
            {
                return;
            }

            bool nextAutoLock = !_activeThread.AutoFitPaneContentLocked;
            ApplyFitToVisiblePanes(_activeThread, persistLockState: true, autoLock: nextAutoLock, reason: "button-double");
            e.Handled = true;
        }

        private void OnToggleInspectorClicked(object sender, RoutedEventArgs e)
        {
            ToggleInspector();
        }

        private void OnRefreshDiffClicked(object sender, RoutedEventArgs e)
        {
            QueueActiveThreadGitRefresh(preserveSelection: true);
        }

        private async void OnOpenFullPatchClicked(object sender, RoutedEventArgs e)
        {
            await OpenFullPatchReviewAsync().ConfigureAwait(true);
        }

        private async void OnCaptureCheckpointClicked(object sender, RoutedEventArgs e)
        {
            await CaptureDiffCheckpointAsync().ConfigureAwait(true);
        }

        private void OnDiffReviewSourceSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressDiffReviewSourceSelectionChanged || DiffReviewSourceComboBox?.SelectedItem is not DiffReviewSourceOption option)
            {
                return;
            }

            ApplyDiffReviewSourceSelection(option.Kind, option.CheckpointId);
        }

        private void OnDiffFileButtonClicked(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button)
            {
                return;
            }

            GitChangedFile changedFile = ResolveSelectedDiffFile(button);
            if (_activeThread is null || changedFile is null)
            {
                return;
            }

            SelectDiffPathInCurrentReview(changedFile.Path);
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

            RenderPaneWorkspace();
            SyncInspectorSectionWithSelectedPane();
            RefreshInspectorFileBrowser();
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

        private GitThreadSnapshot ResolveDisplayedGitSnapshot()
        {
            if (_activeThread is null)
            {
                return null;
            }

            NormalizeDiffReviewSource(_activeThread);
            return _activeThread.DiffReviewSource switch
            {
                DiffReviewSourceKind.Baseline when _activeThread.BaselineSnapshot is not null => _activeThread.BaselineSnapshot,
                DiffReviewSourceKind.Checkpoint when !string.IsNullOrWhiteSpace(_activeThread.SelectedCheckpointId) =>
                    _activeThread.DiffCheckpoints.FirstOrDefault(checkpoint => string.Equals(checkpoint.Id, _activeThread.SelectedCheckpointId, StringComparison.Ordinal))?.Snapshot
                    ?? _activeGitSnapshot,
                _ => _activeGitSnapshot,
            };
        }

        private IReadOnlyList<DiffReviewSourceOption> BuildDiffReviewSourceOptions(WorkspaceThread thread)
        {
            if (thread is null)
            {
                return Array.Empty<DiffReviewSourceOption>();
            }

            List<DiffReviewSourceOption> options = new()
            {
                new DiffReviewSourceOption
                {
                    Kind = DiffReviewSourceKind.Live,
                    Label = "Live working tree",
                }
            };

            if (thread.BaselineSnapshot is not null)
            {
                options.Add(new DiffReviewSourceOption
                {
                    Kind = DiffReviewSourceKind.Baseline,
                    Label = "Thread baseline",
                });
            }

            foreach (WorkspaceDiffCheckpoint checkpoint in thread.DiffCheckpoints.OrderByDescending(candidate => candidate.CapturedAt))
            {
                options.Add(new DiffReviewSourceOption
                {
                    Kind = DiffReviewSourceKind.Checkpoint,
                    CheckpointId = checkpoint.Id,
                    Label = string.IsNullOrWhiteSpace(checkpoint.Name)
                        ? checkpoint.CapturedAt.LocalDateTime.ToString("MMM d HH:mm")
                        : checkpoint.Name,
                });
            }

            return options;
        }

        private void RefreshDiffReviewSourceControls()
        {
            if (DiffReviewSourceComboBox is null || DiffReviewSourceMetaText is null || CaptureCheckpointButton is null || DiffReviewSourceSection is null)
            {
                return;
            }

            if (_activeThread is null)
            {
                DiffReviewSourceSection.Visibility = Visibility.Collapsed;
                _suppressDiffReviewSourceSelectionChanged = true;
                DiffReviewSourceComboBox.ItemsSource = null;
                DiffReviewSourceComboBox.SelectedItem = null;
                _suppressDiffReviewSourceSelectionChanged = false;
                DiffReviewSourceComboBox.IsEnabled = false;
                DiffReviewSourceMetaText.Text = "No thread selected";
                CaptureCheckpointButton.IsEnabled = false;
                return;
            }

            IReadOnlyList<DiffReviewSourceOption> options = BuildDiffReviewSourceOptions(_activeThread);
            DiffReviewSourceOption selectedOption = options.FirstOrDefault(option =>
                option.Kind == _activeThread.DiffReviewSource &&
                string.Equals(option.CheckpointId, _activeThread.SelectedCheckpointId, StringComparison.Ordinal))
                ?? options.FirstOrDefault();

            _suppressDiffReviewSourceSelectionChanged = true;
            DiffReviewSourceComboBox.DisplayMemberPath = nameof(DiffReviewSourceOption.Label);
            DiffReviewSourceComboBox.ItemsSource = options;
            DiffReviewSourceComboBox.SelectedItem = selectedOption;
            _suppressDiffReviewSourceSelectionChanged = false;

            DiffReviewSourceSection.Visibility = options.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            DiffReviewSourceComboBox.IsEnabled = options.Count > 1;
            CaptureCheckpointButton.IsEnabled = !_capturingDiffCheckpoint;
            DiffReviewSourceMetaText.Text = BuildDiffReviewSourceMeta(_activeThread, selectedOption);
        }

        private static string BuildDiffReviewSourceMeta(WorkspaceThread thread, DiffReviewSourceOption selectedOption)
        {
            if (thread is null || selectedOption is null)
            {
                return "Live working tree";
            }

            return selectedOption.Kind switch
            {
                DiffReviewSourceKind.Live => thread.BaselineSnapshot is null
                    ? "Current working tree"
                    : $"Current working tree · {thread.DiffCheckpoints.Count} checkpoint{(thread.DiffCheckpoints.Count == 1 ? string.Empty : "s")}",
                DiffReviewSourceKind.Baseline => "Thread-start snapshot",
                DiffReviewSourceKind.Checkpoint => thread.DiffCheckpoints.FirstOrDefault(checkpoint => string.Equals(checkpoint.Id, selectedOption.CheckpointId, StringComparison.Ordinal)) is WorkspaceDiffCheckpoint checkpoint
                    ? $"Checkpoint · {checkpoint.CapturedAt.LocalDateTime:g}"
                    : "Saved checkpoint",
                _ => "Current working tree",
            };
        }

        private void ApplyDiffReviewSourceSelection(DiffReviewSourceKind kind, string checkpointId = null)
        {
            if (_activeThread is null)
            {
                return;
            }

            _activeThread.DiffReviewSource = kind;
            _activeThread.SelectedCheckpointId = kind == DiffReviewSourceKind.Checkpoint ? checkpointId : null;
            NormalizeDiffReviewSource(_activeThread);
            RefreshDiffReviewSourceControls();
            ApplyGitSnapshotToUi();
            QueueSessionSave();
            LogAutomationEvent("git", "review_source.selected", $"Selected {FormatDiffReviewSource(_activeThread.DiffReviewSource)} review source", new Dictionary<string, string>
            {
                ["threadId"] = _activeThread.Id,
                ["projectId"] = _activeProject?.Id ?? string.Empty,
                ["reviewSource"] = FormatDiffReviewSource(_activeThread.DiffReviewSource),
                ["checkpointId"] = _activeThread.SelectedCheckpointId ?? string.Empty,
            });
        }

        private void SelectDiffReviewSource(string value)
        {
            string normalized = value?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalized) || string.Equals(normalized, "live", StringComparison.OrdinalIgnoreCase))
            {
                ApplyDiffReviewSourceSelection(DiffReviewSourceKind.Live);
                return;
            }

            if (string.Equals(normalized, "baseline", StringComparison.OrdinalIgnoreCase))
            {
                ApplyDiffReviewSourceSelection(DiffReviewSourceKind.Baseline);
                return;
            }

            string checkpointId = normalized.StartsWith("checkpoint:", StringComparison.OrdinalIgnoreCase)
                ? normalized["checkpoint:".Length..]
                : normalized;
            ApplyDiffReviewSourceSelection(DiffReviewSourceKind.Checkpoint, checkpointId);
        }

        private void OnInspectorReviewTabClicked(object sender, RoutedEventArgs e)
        {
            SetInspectorSection(InspectorSection.Review);
        }

        private void OnInspectorFilesTabClicked(object sender, RoutedEventArgs e)
        {
            SetInspectorSection(InspectorSection.Files);
        }

        private async void OnInspectorSaveFileClicked(object sender, RoutedEventArgs e)
        {
            EditorPaneRecord editorPane = ResolveInspectorEditorPane(createIfNeeded: false);
            if (editorPane is null)
            {
                return;
            }

            await editorPane.Editor.SaveCurrentFilePublicAsync().ConfigureAwait(true);
            RefreshInspectorFileBrowser();
        }

        private async void OnInspectorDirectoryTreeDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (ResolveSelectedInspectorDirectoryItem() is not InspectorDirectoryTreeItem item || item.IsDirectory)
            {
                return;
            }

            await OpenEditorFileFromInspectorAsync(item.RelativePath).ConfigureAwait(true);
        }

        private async void OnInspectorDirectoryTreeKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != Windows.System.VirtualKey.Enter)
            {
                return;
            }

            if (ResolveSelectedInspectorDirectoryItem() is not InspectorDirectoryTreeItem item || item.IsDirectory)
            {
                return;
            }

            e.Handled = true;
            await OpenEditorFileFromInspectorAsync(item.RelativePath).ConfigureAwait(true);
        }

        private void SetInspectorSection(InspectorSection section)
        {
            _activeInspectorSection = section;
            UpdateInspectorSectionChrome();
            if (section == InspectorSection.Files)
            {
                RefreshInspectorFileBrowser();
            }
        }

        private void SyncInspectorSectionWithSelectedPane()
        {
            WorkspacePaneRecord selectedPane = GetSelectedPane(_activeThread);
            if (selectedPane?.Kind == WorkspacePaneKind.Editor)
            {
                SetInspectorSection(InspectorSection.Files);
                return;
            }

            if (selectedPane?.Kind == WorkspacePaneKind.Diff)
            {
                SetInspectorSection(InspectorSection.Review);
            }
        }

        private void UpdateInspectorSectionChrome()
        {
            if (InspectorReviewTabButton is null || InspectorFilesTabButton is null)
            {
                return;
            }

            ApplyThreadButtonState(InspectorReviewTabButton, _activeInspectorSection == InspectorSection.Review);
            ApplyThreadButtonState(InspectorFilesTabButton, _activeInspectorSection == InspectorSection.Files);

            if (InspectorReviewContent is not null)
            {
                InspectorReviewContent.Visibility = _activeInspectorSection == InspectorSection.Review ? Visibility.Visible : Visibility.Collapsed;
            }

            if (InspectorFilesContent is not null)
            {
                InspectorFilesContent.Visibility = _activeInspectorSection == InspectorSection.Files ? Visibility.Visible : Visibility.Collapsed;
            }

            if (InspectorReviewActionsPanel is not null)
            {
                InspectorReviewActionsPanel.Visibility = _activeInspectorSection == InspectorSection.Review ? Visibility.Visible : Visibility.Collapsed;
            }

            if (InspectorFileActionsPanel is not null)
            {
                InspectorFileActionsPanel.Visibility = _activeInspectorSection == InspectorSection.Files ? Visibility.Visible : Visibility.Collapsed;
            }

            UpdateInspectorFileActionState();
        }

        private void RefreshInspectorFileBrowser(bool forceRebuild = false)
        {
            if (InspectorDirectoryTree is null || InspectorDirectoryRootText is null || InspectorDirectoryMetaText is null || InspectorDirectoryEmptyText is null)
            {
                return;
            }

            if (_activeInspectorSection != InspectorSection.Files && !forceRebuild)
            {
                UpdateInspectorFileActionState();
                return;
            }

            string rootPath = ResolveThreadRootPath(_activeProject, _activeThread);
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
            {
                _lastInspectorDirectoryRootPath = null;
                _inspectorDirectoryNodesByPath.Clear();
                _inspectorDirectoryItemsByNode.Clear();
                InspectorDirectoryTree.RootNodes.Clear();
                InspectorDirectoryRootText.Text = "No active project";
                InspectorDirectoryMetaText.Text = "Open an editor pane to browse files.";
                InspectorDirectoryEmptyText.Visibility = Visibility.Visible;
                UpdateInspectorFileActionState();
                return;
            }

            string directoryTitle = _activeProject?.Name;
            if (string.IsNullOrWhiteSpace(directoryTitle))
            {
                directoryTitle = Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }

            InspectorDirectoryRootText.Text = directoryTitle;
            ToolTipService.SetToolTip(InspectorDirectoryRootText, ShellProfiles.ResolveDisplayPath(rootPath, _activeProject?.ShellProfileId ?? SampleConfig.DefaultShellProfileId));
            bool shouldRebuild = forceRebuild ||
                !string.Equals(_lastInspectorDirectoryRootPath, rootPath, StringComparison.OrdinalIgnoreCase);
            if (!shouldRebuild && ResolveDisplayedGitSnapshot()?.ChangedFiles is IReadOnlyList<GitChangedFile> liveFiles)
            {
                string renderKey = string.Join("|", liveFiles.Select(file => $"{file.Path}:{file.Status}:{file.AddedLines}:{file.RemovedLines}"));
                if (!string.Equals(InspectorDirectoryTree.Tag as string, renderKey, StringComparison.Ordinal))
                {
                    shouldRebuild = true;
                }
            }

            if (shouldRebuild)
            {
                BuildInspectorDirectoryTree(rootPath);
                _lastInspectorDirectoryRootPath = rootPath;
            }

            EditorPaneRecord editorPane = ResolveInspectorEditorPane(createIfNeeded: false);
            string selectedPath = editorPane?.Editor.SelectedFilePath;
            InspectorDirectoryMetaText.Text = editorPane is null
                ? "Select a file to open it in a new editor pane."
                : editorPane.Editor.StatusText;
            UpdateInspectorDirectorySelection(selectedPath);
            UpdateInspectorFileActionState();
        }

        private void BuildInspectorDirectoryTree(string rootPath)
        {
            _inspectorDirectoryNodesByPath.Clear();
            _inspectorDirectoryItemsByNode.Clear();
            InspectorDirectoryTree.SelectedNode = null;
            InspectorDirectoryTree.RootNodes.Clear();

            List<EditorPaneFileEntry> files = EditorPaneControl.EnumerateProjectFilesForRoot(rootPath);
            IReadOnlyList<GitChangedFile> changedFiles = ResolveDisplayedGitSnapshot()?.ChangedFiles?.ToList() ?? new List<GitChangedFile>();
            Dictionary<string, InspectorDirectoryDecoration> decorationsByPath = BuildInspectorDirectoryDecorations(changedFiles);
            InspectorDirectoryTree.Tag = string.Join("|", changedFiles.Select(file => $"{file.Path}:{file.Status}:{file.AddedLines}:{file.RemovedLines}"));
            InspectorDirectoryEmptyText.Visibility = files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (files.Count == 0)
            {
                return;
            }

            Dictionary<string, InspectorDirectoryNodeModel> rootNodes = new(StringComparer.OrdinalIgnoreCase);
            foreach (EditorPaneFileEntry file in files)
            {
                string[] segments = file.RelativePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                Dictionary<string, InspectorDirectoryNodeModel> siblings = rootNodes;
                string cumulativePath = string.Empty;

                for (int index = 0; index < segments.Length; index++)
                {
                    string segment = segments[index];
                    cumulativePath = string.IsNullOrWhiteSpace(cumulativePath) ? segment : $"{cumulativePath}/{segment}";
                    bool isFile = index == segments.Length - 1;
                    if (!siblings.TryGetValue(segment, out InspectorDirectoryNodeModel node))
                    {
                        decorationsByPath.TryGetValue(cumulativePath, out InspectorDirectoryDecoration decoration);
                        node = new InspectorDirectoryNodeModel
                        {
                            Name = segment,
                            RelativePath = cumulativePath,
                            IsDirectory = !isFile,
                            Decoration = decoration,
                        };
                        siblings[segment] = node;
                    }

                    if (!isFile)
                    {
                        siblings = node.Children;
                    }
                }
            }

            foreach (InspectorDirectoryNodeModel node in OrderInspectorDirectoryNodes(rootNodes.Values))
            {
                InspectorDirectoryTree.RootNodes.Add(BuildInspectorDirectoryTreeNode(node, 0));
            }
        }

        private TreeViewNode BuildInspectorDirectoryTreeNode(InspectorDirectoryNodeModel node, int depth)
        {
            InspectorDirectoryDecoration decoration = node.Decoration;
            InspectorDirectoryTreeItem item = new()
            {
                Name = node.Name,
                RelativePath = node.RelativePath,
                IsDirectory = node.IsDirectory,
                IconGlyph = ResolveInspectorItemGlyph(node.RelativePath, node.IsDirectory),
                IconBrush = AppBrush(InspectorDirectoryTree, ResolveInspectorIconBrushKey(node.RelativePath, node.IsDirectory, decoration)),
                KindText = ResolveInspectorKindText(node.RelativePath, node.IsDirectory),
                KindBrush = AppBrush(InspectorDirectoryTree, ResolveInspectorIconBrushKey(node.RelativePath, node.IsDirectory, decoration)),
                StatusText = ResolveInspectorChangeMarker(decoration?.File, hasChangedDescendant: decoration?.HasChangedDescendant == true),
                StatusBrush = AppBrush(InspectorDirectoryTree, ResolveInspectorChangeBrushKey(decoration?.File, hasChangedDescendant: decoration?.HasChangedDescendant == true)),
            };

            TreeViewNode treeNode = new()
            {
                Content = BuildInspectorDirectoryNodeContent(item),
                IsExpanded = node.IsDirectory && ShouldExpandInspectorDirectoryNode(node, depth),
            };
            _inspectorDirectoryItemsByNode[treeNode] = item;

            if (node.IsDirectory)
            {
                foreach (InspectorDirectoryNodeModel child in OrderInspectorDirectoryNodes(node.Children.Values))
                {
                    treeNode.Children.Add(BuildInspectorDirectoryTreeNode(child, depth + 1));
                }
            }
            else
            {
                _inspectorDirectoryNodesByPath[item.RelativePath] = treeNode;
            }

            return treeNode;
        }

        private static IEnumerable<InspectorDirectoryNodeModel> OrderInspectorDirectoryNodes(IEnumerable<InspectorDirectoryNodeModel> nodes)
        {
            return nodes
                .OrderByDescending(node => node.IsDirectory)
                .ThenBy(node => node.Name, StringComparer.OrdinalIgnoreCase);
        }

        private static bool ShouldExpandInspectorDirectoryNode(InspectorDirectoryNodeModel node, int depth)
        {
            return depth == 0 && node.Decoration?.HasChangedDescendant == true;
        }

        private InspectorDirectoryTreeItem ResolveSelectedInspectorDirectoryItem()
        {
            return InspectorDirectoryTree?.SelectedNode is TreeViewNode node && _inspectorDirectoryItemsByNode.TryGetValue(node, out InspectorDirectoryTreeItem item)
                ? item
                : null;
        }

        private FrameworkElement BuildInspectorDirectoryNodeContent(InspectorDirectoryTreeItem item)
        {
            Grid row = new()
            {
                MinHeight = 26,
                ColumnSpacing = 9,
                Margin = new Thickness(0, 2, 0, 2),
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ToolTipService.SetToolTip(row, item.RelativePath);

            row.Children.Add(BuildInspectorNodeGlyph(item));

            TextBlock name = new()
            {
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11.5,
                FontWeight = item.IsDirectory || item.StatusVisibility == Visibility.Visible
                    ? Microsoft.UI.Text.FontWeights.SemiBold
                    : Microsoft.UI.Text.FontWeights.Normal,
                Foreground = AppBrush(InspectorDirectoryTree, "ShellTextPrimaryBrush"),
                Text = item.Name,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(name, 1);
            row.Children.Add(name);

            StackPanel adornments = new()
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(adornments, 2);

            if (!string.IsNullOrWhiteSpace(item.StatusText))
            {
                adornments.Children.Add(BuildInspectorChangeBadge(item));
            }

            if (adornments.Children.Count > 0)
            {
                row.Children.Add(adornments);
            }

            return row;
        }

        private FrameworkElement BuildInspectorNodeGlyph(InspectorDirectoryTreeItem item)
        {
            return BuildInspectorPathBadge(item);
        }

        private FrameworkElement BuildInspectorChangeBadge(InspectorDirectoryTreeItem item)
        {
            return new Border
            {
                Padding = item.IsDirectory ? new Thickness(4, 0, 4, 0) : new Thickness(5, 0, 5, 0),
                MinWidth = item.IsDirectory ? 16 : 18,
                Height = item.IsDirectory ? 14 : 18,
                CornerRadius = new CornerRadius(4),
                Background = CreateInspectorStatusBackground(item.StatusBrush, item.IsDirectory),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = item.StatusText,
                    FontSize = item.IsDirectory ? 9 : 9.5,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = item.StatusBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                },
            };
        }

        private static Brush CreateInspectorBadgeBackground(Brush foreground, bool isDirectory)
        {
            Windows.UI.Color fallback = isDirectory
                ? Windows.UI.Color.FromArgb(0x22, 0x71, 0x71, 0x7A)
                : Windows.UI.Color.FromArgb(0x18, 0x71, 0x71, 0x7A);
            if (foreground is not SolidColorBrush solid)
            {
                return new SolidColorBrush(fallback);
            }

            Windows.UI.Color color = solid.Color;
            byte alpha = isDirectory ? (byte)0x24 : (byte)0x16;
            return new SolidColorBrush(Windows.UI.Color.FromArgb(alpha, color.R, color.G, color.B));
        }

        private static Brush CreateInspectorStatusBackground(Brush foreground, bool isDirectory)
        {
            Windows.UI.Color fallback = isDirectory
                ? Windows.UI.Color.FromArgb(0x20, 0x71, 0x71, 0x7A)
                : Windows.UI.Color.FromArgb(0x28, 0x71, 0x71, 0x7A);
            if (foreground is not SolidColorBrush solid)
            {
                return new SolidColorBrush(fallback);
            }

            Windows.UI.Color color = solid.Color;
            byte alpha = isDirectory ? (byte)0x20 : (byte)0x28;
            return new SolidColorBrush(Windows.UI.Color.FromArgb(alpha, color.R, color.G, color.B));
        }

        private static Dictionary<string, InspectorDirectoryDecoration> BuildInspectorDirectoryDecorations(IReadOnlyList<GitChangedFile> changedFiles)
        {
            Dictionary<string, InspectorDirectoryDecoration> map = new(StringComparer.OrdinalIgnoreCase);
            foreach (GitChangedFile file in changedFiles ?? Array.Empty<GitChangedFile>())
            {
                if (string.IsNullOrWhiteSpace(file?.Path))
                {
                    continue;
                }

                string normalizedPath = file.Path.Replace('\\', '/');
                map[normalizedPath] = new InspectorDirectoryDecoration
                {
                    File = file,
                };

                int slashIndex = normalizedPath.LastIndexOf('/');
                while (slashIndex > 0)
                {
                    string directoryPath = normalizedPath[..slashIndex];
                    map[directoryPath] = new InspectorDirectoryDecoration
                    {
                        File = map.TryGetValue(directoryPath, out InspectorDirectoryDecoration existing) ? existing.File : null,
                        HasChangedDescendant = true,
                    };
                    slashIndex = directoryPath.LastIndexOf('/');
                }
            }

            return map;
        }

        private static string ResolveInspectorItemGlyph(string relativePath, bool isDirectory)
        {
            return isDirectory ? "\uE8B7" : "\uE8A5";
        }

        private static string ResolveInspectorKindText(string relativePath, bool isDirectory)
        {
            if (isDirectory)
            {
                return string.Empty;
            }

            string extension = Path.GetExtension(relativePath ?? string.Empty)?.ToLowerInvariant();
            return extension switch
            {
                ".appxmanifest" or ".manifest" => "APPX",
                ".csproj" or ".props" or ".targets" => "PROJ",
                ".cs" => "C#",
                ".css" => "CSS",
                ".html" => "HTML",
                ".ini" => "INI",
                ".js" => "JS",
                ".mjs" => "MJS",
                ".json" => "JSON",
                ".jsx" => "JSX",
                ".md" => "MD",
                ".ps1" => "PS1",
                ".sh" => "SH",
                ".cmd" or ".bat" => "CMD",
                ".toml" => "TOML",
                ".ts" => "TS",
                ".tsx" => "TSX",
                ".txt" => "TXT",
                ".xaml" => "XAML",
                ".xml" => "XML",
                ".yaml" or ".yml" => "YAML",
                ".resw" => "RESW",
                ".sln" => "SLN",
                _ => string.IsNullOrWhiteSpace(extension)
                    ? "FILE"
                    : extension.TrimStart('.').ToUpperInvariant()[..Math.Min(extension.Length - 1, 4)],
            };
        }

        private static string ResolveInspectorIconBrushKey(string relativePath, bool isDirectory, InspectorDirectoryDecoration decoration)
        {
            if (isDirectory)
            {
                return decoration?.HasChangedDescendant == true
                    ? "ShellWarningBrush"
                    : "ShellTextTertiaryBrush";
            }

            string extension = Path.GetExtension(relativePath ?? string.Empty)?.ToLowerInvariant();
            return extension switch
            {
                ".cs" or ".csproj" or ".props" or ".targets" => "ShellCSharpBrush",
                ".ts" or ".tsx" => "ShellTypeScriptBrush",
                ".js" or ".jsx" or ".mjs" => "ShellJavaScriptBrush",
                ".ps1" or ".sh" or ".cmd" or ".bat" => "ShellScriptBrush",
                ".json" or ".yml" or ".yaml" or ".toml" or ".ini" => "ShellConfigBrush",
                ".md" or ".txt" => "ShellMarkdownBrush",
                ".xaml" or ".xml" or ".html" => "ShellMarkupBrush",
                ".css" => "ShellStyleBrush",
                _ => "ShellTextTertiaryBrush",
            };
        }

        private static FrameworkElement BuildInspectorPathBadge(InspectorDirectoryTreeItem item)
        {
            Border iconBadge = new()
            {
                Width = item.IsDirectory ? 22 : 28,
                Height = 18,
                CornerRadius = new CornerRadius(3),
                VerticalAlignment = VerticalAlignment.Center,
                Background = CreateInspectorBadgeBackground(item.IconBrush, item.IsDirectory),
            };

            iconBadge.Child = item.IsDirectory
                ? new FontIcon
                {
                    Glyph = item.IconGlyph,
                    FontSize = 11,
                    Foreground = item.IconBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                }
                : new TextBlock
                {
                    Text = item.KindText,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = item.KindText?.Length > 3 ? 8.3 : 9.3,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = item.KindBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
            return iconBadge;
        }

        private static FrameworkElement BuildInspectorPathBadge(string relativePath, bool isDirectory, Brush accentBrush)
        {
            return BuildInspectorPathBadge(new InspectorDirectoryTreeItem
            {
                RelativePath = relativePath,
                IsDirectory = isDirectory,
                IconGlyph = ResolveInspectorItemGlyph(relativePath, isDirectory),
                IconBrush = accentBrush,
                KindText = ResolveInspectorKindText(relativePath, isDirectory),
                KindBrush = accentBrush,
            });
        }

        private static string ResolveInspectorChangeMarker(GitChangedFile file, bool hasChangedDescendant)
        {
            if (file is null)
            {
                return hasChangedDescendant ? "•" : string.Empty;
            }

            string status = file.Status?.Trim() ?? string.Empty;
            if (status == "??" || status.IndexOf('A') >= 0)
            {
                return "A";
            }

            if (status.IndexOf('D') >= 0)
            {
                return "D";
            }

            if (status.IndexOf('R') >= 0)
            {
                return "R";
            }

            return "M";
        }

        private static string ResolveInspectorChangeBrushKey(GitChangedFile file, bool hasChangedDescendant)
        {
            if (file is null)
            {
                return hasChangedDescendant ? "ShellWarningBrush" : "ShellTextTertiaryBrush";
            }

            string status = file.Status?.Trim() ?? string.Empty;
            if (status == "??" || status.IndexOf('A') >= 0)
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

        private void UpdateInspectorDirectorySelection(string selectedPath)
        {
            if (InspectorDirectoryTree is null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(selectedPath) ||
                !_inspectorDirectoryNodesByPath.TryGetValue(selectedPath, out TreeViewNode node) ||
                !TreeContainsNode(InspectorDirectoryTree.RootNodes, node))
            {
                if (InspectorDirectoryTree.SelectedNode is not null)
                {
                    InspectorDirectoryTree.SelectedNode = null;
                }

                return;
            }

            if (ReferenceEquals(InspectorDirectoryTree.SelectedNode, node))
            {
                return;
            }

            TreeViewNode current = node.Parent as TreeViewNode;
            while (current is not null)
            {
                current.IsExpanded = true;
                current = current.Parent as TreeViewNode;
            }

            InspectorDirectoryTree.SelectedNode = node;
        }

        private static bool TreeContainsNode(IList<TreeViewNode> nodes, TreeViewNode target)
        {
            if (nodes is null || target is null)
            {
                return false;
            }

            foreach (TreeViewNode node in nodes)
            {
                if (ReferenceEquals(node, target))
                {
                    return true;
                }

                if (TreeContainsNode(node.Children, target))
                {
                    return true;
                }
            }

            return false;
        }

        private void UpdateInspectorFileActionState()
        {
            EditorPaneRecord editorPane = ResolveInspectorEditorPane(createIfNeeded: false);
            if (InspectorSaveFileButton is not null)
            {
                InspectorSaveFileButton.IsEnabled = editorPane?.Editor.CanSave == true;
            }
        }

        private EditorPaneRecord ResolveInspectorEditorPane(bool createIfNeeded)
        {
            if (_activeThread is null)
            {
                return null;
            }

            if (GetSelectedPane(_activeThread) is EditorPaneRecord selectedEditor)
            {
                return selectedEditor;
            }

            EditorPaneRecord existingEditor = _activeThread.Panes.OfType<EditorPaneRecord>().FirstOrDefault();
            if (existingEditor is not null || !createIfNeeded)
            {
                return existingEditor;
            }

            return AddEditorPane(_activeProject, _activeThread);
        }

        private async System.Threading.Tasks.Task OpenEditorFileFromInspectorAsync(string relativePath)
        {
            if (_activeThread is null || string.IsNullOrWhiteSpace(relativePath))
            {
                return;
            }

            EditorPaneRecord editorPane = ResolveInspectorEditorPane(createIfNeeded: true);
            if (editorPane is null)
            {
                return;
            }

            await editorPane.Editor.OpenFilePathAsync(relativePath).ConfigureAwait(true);
            SelectPane(editorPane);
            RefreshInspectorFileBrowser();
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
                ActiveView = ResolveActiveViewName(),
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
                        DiffReviewSource = FormatDiffReviewSource(thread.DiffReviewSource),
                        SelectedCheckpointId = thread.SelectedCheckpointId,
                        BaselineSnapshot = CreateGitSnapshotSessionSnapshot(thread.BaselineSnapshot),
                        SelectedPaneId = thread.SelectedPaneId,
                        Layout = WorkspaceSessionStore.FormatLayout(thread.LayoutPreset),
                        PrimarySplitRatio = thread.PrimarySplitRatio,
                        SecondarySplitRatio = thread.SecondarySplitRatio,
                        AutoFitPaneContentLocked = thread.AutoFitPaneContentLocked,
                        DiffCheckpoints = thread.DiffCheckpoints.Select(checkpoint => new GitCheckpointSessionSnapshot
                        {
                            Id = checkpoint.Id,
                            Name = checkpoint.Name,
                            CapturedAt = checkpoint.CapturedAt.ToString("O"),
                            Snapshot = CreateGitSnapshotSessionSnapshot(checkpoint.Snapshot),
                        }).ToList(),
                        Panes = thread.Panes
                            .Where(ShouldPersistPane)
                            .Select(pane => new PaneSessionSnapshot
                            {
                                Id = pane.Id,
                                Kind = pane.Kind.ToString().ToLowerInvariant(),
                                Title = pane.Title,
                                HasCustomTitle = pane.HasCustomTitle,
                                IsExited = pane.IsExited,
                                ReplayRestoreFailed = pane.ReplayRestoreFailed,
                                BrowserUri = pane is BrowserPaneRecord browserPane ? browserPane.Browser.CurrentUri : null,
                                SelectedBrowserTabId = pane is BrowserPaneRecord browserPaneState ? browserPaneState.Browser.SelectedTabId : null,
                                BrowserTabs = pane is BrowserPaneRecord browserPaneTabs
                                    ? browserPaneTabs.Browser.Tabs.Select(tab => new BrowserTabSessionSnapshot
                                    {
                                        Id = tab.Id,
                                        Title = tab.Title,
                                        Uri = tab.Uri,
                                    }).ToList()
                                    : new List<BrowserTabSessionSnapshot>(),
                                DiffPath = pane is DiffPaneRecord diffPane ? diffPane.DiffPath : null,
                                EditorFilePath = pane is EditorPaneRecord editorPane ? editorPane.Editor.SelectedFilePath : null,
                                ReplayTool = pane.ReplayTool,
                                ReplaySessionId = pane.ReplaySessionId,
                                ReplayCommand = pane.ReplayCommand,
                            })
                            .ToList(),
                    }).ToList(),
                }).ToList(),
            };
        }

        private static bool ShouldPersistPane(WorkspacePaneRecord pane)
        {
            return pane is not null && (!pane.IsExited || pane.PersistExitedState);
        }

        private static GitSnapshotSessionSnapshot CreateGitSnapshotSessionSnapshot(GitThreadSnapshot snapshot)
        {
            if (snapshot is null)
            {
                return null;
            }

            return new GitSnapshotSessionSnapshot
            {
                BranchName = snapshot.BranchName,
                RepositoryRootPath = snapshot.RepositoryRootPath,
                WorktreePath = snapshot.WorktreePath,
                StatusSummary = snapshot.StatusSummary,
                DiffSummary = snapshot.DiffSummary,
                SelectedPath = snapshot.SelectedPath,
                SelectedDiff = snapshot.SelectedDiff,
                Error = snapshot.Error,
                ChangedFiles = snapshot.ChangedFiles.Select(file => new GitChangedFileSessionSnapshot
                {
                    Status = file.Status,
                    Path = file.Path,
                    OriginalPath = file.OriginalPath,
                    AddedLines = file.AddedLines,
                    RemovedLines = file.RemovedLines,
                    DiffText = file.DiffText,
                    OriginalText = file.OriginalText,
                    ModifiedText = file.ModifiedText,
                }).ToList(),
            };
        }

        private static GitThreadSnapshot RestoreGitThreadSnapshot(GitSnapshotSessionSnapshot snapshot)
        {
            if (snapshot is null)
            {
                return null;
            }

            GitThreadSnapshot restored = new()
            {
                BranchName = snapshot.BranchName,
                RepositoryRootPath = snapshot.RepositoryRootPath,
                WorktreePath = snapshot.WorktreePath,
                StatusSummary = snapshot.StatusSummary,
                DiffSummary = snapshot.DiffSummary,
                SelectedPath = snapshot.SelectedPath,
                SelectedDiff = snapshot.SelectedDiff,
                Error = snapshot.Error,
                ChangedFiles = (snapshot.ChangedFiles ?? new List<GitChangedFileSessionSnapshot>()).Select(file => new GitChangedFile
                {
                    Status = file.Status,
                    Path = file.Path,
                    OriginalPath = file.OriginalPath,
                    AddedLines = file.AddedLines,
                    RemovedLines = file.RemovedLines,
                    DiffText = file.DiffText,
                    OriginalText = file.OriginalText,
                    ModifiedText = file.ModifiedText,
                }).ToList(),
            };
            GitStatusService.SelectDiffPath(restored, restored.SelectedPath);
            return restored;
        }

        private static string FormatDiffReviewSource(DiffReviewSourceKind kind)
        {
            return kind switch
            {
                DiffReviewSourceKind.Baseline => "baseline",
                DiffReviewSourceKind.Checkpoint => "checkpoint",
                _ => "live",
            };
        }

        private string ResolveActiveViewName()
        {
            if (_showingSettings)
            {
                return "settings";
            }

            return _showingOverview ? "overview" : "terminal";
        }

        private static DiffReviewSourceKind ParseDiffReviewSource(string value)
        {
            return value?.Trim().ToLowerInvariant() switch
            {
                "baseline" => DiffReviewSourceKind.Baseline,
                "checkpoint" => DiffReviewSourceKind.Checkpoint,
                _ => DiffReviewSourceKind.Live,
            };
        }

        private static void NormalizeDiffReviewSource(WorkspaceThread thread)
        {
            if (thread is null)
            {
                return;
            }

            if (thread.DiffReviewSource == DiffReviewSourceKind.Baseline && thread.BaselineSnapshot is not null)
            {
                return;
            }

            if (thread.DiffReviewSource == DiffReviewSourceKind.Checkpoint &&
                !string.IsNullOrWhiteSpace(thread.SelectedCheckpointId) &&
                thread.DiffCheckpoints.Any(checkpoint => string.Equals(checkpoint.Id, thread.SelectedCheckpointId, StringComparison.Ordinal)))
            {
                return;
            }

            thread.DiffReviewSource = DiffReviewSourceKind.Live;
            thread.SelectedCheckpointId = null;
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
                            DiffReviewSource = ParseDiffReviewSource(threadSnapshot.DiffReviewSource),
                            SelectedCheckpointId = threadSnapshot.SelectedCheckpointId,
                            BaselineSnapshot = RestoreGitThreadSnapshot(threadSnapshot.BaselineSnapshot),
                            LayoutPreset = WorkspaceSessionStore.ParseLayout(threadSnapshot.Layout),
                            PrimarySplitRatio = ClampPaneSplitRatio(threadSnapshot.PrimarySplitRatio <= 0 ? 0.58 : threadSnapshot.PrimarySplitRatio),
                            SecondarySplitRatio = ClampPaneSplitRatio(threadSnapshot.SecondarySplitRatio <= 0 ? 0.5 : threadSnapshot.SecondarySplitRatio),
                            AutoFitPaneContentLocked = threadSnapshot.AutoFitPaneContentLocked,
                        };
                        foreach (GitCheckpointSessionSnapshot checkpointSnapshot in threadSnapshot.DiffCheckpoints ?? new List<GitCheckpointSessionSnapshot>())
                        {
                            thread.DiffCheckpoints.Add(new WorkspaceDiffCheckpoint(checkpointSnapshot.Name, checkpointSnapshot.Id)
                            {
                                CapturedAt = DateTimeOffset.TryParse(checkpointSnapshot.CapturedAt, out DateTimeOffset capturedAt)
                                    ? capturedAt
                                    : DateTimeOffset.UtcNow,
                                Snapshot = RestoreGitThreadSnapshot(checkpointSnapshot.Snapshot),
                            });
                        }

                        NormalizeDiffReviewSource(thread);
                        project.Threads.Add(thread);

                        foreach (PaneSessionSnapshot paneSnapshot in threadSnapshot.Panes ?? new List<PaneSessionSnapshot>())
                        {
                            try
                            {
                                WorkspacePaneRecord pane = RestorePaneFromSnapshot(project, thread, paneSnapshot);
                                if (pane is not null)
                                {
                                    thread.Panes.Add(pane);
                                }
                            }
                            catch (Exception ex)
                            {
                                LogAutomationEvent("shell", "workspace.restore_pane_failed", $"Could not restore pane {paneSnapshot.Id}: {ex.Message}", new Dictionary<string, string>
                                {
                                    ["projectId"] = project.Id,
                                    ["threadId"] = thread.Id,
                                    ["paneId"] = paneSnapshot.Id ?? string.Empty,
                                    ["paneKind"] = paneSnapshot.Kind ?? string.Empty,
                                });
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
                _showingOverview = string.Equals(snapshot.ActiveView, "overview", StringComparison.OrdinalIgnoreCase);
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

        private WorkspaceThread CreateThread(WorkspaceProject project, string threadName = null, bool ensureInitialPane = true, WorkspaceThread inheritFromThread = null)
        {
            project ??= _activeProject ?? GetOrCreateProject(Environment.CurrentDirectory);

            WorkspaceThread thread = new(project, string.IsNullOrWhiteSpace(threadName) ? $"Thread {_threadSequence++}" : threadName.Trim());
            if (TryResolveInheritedWorktreePath(project, inheritFromThread, out string inheritedWorktreePath))
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

        private bool TryResolveInheritedWorktreePath(WorkspaceProject project, WorkspaceThread explicitSourceThread, out string worktreePath)
        {
            worktreePath = null;
            if (project is null)
            {
                return false;
            }

            WorkspaceThread sourceThread = explicitSourceThread;
            if (sourceThread is null && ReferenceEquals(project, _activeProject) && _activeThread is not null)
            {
                sourceThread = _activeThread;
            }
            else if (sourceThread is null && !string.IsNullOrWhiteSpace(project.SelectedThreadId))
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

            WorkspaceThread overflowThread = CreateThread(project, ensureInitialPane: false, inheritFromThread: thread);
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
                SetInspectorSection(InspectorSection.Files);
                RefreshInspectorFileBrowser(forceRebuild: true);
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

        private DiffPaneRecord AddOrSelectDiffPane(
            WorkspaceProject project,
            WorkspaceThread thread,
            string diffPath,
            string diffText,
            GitThreadSnapshot sourceSnapshot = null,
            DiffPaneDisplayMode displayMode = DiffPaneDisplayMode.FileCompare)
        {
            project ??= _activeProject ?? GetOrCreateProject(Environment.CurrentDirectory);
            thread = ResolveTargetThreadForNewPane(project, thread, WorkspacePaneKind.Diff);
            thread.SelectedDiffPath = diffPath;

            DiffPaneRecord pane = thread.Panes.OfType<DiffPaneRecord>().FirstOrDefault();
            bool created = false;
            if (pane is null)
            {
                pane = CreateDiffPane(project, thread, diffPath, diffText, BuildDiffPaneTitle(diffPath), sourceSnapshot, displayMode);
                thread.Panes.Add(pane);
                PromoteLayoutForPaneCount(thread);
                created = true;
            }
            else
            {
                UpdateDiffPane(pane, diffPath, diffText, sourceSnapshot, displayMode);
            }

            thread.SelectedPaneId = pane.Id;
            project.SelectedThreadId = thread.Id;

            if (thread == _activeThread)
            {
                RefreshTabView();
                SyncInspectorSectionWithSelectedPane();
                RefreshInspectorFileBrowser();
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

        private EditorPaneRecord AddEditorPane(WorkspaceProject project, WorkspaceThread thread, string initialFilePath = null)
        {
            project ??= _activeProject ?? GetOrCreateProject(Environment.CurrentDirectory);
            thread = ResolveTargetThreadForNewPane(project, thread, WorkspacePaneKind.Editor);
            string preferredFilePath = string.IsNullOrWhiteSpace(initialFilePath) ? thread?.SelectedDiffPath : initialFilePath;
            EditorPaneRecord pane = CreateEditorPane(project, thread, preferredFilePath, "Editor");
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
                ["initialFilePath"] = preferredFilePath ?? string.Empty,
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

        private EditorPaneRecord CreateEditorPane(WorkspaceProject project, WorkspaceThread thread, string initialFilePath, string initialTitle, string paneId = null)
        {
            string threadRootPath = ResolveThreadRootPath(project, thread);
            string baseTitle = string.IsNullOrWhiteSpace(initialTitle) ? "Editor" : initialTitle;
            EditorPaneControl editor = new()
            {
                ProjectRootPath = threadRootPath,
                InitialFilePath = initialFilePath,
            };
            editor.ApplyTheme(ResolveTheme(SampleConfig.CurrentTheme));
            editor.SetAutoFitWidth(thread.AutoFitPaneContentLocked);

            EditorPaneRecord pane = new(baseTitle, editor, paneId);
            editor.ApplyAutomationIdentity(pane.Id);
            AttachPaneInteraction(project, thread, pane);
            editor.TitleChanged += (_, title) =>
            {
                if (!pane.HasCustomTitle)
                {
                    pane.Title = string.IsNullOrWhiteSpace(title) ? baseTitle : title;
                }

                LogAutomationEvent("editor", "title.changed", $"Editor title changed to {pane.Title}", new Dictionary<string, string>
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
            editor.StateChanged += (_, _) =>
            {
                if (thread == _activeThread)
                {
                    RefreshInspectorFileBrowser();
                }

                QueueSessionSave();
            };
            return pane;
        }

        private DiffPaneRecord CreateDiffPane(
            WorkspaceProject project,
            WorkspaceThread thread,
            string diffPath,
            string diffText,
            string initialTitle,
            GitThreadSnapshot sourceSnapshot = null,
            DiffPaneDisplayMode displayMode = DiffPaneDisplayMode.FileCompare,
            string paneId = null)
        {
            string threadRootPath = ResolveThreadRootPath(project, thread);
            DiffPaneHostControl diffPaneControl = new()
            {
                ProjectRootPath = threadRootPath,
            };
            diffPaneControl.ApplyTheme(ResolveTheme(SampleConfig.CurrentTheme));
            diffPaneControl.SetAutoFitWidth(thread.AutoFitPaneContentLocked);

            DiffPaneRecord pane = new(initialTitle, diffPaneControl, diffPath, paneId);
            diffPaneControl.ApplyAutomationIdentity(pane.Id);
            AttachPaneInteraction(project, thread, pane);
            UpdateDiffPane(pane, diffPath, diffText, sourceSnapshot, displayMode);
            return pane;
        }

        private void UpdateDiffPane(
            DiffPaneRecord pane,
            string diffPath,
            string diffText,
            GitThreadSnapshot sourceSnapshot = null,
            DiffPaneDisplayMode? displayMode = null)
        {
            if (pane is null)
            {
                return;
            }

            pane.DiffPath = diffPath;
            string selectedDiffText = !string.IsNullOrWhiteSpace(sourceSnapshot?.SelectedDiff)
                ? sourceSnapshot.SelectedDiff
                : diffText;
            DiffPaneDisplayMode resolvedMode = displayMode ?? pane.DiffPane.DisplayMode;
            GitChangedFile selectedFile = sourceSnapshot?.ChangedFiles
                .FirstOrDefault(file => string.Equals(file.Path, diffPath, StringComparison.Ordinal));

            if (!pane.HasCustomTitle)
            {
                pane.Title = resolvedMode == DiffPaneDisplayMode.FullPatchReview
                    ? "Review Changes"
                    : BuildDiffPaneTitle(diffPath);
            }

            if (resolvedMode == DiffPaneDisplayMode.FullPatchReview && HasCompleteDiffSet(sourceSnapshot))
            {
                pane.DiffPane.ShowFullPatch(sourceSnapshot.ChangedFiles, diffPath);
            }
            else if (!string.IsNullOrWhiteSpace(selectedDiffText))
            {
                pane.DiffPane.ShowFileCompare(selectedFile, string.IsNullOrWhiteSpace(diffPath) ? "Patch review" : "Patch view");
            }
            else
            {
                pane.DiffPane.ShowUnifiedDiff(diffPath, selectedDiffText);
            }

            if (ReferenceEquals(FindThreadForPane(pane.Id), _activeThread))
            {
                UpdateTabViewItem(pane);
            }
        }

        private TerminalPaneRecord CreateTerminalPane(
            WorkspaceProject project,
            WorkspaceThread thread,
            WorkspacePaneKind kind,
            string startupInput,
            string initialTitle,
            string paneId = null,
            string restoreReplayCommand = null,
            bool autoStartSession = true,
            string suspendedStatusText = null)
        {
            string threadRootPath = ResolveThreadRootPath(project, thread);
            TerminalControl terminal = new()
            {
                DisplayWorkingDirectory = FormatThreadPath(project, thread),
                InitialWorkingDirectory = FormatThreadPath(project, thread),
                ProcessWorkingDirectory = ShellProfiles.ResolveProcessWorkingDirectory(threadRootPath),
                ShellCommand = ShellProfiles.BuildLaunchCommand(project.ShellProfileId, threadRootPath),
                LaunchEnvironment = BuildTerminalLaunchEnvironment(project, thread, threadRootPath),
                StartupInput = startupInput,
                RestoreReplayCommand = restoreReplayCommand,
                AutoStartSession = autoStartSession,
                SuspendedStatusText = suspendedStatusText,
            };

            terminal.ApplyTheme(ResolveTheme(SampleConfig.CurrentTheme));

            TerminalPaneRecord pane = new(initialTitle, terminal, kind, paneId);
            terminal.SetHeaderTitleOverride(pane.HasCustomTitle ? pane.Title : null);
            AttachPaneInteraction(project, thread, pane);
            terminal.SessionTitleChanged += (_, title) =>
            {
                if (!pane.HasCustomTitle)
                {
                    pane.Title = string.IsNullOrWhiteSpace(title) ? initialTitle : title;
                }
                terminal.SetHeaderTitleOverride(pane.HasCustomTitle ? pane.Title : null);
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
                QueueProjectTreeRefresh(immediate: true);
                QueueSessionSave();
            };
            terminal.ToolSessionStateChanged += (_, _) => QueueProjectTreeRefresh(immediate: true);
            terminal.ReplayRestoreStateChanged += (_, _) =>
            {
                pane.ReplayRestorePending = terminal.ReplayRestorePending;
                pane.ReplayRestoreFailed = terminal.ReplayRestoreFailed;
                pane.PersistExitedState = terminal.ReplayRestoreFailed;
                if (!terminal.ReplayRestorePending && !terminal.ReplayRestoreFailed)
                {
                    pane.MarkReplayRestoreSucceeded();
                }

                UpdateTabViewItem(pane);
                QueueProjectTreeRefresh(immediate: true);
                QueueSessionSave();
            };
            terminal.ToolInteractionCompleted += (_, _) =>
            {
                if (ReferenceEquals(thread, _activeThread) &&
                    string.Equals(thread.SelectedPaneId, pane.Id, StringComparison.Ordinal))
                {
                    return;
                }

                MarkPaneRequiresAttention(pane);
            };
            terminal.SessionExited += (_, _) =>
            {
                pane.MarkExited();
                pane.ReplayRestorePending = false;
                pane.ReplayRestoreFailed = terminal.ReplayRestoreFailed;
                pane.PersistExitedState = terminal.ReplayRestoreFailed;
                if (terminal.ReplayRestoreFailed)
                {
                    pane.MarkReplayRestoreFailed();
                }
                LogAutomationEvent("terminal", "session.exited", $"Terminal exited for pane {pane.Id}", new Dictionary<string, string>
                {
                    ["paneId"] = pane.Id,
                    ["threadId"] = thread.Id,
                    ["projectId"] = project.Id,
                    ["paneKind"] = pane.Kind.ToString().ToLowerInvariant(),
                    ["replayRestoreFailed"] = terminal.ReplayRestoreFailed.ToString(),
                });
                if (thread == _activeThread)
                {
                    UpdateTabViewItem(pane);
                }

                if (!string.IsNullOrWhiteSpace(pane.ReplayTool) || terminal.ReplayRestoreFailed)
                {
                    MarkPaneRequiresAttention(pane);
                }

                QueueProjectTreeRefresh(immediate: true);
                QueueSessionSave();
            };

            return pane;
        }

        private static IReadOnlyDictionary<string, string> BuildTerminalLaunchEnvironment(WorkspaceProject project, WorkspaceThread thread, string threadRootPath)
        {
            Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase)
            {
                ["WINMUX_REPO_ROOT"] = Environment.CurrentDirectory,
                ["WINMUX_PROJECT_ROOT"] = project?.RootPath ?? string.Empty,
                ["WINMUX_THREAD_ROOT"] = threadRootPath ?? string.Empty,
                ["WINMUX_PROJECT_ID"] = project?.Id ?? string.Empty,
                ["WINMUX_THREAD_ID"] = thread?.Id ?? string.Empty,
            };

            return values;
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
                "diff" => CreateDiffPane(
                    project,
                    thread,
                    snapshot.DiffPath,
                    diffText: null,
                    string.IsNullOrWhiteSpace(snapshot.Title) ? BuildDiffPaneTitle(snapshot.DiffPath) : snapshot.Title,
                    paneId: snapshot.Id),
                "editor" => CreateEditorPane(project, thread, snapshot.EditorFilePath ?? thread.SelectedDiffPath, string.IsNullOrWhiteSpace(snapshot.Title) ? "Editor" : snapshot.Title, snapshot.Id),
                _ => CreateTerminalPane(
                    project,
                    thread,
                    WorkspacePaneKind.Terminal,
                    startupInput: null,
                    initialTitle: string.IsNullOrWhiteSpace(snapshot.Title) ? FormatThreadPath(project, thread) : snapshot.Title,
                    paneId: snapshot.Id,
                    restoreReplayCommand: snapshot.IsExited ? null : snapshot.ReplayCommand,
                    autoStartSession: !snapshot.IsExited,
                    suspendedStatusText: snapshot.IsExited && snapshot.ReplayRestoreFailed
                        ? "Replay restore failed last time. Close the tab or reopen the saved session manually."
                        : null),
            };

            pane.HasCustomTitle = snapshot.HasCustomTitle;
            if (snapshot.HasCustomTitle && !string.IsNullOrWhiteSpace(snapshot.Title))
            {
                pane.Title = snapshot.Title;
            }
            if (pane is TerminalPaneRecord terminalPane)
            {
                terminalPane.Terminal.SetHeaderTitleOverride(pane.HasCustomTitle ? pane.Title : null);
            }
            pane.ReplayTool = snapshot.ReplayTool;
            pane.ReplaySessionId = snapshot.ReplaySessionId;
            pane.ReplayCommand = snapshot.ReplayCommand;
            pane.RestoredFromSession = true;
            pane.ReplayRestoreFailed = snapshot.ReplayRestoreFailed;
            pane.PersistExitedState = snapshot.IsExited && snapshot.ReplayRestoreFailed;
            if (snapshot.IsExited && pane is TerminalPaneRecord exitedPane)
            {
                exitedPane.MarkExited();
            }
            else if (!string.IsNullOrWhiteSpace(snapshot.ReplayCommand) && pane is TerminalPaneRecord replayPane)
            {
                replayPane.MarkReplayRestorePending();
            }
            if (pane is BrowserPaneRecord browserPane && snapshot.BrowserTabs?.Count > 0)
            {
                browserPane.Browser.RestoreTabSession(
                    snapshot.BrowserTabs.Select(tab => new BrowserPaneControl.BrowserPaneTabSnapshot
                    {
                        Id = tab.Id,
                        Title = tab.Title,
                        Uri = tab.Uri,
                    }).ToList(),
                    snapshot.SelectedBrowserTabId);
            }
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

            GitThreadSnapshot sourceSnapshot = ReferenceEquals(thread, _activeThread)
                ? ResolveDisplayedGitSnapshot()
                : null;
            if (sourceSnapshot is not null)
            {
                GitStatusService.SelectDiffPath(sourceSnapshot, thread.SelectedDiffPath);
            }

            DiffPaneRecord pane = CreateDiffPane(
                project,
                thread,
                thread.SelectedDiffPath,
                diffText: null,
                BuildDiffPaneTitle(thread.SelectedDiffPath),
                sourceSnapshot: sourceSnapshot);
            thread.Panes.Add(pane);
            PromoteLayoutForPaneCount(thread);
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

                ClearPaneAttention(pane);
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
                case EditorPaneRecord editorPane:
                    editorPane.Editor.InteractionRequested += (_, _) => ActivatePaneFromInteraction();
                    break;
            }
        }

        private void MarkPaneRequiresAttention(WorkspacePaneRecord pane)
        {
            if (pane is null || pane.RequiresAttention)
            {
                return;
            }

            pane.RequiresAttention = true;
            QueueProjectTreeRefresh(immediate: true);
        }

        private void ClearPaneAttention(WorkspacePaneRecord pane)
        {
            if (pane is null || !pane.RequiresAttention)
            {
                return;
            }

            pane.RequiresAttention = false;
            QueueProjectTreeRefresh(immediate: true);
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

                    if (string.Equals(thread.ZoomedPaneId, pane.Id, StringComparison.Ordinal))
                    {
                        thread.ZoomedPaneId = null;
                    }

                    thread.SelectedPaneId = thread.Panes.FirstOrDefault()?.Id;

                    if (thread == _activeThread)
                    {
                        _lastPaneWorkspaceRenderKey = null;
                        RefreshTabView();
                        RenderPaneWorkspace();
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

                    project.SelectedThreadId = thread.Id;
                    if (!ReferenceEquals(thread, _activeThread))
                    {
                        ActivateThread(thread);
                    }

                    SelectPane(pane);
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
            NormalizeDiffReviewSource(_activeThread);
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
            RefreshDiffReviewSourceControls();
            SyncInspectorSectionWithSelectedPane();
            RefreshInspectorFileBrowser(forceRebuild: true);
            SyncAutoFitStateForVisiblePanes(_activeThread);
            if (_activeThread.AutoFitPaneContentLocked)
            {
                ApplyFitToVisiblePanes(_activeThread, persistLockState: false, autoLock: true, reason: "thread-activated");
            }

            ClearPaneAttention(GetSelectedPane(_activeThread));
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
            if (pane is TerminalPaneRecord terminalPane)
            {
                terminalPane.Terminal.SetHeaderTitleOverride(pane.Title);
            }
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
            WorkspaceThread duplicate = CreateThread(project, $"Copy of {source.Name}", inheritFromThread: source);

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
                        AddEditorPane(project, duplicate, (pane as EditorPaneRecord)?.Editor.SelectedFilePath);
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
                    Spacing = 3,
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
                ToolTipService.SetToolTip(projectButton, FormatProjectPath(project));

                Grid projectLayout = new()
                {
                    ColumnSpacing = 6,
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
                    projectButton.Height = double.NaN;
                    projectButton.MinHeight = 36;
                    projectButton.Padding = new Thickness(8, 4, 30, 4);
                    StackPanel textStack = new()
                    {
                        Spacing = 0,
                    };
                    AutomationProperties.SetAutomationId(textStack, $"shell-project-text-{project.Id}");
                    Grid.SetColumn(textStack, 1);
                    TextBlock projectTitle = new()
                    {
                        Text = project.Name,
                        FontSize = 11.5,
                        TextWrapping = TextWrapping.NoWrap,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    };
                    AutomationProperties.SetAutomationId(projectTitle, $"shell-project-title-{project.Id}");
                    textStack.Children.Add(projectTitle);
                    TextBlock projectMeta = new()
                    {
                        Text = BuildProjectRailMeta(project),
                        Style = (Style)Application.Current.Resources["ShellInteractiveHintTextStyle"],
                        FontSize = 9.8,
                        TextWrapping = TextWrapping.NoWrap,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    };
                    AutomationProperties.SetAutomationId(projectMeta, $"shell-project-meta-{project.Id}");
                    ToolTipService.SetToolTip(projectMeta, FormatProjectPath(project));
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
                MenuFlyoutItem projectOverviewItem = new()
                {
                    Text = "Thread overview",
                    Tag = project.Id,
                    IsEnabled = project.Threads.Count > 0,
                };
                AutomationProperties.SetAutomationId(projectOverviewItem, $"shell-project-overview-{project.Id}");
                projectOverviewItem.Click += OnProjectOverviewMenuClicked;
                projectMenu.Items.Add(projectOverviewItem);
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
                    VerticalAlignment = VerticalAlignment.Center,
                    Width = 18,
                    Height = 18,
                    Opacity = 0.74,
                    Margin = new Thickness(0, 0, 6, 0),
                };
                AutomationProperties.SetAutomationId(addThreadButton, $"shell-project-add-thread-{project.Id}");
                AutomationProperties.SetName(addThreadButton, $"Add thread to {project.Name}");
                addThreadButton.Click += OnProjectAddThreadClicked;
                ToolTipService.SetToolTip(addThreadButton, "Add thread");
                addThreadButton.Content = new FontIcon
                {
                    FontSize = 9.5,
                    Glyph = "\uE710",
                };

                Grid projectHeader = new()
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };
                AutomationProperties.SetAutomationId(projectHeader, $"shell-project-header-{project.Id}");
                projectHeader.Children.Add(projectButton);
                projectHeader.Children.Add(addThreadButton);
                group.Children.Add(projectHeader);

                if (showProjectThreads)
                {
                    StackPanel threadStack = new()
                    {
                        Spacing = 1,
                        Margin = new Thickness(14, 0, 0, 0),
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                    };
                    AutomationProperties.SetAutomationId(threadStack, $"shell-thread-list-{project.Id}");

                    foreach (WorkspaceThread thread in project.Threads)
                    {
                        ThreadActivitySummary activitySummary = ResolveThreadActivitySummary(thread);
                        Button threadButton = new()
                        {
                            Style = (Style)Application.Current.Resources["ShellSidebarThreadButtonStyle"],
                            Tag = thread.Id,
                            HorizontalContentAlignment = HorizontalAlignment.Stretch,
                            HorizontalAlignment = HorizontalAlignment.Stretch,
                            Height = double.NaN,
                            MinHeight = 36,
                            Padding = new Thickness(8, 4, 8, 4),
                        };
                        AutomationProperties.SetAutomationId(threadButton, $"shell-thread-{thread.Id}");
                        AutomationProperties.SetName(threadButton, BuildThreadAutomationLabel(project, thread, activitySummary));
                        threadButton.Click += OnThreadButtonClicked;
                        threadButton.DoubleTapped += OnThreadButtonDoubleTapped;
                        ToolTipService.SetToolTip(threadButton, $"{FormatThreadPath(project, thread)} · {BuildOverviewPaneSummary(thread)}");

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
                            ColumnSpacing = 6,
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
                            Spacing = 2,
                        };
                        AutomationProperties.SetAutomationId(threadText, $"shell-thread-text-{thread.Id}");
                        Grid.SetColumn(threadText, 1);
                        TextBlock threadTitle = new()
                        {
                            Text = thread.Name,
                            FontSize = 11.25,
                            TextWrapping = TextWrapping.NoWrap,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                        };
                        AutomationProperties.SetAutomationId(threadTitle, $"shell-thread-title-{thread.Id}");
                        threadText.Children.Add(threadTitle);

                        Grid threadFooter = new()
                        {
                            ColumnDefinitions =
                            {
                                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                                new ColumnDefinition { Width = GridLength.Auto },
                                new ColumnDefinition { Width = GridLength.Auto },
                            },
                            ColumnSpacing = 6,
                        };
                        TextBlock threadMeta = new()
                        {
                            Text = BuildThreadRailMeta(project, thread),
                            Style = (Style)Application.Current.Resources["ShellInteractiveHintTextStyle"],
                            FontSize = 9.6,
                            TextWrapping = TextWrapping.NoWrap,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            VerticalAlignment = VerticalAlignment.Center,
                        };
                        AutomationProperties.SetAutomationId(threadMeta, $"shell-thread-meta-{thread.Id}");
                        threadFooter.Children.Add(threadMeta);

                        FrameworkElement paneStrip = BuildThreadPaneStrip(thread);
                        AutomationProperties.SetAutomationId(paneStrip, $"shell-thread-panes-{thread.Id}");
                        Grid.SetColumn(paneStrip, 1);
                        threadFooter.Children.Add(paneStrip);

                        FrameworkElement threadStatus = BuildThreadActivityIndicator(activitySummary);
                        if (threadStatus is not null)
                        {
                            AutomationProperties.SetAutomationId(threadStatus, $"shell-thread-status-{thread.Id}");
                            Grid.SetColumn(threadStatus, 2);
                            threadFooter.Children.Add(threadStatus);
                        }

                        threadText.Children.Add(threadFooter);

                        threadLayout.Children.Add(threadText);
                        threadButton.Content = threadLayout;
                        ApplySidebarThreadButtonState(threadButton, thread == _activeThread && !_showingSettings, activitySummary);
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

            int selectionGeneration = ++_tabSelectionChangeGeneration;
            bool previousSuppression = _suppressTabSelectionChanged;
            bool previousRefreshingTabView = _refreshingTabView;
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
                    if (selectedPane.Kind == WorkspacePaneKind.Editor)
                    {
                        SetInspectorSection(InspectorSection.Files);
                    }
                    else if (selectedPane.Kind == WorkspacePaneKind.Diff)
                    {
                        SetInspectorSection(InspectorSection.Review);
                    }
                }
            }
            finally
            {
                RestoreTabSelectionFlagsAsync(selectionGeneration, previousSuppression, previousRefreshingTabView);
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

        private static string ResolvePaneKindTabLabel(WorkspacePaneKind kind)
        {
            return kind switch
            {
                WorkspacePaneKind.Browser => "WEB",
                WorkspacePaneKind.Editor => "EDIT",
                WorkspacePaneKind.Diff => "DIFF",
                _ => "TERM",
            };
        }

        private static string ResolvePaneKindBrushKey(WorkspacePaneKind kind)
        {
            return kind switch
            {
                WorkspacePaneKind.Browser => "ShellWarningBrush",
                WorkspacePaneKind.Editor => "ShellSuccessBrush",
                WorkspacePaneKind.Diff => "ShellInfoBrush",
                _ => "ShellTextSecondaryBrush",
            };
        }

        private static string FormatLayoutPresetLabel(WorkspaceLayoutPreset preset)
        {
            return preset switch
            {
                WorkspaceLayoutPreset.Solo => "solo",
                WorkspaceLayoutPreset.Dual => "dual",
                WorkspaceLayoutPreset.Triple => "triple",
                _ => "grid",
            };
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

        private void RestoreTabSelectionFlagsAsync(int generation, bool previousSuppression, bool previousRefreshingTabView)
        {
            if (DispatcherQueue is null)
            {
                _suppressTabSelectionChanged = previousSuppression;
                _refreshingTabView = previousRefreshingTabView;
                return;
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                if (_tabSelectionChangeGeneration != generation)
                {
                    return;
                }

                _suppressTabSelectionChanged = previousSuppression;
                _refreshingTabView = previousRefreshingTabView;
            });
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
                    AddCenterSplitter(1, 1);
                    AddPaneCell(visiblePanes[2], 2, 2);
                    break;
                default:
                    ConfigureSplitRows(_activeThread.SecondarySplitRatio);
                    ConfigureSplitColumns(_activeThread.PrimarySplitRatio);
                    AddPaneCell(visiblePanes[0], 0, 0);
                    AddVerticalSplitter(0, 1, rowSpan: 3);
                    AddPaneCell(visiblePanes[1], 0, 2);
                    AddHorizontalSplitter(1, 0, columnSpan: 3);
                    AddCenterSplitter(1, 1);
                    AddPaneCell(visiblePanes[2], 2, 0);
                    AddPaneCell(visiblePanes[3], 2, 2);
                    break;
            }

            SyncAutoFitStateForVisiblePanes(_activeThread);
            UpdatePaneSelectionChrome();
        }

        private void AddPaneCell(WorkspacePaneRecord pane, int row, int column, int rowSpan = 1, int columnSpan = 1)
        {
            if (!_paneContainersById.TryGetValue(pane.Id, out Border border))
            {
                Grid containerContent = BuildPaneContainerContent(pane);
                border = new Border
                {
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Child = containerContent,
                    Margin = new Thickness(1),
                    Tag = pane,
                };
                AutomationProperties.SetAutomationId(border, $"shell-pane-{pane.Id}");
                border.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPaneContainerPointerPressed), true);
                _paneContainersById[pane.Id] = border;
            }
            else
            {
                border.Tag = pane;
            }

            border.Background = AppBrush(border, "ShellSurfaceBackgroundBrush");
            border.Visibility = Visibility.Visible;
            Grid.SetRow(border, row);
            Grid.SetColumn(border, column);
            Grid.SetRowSpan(border, rowSpan);
            Grid.SetColumnSpan(border, columnSpan);
            PaneWorkspaceGrid.Children.Add(border);
            UpdatePaneZoomButtonState(border, pane);

            if (pane is BrowserPaneRecord browserPane)
            {
                _ = browserPane.Browser.EnsureInitializedAsync();
            }
        }

        private Grid BuildPaneContainerContent(WorkspacePaneRecord pane)
        {
            Grid content = new();
            content.Children.Add(pane.View);

            Button zoomButton = new()
            {
                Width = 24,
                Height = 24,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 6, 6, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Style = (Style)Application.Current.Resources["ShellChromeButtonStyle"],
                Tag = pane,
            };
            zoomButton.Click += OnPaneZoomButtonClicked;
            content.Children.Add(zoomButton);
            return content;
        }

        private void UpdatePaneZoomButtonState(Border border, WorkspacePaneRecord pane)
        {
            if (border?.Child is not Grid content)
            {
                return;
            }

            Button zoomButton = content.Children.OfType<Button>().FirstOrDefault(button => ReferenceEquals(button.Tag, pane) || button.Tag is WorkspacePaneRecord);
            if (zoomButton is null)
            {
                return;
            }

            zoomButton.Tag = pane;
            bool isZoomed = string.Equals(_activeThread?.ZoomedPaneId, pane.Id, StringComparison.Ordinal);
            zoomButton.Content = new FontIcon
            {
                FontSize = 10.5,
                Glyph = isZoomed ? "\uE73F" : "\uE740",
            };
            AutomationProperties.SetAutomationId(zoomButton, $"shell-pane-zoom-{pane.Id}");
            AutomationProperties.SetName(zoomButton, isZoomed ? "Restore pane layout" : "Focus pane");
            ToolTipService.SetToolTip(zoomButton, isZoomed ? "Restore pane layout" : "Focus this pane");
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
            ToolTipService.SetToolTip(splitter, "Drag to resize. Hold Shift to resize both axes. Double-click to fit panes.");
            splitter.PointerPressed += OnPaneSplitterPointerPressed;
            splitter.PointerMoved += OnPaneSplitterPointerMoved;
            splitter.PointerReleased += OnPaneSplitterPointerReleased;
            splitter.PointerCanceled += OnPaneSplitterPointerCanceled;
            splitter.DoubleTapped += OnPaneSplitterDoubleTapped;
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
            ToolTipService.SetToolTip(splitter, "Drag to resize. Hold Shift to resize both axes. Double-click to fit panes.");
            splitter.PointerPressed += OnPaneSplitterPointerPressed;
            splitter.PointerMoved += OnPaneSplitterPointerMoved;
            splitter.PointerReleased += OnPaneSplitterPointerReleased;
            splitter.PointerCanceled += OnPaneSplitterPointerCanceled;
            splitter.DoubleTapped += OnPaneSplitterDoubleTapped;
            Grid.SetRow(splitter, row);
            Grid.SetColumn(splitter, column);
            Grid.SetColumnSpan(splitter, columnSpan);
            PaneWorkspaceGrid.Children.Add(splitter);
        }

        private void AddCenterSplitter(int row, int column)
        {
            Border splitter = new()
            {
                Width = 14,
                Height = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Background = AppBrush(PaneWorkspaceGrid, "ShellPaneDividerBrush"),
                BorderBrush = AppBrush(PaneWorkspaceGrid, "ShellBorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(-5),
                Tag = "both",
            };
            AutomationProperties.SetAutomationId(splitter, $"shell-pane-splitter-both-{row}-{column}");
            ToolTipService.SetToolTip(splitter, "Drag to resize both axes. Double-click to fit panes.");
            splitter.PointerPressed += OnPaneSplitterPointerPressed;
            splitter.PointerMoved += OnPaneSplitterPointerMoved;
            splitter.PointerReleased += OnPaneSplitterPointerReleased;
            splitter.PointerCanceled += OnPaneSplitterPointerCanceled;
            splitter.DoubleTapped += OnPaneSplitterDoubleTapped;
            Grid.SetRow(splitter, row);
            Grid.SetColumn(splitter, column);
            Canvas.SetZIndex(splitter, 3);
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
            bool resizeBothAxes = (e.KeyModifiers & Windows.System.VirtualKeyModifiers.Shift) == Windows.System.VirtualKeyModifiers.Shift;

            if (string.Equals(_activeSplitterDirection, "vertical", StringComparison.Ordinal))
            {
                UpdatePaneSplitRatiosFromPointer(point, adjustPrimary: true, adjustSecondary: resizeBothAxes);
                e.Handled = true;
                return;
            }

            if (string.Equals(_activeSplitterDirection, "both", StringComparison.Ordinal))
            {
                UpdatePaneSplitRatiosFromPointer(point, adjustPrimary: true, adjustSecondary: true);
                e.Handled = true;
                return;
            }

            if (string.Equals(_activeSplitterDirection, "horizontal", StringComparison.Ordinal))
            {
                UpdatePaneSplitRatiosFromPointer(point, adjustPrimary: resizeBothAxes, adjustSecondary: true);
                e.Handled = true;
            }
        }

        private void OnPaneSplitterDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            EqualizeVisiblePaneSplits(_activeThread, equalizePrimary: true, equalizeSecondary: true, reason: "splitter");
            e.Handled = true;
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

        private void UpdatePaneSplitRatiosFromPointer(Point point, bool adjustPrimary, bool adjustSecondary)
        {
            if (_activeThread is null)
            {
                return;
            }

            if (adjustPrimary && PaneWorkspaceGrid.ColumnDefinitions.Count >= 3)
            {
                double leftWidth = PaneWorkspaceGrid.ColumnDefinitions[0].ActualWidth;
                double rightWidth = PaneWorkspaceGrid.ColumnDefinitions[2].ActualWidth;
                double totalWidth = leftWidth + rightWidth;
                if (totalWidth > 0)
                {
                    double nextLeftWidth = Math.Clamp((totalWidth * _splitterStartPrimaryRatio) + (point.X - _splitterDragOriginX), totalWidth * MinPaneSplitRatio, totalWidth * MaxPaneSplitRatio);
                    _activeThread.PrimarySplitRatio = ClampPaneSplitRatio(nextLeftWidth / totalWidth);
                    PaneWorkspaceGrid.ColumnDefinitions[0].Width = new GridLength(_activeThread.PrimarySplitRatio, GridUnitType.Star);
                    PaneWorkspaceGrid.ColumnDefinitions[2].Width = new GridLength(1 - _activeThread.PrimarySplitRatio, GridUnitType.Star);
                }
            }

            if (adjustSecondary && PaneWorkspaceGrid.RowDefinitions.Count >= 3)
            {
                double topHeight = PaneWorkspaceGrid.RowDefinitions[0].ActualHeight;
                double bottomHeight = PaneWorkspaceGrid.RowDefinitions[2].ActualHeight;
                double totalHeight = topHeight + bottomHeight;
                if (totalHeight > 0)
                {
                    double nextTopHeight = Math.Clamp((totalHeight * _splitterStartSecondaryRatio) + (point.Y - _splitterDragOriginY), totalHeight * MinPaneSplitRatio, totalHeight * MaxPaneSplitRatio);
                    _activeThread.SecondarySplitRatio = ClampPaneSplitRatio(nextTopHeight / totalHeight);
                    PaneWorkspaceGrid.RowDefinitions[0].Height = new GridLength(_activeThread.SecondarySplitRatio, GridUnitType.Star);
                    PaneWorkspaceGrid.RowDefinitions[2].Height = new GridLength(1 - _activeThread.SecondarySplitRatio, GridUnitType.Star);
                }
            }
        }

        private void EqualizeVisiblePaneSplits(WorkspaceThread thread, bool equalizePrimary, bool equalizeSecondary, string reason)
        {
            if (thread is null)
            {
                return;
            }

            int visiblePaneCount = GetVisiblePanes(thread).Count();
            bool updated = false;

            if (equalizePrimary && visiblePaneCount >= 2)
            {
                thread.PrimarySplitRatio = 0.5;
                updated = true;
            }

            if (equalizeSecondary && visiblePaneCount >= 3)
            {
                thread.SecondarySplitRatio = 0.5;
                updated = true;
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

            QueueProjectTreeRefresh();
            QueueSessionSave();
            LogAutomationEvent("render", "pane.split_equalized", "Equalized visible pane splits", new Dictionary<string, string>
            {
                ["threadId"] = thread.Id,
                ["projectId"] = FindProjectForThread(thread)?.Id ?? string.Empty,
                ["reason"] = reason ?? string.Empty,
                ["visiblePaneCount"] = visiblePaneCount.ToString(),
                ["primarySplitRatio"] = thread.PrimarySplitRatio.ToString("0.000"),
                ["secondarySplitRatio"] = thread.SecondarySplitRatio.ToString("0.000"),
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

        private void OnPaneZoomButtonClicked(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is WorkspacePaneRecord pane)
            {
                TogglePaneZoom(pane);
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

        private void TogglePaneZoom(WorkspacePaneRecord pane)
        {
            if (_activeThread is null || pane is null)
            {
                return;
            }

            bool isZoomed = string.Equals(_activeThread.ZoomedPaneId, pane.Id, StringComparison.Ordinal);
            _activeThread.ZoomedPaneId = isZoomed ? null : pane.Id;
            _lastPaneWorkspaceRenderKey = null;
            RenderPaneWorkspace();
            RequestLayoutForVisiblePanes();
            UpdateHeader();
            QueueSessionSave();
            LogAutomationEvent("render", isZoomed ? "pane.zoom_reset" : "pane.zoomed", isZoomed ? "Restored pane layout" : "Focused pane in workspace", new Dictionary<string, string>
            {
                ["threadId"] = _activeThread.Id,
                ["projectId"] = _activeProject?.Id ?? string.Empty,
                ["paneId"] = pane.Id,
                ["paneKind"] = pane.Kind.ToString().ToLowerInvariant(),
            });
        }

        private void SelectPane(WorkspacePaneRecord pane, bool focusPane = true)
        {
            if (_activeThread is null || pane is null)
            {
                return;
            }

            ClearPaneAttention(pane);
            bool alreadySelected = string.Equals(_activeThread.SelectedPaneId, pane.Id, StringComparison.Ordinal);
            if (alreadySelected)
            {
                if (focusPane)
                {
                    FocusSelectedPane();
                }

                return;
            }

            _activeThread.SelectedPaneId = pane.Id;
            if (!string.IsNullOrWhiteSpace(_activeThread.ZoomedPaneId) &&
                !string.Equals(_activeThread.ZoomedPaneId, pane.Id, StringComparison.Ordinal))
            {
                _activeThread.ZoomedPaneId = pane.Id;
                _lastPaneWorkspaceRenderKey = null;
                RenderPaneWorkspace();
            }

            if (_tabItemsById.TryGetValue(pane.Id, out TabViewItem item) && !ReferenceEquals(TerminalTabs.SelectedItem, item))
            {
                int selectionGeneration = ++_tabSelectionChangeGeneration;
                bool previousSuppression = _suppressTabSelectionChanged;
                _suppressTabSelectionChanged = true;
                TerminalTabs.SelectedItem = item;
                RestoreTabSelectionFlagsAsync(selectionGeneration, previousSuppression, _refreshingTabView);
            }

            UpdatePaneSelectionChrome();
            SyncInspectorSectionWithSelectedPane();
            RefreshInspectorFileBrowser();
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
                if (border.Tag is WorkspacePaneRecord pane)
                {
                    UpdatePaneZoomButtonState(border, pane);
                }

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

            if (!string.IsNullOrWhiteSpace(thread.ZoomedPaneId))
            {
                WorkspacePaneRecord zoomedPane = thread.Panes.FirstOrDefault(candidate => string.Equals(candidate.Id, thread.ZoomedPaneId, StringComparison.Ordinal));
                if (zoomedPane is not null)
                {
                    return new[] { zoomedPane };
                }

                thread.ZoomedPaneId = null;
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
            _showingOverview = false;
            SettingsFrame.Navigate(typeof(SettingsPage));
            UpdateWorkspaceVisibility();
            UpdateSidebarActions();
            UpdateHeader();
            QueueSessionSave();
            LogAutomationEvent("shell", "view.settings", "Opened preferences");
        }

        private void ShowTerminalShell(bool queueGitRefresh = true)
        {
            bool wasShowingSettings = _showingSettings;
            bool wasShowingOverview = _showingOverview;
            _showingSettings = false;
            _showingOverview = false;
            UpdateWorkspaceVisibility();
            if (wasShowingSettings || wasShowingOverview)
            {
                _lastPaneWorkspaceRenderKey = null;
                RenderPaneWorkspace();
                RefreshTabView();
            }
            UpdateSidebarActions();
            FocusSelectedPane();
            RequestLayoutForVisiblePanes();
            UpdateHeader();
            if (queueGitRefresh)
            {
                QueueActiveThreadGitRefresh(_activeThread?.SelectedDiffPath ?? _activeGitSnapshot?.SelectedPath, preserveSelection: true);
            }

            QueueSessionSave();
            LogAutomationEvent("shell", "view.terminal", _activeThread is null ? "Showing empty project state" : "Showing pane workspace", new Dictionary<string, string>
            {
                ["projectId"] = _activeProject?.Id ?? string.Empty,
                ["threadId"] = _activeThread?.Id ?? string.Empty,
            });
        }

        private void ShowThreadOverview()
        {
            _showingSettings = false;
            _showingOverview = _activeProject is not null;
            UpdateWorkspaceVisibility();
            UpdateSidebarActions();
            UpdateHeader();
            QueueSessionSave();
            LogAutomationEvent("shell", "view.overview", _activeProject is null ? "Overview unavailable" : "Showing thread overview", new Dictionary<string, string>
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
            bool captureComplete = thread.Panes.OfType<DiffPaneRecord>().FirstOrDefault()?.DiffPane.RequiresCompleteSnapshot == true;
            Stopwatch stopwatch = Stopwatch.StartNew();

            GitThreadSnapshot snapshot = await System.Threading.Tasks.Task
                .Run(() => captureComplete
                    ? GitStatusService.CaptureComplete(worktreePath, targetPath)
                    : GitStatusService.Capture(worktreePath, targetPath))
                .ConfigureAwait(true);

            if (requestId != _latestGitRefreshRequestId ||
                !ReferenceEquals(thread, _activeThread) ||
                !ReferenceEquals(project, _activeProject))
            {
                return;
            }

            ApplyActiveGitSnapshot(snapshot);
            LogAutomationEvent("performance", "git.snapshot_ready", "Refreshed active thread git state", new Dictionary<string, string>
            {
                ["selectedPath"] = targetPath ?? string.Empty,
                ["durationMs"] = stopwatch.ElapsedMilliseconds.ToString(),
                ["mode"] = captureComplete ? "complete" : "partial",
            });
        }

        private async System.Threading.Tasks.Task CaptureDiffCheckpointAsync(string checkpointName = null)
        {
            if (_activeThread is null || _activeProject is null || _capturingDiffCheckpoint)
            {
                return;
            }

            WorkspaceThread thread = _activeThread;
            WorkspaceProject project = _activeProject;
            string selectedPath = ResolveDisplayedGitSnapshot()?.SelectedPath ?? thread.SelectedDiffPath;
            string worktreePath = thread.WorktreePath ?? project.RootPath;
            _capturingDiffCheckpoint = true;
            RefreshDiffReviewSourceControls();

            try
            {
                GitThreadSnapshot checkpointSnapshot = await System.Threading.Tasks.Task
                    .Run(() => GitStatusService.CaptureComplete(worktreePath, selectedPath))
                    .ConfigureAwait(true);

                if (!ReferenceEquals(thread, _activeThread) || !ReferenceEquals(project, _activeProject))
                {
                    return;
                }

                if (thread.BaselineSnapshot is null)
                {
                    thread.BaselineSnapshot = GitStatusService.CloneSnapshot(checkpointSnapshot);
                    LogAutomationEvent("git", "thread.baseline_captured", "Captured thread baseline from checkpoint flow", new Dictionary<string, string>
                    {
                        ["threadId"] = thread.Id,
                        ["projectId"] = project.Id,
                        ["selectedPath"] = checkpointSnapshot.SelectedPath ?? string.Empty,
                    });
                }

                WorkspaceDiffCheckpoint checkpoint = new(
                    string.IsNullOrWhiteSpace(checkpointName) ? $"Checkpoint {thread.DiffCheckpoints.Count + 1}" : checkpointName.Trim());
                checkpoint.CapturedAt = DateTimeOffset.UtcNow;
                checkpoint.Snapshot = GitStatusService.CloneSnapshot(checkpointSnapshot);
                thread.DiffCheckpoints.Add(checkpoint);

                RefreshDiffReviewSourceControls();
                QueueSessionSave();
                LogAutomationEvent("git", "checkpoint.captured", $"Captured {checkpoint.Name}", new Dictionary<string, string>
                {
                    ["threadId"] = thread.Id,
                    ["projectId"] = project.Id,
                    ["checkpointId"] = checkpoint.Id,
                    ["checkpointName"] = checkpoint.Name,
                    ["selectedPath"] = checkpointSnapshot.SelectedPath ?? string.Empty,
                });
            }
            finally
            {
                _capturingDiffCheckpoint = false;
                RefreshDiffReviewSourceControls();
            }
        }

        private void ApplyActiveGitSnapshot(GitThreadSnapshot snapshot)
        {
            MergeCachedDiffTexts(_activeGitSnapshot, snapshot);
            _activeGitSnapshot = snapshot;
            _activeThread.BranchName = snapshot.BranchName;
            _activeThread.WorktreePath = string.IsNullOrWhiteSpace(_activeThread.WorktreePath) ? snapshot.WorktreePath : _activeThread.WorktreePath;
            _activeThread.ChangedFileCount = snapshot.ChangedFiles.Count;
            _activeThread.SelectedDiffPath = snapshot.SelectedPath;
            EnsureThreadBaselineCapture(_activeThread, _activeProject, snapshot);
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

        private void EnsureThreadBaselineCapture(WorkspaceThread thread, WorkspaceProject project, GitThreadSnapshot liveSnapshot)
        {
            if (thread is null || project is null || thread.BaselineSnapshot is not null)
            {
                RefreshDiffReviewSourceControls();
                return;
            }

            if (!_baselineCaptureInFlightThreadIds.Add(thread.Id))
            {
                RefreshDiffReviewSourceControls();
                return;
            }

            string worktreePath = thread.WorktreePath ?? project.RootPath;
            string selectedPath = liveSnapshot?.SelectedPath;
            _ = System.Threading.Tasks.Task.Run(() => GitStatusService.CaptureComplete(worktreePath, selectedPath))
                .ContinueWith(task =>
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        _baselineCaptureInFlightThreadIds.Remove(thread.Id);
                        if (task.IsFaulted || task.IsCanceled)
                        {
                            LogAutomationEvent("git", "thread.baseline_failed", "Failed to capture thread baseline", new Dictionary<string, string>
                            {
                                ["threadId"] = thread.Id,
                                ["projectId"] = project.Id,
                            });
                            RefreshDiffReviewSourceControls();
                            return;
                        }

                        if (thread.BaselineSnapshot is null)
                        {
                            thread.BaselineSnapshot = GitStatusService.CloneSnapshot(task.Result);
                            QueueSessionSave();
                            LogAutomationEvent("git", "thread.baseline_captured", "Captured thread baseline", new Dictionary<string, string>
                            {
                                ["threadId"] = thread.Id,
                                ["projectId"] = project.Id,
                                ["selectedPath"] = task.Result?.SelectedPath ?? string.Empty,
                            });
                        }

                        RefreshDiffReviewSourceControls();
                        if (ReferenceEquals(thread, _activeThread) && thread.DiffReviewSource == DiffReviewSourceKind.Baseline)
                        {
                            ApplyGitSnapshotToUi();
                        }
                    });
                }, System.Threading.Tasks.TaskScheduler.Default);
            RefreshDiffReviewSourceControls();
        }

        private void ApplyGitSnapshotToUi()
        {
            RefreshDiffReviewSourceControls();
            GitThreadSnapshot displayedSnapshot = ResolveDisplayedGitSnapshot();
            if (displayedSnapshot is null)
            {
                DiffBranchText.Text = "No git context";
                DiffWorktreeText.Text = string.Empty;
                DiffSummaryText.Text = "No working tree changes";
                PopulateDiffFileList(null, null, null);
                if (_activeThread?.Panes.OfType<DiffPaneRecord>().FirstOrDefault() is DiffPaneRecord emptyDiffPane)
                {
                    UpdateDiffPane(emptyDiffPane, emptyDiffPane.DiffPath, null);
                }
                return;
            }

            int totalAddedLines = displayedSnapshot.ChangedFiles.Sum(file => file.AddedLines);
            int totalRemovedLines = displayedSnapshot.ChangedFiles.Sum(file => file.RemovedLines);
            DiffBranchText.Text = string.IsNullOrWhiteSpace(displayedSnapshot.BranchName)
                ? "Git metadata unavailable"
                : displayedSnapshot.BranchName;
            DiffWorktreeText.Text = displayedSnapshot.WorktreePath ?? string.Empty;
            DiffSummaryText.Text = string.IsNullOrWhiteSpace(displayedSnapshot.Error)
                ? FormatGitSummary(displayedSnapshot.StatusSummary, totalAddedLines, totalRemovedLines)
                : displayedSnapshot.Error;
            PopulateDiffFileList(displayedSnapshot.ChangedFiles, displayedSnapshot.SelectedPath, displayedSnapshot);

            if (_activeThread?.Panes.OfType<DiffPaneRecord>().FirstOrDefault() is DiffPaneRecord diffPane)
            {
                bool hasSelectedDiff = !string.IsNullOrWhiteSpace(displayedSnapshot.SelectedPath) &&
                    displayedSnapshot.ChangedFiles.Any(file => string.Equals(file.Path, displayedSnapshot.SelectedPath, StringComparison.Ordinal));
                DiffPaneDisplayMode displayMode = diffPane.DiffPane.DisplayMode;
                UpdateDiffPane(
                    diffPane,
                    hasSelectedDiff ? displayedSnapshot.SelectedPath : null,
                    hasSelectedDiff ? displayedSnapshot.SelectedDiff : null,
                    displayedSnapshot.ChangedFiles.Count > 0 ? displayedSnapshot : null,
                    displayMode);
            }

            RefreshInspectorFileBrowser();
        }

        private void PopulateDiffFileList(IReadOnlyList<GitChangedFile> changedFiles, string selectedPath, GitThreadSnapshot sourceSnapshot)
        {
            DiffFileListPanel.Children.Clear();
            IReadOnlyList<GitChangedFile> files = changedFiles ?? Array.Empty<GitChangedFile>();
            DiffEmptyText.Visibility = files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            foreach (GitChangedFile changedFile in files)
            {
                DiffFileListPanel.Children.Add(BuildDiffFileButton(changedFile));
            }

            if (sourceSnapshot is not null)
            {
                GitStatusService.SelectDiffPath(sourceSnapshot, selectedPath);
                if (ReferenceEquals(sourceSnapshot, _activeGitSnapshot) && _activeThread is not null)
                {
                    _activeThread.SelectedDiffPath = sourceSnapshot.SelectedPath;
                }
            }

            UpdateDiffFileSelection();
        }

        private async void SelectDiffPathInCurrentReview(string selectedPath)
        {
            if (_activeThread is null || string.IsNullOrWhiteSpace(selectedPath))
            {
                return;
            }

            GitThreadSnapshot displayedSnapshot = ResolveDisplayedGitSnapshot();
            if (displayedSnapshot is null)
            {
                return;
            }

            GitStatusService.SelectDiffPath(displayedSnapshot, selectedPath);
            if (ReferenceEquals(displayedSnapshot, _activeGitSnapshot))
            {
                _activeThread.SelectedDiffPath = displayedSnapshot.SelectedPath;
                UpdateDiffFileSelection();
                if (HasSelectedDiffAvailable(displayedSnapshot, displayedSnapshot.SelectedPath))
                {
                    AddOrSelectDiffPane(_activeProject, _activeThread, displayedSnapshot.SelectedPath, null, displayedSnapshot, DiffPaneDisplayMode.FileCompare);
                    QueueSessionSave();
                }
                else
                {
                    await EnsureSelectedDiffReadyAsync(_activeThread, _activeProject, displayedSnapshot.SelectedPath).ConfigureAwait(true);
                }
            }
            else
            {
                UpdateDiffFileSelection();
                AddOrSelectDiffPane(_activeProject, _activeThread, displayedSnapshot.SelectedPath, displayedSnapshot.SelectedDiff, displayedSnapshot, DiffPaneDisplayMode.FileCompare);
                QueueSessionSave();
            }
        }

        private static bool HasSelectedDiffAvailable(GitThreadSnapshot snapshot, string selectedPath = null)
        {
            if (snapshot is null)
            {
                return false;
            }

            string resolvedPath = selectedPath ?? snapshot.SelectedPath;
            if (string.IsNullOrWhiteSpace(resolvedPath))
            {
                return false;
            }

            return !string.IsNullOrWhiteSpace(snapshot.ChangedFiles
                .FirstOrDefault(file => string.Equals(file.Path, resolvedPath, StringComparison.Ordinal))?.DiffText);
        }

        private static bool HasCompleteDiffSet(GitThreadSnapshot snapshot)
        {
            return snapshot is not null &&
                snapshot.ChangedFiles.Count > 1 &&
                snapshot.ChangedFiles.All(file => !string.IsNullOrWhiteSpace(file.DiffText));
        }

        private async System.Threading.Tasks.Task EnsureSelectedDiffReadyAsync(WorkspaceThread thread, WorkspaceProject project, string selectedPath)
        {
            if (thread is null || project is null)
            {
                return;
            }

            int requestId = ++_latestGitRefreshRequestId;
            string worktreePath = thread.WorktreePath ?? project.RootPath;
            GitThreadSnapshot activeSnapshot = _activeGitSnapshot;
            GitChangedFile selectedFile = activeSnapshot?.ChangedFiles
                .FirstOrDefault(file => string.Equals(file.Path, selectedPath, StringComparison.Ordinal));
            Stopwatch stopwatch = Stopwatch.StartNew();

            if (selectedFile is null)
            {
                GitThreadSnapshot snapshot = await System.Threading.Tasks.Task
                    .Run(() => GitStatusService.Capture(worktreePath, selectedPath))
                    .ConfigureAwait(true);

                if (requestId != _latestGitRefreshRequestId ||
                    !ReferenceEquals(thread, _activeThread) ||
                    !ReferenceEquals(project, _activeProject))
                {
                    return;
                }

                if (!string.Equals(thread.SelectedDiffPath, selectedPath, StringComparison.Ordinal))
                {
                    return;
                }

                ApplyActiveGitSnapshot(snapshot);
                if (!string.IsNullOrWhiteSpace(snapshot.SelectedPath))
                {
                    AddOrSelectDiffPane(project, thread, snapshot.SelectedPath, snapshot.SelectedDiff, snapshot, DiffPaneDisplayMode.FileCompare);
                    QueueSessionSave();
                }

                LogAutomationEvent("performance", "diff.selection_ready", "Loaded selected diff via full snapshot capture", new Dictionary<string, string>
                {
                    ["selectedPath"] = selectedPath ?? string.Empty,
                    ["durationMs"] = stopwatch.ElapsedMilliseconds.ToString(),
                    ["mode"] = "capture",
                });
                return;
            }

            await System.Threading.Tasks.Task
                .Run(() => GitStatusService.EnsureDiffText(worktreePath, selectedFile))
                .ConfigureAwait(true);

            if (requestId != _latestGitRefreshRequestId ||
                !ReferenceEquals(thread, _activeThread) ||
                !ReferenceEquals(project, _activeProject))
            {
                return;
            }

            if (!string.Equals(thread.SelectedDiffPath, selectedPath, StringComparison.Ordinal))
            {
                return;
            }

            GitStatusService.SelectDiffPath(activeSnapshot, selectedPath);
            if (ReferenceEquals(activeSnapshot, _activeGitSnapshot))
            {
                _activeThread.SelectedDiffPath = activeSnapshot.SelectedPath;
            }

            UpdateDiffFileSelection();
            if (!string.IsNullOrWhiteSpace(activeSnapshot?.SelectedPath))
            {
                AddOrSelectDiffPane(project, thread, activeSnapshot.SelectedPath, activeSnapshot.SelectedDiff, activeSnapshot, DiffPaneDisplayMode.FileCompare);
                QueueSessionSave();
            }

            LogAutomationEvent("performance", "diff.selection_ready", "Loaded selected diff from cached snapshot", new Dictionary<string, string>
            {
                ["selectedPath"] = selectedPath ?? string.Empty,
                ["durationMs"] = stopwatch.ElapsedMilliseconds.ToString(),
                ["mode"] = "selected-file",
            });
        }

        private async System.Threading.Tasks.Task OpenFullPatchReviewAsync()
        {
            if (_activeThread is null || _activeProject is null)
            {
                return;
            }

            try
            {
                WorkspaceThread thread = _activeThread;
                WorkspaceProject project = _activeProject;
                GitThreadSnapshot displayedSnapshot = ResolveDisplayedGitSnapshot();
                if (displayedSnapshot is null)
                {
                    string worktreePath = thread.WorktreePath ?? project.RootPath;
                    displayedSnapshot = await System.Threading.Tasks.Task
                        .Run(() => GitStatusService.CaptureComplete(worktreePath, thread.SelectedDiffPath))
                        .ConfigureAwait(true);

                    if (!ReferenceEquals(thread, _activeThread) || !ReferenceEquals(project, _activeProject))
                    {
                        return;
                    }

                    ApplyActiveGitSnapshot(displayedSnapshot);
                }

                if (ReferenceEquals(displayedSnapshot, _activeGitSnapshot) && !HasCompleteDiffSet(displayedSnapshot))
                {
                    string selectedPath = displayedSnapshot.SelectedPath;
                    string worktreePath = thread.WorktreePath ?? project.RootPath;
                    GitThreadSnapshot snapshot = await System.Threading.Tasks.Task
                        .Run(() => GitStatusService.CaptureComplete(worktreePath, selectedPath))
                        .ConfigureAwait(true);

                    if (!ReferenceEquals(thread, _activeThread) || !ReferenceEquals(project, _activeProject))
                    {
                        return;
                    }

                    ApplyActiveGitSnapshot(snapshot);
                    displayedSnapshot = snapshot;
                }

                AddOrSelectDiffPane(
                    project,
                    thread,
                    displayedSnapshot.SelectedPath,
                    displayedSnapshot.SelectedDiff,
                    displayedSnapshot,
                    DiffPaneDisplayMode.FullPatchReview);
                LogAutomationEvent("git", "full_patch_review_opened", "Opened full patch review", new Dictionary<string, string>
                {
                    ["threadId"] = thread.Id,
                    ["projectId"] = project.Id,
                    ["selectedPath"] = displayedSnapshot.SelectedPath ?? string.Empty,
                });
            }
            catch (Exception ex)
            {
                LogAutomationEvent("git", "full_patch_review_failed", ex.Message);
            }
        }

        private static void MergeCachedDiffTexts(GitThreadSnapshot cachedSnapshot, GitThreadSnapshot nextSnapshot)
        {
            if (cachedSnapshot is null || nextSnapshot is null || nextSnapshot.ChangedFiles.Count == 0)
            {
                return;
            }

            Dictionary<string, GitChangedFile> cachedFilesByPath = cachedSnapshot.ChangedFiles
                .Where(file => !string.IsNullOrWhiteSpace(file.Path))
                .ToDictionary(file => file.Path, StringComparer.Ordinal);

            foreach (GitChangedFile changedFile in nextSnapshot.ChangedFiles)
            {
                if (string.IsNullOrWhiteSpace(changedFile.Path) ||
                    !cachedFilesByPath.TryGetValue(changedFile.Path, out GitChangedFile cachedFile))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(changedFile.DiffText))
                {
                    changedFile.DiffText = cachedFile.DiffText;
                }

                if (string.IsNullOrWhiteSpace(changedFile.OriginalText))
                {
                    changedFile.OriginalText = cachedFile.OriginalText;
                }

                if (string.IsNullOrWhiteSpace(changedFile.ModifiedText))
                {
                    changedFile.ModifiedText = cachedFile.ModifiedText;
                }

                if (string.IsNullOrWhiteSpace(changedFile.OriginalPath))
                {
                    changedFile.OriginalPath = cachedFile.OriginalPath;
                }
            }

            if (string.IsNullOrWhiteSpace(nextSnapshot.SelectedDiff) && !string.IsNullOrWhiteSpace(nextSnapshot.SelectedPath))
            {
                GitChangedFile selectedFile = nextSnapshot.ChangedFiles
                    .FirstOrDefault(file => string.Equals(file.Path, nextSnapshot.SelectedPath, StringComparison.Ordinal));
                nextSnapshot.SelectedDiff = selectedFile?.DiffText;
            }
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
                Style = (Style)Application.Current.Resources["ShellDiffFileButtonStyle"],
                Tag = changedFile,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
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

            Brush statusBrush = AppBrush(button, ResolveGitStatusBrushKey(changedFile.Status));
            Border statusBadge = new()
            {
                Padding = new Thickness(5, 0, 5, 0),
                MinWidth = 20,
                Height = 18,
                CornerRadius = new CornerRadius(4),
                VerticalAlignment = VerticalAlignment.Top,
                Background = CreateInspectorStatusBackground(statusBrush, isDirectory: false),
                Child = new TextBlock
                {
                    Text = ResolveGitStatusSymbol(changedFile.Status),
                    FontSize = 10,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = statusBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                },
            };
            AutomationProperties.SetAutomationId(statusBadge, $"shell-diff-file-status-{BuildAutomationKey(changedFile.Path)}");
            layout.Children.Add(statusBadge);

            StackPanel textStack = new()
            {
                Spacing = 1,
            };
            AutomationProperties.SetAutomationId(textStack, $"shell-diff-file-text-{BuildAutomationKey(changedFile.Path)}");
            Grid.SetColumn(textStack, 1);

            StackPanel titleRow = new()
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
            };
            Brush fileAccentBrush = AppBrush(button, ResolveInspectorIconBrushKey(changedFile.Path, isDirectory: false, decoration: null));
            titleRow.Children.Add(BuildInspectorPathBadge(changedFile.Path, isDirectory: false, fileAccentBrush));

            TextBlock fileNameText = new()
            {
                Text = Path.GetFileName(changedFile.DisplayName),
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            AutomationProperties.SetAutomationId(fileNameText, $"shell-diff-file-name-{BuildAutomationKey(changedFile.Path)}");
            titleRow.Children.Add(fileNameText);
            textStack.Children.Add(titleRow);

            TextBlock fileMetaText = new()
            {
                Text = BuildDiffFileMeta(changedFile),
                FontSize = 10.5,
                Opacity = 0.74,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            AutomationProperties.SetAutomationId(fileMetaText, $"shell-diff-file-meta-{BuildAutomationKey(changedFile.Path)}");
            textStack.Children.Add(fileMetaText);

            layout.Children.Add(textStack);

            StackPanel metricsStack = new()
            {
                Orientation = Orientation.Horizontal,
                Spacing = 7,
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

        private static string BuildDiffFileMeta(GitChangedFile changedFile)
        {
            string directory = Path.GetDirectoryName(changedFile?.DisplayName ?? string.Empty)?
                .Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(directory) || string.Equals(directory, ".", StringComparison.Ordinal))
            {
                return ResolveGitStatusDescription(changedFile?.Status);
            }

            return $"{directory} · {ResolveGitStatusDescription(changedFile?.Status)}";
        }

        private void UpdateDiffFileSelection()
        {
            string selectedPath = ResolveDisplayedGitSnapshot()?.SelectedPath;
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
            if (_activeThread is null)
            {
                return;
            }

            _paneLayoutTimer.Stop();
            _paneLayoutTimer.Start();
        }

        private void OnPaneLayoutTimerTick(DispatcherQueueTimer sender, object args)
        {
            _paneLayoutTimer.Stop();
            foreach (WorkspacePaneRecord pane in GetVisiblePanes(_activeThread))
            {
                pane.RequestLayout();
            }
        }

        private void SyncAutoFitStateForVisiblePanes(WorkspaceThread thread)
        {
            if (thread is null)
            {
                return;
            }

            foreach (WorkspacePaneRecord pane in GetVisiblePanes(thread))
            {
                switch (pane)
                {
                    case EditorPaneRecord editorPane:
                        editorPane.Editor.SetAutoFitWidth(thread.AutoFitPaneContentLocked);
                        break;
                    case DiffPaneRecord diffPane:
                        diffPane.DiffPane.SetAutoFitWidth(thread.AutoFitPaneContentLocked);
                        break;
                }
            }
        }

        private void ApplyFitToVisiblePanes(WorkspaceThread thread, bool persistLockState, bool autoLock, string reason)
        {
            if (thread is null)
            {
                return;
            }

            if (persistLockState)
            {
                thread.AutoFitPaneContentLocked = autoLock;
            }

            foreach (WorkspacePaneRecord pane in GetVisiblePanes(thread))
            {
                ApplyFitToPane(pane, autoLock);
            }

            if (ReferenceEquals(thread, _activeThread))
            {
                UpdateSidebarActions();
            }

            if (persistLockState)
            {
                QueueSessionSave();
            }

            LogAutomationEvent("render", "pane.fit_applied", "Applied fit-to-width for visible panes", new Dictionary<string, string>
            {
                ["threadId"] = thread.Id,
                ["projectId"] = FindProjectForThread(thread)?.Id ?? string.Empty,
                ["reason"] = reason ?? string.Empty,
                ["autoLock"] = autoLock.ToString(),
                ["persisted"] = persistLockState.ToString(),
            });
        }

        private static void ApplyFitToPane(WorkspacePaneRecord pane, bool autoLock)
        {
            switch (pane)
            {
                case EditorPaneRecord editorPane:
                    editorPane.Editor.ApplyFitToWidth(autoLock);
                    break;
                case DiffPaneRecord diffPane:
                    diffPane.DiffPane.ApplyFitToWidth(autoLock);
                    break;
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
            ApplyChromeButtonState(FitPanesButton, _activeThread?.AutoFitPaneContentLocked == true);
            if (FitPanesButton is not null)
            {
                ToolTipService.SetToolTip(
                    FitPanesButton,
                    _activeThread?.AutoFitPaneContentLocked == true
                        ? "Fit visible panes (auto-fit locked)"
                        : "Fit visible panes");
            }
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

            bool showInspector = _inspectorOpen && !_showingSettings && !_showingOverview && _activeThread is not null;

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

                if (_showingOverview)
                {
                    ThreadNameBox.Text = _activeProject?.Name ?? "Thread overview";
                    ThreadNameBox.IsReadOnly = true;
                    ActiveDirectoryText.Visibility = Visibility.Visible;
                    ActiveDirectoryText.Text = _activeProject is null
                        ? "No active project"
                        : $"{_activeProject.Threads.Count} thread{(_activeProject.Threads.Count == 1 ? string.Empty : "s")}";
                    return;
                }

                ThreadNameBox.IsReadOnly = _activeThread is null;
                ThreadNameBox.Text = _activeThread?.Name ?? "No thread selected";
                ActiveDirectoryText.Visibility = Visibility.Visible;
                ActiveDirectoryText.Text = _activeProject is null ? string.Empty : BuildHeaderContext(_activeProject, _activeThread);
            }
            finally
            {
                _suppressThreadNameSync = false;
            }
        }

        private static string BuildHeaderContext(WorkspaceProject project, WorkspaceThread thread)
        {
            string leaf = ShellProfiles.DeriveName(thread?.WorktreePath ?? project?.RootPath ?? string.Empty);
            if (string.IsNullOrWhiteSpace(leaf))
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(thread?.BranchName))
            {
                return $"{thread.BranchName} · {leaf}";
            }

            return leaf;
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
            thread.BaselineSnapshot = null;
            thread.DiffCheckpoints.Clear();
            thread.SelectedCheckpointId = null;
            thread.DiffReviewSource = DiffReviewSourceKind.Live;

            if (thread == _activeThread)
            {
                UpdateHeader();
                QueueActiveThreadGitRefresh();
            }

            QueueProjectTreeRefresh();
            RefreshDiffReviewSourceControls();
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
            RefreshDiffReviewSourceControls();
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
            UpdateThreadOverviewVisibility();
            UpdateInspectorVisibility();
        }

        private void UpdateThreadOverviewVisibility()
        {
            if (ThreadOverviewPanel is null || PaneWorkspaceGrid is null)
            {
                return;
            }

            bool showOverview = _showingOverview && !_showingSettings && _activeProject is not null;
            ThreadOverviewPanel.Visibility = showOverview ? Visibility.Visible : Visibility.Collapsed;
            PaneWorkspaceGrid.Visibility = showOverview ? Visibility.Collapsed : Visibility.Visible;

            if (showOverview)
            {
                RenderThreadOverview();
            }
        }

        private void RenderThreadOverview()
        {
            if (ThreadOverviewListPanel is null)
            {
                return;
            }

            string renderKey = BuildThreadOverviewRenderKey();
            if (string.Equals(renderKey, _lastThreadOverviewRenderKey, StringComparison.Ordinal))
            {
                return;
            }

            _lastThreadOverviewRenderKey = renderKey;
            ThreadOverviewListPanel.Children.Clear();

            if (_activeProject is null)
            {
                ThreadOverviewMetaText.Text = "No active project";
                return;
            }

            ThreadOverviewMetaText.Text = $"{_activeProject.Name} · {_activeProject.Threads.Count} thread{(_activeProject.Threads.Count == 1 ? string.Empty : "s")}";
            foreach (WorkspaceThread thread in _activeProject.Threads
                         .OrderByDescending(candidate => ReferenceEquals(candidate, _activeThread))
                         .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase))
            {
                bool isActiveThread = ReferenceEquals(thread, _activeThread);
                Border card = new()
                {
                    Background = AppBrush(ThreadOverviewListPanel, "ShellSurfaceBackgroundBrush"),
                    BorderBrush = AppBrush(ThreadOverviewListPanel, isActiveThread ? "ShellPaneActiveBorderBrush" : "ShellBorderBrush"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12),
                };
                AutomationProperties.SetAutomationId(card, $"shell-overview-card-{thread.Id}");

                Button button = new()
                {
                    Style = (Style)Application.Current.Resources["ShellButtonBaseStyle"],
                    Tag = thread.Id,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(0),
                };
                AutomationProperties.SetAutomationId(button, $"shell-overview-thread-{thread.Id}");
                button.Click += OnOverviewThreadClicked;

                Grid layout = new()
                {
                    RowSpacing = 10,
                };
                layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                Grid titleRow = new()
                {
                    ColumnDefinitions =
                    {
                        new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                        new ColumnDefinition { Width = GridLength.Auto },
                    },
                    ColumnSpacing = 8,
                };

                StackPanel headingStack = new()
                {
                    Spacing = 4,
                };

                TextBlock title = new()
                {
                    Text = thread.Name,
                    Style = (Style)Application.Current.Resources["ShellSectionTextStyle"],
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                AutomationProperties.SetAutomationId(title, $"shell-overview-title-{thread.Id}");
                headingStack.Children.Add(title);

                TextBlock path = new()
                {
                    Text = FormatThreadPath(_activeProject, thread),
                    Style = (Style)Application.Current.Resources["ShellInteractiveMetaTextStyle"],
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                AutomationProperties.SetAutomationId(path, $"shell-overview-path-{thread.Id}");
                headingStack.Children.Add(path);
                titleRow.Children.Add(headingStack);

                StackPanel summaryStack = new()
                {
                    Spacing = 2,
                    HorizontalAlignment = HorizontalAlignment.Right,
                };

                TextBlock paneCount = new()
                {
                    Text = thread.PaneSummary,
                    Style = (Style)Application.Current.Resources["ShellInteractiveMetaTextStyle"],
                    HorizontalTextAlignment = TextAlignment.Right,
                };
                AutomationProperties.SetAutomationId(paneCount, $"shell-overview-panes-{thread.Id}");
                summaryStack.Children.Add(paneCount);

                TextBlock layoutText = new()
                {
                    Text = $"{FormatLayoutPresetLabel(thread.LayoutPreset)} · {Math.Min(thread.VisiblePaneCapacity, thread.Panes.Count)} visible",
                    Style = (Style)Application.Current.Resources["ShellInteractiveHintTextStyle"],
                    HorizontalTextAlignment = TextAlignment.Right,
                };
                AutomationProperties.SetAutomationId(layoutText, $"shell-overview-layout-{thread.Id}");
                summaryStack.Children.Add(layoutText);

                Grid.SetColumn(summaryStack, 1);
                titleRow.Children.Add(summaryStack);
                layout.Children.Add(titleRow);

                TextBlock meta = new()
                {
                    Text = BuildOverviewMeta(thread),
                    Style = (Style)Application.Current.Resources["ShellInteractiveMetaTextStyle"],
                    TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                AutomationProperties.SetAutomationId(meta, $"shell-overview-meta-{thread.Id}");
                Grid.SetRow(meta, 1);
                layout.Children.Add(meta);

                TextBlock paneMapText = new()
                {
                    Text = BuildOverviewPaneSummary(thread),
                    Style = (Style)Application.Current.Resources["ShellInteractiveHintTextStyle"],
                    TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                AutomationProperties.SetAutomationId(paneMapText, $"shell-overview-pane-map-{thread.Id}");
                Grid.SetRow(paneMapText, 2);
                layout.Children.Add(paneMapText);

                button.Content = layout;
                card.Child = button;
                ThreadOverviewListPanel.Children.Add(card);
            }
        }

        private string BuildThreadOverviewRenderKey()
        {
            StringBuilder builder = new();
            builder.Append(_showingOverview ? '1' : '0')
                .Append('|')
                .Append(_activeProject?.Id)
                .Append('|')
                .Append(_activeThread?.Id);

            foreach (WorkspaceThread thread in _activeProject?.Threads ?? Enumerable.Empty<WorkspaceThread>())
            {
                builder.Append('|')
                    .Append(thread.Id)
                    .Append(':')
                    .Append(thread.Name)
                    .Append(':')
                    .Append(thread.WorktreePath)
                    .Append(':')
                    .Append(thread.BranchName)
                    .Append(':')
                    .Append(thread.LayoutPreset)
                    .Append(':')
                    .Append(thread.VisiblePaneCapacity)
                    .Append(':')
                    .Append(thread.Panes.Count)
                    .Append(':')
                    .Append(thread.SelectedPaneId)
                    .Append(':')
                    .Append(thread.SelectedDiffPath)
                    .Append(':')
                    .Append(thread.ChangedFileCount)
                    .Append(':')
                    .Append(thread.DiffCheckpoints.Count)
                    .Append(':')
                    .Append(thread.DiffReviewSource)
                    .Append(':')
                    .Append(thread.SelectedCheckpointId);

                foreach (WorkspacePaneRecord pane in thread.Panes)
                {
                    builder.Append(':')
                        .Append(pane.Id)
                        .Append(',')
                        .Append(pane.Kind)
                        .Append(',')
                        .Append(pane.Title)
                        .Append(',')
                        .Append(pane.IsExited);
                }
            }

            return builder.ToString();
        }

        private static string FormatOverviewReviewSource(WorkspaceThread thread)
        {
            return thread.DiffReviewSource switch
            {
                DiffReviewSourceKind.Baseline => "Baseline",
                DiffReviewSourceKind.Checkpoint => "Checkpoint",
                _ => "Live",
            };
        }

        private static string BuildOverviewPaneLabel(WorkspacePaneRecord pane)
        {
            string kind = pane.Kind switch
            {
                WorkspacePaneKind.Browser => "Web",
                WorkspacePaneKind.Editor => "Edit",
                WorkspacePaneKind.Diff => "Diff",
                _ => "Term",
            };

            string title = string.IsNullOrWhiteSpace(pane.Title) ? string.Empty : pane.Title.Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                return kind;
            }

            string normalized = pane.Kind switch
            {
                WorkspacePaneKind.Browser when title.StartsWith("Web ", StringComparison.OrdinalIgnoreCase) => title[4..],
                WorkspacePaneKind.Diff when title.StartsWith("Diff ", StringComparison.OrdinalIgnoreCase) => title[5..],
                _ => title,
            };

            string compact = normalized.Length > 18 ? normalized[..15] + "..." : normalized;
            return $"{kind} {compact}";
        }

        private static string BuildProjectRailMeta(WorkspaceProject project)
        {
            if (project is null)
            {
                return "No project";
            }

            int liveCount = 0;
            int readyCount = 0;
            foreach (WorkspaceThread thread in project.Threads)
            {
                ThreadActivitySummary summary = ResolveThreadActivitySummary(thread);
                if (summary?.IsRunning == true)
                {
                    liveCount++;
                }

                if (summary?.RequiresAttention == true)
                {
                    readyCount++;
                }
            }

            List<string> parts = new()
            {
                project.Threads.Count == 0
                    ? "No threads yet"
                    : $"{project.Threads.Count} thread{(project.Threads.Count == 1 ? string.Empty : "s")}",
            };
            if (liveCount > 0)
            {
                parts.Add($"{liveCount} live");
            }

            if (readyCount > 0)
            {
                parts.Add($"{readyCount} ready");
            }

            return string.Join(" · ", parts);
        }

        private static ThreadActivitySummary ResolveThreadActivitySummary(WorkspaceThread thread)
        {
            if (thread is null)
            {
                return null;
            }

            List<TerminalPaneRecord> terminalPanes = thread.Panes.OfType<TerminalPaneRecord>().ToList();
            List<TerminalPaneRecord> activeToolPanes = terminalPanes
                .Where(pane => pane.Terminal?.HasLiveToolSession == true && !pane.IsExited && !pane.ReplayRestoreFailed)
                .ToList();
            List<WorkspacePaneRecord> attentionPanes = thread.Panes
                .Where(pane => pane.RequiresAttention)
                .ToList();

            if (attentionPanes.Count > 0)
            {
                TerminalPaneRecord attentionToolPane = attentionPanes
                    .OfType<TerminalPaneRecord>()
                    .FirstOrDefault(pane => !string.IsNullOrWhiteSpace(pane.Terminal?.ActiveToolSession) || !string.IsNullOrWhiteSpace(pane.ReplayTool))
                    ?? activeToolPanes.FirstOrDefault();
                string toolName = ResolveToolName(attentionToolPane?.Terminal?.ActiveToolSession)
                    ?? ResolveToolName(attentionToolPane?.ReplayTool);
                return new ThreadActivitySummary
                {
                    Label = string.IsNullOrWhiteSpace(toolName)
                        ? (attentionPanes.Count == 1 ? "Ready" : $"{attentionPanes.Count} ready")
                        : toolName,
                    ToolTip = string.IsNullOrWhiteSpace(toolName)
                        ? $"{attentionPanes.Count} pane{(attentionPanes.Count == 1 ? string.Empty : "s")} have unread activity."
                        : $"{toolName} has unread activity.",
                    RequiresAttention = true,
                };
            }

            if (activeToolPanes.Count > 0)
            {
                string toolName = activeToolPanes.Count == 1
                    ? ResolveToolName(activeToolPanes[0].Terminal?.ActiveToolSession) ?? ResolveToolName(activeToolPanes[0].ReplayTool) ?? "Agent"
                    : $"{activeToolPanes.Count} live";
                return new ThreadActivitySummary
                {
                    Label = toolName,
                    ToolTip = activeToolPanes.Count == 1
                        ? $"{toolName} session is active."
                        : $"{activeToolPanes.Count} agent sessions are active.",
                    IsRunning = true,
                };
            }

            return null;
        }

        private static string ResolveToolName(string value)
        {
            return value?.Trim().ToLowerInvariant() switch
            {
                "claude" => "Claude",
                "codex" => "Codex",
                _ => null,
            };
        }

        private static string BuildThreadAutomationLabel(WorkspaceProject project, WorkspaceThread thread, ThreadActivitySummary summary)
        {
            StringBuilder builder = new();
            builder.Append(thread?.Name ?? "Thread");
            if (project is not null && thread is not null)
            {
                builder.Append(' ')
                    .Append(BuildThreadRailMeta(project, thread));
            }

            if (summary?.RequiresAttention == true)
            {
                builder.Append(' ')
                    .Append(summary.Label)
                    .Append(" ready");
            }
            else if (summary?.IsRunning == true)
            {
                builder.Append(' ')
                    .Append(summary.Label)
                    .Append(" active");
            }

            return builder.ToString();
        }

        private static string BuildThreadRailMeta(WorkspaceProject project, WorkspaceThread thread)
        {
            string worktreeName = ShellProfiles.DeriveName(thread.WorktreePath ?? project.RootPath);
            string location = string.IsNullOrWhiteSpace(thread.BranchName)
                ? worktreeName
                : thread.BranchName;
            string changeText = thread.ChangedFileCount <= 0
                ? "clean"
                : $"{thread.ChangedFileCount} changed";
            return $"{location} · {changeText}";
        }

        private FrameworkElement BuildThreadPaneStrip(WorkspaceThread thread)
        {
            StackPanel strip = new()
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                VerticalAlignment = VerticalAlignment.Center,
            };

            List<WorkspacePaneRecord> visiblePanes = GetVisiblePanes(thread).ToList();
            foreach (WorkspacePaneRecord pane in visiblePanes)
            {
                strip.Children.Add(BuildThreadPaneBadge(pane, string.Equals(thread.SelectedPaneId, pane.Id, StringComparison.Ordinal)));
            }

            int hiddenPaneCount = Math.Max(0, thread.Panes.Count - visiblePanes.Count);
            if (hiddenPaneCount > 0)
            {
                Border overflowBadge = new()
                {
                    MinWidth = 16,
                    Padding = new Thickness(3, 0, 3, 0),
                    CornerRadius = new CornerRadius(2.5),
                    BorderThickness = new Thickness(1),
                    BorderBrush = AppBrush(strip, "ShellBorderBrush"),
                    Background = AppBrush(strip, "ShellMutedSurfaceBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = $"+{hiddenPaneCount}",
                        FontSize = 8.8,
                        Foreground = AppBrush(strip, "ShellTextTertiaryBrush"),
                    },
                };
                ToolTipService.SetToolTip(overflowBadge, $"{hiddenPaneCount} additional pane{(hiddenPaneCount == 1 ? string.Empty : "s")} are hidden in this layout.");
                strip.Children.Add(overflowBadge);
            }

            return strip;
        }

        private static FrameworkElement BuildThreadPaneBadge(WorkspacePaneRecord pane, bool selected)
        {
            Border badge = new()
            {
                MinWidth = 15,
                Padding = new Thickness(3, 0, 3, 0),
                CornerRadius = new CornerRadius(2.5),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
            };

            badge.Background = selected
                ? CreateSidebarTintedBrush(AppBrush(badge, "ShellPaneActiveBorderBrush"), 0x18, Windows.UI.Color.FromArgb(0xFF, 0x25, 0x63, 0xEB))
                : CreateSidebarTintedBrush(AppBrush(badge, "ShellBorderBrush"), 0x0A, Windows.UI.Color.FromArgb(0xFF, 0x71, 0x71, 0x7A));
            badge.BorderBrush = selected
                ? CreateSidebarTintedBrush(AppBrush(badge, "ShellPaneActiveBorderBrush"), 0x78, Windows.UI.Color.FromArgb(0xFF, 0x25, 0x63, 0xEB))
                : CreateSidebarTintedBrush(AppBrush(badge, "ShellBorderBrush"), 0x7C, Windows.UI.Color.FromArgb(0xFF, 0x71, 0x71, 0x7A));

            TextBlock label = new()
            {
                Text = pane.Kind switch
                {
                    WorkspacePaneKind.Browser => "W",
                    WorkspacePaneKind.Editor => "E",
                    WorkspacePaneKind.Diff => "D",
                    _ => "T",
                },
                FontSize = 8.6,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = selected
                    ? AppBrush(badge, "ShellPaneActiveBorderBrush")
                    : AppBrush(badge, "ShellTextTertiaryBrush"),
            };
            badge.Child = label;
            ToolTipService.SetToolTip(badge, BuildOverviewPaneLabel(pane));
            return badge;
        }

        private static FrameworkElement BuildThreadActivityIndicator(ThreadActivitySummary summary)
        {
            if (summary is null)
            {
                return null;
            }

            string accentKey = summary.RequiresAttention ? "ShellSuccessBrush" : "ShellInfoBrush";
            Border badge = new()
            {
                Padding = new Thickness(5, 2, 5, 2),
                CornerRadius = new CornerRadius(999),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
            };
            badge.Background = CreateSidebarTintedBrush(AppBrush(badge, accentKey), summary.RequiresAttention ? (byte)0x1E : (byte)0x14, Windows.UI.Color.FromArgb(0xFF, 0x25, 0x63, 0xEB));
            badge.BorderBrush = CreateSidebarTintedBrush(AppBrush(badge, accentKey), summary.RequiresAttention ? (byte)0x52 : (byte)0x36, Windows.UI.Color.FromArgb(0xFF, 0x25, 0x63, 0xEB));

            StackPanel layout = new()
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center,
            };

            if (summary.RequiresAttention)
            {
                layout.Children.Add(new Border
                {
                    Width = 6,
                    Height = 6,
                    CornerRadius = new CornerRadius(3),
                    Background = AppBrush(badge, accentKey),
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            else
            {
                layout.Children.Add(new ProgressRing
                {
                    Width = 10,
                    Height = 10,
                    IsActive = true,
                    Foreground = AppBrush(badge, accentKey),
                });
            }

            layout.Children.Add(new TextBlock
            {
                Text = summary.Label,
                FontSize = 10,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = AppBrush(badge, summary.RequiresAttention ? "ShellTextPrimaryBrush" : "ShellTextSecondaryBrush"),
            });

            badge.Child = layout;
            ToolTipService.SetToolTip(badge, summary.ToolTip);
            return badge;
        }

        private static void ApplySidebarThreadButtonState(Button button, bool active, ThreadActivitySummary summary)
        {
            if (active)
            {
                button.Background = AppBrush(button, "ShellNavActiveBrush");
                button.BorderBrush = CreateSidebarTintedBrush(AppBrush(button, "ShellBorderBrush"), 0x7A, Windows.UI.Color.FromArgb(0xFF, 0x71, 0x71, 0x7A));
                button.Foreground = AppBrush(button, "ShellTextPrimaryBrush");
                return;
            }

            if (summary?.RequiresAttention == true)
            {
                Brush accentBrush = AppBrush(button, "ShellSuccessBrush");
                button.Background = CreateSidebarTintedBrush(accentBrush, 0x12, Windows.UI.Color.FromArgb(0xFF, 0x16, 0xA3, 0x4A));
                button.BorderBrush = CreateSidebarTintedBrush(accentBrush, 0x34, Windows.UI.Color.FromArgb(0xFF, 0x16, 0xA3, 0x4A));
                button.Foreground = AppBrush(button, "ShellTextPrimaryBrush");
                return;
            }

            if (summary?.IsRunning == true)
            {
                Brush accentBrush = AppBrush(button, "ShellInfoBrush");
                button.Background = CreateSidebarTintedBrush(accentBrush, 0x0F, Windows.UI.Color.FromArgb(0xFF, 0x25, 0x63, 0xEB));
                button.BorderBrush = CreateSidebarTintedBrush(accentBrush, 0x1E, Windows.UI.Color.FromArgb(0xFF, 0x25, 0x63, 0xEB));
                button.Foreground = AppBrush(button, "ShellTextPrimaryBrush");
                return;
            }

            button.Background = null;
            button.BorderBrush = null;
            button.Foreground = AppBrush(button, "ShellTextSecondaryBrush");
        }

        private static Brush CreateSidebarTintedBrush(Brush source, byte alpha, Windows.UI.Color fallbackBaseColor)
        {
            Windows.UI.Color baseColor = source is SolidColorBrush solid
                ? solid.Color
                : fallbackBaseColor;
            return new SolidColorBrush(Windows.UI.Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B));
        }

        private static string BuildOverviewMeta(WorkspaceThread thread)
        {
            string branchText = string.IsNullOrWhiteSpace(thread.BranchName) ? "No branch" : thread.BranchName;
            string diffText = string.IsNullOrWhiteSpace(thread.SelectedDiffPath) ? "No diff selected" : $"Reviewing {thread.SelectedDiffPath}";
            return $"{branchText} · {thread.ChangedFileCount} changed · {thread.DiffCheckpoints.Count} checkpoints · {FormatOverviewReviewSource(thread)} · {diffText}";
        }

        private string BuildOverviewPaneSummary(WorkspaceThread thread)
        {
            List<WorkspacePaneRecord> visiblePanes = GetVisiblePanes(thread).ToList();
            if (visiblePanes.Count == 0)
            {
                return "No visible panes";
            }

            string summary = string.Join(" · ", visiblePanes.Select(BuildOverviewPaneLabel));
            int hiddenPaneCount = Math.Max(0, thread.Panes.Count - visiblePanes.Count);
            if (hiddenPaneCount > 0)
            {
                summary += $" · +{hiddenPaneCount} more";
            }

            return summary;
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

        private IEnumerable<(WorkspaceProject Project, WorkspaceThread Thread, EditorPaneRecord Pane)> EnumerateEditorRecords()
        {
            return _projects.SelectMany(project => project.Threads.SelectMany(thread => thread.Panes.OfType<EditorPaneRecord>().Select(pane => (project, thread, pane))));
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

        private WorkspaceThread ResolveActionThread(NativeAutomationActionRequest request)
        {
            if (!string.IsNullOrWhiteSpace(request.ThreadId))
            {
                return FindThread(request.ThreadId);
            }

            if (_activeThread is not null)
            {
                return _activeThread;
            }

            WorkspaceProject project = ResolveActionProject(request);
            return project?.Threads.FirstOrDefault();
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

        private NativeAutomationUiNode BuildUiNodeTree(DependencyObject node, string path, ref int interactiveIndex, int depth)
        {
            if (node is null || ReferenceEquals(node, AutomationOverlayCanvas))
            {
                return null;
            }

            List<NativeAutomationUiNode> children = new();
            if (depth < AutomationUiTreeMaxDepth && ShouldTraverseUiNodeChildren(node))
            {
                int childCount = VisualTreeHelper.GetChildrenCount(node);
                for (int index = 0; index < childCount; index++)
                {
                    NativeAutomationUiNode childNode = BuildUiNodeTree(VisualTreeHelper.GetChild(node, index), $"{path}/{index}", ref interactiveIndex, depth + 1);
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
                        ?? TryExtractPropertyText(node, "Text");
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
                DiffReviewSource = FormatDiffReviewSource(thread.DiffReviewSource),
                SelectedCheckpointId = thread.SelectedCheckpointId,
                CheckpointCount = thread.DiffCheckpoints.Count,
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
                (ElementTheme.Light, "ShellPaneBackgroundBrush") => Windows.UI.Color.FromArgb(0xFF, 0xFA, 0xFA, 0xFA),
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
                (ElementTheme.Dark, "ShellPaneBackgroundBrush") => Windows.UI.Color.FromArgb(0xFF, 0x09, 0x09, 0x0B),
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
            button.BorderBrush = active ? AppBrush(button, "ShellPaneActiveBorderBrush") : null;
            button.Foreground = active ? AppBrush(button, "ShellTextPrimaryBrush") : AppBrush(button, "ShellTextSecondaryBrush");
        }

        private static string ResolveGitStatusSymbol(string status)
        {
            status = status?.Trim() ?? string.Empty;
            if (status.IndexOf('A') >= 0 || status == "??")
            {
                return "A";
            }

            if (status.IndexOf('D') >= 0)
            {
                return "D";
            }

            if (status.IndexOf('R') >= 0)
            {
                return "R";
            }

            return "M";
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

        private static void ApplyChromeButtonState(Button button, bool active)
        {
            if (button is null)
            {
                return;
            }

            button.Background = active ? AppBrush(button, "ShellNavActiveBrush") : null;
            button.BorderBrush = active ? AppBrush(button, "ShellBorderBrush") : null;
            button.Foreground = active ? AppBrush(button, "ShellTextPrimaryBrush") : AppBrush(button, "ShellTextSecondaryBrush");
        }

        private static void ApplyProjectButtonState(Button button, bool active)
        {
            button.Background = active ? AppBrush(button, "ShellNavActiveBrush") : null;
            button.BorderBrush = active
                ? CreateSidebarTintedBrush(AppBrush(button, "ShellBorderBrush"), 0x72, Windows.UI.Color.FromArgb(0xFF, 0x71, 0x71, 0x7A))
                : null;
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
            builder.Append(ResolveTheme(SampleConfig.CurrentTheme))
                .Append('|')
                .Append(ShellSplitView?.IsPaneOpen == true ? '1' : '0')
                .Append('|')
                .Append(_showingSettings ? '1' : '0')
                .Append('|')
                .Append(_activeProject?.Id)
                .Append('|')
                .Append(_activeThread?.Id)
                .Append('|');

            foreach (WorkspaceProject project in _projects)
            {
                int liveCount = 0;
                int readyCount = 0;
                foreach (WorkspaceThread projectThread in project.Threads)
                {
                    ThreadActivitySummary summary = ResolveThreadActivitySummary(projectThread);
                    if (summary?.IsRunning == true)
                    {
                        liveCount++;
                    }

                    if (summary?.RequiresAttention == true)
                    {
                        readyCount++;
                    }
                }

                builder.Append(project.Id)
                    .Append(':')
                    .Append(project.Name)
                    .Append(':')
                    .Append(project.SelectedThreadId)
                    .Append(':')
                    .Append(project.Threads.Count)
                    .Append(':')
                    .Append(project.RootPath)
                    .Append(':')
                    .Append(liveCount)
                    .Append(':')
                    .Append(readyCount)
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
                        .Append(':')
                        .Append(thread.ChangedFileCount)
                        .Append(':')
                        .Append(thread.SelectedPaneId)
                        .Append('|');

                    foreach (WorkspacePaneRecord pane in thread.Panes)
                    {
                        string activeToolSession = pane is TerminalPaneRecord terminalPane
                            ? terminalPane.Terminal.ActiveToolSession
                            : null;
                        builder.Append(pane.Id)
                            .Append(',')
                            .Append(pane.Kind)
                            .Append(',')
                            .Append(pane.Title)
                            .Append(',')
                            .Append(pane.IsExited ? '1' : '0')
                            .Append(',')
                            .Append(pane.RequiresAttention ? '1' : '0')
                            .Append(',')
                            .Append(pane.ReplayTool)
                            .Append(',')
                            .Append(activeToolSession)
                            .Append(',')
                            .Append(pane.ReplayRestorePending ? '1' : '0')
                            .Append(',')
                            .Append(pane.ReplayRestoreFailed ? '1' : '0')
                            .Append('|');
                    }
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
                .Append(_activeThread.ZoomedPaneId)
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
