using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Input;
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
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using WinRT.Interop;

namespace SelfContainedDeployment
{
    public partial class MainPage : Page
    {
        private const double PaneDividerThickness = 3;
        private const double MinPaneSplitRatio = 0.24;
        private const double MaxPaneSplitRatio = 0.76;
        private const int AutomationUiTreeMaxDepth = 28;
        private static readonly bool EnableBackgroundPaneWarmup = IsFeatureEnabled("WINMUX_ENABLE_BACKGROUND_PANE_WARMUP");
        private static readonly bool DisableSettingsPagePreload = IsFeatureEnabled("WINMUX_DISABLE_SETTINGS_PRELOAD");
        private static readonly bool EnableSettingsPagePreload = IsFeatureEnabled("WINMUX_ENABLE_SETTINGS_PRELOAD") || !DisableSettingsPagePreload;
        private static readonly bool EnableAutomaticBaselineCapture = IsFeatureEnabled("WINMUX_ENABLE_AUTOMATIC_BASELINE_CAPTURE");
        private static readonly bool DisableVisibleDeferredPaneMaterialization = IsFeatureEnabled("WINMUX_DISABLE_VISIBLE_DEFERRED_PANES");
        private static readonly bool EnableVisibleDeferredPaneMaterialization = IsFeatureEnabled("WINMUX_ENABLE_VISIBLE_DEFERRED_PANES") || !DisableVisibleDeferredPaneMaterialization;
        private const int MaxPersistedSnapshotDiffFiles = 8;
        private const int MaxPersistedSnapshotDiffChars = 200_000;
        private static readonly TimeSpan CachedThreadGitSnapshotMaxAge = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan CachedShellOnlyGitSnapshotMaxAge = TimeSpan.FromMinutes(30);
        private readonly List<WorkspaceProject> _projects = new();
        private readonly Dictionary<string, TabViewItem> _tabItemsById = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Border> _paneContainersById = new(StringComparer.Ordinal);
        private readonly List<Border> _paneSplitPreviewItems = new();
        private WorkspaceProject _activeProject;
        private WorkspaceThread _activeThread;
        private Border _activeSplitter;
        private string _activeSplitterDirection;
        private uint? _activeSplitterPointerId;
        private double _splitterDragOriginX;
        private double _splitterDragOriginY;
        private double _splitterStartPrimaryRatio;
        private double _splitterStartSecondaryRatio;
        private double? _splitterPreviewPrimaryRatio;
        private double? _splitterPreviewSecondaryRatio;
        private bool _showingSettings;
        private bool _suppressTabSelectionChanged;
        private bool _refreshingTabView;
        private int _tabSelectionChangeGeneration;
        private string _lastTabStripThreadId;
        private bool _suppressPaneInteractionRequests;
        private bool _suppressThreadNameSync;
        private DateTimeOffset _suppressSettingsUntil;
        private bool _settingsPageNeedsRefresh = true;
        private bool _settingsPagePreloadStarted;
        private string _inlineRenamingPaneId;
        private bool _restoringSession;
        private bool _inspectorOpen = true;
        private int _threadSequence = 1;
        private readonly UISettings _uiSettings;
        private readonly DispatcherQueueTimer _sessionSaveTimer;
        private readonly DispatcherQueueTimer _projectTreeRefreshTimer;
        private readonly DispatcherQueueTimer _gitRefreshTimer;
        private readonly DispatcherQueueTimer _paneLayoutTimer;
        private SessionSaveDetail _pendingSessionSaveDetail = SessionSaveDetail.Lightweight;
        private bool _sessionSaveInFlight;
        private bool _sessionSavePending;
        private readonly HashSet<string> _baselineCaptureInFlightThreadIds = new(StringComparer.Ordinal);
        private GitThreadSnapshot _activeGitSnapshot;
        private int _latestGitRefreshRequestId;
        private bool _gitRefreshInFlight;
        private bool _gitRefreshPending;
        private string _pendingGitSelectedPath;
        private bool _pendingGitPreserveSelection;
        private bool _pendingGitIncludeSelectedDiff = true;
        private bool _pendingGitPreferFastRefresh;
        private string _pendingGitCorrelationId;
        private string _lastProjectTreeRenderKey;
        private string _lastPaneWorkspaceRenderKey;
        private string _lastDiffFileListRenderKey;
        private bool _projectTreeRefreshEnqueued;
        private bool _suppressDiffReviewSourceSelectionChanged;
        private bool _capturingDiffCheckpoint;
        private NotesListScope _activeNotesListScope = NotesListScope.Thread;
        private InspectorSection _activeInspectorSection = InspectorSection.Review;
        private string _lastInspectorDirectoryRootPath;
        private string _pendingInspectorDirectoryRootPath;
        private string _pendingInspectorDirectoryRenderKey;
        private int _latestInspectorDirectoryBuildRequestId;
        private System.Threading.CancellationTokenSource _inspectorDirectoryBuildCancellation;
        private int _latestPaneWarmupRequestId;
        private readonly Dictionary<string, Button> _projectButtonsById = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Border> _projectHeaderBordersById = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Button> _threadButtonsById = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Button> _diffFileButtonsByPath = new(StringComparer.Ordinal);
        private readonly HashSet<string> _hoveredDiffFilePaths = new(StringComparer.Ordinal);
        private readonly List<DiffFileListItem> _diffFileListItems = new();
        private readonly Dictionary<string, ThreadActivitySummary> _threadActivitySummariesById = new(StringComparer.Ordinal);
        private readonly HashSet<string> _hoveredProjectIds = new(StringComparer.Ordinal);
        private readonly HashSet<string> _hoveredThreadIds = new(StringComparer.Ordinal);
        private readonly HashSet<string> _hoveredPaneStripButtonNames = new(StringComparer.Ordinal);
        private readonly HashSet<string> _expandedArchivedNoteThreadIds = new(StringComparer.Ordinal);
        private readonly Dictionary<string, NoteDraftState> _noteDraftsById = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TreeViewNode> _inspectorDirectoryNodesByPath = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<TreeViewNode, InspectorDirectoryTreeItem> _inspectorDirectoryItemsByNode = new();
        private readonly Dictionary<TreeViewNode, InspectorDirectoryNodeModel> _inspectorDirectoryModelsByNode = new();
        private readonly Dictionary<TreeViewNode, int> _inspectorDirectoryDepthByNode = new();
        private readonly Dictionary<string, InspectorDirectoryNodeModel> _inspectorDirectoryModelsByPath = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, InspectorDirectoryUiCache> _inspectorDirectoryUiCacheByKey = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, InspectorDirectoryUiCache> _inspectorDirectoryUiCacheByRootPath = new(StringComparer.OrdinalIgnoreCase);
        private int _paneFocusRequestId;
        private int _visibleDeferredPaneMaterializationRequestId;
        private bool _lifetimeResourcesReleased;

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

        private sealed class InspectorDirectoryBuildResult
        {
            public string RootPath { get; init; }

            public string RenderKey { get; init; }

            public int FileCount { get; init; }

            public List<InspectorDirectoryNodeModel> RootNodes { get; init; } = new();
        }

        private sealed class InspectorDirectoryUiCache
        {
            public string RootPath { get; init; }

            public string RenderKey { get; init; }

            public int FileCount { get; init; }

            public List<InspectorDirectoryNodeModel> RootNodes { get; init; } = new();

            public Dictionary<string, InspectorDirectoryNodeModel> ModelsByPath { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class ThreadActivitySummary
        {
            public string Label { get; init; }

            public string ToolTip { get; init; }

            public bool IsRunning { get; init; }

            public bool RequiresAttention { get; init; }
        }

        private sealed class NoteDraftState
        {
            public string EditableTitle { get; set; } = string.Empty;

            public string Text { get; set; }

            public bool Dirty { get; set; }
        }

        private sealed class InspectorNoteCardItem
        {
            public string NoteId { get; init; }

            public string ThreadId { get; init; }

            public string Title { get; init; }

            public string EditableTitle { get; init; }

            public string TitlePlaceholderText { get; init; }

            public string TitleEditorAutomationId { get; init; }

            public string Meta { get; init; }

            public string ScopeButtonLabel { get; init; }

            public string ScopeToolTip { get; init; }

            public string ArchiveButtonLabel { get; init; }

            public string ArchiveToolTip { get; init; }

            public string DeleteToolTip { get; init; }

            public string PlaceholderText { get; init; }

            public string EditorAutomationId { get; init; }

            public string Text { get; init; }

            public string StatusText { get; init; }

            public Visibility StatusVisibility => string.IsNullOrWhiteSpace(StatusText) ? Visibility.Collapsed : Visibility.Visible;

            public string TimestampText { get; init; }

            public Brush AccentBrush { get; init; }

            public Brush CardBackground { get; init; }

            public Brush CardBorderBrush { get; init; }

            public Visibility ArchiveButtonVisibility { get; init; }

            public bool IsArchived { get; init; }

            public bool IsSelected { get; init; }
        }

        private sealed class InspectorNoteGroupItem
        {
            public string ThreadId { get; init; }

            public string Title { get; init; }

            public string Meta { get; init; }

            public Brush HeaderAccentBrush { get; init; }

            public Visibility HeaderVisibility { get; init; }

            public string ArchivedToggleText { get; init; }

            public Visibility ArchivedSectionVisibility { get; init; }

            public Visibility ArchivedItemsVisibility { get; init; }

            public List<InspectorNoteCardItem> ActiveNotes { get; init; } = new();

            public List<InspectorNoteCardItem> ArchivedNotes { get; init; } = new();
        }

        private sealed class NoteScopeOption
        {
            public string ThreadId { get; init; }

            public string NoteId { get; init; }

            public string PaneId { get; init; }

            public string Label { get; init; }
        }

        private enum InspectorSection
        {
            Review,
            Files,
            Notes,
        }

        private enum NotesListScope
        {
            Thread,
            Project,
        }

        private enum SessionSaveDetail
        {
            Lightweight,
            Full,
        }

        public static MainPage Current;

        public MainPage()
        {
            InitializeComponent();
            Current = this;
            Loaded += OnLoaded;
            ActualThemeChanged += OnActualThemeChanged;
            InspectorDirectoryTree.Expanding += OnInspectorDirectoryTreeExpanding;
            _uiSettings = new UISettings();
            _sessionSaveTimer = DispatcherQueue.CreateTimer();
            _sessionSaveTimer.IsRepeating = false;
            _sessionSaveTimer.Interval = TimeSpan.FromMilliseconds(1200);
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

        internal void ReleaseLifetimeResources()
        {
            if (_lifetimeResourcesReleased)
            {
                return;
            }

            _lifetimeResourcesReleased = true;
            Loaded -= OnLoaded;
            ActualThemeChanged -= OnActualThemeChanged;
            InspectorDirectoryTree.Expanding -= OnInspectorDirectoryTreeExpanding;
            _sessionSaveTimer.Stop();
            _sessionSaveTimer.Tick -= OnSessionSaveTimerTick;
            _projectTreeRefreshTimer.Stop();
            _projectTreeRefreshTimer.Tick -= OnProjectTreeRefreshTimerTick;
            _gitRefreshTimer.Stop();
            _gitRefreshTimer.Tick -= OnGitRefreshTimerTick;
            _paneLayoutTimer.Stop();
            _paneLayoutTimer.Tick -= OnPaneLayoutTimerTick;
            _latestPaneWarmupRequestId++;
            _latestInspectorDirectoryBuildRequestId++;
            _paneFocusRequestId++;
            _gitRefreshPending = false;
            _gitRefreshInFlight = false;
            _pendingGitCorrelationId = null;
            _inspectorDirectoryBuildCancellation?.Cancel();
            _inspectorDirectoryBuildCancellation?.Dispose();
            _inspectorDirectoryBuildCancellation = null;
            DisposeAllWorkspacePanes();
            _tabItemsById.Clear();
            _paneContainersById.Clear();
            _projectButtonsById.Clear();
            _projectHeaderBordersById.Clear();
            _threadButtonsById.Clear();
            _diffFileButtonsByPath.Clear();
            _threadActivitySummariesById.Clear();
            _hoveredProjectIds.Clear();
            _hoveredThreadIds.Clear();
            _inspectorDirectoryNodesByPath.Clear();
            _inspectorDirectoryItemsByNode.Clear();
            _inspectorDirectoryModelsByNode.Clear();
            _inspectorDirectoryDepthByNode.Clear();
            _inspectorDirectoryModelsByPath.Clear();
            _inspectorDirectoryUiCacheByKey.Clear();
            _inspectorDirectoryUiCacheByRootPath.Clear();
            _baselineCaptureInFlightThreadIds.Clear();
            _lastDiffFileListRenderKey = null;
            CancelPendingInspectorDirectoryBuilds();
            _projects.Clear();
            _activeProject = null;
            _activeThread = null;
            if (ReferenceEquals(Current, this))
            {
                Current = null;
            }
        }

        private void DisposeAllWorkspacePanes()
        {
            foreach (WorkspacePaneRecord pane in _projects
                .SelectMany(project => project.Threads)
                .SelectMany(thread => thread.Panes)
                .ToList())
            {
                try
                {
                    pane.DisposePane();
                }
                catch
                {
                }
            }

            foreach (Border container in _paneContainersById.Values)
            {
                container.Child = null;
            }
        }

        private static void LogAutomationEvent(string category, string name, string message = null, IReadOnlyDictionary<string, string> data = null)
        {
            NativeAutomationEventLog.Record(category, name, message, data);
        }

        public void ApplyTheme(ElementTheme theme)
        {
            if (SampleConfig.CurrentTheme == theme && ShellRoot.RequestedTheme == theme)
            {
                return;
            }

            SampleConfig.CurrentTheme = theme;
            ShellRoot.RequestedTheme = theme;
            SettingsFrame.RequestedTheme = theme;
            ((App)Application.Current).MainWindowInstance?.ApplyChromeTheme(ResolveTheme(theme));
            _settingsPageNeedsRefresh = !_showingSettings;

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
            StartShellThemeTransition();
            PlayShellLayoutTransition(includeSidebar: ShellSplitView?.IsPaneOpen == true, includeInspector: _inspectorOpen);
            LogAutomationEvent("shell", "theme.changed", $"Theme set to {ResolveTheme(theme).ToString().ToLowerInvariant()}", new Dictionary<string, string>
            {
                ["theme"] = ResolveTheme(theme).ToString().ToLowerInvariant(),
            });
            QueueSessionSave();
        }

        private bool AnimationsEnabled => _uiSettings?.AnimationsEnabled ?? true;

        private void PlayShellLayoutTransition(bool includeSidebar, bool includeInspector)
        {
            AnimateOpacity(ShellMainContent, 0.965, 1, 170);
            AnimateOpacity(PaneWorkspaceShell, 0.94, 1, 170);

            if (includeSidebar)
            {
                AnimateOpacity(SidebarScrollViewer, 0.84, 1, 160);
                AnimateOpacity(SidebarFooterStack, 0.88, 1, 160);

                if (PaneBrandTextStack?.Visibility == Visibility.Visible)
                {
                    AnimateOpacity(PaneBrandTextStack, 0, 1, 190);
                }
            }

            if (includeInspector && InspectorSidebar?.Visibility == Visibility.Visible)
            {
                AnimateOpacity(InspectorSidebar, 0, 1, 190);
            }
        }

        private void StartShellThemeTransition()
        {
            if (ShellThemeTransitionOverlay is null)
            {
                return;
            }

            if (!AnimationsEnabled)
            {
                ShellThemeTransitionOverlay.Opacity = 0;
                ShellThemeTransitionOverlay.Visibility = Visibility.Collapsed;
                return;
            }

            ShellThemeTransitionOverlay.Visibility = Visibility.Visible;
            AnimateOpacity(ShellThemeTransitionOverlay, 1, 0, 180, () =>
            {
                if (ShellThemeTransitionOverlay is not null)
                {
                    ShellThemeTransitionOverlay.Opacity = 0;
                    ShellThemeTransitionOverlay.Visibility = Visibility.Collapsed;
                }
            });
        }

        private void AnimateOpacity(FrameworkElement element, double from, double to, int durationMs, Action completed = null)
        {
            if (element is null)
            {
                completed?.Invoke();
                return;
            }

            if (!AnimationsEnabled || durationMs <= 0)
            {
                element.Opacity = to;
                completed?.Invoke();
                return;
            }

            Storyboard storyboard = new();
            DoubleAnimation opacityAnimation = new()
            {
                From = from,
                To = to,
                Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
                EasingFunction = new CubicEase
                {
                    EasingMode = EasingMode.EaseOut,
                },
                EnableDependentAnimation = true,
            };

            element.Opacity = from;
            Storyboard.SetTarget(opacityAnimation, element);
            Storyboard.SetTargetProperty(opacityAnimation, nameof(UIElement.Opacity));
            storyboard.Children.Add(opacityAnimation);
            if (completed is not null)
            {
                storyboard.Completed += (_, _) => completed();
            }

            storyboard.Begin();
        }

        public void ApplyShellProfile(string profileId)
        {
            string resolvedProfileId = ShellProfiles.Resolve(profileId).Id;
            if (string.Equals(SampleConfig.DefaultShellProfileId, resolvedProfileId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_activeProject?.ShellProfileId, resolvedProfileId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            SampleConfig.DefaultShellProfileId = resolvedProfileId;
            _settingsPageNeedsRefresh = !_showingSettings;

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
            int resolvedPaneLimit = Math.Clamp(paneLimit, 2, 4);
            if (SampleConfig.MaxPaneCountPerThread == resolvedPaneLimit)
            {
                return;
            }

            SampleConfig.MaxPaneCountPerThread = resolvedPaneLimit;
            _settingsPageNeedsRefresh = !_showingSettings;

            RefreshProjectTree();
            RefreshTabView();
            RequestLayoutForVisiblePanes();

            LogAutomationEvent("shell", "pane-limit.changed", $"Thread pane limit set to {SampleConfig.MaxPaneCountPerThread}", new Dictionary<string, string>
            {
                ["paneLimit"] = SampleConfig.MaxPaneCountPerThread.ToString(),
            });
            QueueSessionSave();
        }

        public NativeAutomationUiTreeResponse GetAutomationUiTree()
        {
            using var perfScope = NativeAutomationDiagnostics.TrackOperation("ui-tree.snapshot");
            NativeAutomationDiagnostics.IncrementCounter("uiTreeSnapshot.count");
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
            NativeAutomationActionScope scope = NativeAutomationDiagnostics.BeginAction("ui-action", request.Action, new Dictionary<string, string>
            {
                ["automationId"] = request.AutomationId ?? string.Empty,
                ["refLabel"] = request.RefLabel ?? string.Empty,
                ["elementId"] = request.ElementId ?? string.Empty,
                ["text"] = request.Text ?? string.Empty,
            });

            try
            {
                DependencyObject target = FindUiElement(request);
                if (target is null)
                {
                    if (TryPerformKnownUiActionWithoutElement(request))
                    {
                        NativeAutomationDiagnostics.CompleteAction(scope);
                        return new NativeAutomationUiActionResponse
                        {
                            Ok = true,
                            CorrelationId = scope.CorrelationId,
                            DurationMs = scope.Stopwatch.Elapsed.TotalMilliseconds,
                        };
                    }

                    NativeAutomationDiagnostics.CompleteAction(scope);
                    return new NativeAutomationUiActionResponse
                    {
                        Ok = false,
                        Message = "No matching UI element was found.",
                        CorrelationId = scope.CorrelationId,
                        DurationMs = scope.Stopwatch.Elapsed.TotalMilliseconds,
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
                        NativeAutomationDiagnostics.CompleteAction(scope);
                        return new NativeAutomationUiActionResponse
                        {
                            Ok = false,
                            Message = $"Unknown ui action '{request.Action}'.",
                            CorrelationId = scope.CorrelationId,
                            DurationMs = scope.Stopwatch.Elapsed.TotalMilliseconds,
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
                    ["correlationId"] = scope.CorrelationId,
                });

                NativeAutomationDiagnostics.CompleteAction(scope);
                return new NativeAutomationUiActionResponse
                {
                    Ok = true,
                    Target = snapshot,
                    CorrelationId = scope.CorrelationId,
                    DurationMs = scope.Stopwatch.Elapsed.TotalMilliseconds,
                };
            }
            catch (Exception ex)
            {
                NativeAutomationDiagnostics.CompleteAction(scope, ex);
                return new NativeAutomationUiActionResponse
                {
                    Ok = false,
                    Message = ex.Message,
                    CorrelationId = scope.CorrelationId,
                    DurationMs = scope.Stopwatch.Elapsed.TotalMilliseconds,
                };
            }
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
                    CredentialAutofillOutcome = pane.Browser.CredentialAutofillOutcome,
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
            List<NativeAutomationProjectState> projects = new(_projects.Count);
            List<NativeAutomationThreadState> threads = new();
            foreach (WorkspaceProject project in _projects)
            {
                List<NativeAutomationThreadState> projectThreads = project.Threads.Select(BuildThreadState).ToList();
                projects.Add(new NativeAutomationProjectState
                {
                    Id = project.Id,
                    Name = project.Name,
                    RootPath = project.RootPath,
                    DisplayPath = FormatProjectPath(project),
                    ShellProfileId = project.ShellProfileId,
                    SelectedThreadId = project.SelectedThreadId,
                    Threads = projectThreads,
                    Notes = project.Threads.SelectMany(BuildThreadNoteStates).ToList(),
                });
                threads.AddRange(projectThreads);
            }

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
                InspectorSection = FormatInspectorSection(_activeInspectorSection),
                NotesScope = _activeNotesListScope == NotesListScope.Project ? "project" : "thread",
                ShellProfileId = _activeProject?.ShellProfileId,
                BrowserCredentialCount = BrowserCredentialStore.GetCredentialCount(),
                GitBranch = displayedSnapshot?.BranchName ?? _activeThread?.BranchName,
                WorktreePath = displayedSnapshot?.WorktreePath ?? _activeThread?.WorktreePath,
                ChangedFileCount = displayedSnapshot?.ChangedFiles.Count ?? _activeThread?.ChangedFileCount ?? 0,
                SelectedDiffPath = displayedSnapshot?.SelectedPath ?? _activeThread?.SelectedDiffPath,
                DiffReviewSource = _activeThread is null ? "live" : FormatDiffReviewSource(_activeThread.DiffReviewSource),
                SelectedCheckpointId = _activeThread?.SelectedCheckpointId,
                CheckpointCount = _activeThread?.DiffCheckpoints.Count ?? 0,
                Projects = projects,
                Threads = threads,
            };
        }

        public NativeAutomationActionResponse PerformAutomationAction(NativeAutomationActionRequest request)
        {
            request ??= new NativeAutomationActionRequest();
            NativeAutomationActionScope scope = NativeAutomationDiagnostics.BeginAction("action", request.Action, new Dictionary<string, string>
            {
                ["projectId"] = request.ProjectId ?? string.Empty,
                ["threadId"] = request.ThreadId ?? string.Empty,
                ["tabId"] = request.TabId ?? string.Empty,
                ["targetTabId"] = request.TargetTabId ?? string.Empty,
                ["noteId"] = request.NoteId ?? string.Empty,
                ["title"] = request.Title ?? string.Empty,
                ["value"] = request.Value ?? string.Empty,
            });

            try
            {
                switch (request.Action?.Trim().ToLowerInvariant())
                {
                    case "togglepane":
                        ToggleSidebarPane();
                        break;
                    case "showterminal":
                        ShowTerminalShell(queueGitRefresh: false);
                        break;
                    case "showsettings":
                        ShowSettings();
                        break;
                    case "toggleinspector":
                        ToggleInspector();
                        break;
                    case "newproject":
                        OpenProject(GetOrCreateProject(ResolveRequestedPath(request.Value), null, SampleConfig.DefaultShellProfileId));
                        ShowTerminalShellIfNeeded();
                        break;
                    case "newthread":
                        ActivateThread(CreateThread(ResolveActionProject(request), ResolveThreadName(request.Value)));
                        ShowTerminalShellIfNeeded();
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
                        ShowTerminalShellIfNeeded();
                        break;
                    case "selectthread":
                        ActivateThread(FindThread(request.ThreadId));
                        ShowTerminalShellIfNeeded();
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
                        ShowTerminalShellIfNeeded();
                        break;
                    case "setthreadnote":
                        UpsertThreadNote(
                            ResolveActionThread(request) ?? throw new InvalidOperationException("No thread selected."),
                            request.NoteId,
                            request.Title,
                            request.Value,
                            selectAfterUpdate: true,
                            paneId: request.TabId);
                        break;
                    case "addthreadnote":
                    case "createthreadnote":
                        AddThreadNote(
                            ResolveActionThread(request) ?? throw new InvalidOperationException("No thread selected."),
                            request.Title,
                            request.Value,
                            selectAfterCreate: true,
                            paneId: request.TabId);
                        break;
                    case "updatethreadnote":
                        UpsertThreadNote(
                            ResolveActionThread(request) ?? throw new InvalidOperationException("No thread selected."),
                            request.NoteId,
                            request.Title,
                            request.Value,
                            selectAfterUpdate: false,
                            paneId: request.TabId);
                        break;
                    case "deletethreadnote":
                        DeleteThreadNote(
                            ResolveActionThread(request) ?? throw new InvalidOperationException("No thread selected."),
                            request.NoteId);
                        break;
                    case "archivethreadnote":
                        {
                            WorkspaceThread thread = ResolveActionThread(request) ?? throw new InvalidOperationException("No thread selected.");
                            WorkspaceThreadNote note = string.IsNullOrWhiteSpace(request.NoteId)
                                ? ResolveSelectedThreadNote(thread)
                                : thread.NoteEntries.FirstOrDefault(candidate => string.Equals(candidate.Id, request.NoteId, StringComparison.Ordinal));
                            SetThreadNoteArchived(thread, note, archived: true);
                            break;
                        }
                    case "restorethreadnote":
                    case "unarchivethreadnote":
                        {
                            WorkspaceThread thread = ResolveActionThread(request) ?? throw new InvalidOperationException("No thread selected.");
                            WorkspaceThreadNote note = string.IsNullOrWhiteSpace(request.NoteId)
                                ? ResolveSelectedThreadNote(thread)
                                : thread.NoteEntries.FirstOrDefault(candidate => string.Equals(candidate.Id, request.NoteId, StringComparison.Ordinal));
                            SetThreadNoteArchived(thread, note, archived: false);
                            break;
                        }
                    case "selectthreadnote":
                        SelectThreadNote(
                            ResolveActionThread(request) ?? throw new InvalidOperationException("No thread selected."),
                            request.NoteId);
                        break;
                    case "editthreadnote":
                    case "showthreadnotes":
                        OpenThreadNotes(
                            ResolveActionThread(request) ?? throw new InvalidOperationException("No thread selected."),
                            scope: NotesListScope.Thread);
                        break;
                    case "showprojectnotes":
                    case "shownotes":
                        OpenThreadNotes(
                            ResolveActionThread(request) ?? throw new InvalidOperationException("No thread selected."),
                            scope: NotesListScope.Project);
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
                            _ = SelectDiffPathInCurrentReviewAsync(request.Value);
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
                        NativeAutomationDiagnostics.CompleteAction(scope);
                        return new NativeAutomationActionResponse
                        {
                            Ok = false,
                            Message = $"Unknown action '{request.Action}'.",
                            CorrelationId = scope.CorrelationId,
                            DurationMs = scope.Stopwatch.Elapsed.TotalMilliseconds,
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
                    ["noteId"] = request.NoteId ?? string.Empty,
                    ["title"] = request.Title ?? string.Empty,
                    ["value"] = request.Value ?? string.Empty,
                    ["correlationId"] = scope.CorrelationId,
                });

                NativeAutomationDiagnostics.CompleteAction(scope);
                return new NativeAutomationActionResponse
                {
                    Ok = true,
                    CorrelationId = scope.CorrelationId,
                    DurationMs = scope.Stopwatch.Elapsed.TotalMilliseconds,
                    State = GetAutomationState(),
                };
            }
            catch (Exception ex)
            {
                NativeAutomationDiagnostics.CompleteAction(scope, ex);
                return new NativeAutomationActionResponse
                {
                    Ok = false,
                    Message = ex.Message,
                    CorrelationId = scope.CorrelationId,
                    DurationMs = scope.Stopwatch.Elapsed.TotalMilliseconds,
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

            if (EnableBackgroundPaneWarmup)
            {
                QueueProjectPaneWarmup(_activeProject, _activeThread);
            }

            if (_projects.SelectMany(project => project.Threads).SelectMany(thread => thread.Panes).Any(pane => pane.Kind == WorkspacePaneKind.Browser))
            {
                BrowserPaneControl.PreloadBrowserEnvironmentIfAvailable();
            }

            if (EnableSettingsPagePreload)
            {
                QueueSettingsPagePreload();
            }
        }

        private void OnSessionSaveTimerTick(DispatcherQueueTimer sender, object args)
        {
            _sessionSaveTimer.Stop();
            if (_sessionSaveInFlight)
            {
                _sessionSavePending = true;
                return;
            }

            PersistSessionState(backgroundWrite: true);
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
            if (_gitRefreshInFlight)
            {
                _gitRefreshPending = true;
                return;
            }

            _gitRefreshInFlight = true;
            try
            {
                await RefreshActiveThreadGitStateAsync(_pendingGitSelectedPath, _pendingGitPreserveSelection, _pendingGitIncludeSelectedDiff, _pendingGitPreferFastRefresh).ConfigureAwait(true);
            }
            finally
            {
                _gitRefreshInFlight = false;
                if (_gitRefreshPending && !_lifetimeResourcesReleased)
                {
                    _gitRefreshPending = false;
                    _gitRefreshTimer.Stop();
                    _gitRefreshTimer.Start();
                }
            }
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
                StartShellThemeTransition();
                PlayShellLayoutTransition(includeSidebar: ShellSplitView?.IsPaneOpen == true, includeInspector: _inspectorOpen);
            }
        }

        private void OnPaneToggleClicked(object sender, RoutedEventArgs e)
        {
            ToggleSidebarPane();
        }

        private void ToggleSidebarPane()
        {
            ShellSplitView.IsPaneOpen = !ShellSplitView.IsPaneOpen;
            UpdatePaneLayout();
            RequestLayoutForVisiblePanes();
            QueueSessionSave();
            PlayShellLayoutTransition(includeSidebar: true, includeInspector: false);
        }

        private void OnSettingsNavClicked(object sender, RoutedEventArgs e)
        {
            if (ShouldSuppressSettingsNavigation())
            {
                return;
            }

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
            ShowTerminalShellIfNeeded();
        }

        private void OnProjectAddThreadClicked(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string projectId)
            {
                ActivateThread(CreateThread(FindProject(projectId)));
                ShowTerminalShellIfNeeded();
            }
        }

        private void OnProjectButtonClicked(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string projectId)
            {
                ActivateProject(FindProject(projectId));
                ShowTerminalShellIfNeeded();
            }
        }

        private void OnProjectNewThreadMenuClicked(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.Tag is string projectId)
            {
                ActivateThread(CreateThread(FindProject(projectId)));
                ShowTerminalShellIfNeeded();
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
                ShowTerminalShellIfNeeded();
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

        private void OnEditThreadNotesMenuClicked(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.Tag is string threadId)
            {
                WorkspaceThread thread = FindThread(threadId);
                if (thread is null)
                {
                    return;
                }

                if (thread.NoteEntries.Count == 0)
                {
                    StartInspectorNoteDraft(thread, scope: NotesListScope.Thread);
                    return;
                }

                ClearInspectorNoteDraft();
                OpenThreadNotes(thread, scope: NotesListScope.Thread);
            }
        }

        private void OnNewThreadNoteMenuClicked(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuFlyoutItem item || item.Tag is not string threadId)
            {
                return;
            }

            WorkspaceThread thread = FindThread(threadId);
            if (thread is null)
            {
                return;
            }

            StartInspectorNoteDraft(thread, scope: NotesListScope.Thread);
        }

        private void OnRenamePaneMenuClicked(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.Tag is string paneId)
            {
                BeginInlinePaneRename(paneId);
            }
        }

        private void OnEditPaneThreadNotesMenuClicked(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.Tag is string paneId)
            {
                WorkspaceThread thread = FindThreadForPane(paneId);
                if (thread is null)
                {
                    return;
                }

                WorkspaceThreadNote note = thread.NoteEntries.FirstOrDefault(candidate => string.Equals(candidate.PaneId, paneId, StringComparison.Ordinal));
                if (note is not null)
                {
                    thread.SelectedNoteId = note.Id;
                    ClearInspectorNoteDraft();
                    OpenThreadNotes(thread, scope: NotesListScope.Thread);
                    return;
                }

                StartInspectorNoteDraft(thread, paneId, scope: NotesListScope.Thread);
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
            QueueProjectTreeRefresh();
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
            QueueActiveThreadGitRefresh(preserveSelection: true, includeSelectedDiff: true);
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

            _hoveredDiffFilePaths.Clear();
            _ = SelectDiffPathInCurrentReviewAsync(changedFile.Path);
        }

        private void OnDiffFileItemButtonLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || ResolveSelectedDiffFile(button) is not GitChangedFile changedFile || string.IsNullOrWhiteSpace(changedFile.Path))
            {
                return;
            }

            _diffFileButtonsByPath[changedFile.Path] = button;
            string selectedPath = ResolveDisplayedGitSnapshot()?.SelectedPath;
            ApplyDiffFileButtonState(
                button,
                changedFile,
                string.Equals(changedFile.Path, selectedPath, StringComparison.Ordinal),
                _hoveredDiffFilePaths.Contains(changedFile.Path));
        }

        private void OnDiffFileItemButtonUnloaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || ResolveSelectedDiffFile(button) is not GitChangedFile changedFile || string.IsNullOrWhiteSpace(changedFile.Path))
            {
                return;
            }

            if (_diffFileButtonsByPath.TryGetValue(changedFile.Path, out Button existingButton) && ReferenceEquals(existingButton, button))
            {
                _diffFileButtonsByPath.Remove(changedFile.Path);
            }
        }

        private void OnDiffFileItemButtonPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not Button button || ResolveSelectedDiffFile(button) is not GitChangedFile changedFile || string.IsNullOrWhiteSpace(changedFile.Path))
            {
                return;
            }

            _hoveredDiffFilePaths.Add(changedFile.Path);
            ApplyDiffFileButtonState(
                button,
                changedFile,
                string.Equals(ResolveDisplayedGitSnapshot()?.SelectedPath, changedFile.Path, StringComparison.Ordinal),
                hovered: true);
        }

        private void OnDiffFileItemButtonPointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not Button button || ResolveSelectedDiffFile(button) is not GitChangedFile changedFile || string.IsNullOrWhiteSpace(changedFile.Path))
            {
                return;
            }

            _hoveredDiffFilePaths.Remove(changedFile.Path);
            ApplyDiffFileButtonState(
                button,
                changedFile,
                string.Equals(ResolveDisplayedGitSnapshot()?.SelectedPath, changedFile.Path, StringComparison.Ordinal),
                hovered: false);
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
                EnsureThreadPanesMaterialized(_activeProject, _activeThread);
                WorkspacePaneRecord selectedPane = GetSelectedPane(_activeThread) ?? pane;
                UpdateTabViewItem(selectedPane);
                LogAutomationEvent("shell", "pane.selected", $"Selected pane {selectedPane.Id}", new Dictionary<string, string>
                {
                    ["paneId"] = selectedPane.Id,
                    ["paneKind"] = selectedPane.Kind.ToString().ToLowerInvariant(),
                    ["threadId"] = _activeThread.Id,
                    ["projectId"] = _activeProject?.Id ?? string.Empty,
                });
            }

            RenderPaneWorkspace();
            SyncInspectorSectionWithSelectedPane();
            RefreshInspectorFileBrowser();
            FocusSelectedPane();
            RequestLayoutForVisiblePanes();
            QueueVisibleDeferredPaneMaterialization(_activeProject, _activeThread);
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

            WorkspaceProject project = GetOrCreateProject(ResolveWorkspaceBootstrapPath(), null, SampleConfig.DefaultShellProfileId);
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
            PersistSessionState(backgroundWrite: false);
        }

        private void PersistSessionState(bool backgroundWrite)
        {
            if (_restoringSession)
            {
                return;
            }

            if (backgroundWrite)
            {
                if (_sessionSaveInFlight)
                {
                    _sessionSavePending = true;
                    return;
                }

                _sessionSaveInFlight = true;
            }

            NativeAutomationDiagnostics.IncrementCounter("autosave.count");
            SessionSaveDetail saveDetail = backgroundWrite
                ? _pendingSessionSaveDetail
                : SessionSaveDetail.Full;
            _pendingSessionSaveDetail = SessionSaveDetail.Lightweight;
            WorkspaceSessionSnapshot snapshot;
            Dictionary<string, string> logData;
            try
            {
                snapshot = BuildSessionSnapshot(saveDetail);
                logData = new()
                {
                    ["projectCount"] = snapshot.Projects.Count.ToString(),
                    ["sessionPath"] = WorkspaceSessionStore.GetSessionPath(),
                    ["saveDetail"] = saveDetail.ToString().ToLowerInvariant(),
                };
            }
            catch
            {
                if (backgroundWrite)
                {
                    _sessionSaveInFlight = false;
                }

                throw;
            }

            if (!backgroundWrite)
            {
                using var perfScope = NativeAutomationDiagnostics.TrackOperation("workspace.save");
                WorkspaceSessionStore.Save(snapshot);
                LogAutomationEvent("shell", "workspace.saved", "Saved WinMux workspace session", logData);
                return;
            }

            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    using var perfScope = NativeAutomationDiagnostics.TrackOperation("workspace.save", background: true);
                    WorkspaceSessionStore.Save(snapshot);
                    LogAutomationEvent("shell", "workspace.saved", "Saved WinMux workspace session", logData);
                }
                catch (Exception ex)
                {
                    LogAutomationEvent("shell", "workspace.save_failed", $"Could not save workspace session: {ex.Message}", new Dictionary<string, string>
                    {
                        ["sessionPath"] = WorkspaceSessionStore.GetSessionPath(),
                    });
                }
                finally
                {
                    DispatcherQueue?.TryEnqueue(() =>
                    {
                        _sessionSaveInFlight = false;
                        if (_sessionSavePending)
                        {
                            _sessionSavePending = false;
                            _sessionSaveTimer.Stop();
                            _sessionSaveTimer.Start();
                        }
                    });
                }
            });
        }

        private void QueueSessionSave(SessionSaveDetail detail = SessionSaveDetail.Lightweight)
        {
            if (_restoringSession)
            {
                return;
            }

            if (detail == SessionSaveDetail.Full)
            {
                _pendingSessionSaveDetail = SessionSaveDetail.Full;
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

        private bool QueueActiveThreadGitRefresh(
            string selectedPath = null,
            bool preserveSelection = false,
            bool includeSelectedDiff = true,
            bool preferFastRefresh = false)
        {
            if (_activeThread is null)
            {
                _activeGitSnapshot = null;
                ApplyGitSnapshotToUi();
                return false;
            }

            _pendingGitSelectedPath = ResolveSelectedDiffPathForRefresh(_activeThread, selectedPath, preserveSelection);
            _pendingGitPreserveSelection = preserveSelection;
            _pendingGitIncludeSelectedDiff = includeSelectedDiff;
            _pendingGitPreferFastRefresh = preferFastRefresh;
            _pendingGitCorrelationId = NativeAutomationDiagnostics.CaptureCurrentCorrelationId();

            if (!RequiresActiveThreadGitRefresh(_activeThread, _pendingGitSelectedPath, _pendingGitIncludeSelectedDiff, _pendingGitPreferFastRefresh))
            {
                _pendingGitCorrelationId = null;
                _pendingGitIncludeSelectedDiff = true;
                _pendingGitPreferFastRefresh = false;
                QueueVisibleDiffHydrationIfNeeded(_activeThread, _activeProject, _activeGitSnapshot ?? _activeThread.LiveSnapshot);
                return false;
            }

            if (ShouldAttemptPeerThreadGitSnapshot(_activeThread, _pendingGitSelectedPath, _pendingGitIncludeSelectedDiff, _pendingGitPreferFastRefresh) &&
                TryUsePeerThreadGitSnapshot(_activeThread, _pendingGitSelectedPath, _pendingGitIncludeSelectedDiff, _pendingGitPreferFastRefresh))
            {
                _pendingGitCorrelationId = null;
                _pendingGitIncludeSelectedDiff = true;
                _pendingGitPreferFastRefresh = false;
                QueueVisibleDiffHydrationIfNeeded(_activeThread, _activeProject, _activeGitSnapshot ?? _activeThread.LiveSnapshot);
                return false;
            }

            _gitRefreshTimer.Stop();
            if (_gitRefreshInFlight)
            {
                _gitRefreshPending = true;
            }
            else
            {
                _gitRefreshTimer.Start();
            }

            if (ShouldRefreshReviewInspectorUi())
            {
                DiffBranchText.Text = string.IsNullOrWhiteSpace(_activeThread.BranchName)
                    ? (_activeGitSnapshot?.BranchName ?? "No git context")
                    : _activeThread.BranchName;
                DiffWorktreeText.Text = _activeThread.WorktreePath ?? _activeProject?.RootPath ?? string.Empty;
            }

            return true;
        }

        private bool ShouldAttemptPeerThreadGitSnapshot(
            WorkspaceThread thread,
            string selectedPath,
            bool includeSelectedDiff,
            bool preferFastRefresh)
        {
            if (thread is null)
            {
                return false;
            }

            if (!preferFastRefresh)
            {
                return true;
            }

            if (VisibleDiffPaneRequiresCompleteSnapshot(thread) || VisibleDiffPaneNeedsSelectedDiff(thread, selectedPath))
            {
                return true;
            }

            return _inspectorOpen && _activeInspectorSection == InspectorSection.Review;
        }

        private bool RequiresActiveThreadGitRefresh(WorkspaceThread thread, string selectedPath, bool includeSelectedDiff, bool preferFastRefresh)
        {
            if (thread is null)
            {
                return true;
            }

            string threadRootPath = ResolveThreadRootPath(thread.Project, thread);
            bool requiresCompleteSnapshot = !preferFastRefresh && VisibleDiffPaneRequiresCompleteSnapshot(thread);
            TimeSpan maxAge = ResolveGitSnapshotMaxAge(includeSelectedDiff, preferFastRefresh, requiresCompleteSnapshot);
            return !SnapshotSatisfiesGitRefreshNeeds(
                thread.LiveSnapshot,
                thread.LiveSnapshotCapturedAt,
                threadRootPath,
                selectedPath,
                includeSelectedDiff,
                requiresCompleteSnapshot,
                maxAge);
        }

        private bool VisibleDiffPaneRequiresCompleteSnapshot(WorkspaceThread thread)
        {
            if (thread is null)
            {
                return false;
            }

            foreach (WorkspacePaneRecord pane in GetVisiblePanes(thread))
            {
                if (pane is DiffPaneRecord diffPane &&
                    diffPane.DiffPane.DisplayMode == DiffPaneDisplayMode.FullPatchReview)
                {
                    return true;
                }
            }

            string fallbackPath = ReferenceEquals(thread, _activeThread)
                ? _activeGitSnapshot?.SelectedPath ?? thread.SelectedDiffPath
                : thread.SelectedDiffPath;
            List<string> visibleDiffPaths = GetVisibleFileCompareDiffPaths(thread, fallbackPath);
            return visibleDiffPaths.Count > 1 ||
                (visibleDiffPaths.Count == 1 &&
                    !string.Equals(visibleDiffPaths[0], fallbackPath, StringComparison.Ordinal));
        }

        private bool VisibleDiffPaneNeedsSelectedDiff(WorkspaceThread thread, string selectedPath)
        {
            if (thread is null ||
                thread.DiffReviewSource != DiffReviewSourceKind.Live ||
                VisibleDiffPaneRequiresCompleteSnapshot(thread))
            {
                return false;
            }

            string fallbackPath = string.IsNullOrWhiteSpace(selectedPath)
                ? (ReferenceEquals(thread, _activeThread)
                    ? _activeGitSnapshot?.SelectedPath ?? thread.SelectedDiffPath
                    : thread.SelectedDiffPath)
                : selectedPath;
            List<string> visibleDiffPaths = GetVisibleFileCompareDiffPaths(thread, fallbackPath);
            return visibleDiffPaths.Count == 1 &&
                string.Equals(visibleDiffPaths[0], fallbackPath, StringComparison.Ordinal);
        }

        private static string ResolveVisibleDiffPanePath(DiffPaneRecord diffPane, string fallbackPath)
        {
            return string.IsNullOrWhiteSpace(diffPane?.DiffPath)
                ? fallbackPath
                : diffPane.DiffPath;
        }

        private List<string> GetVisibleFileCompareDiffPaths(WorkspaceThread thread, string fallbackPath)
        {
            if (thread is null)
            {
                return new List<string>();
            }

            List<string> paths = new();
            HashSet<string> uniquePaths = new(StringComparer.Ordinal);
            foreach (WorkspacePaneRecord pane in GetVisiblePanes(thread))
            {
                if (pane is not DiffPaneRecord diffPane ||
                    diffPane.DiffPane.DisplayMode != DiffPaneDisplayMode.FileCompare)
                {
                    continue;
                }

                string path = ResolveVisibleDiffPanePath(diffPane, fallbackPath);
                if (!string.IsNullOrWhiteSpace(path) && uniquePaths.Add(path))
                {
                    paths.Add(path);
                }
            }

            return paths;
        }

        private bool SnapshotSatisfiesGitRefreshNeeds(
            GitThreadSnapshot snapshot,
            DateTimeOffset capturedAt,
            string threadRootPath,
            string selectedPath,
            bool includeSelectedDiff,
            bool requiresCompleteSnapshot,
            TimeSpan maxAge)
        {
            if (snapshot is null)
            {
                return false;
            }

            if (!string.Equals(snapshot.WorktreePath, threadRootPath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (capturedAt == default || DateTimeOffset.UtcNow - capturedAt > maxAge)
            {
                return false;
            }

            if (requiresCompleteSnapshot && !HasCompleteDiffSet(snapshot))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(selectedPath) && snapshot.ChangedFiles.Count > 0)
            {
                bool hasSelectedPath = false;
                foreach (GitChangedFile file in snapshot.ChangedFiles)
                {
                    if (string.Equals(file.Path, selectedPath, StringComparison.Ordinal))
                    {
                        hasSelectedPath = true;
                        break;
                    }
                }

                if (!hasSelectedPath)
                {
                    return false;
                }
            }

            if (includeSelectedDiff && !HasSelectedDiffAvailable(snapshot, selectedPath))
            {
                return false;
            }

            return true;
        }

        private static TimeSpan ResolveGitSnapshotMaxAge(bool includeSelectedDiff, bool preferFastRefresh, bool requiresCompleteSnapshot)
        {
            if (preferFastRefresh && !includeSelectedDiff && !requiresCompleteSnapshot)
            {
                return CachedShellOnlyGitSnapshotMaxAge;
            }

            return CachedThreadGitSnapshotMaxAge;
        }

        private bool TryUsePeerThreadGitSnapshot(WorkspaceThread thread, string selectedPath, bool includeSelectedDiff, bool preferFastRefresh)
        {
            if (thread is null)
            {
                return false;
            }

            string threadRootPath = ResolveThreadRootPath(thread.Project, thread);
            if (string.IsNullOrWhiteSpace(threadRootPath))
            {
                return false;
            }

            bool requiresCompleteSnapshot = !preferFastRefresh && VisibleDiffPaneRequiresCompleteSnapshot(thread);
            TimeSpan maxAge = ResolveGitSnapshotMaxAge(includeSelectedDiff, preferFastRefresh, requiresCompleteSnapshot);
            WorkspaceThread newestThread = null;
            DateTimeOffset newestCapturedAt = default;

            foreach (WorkspaceThread candidate in _projects.SelectMany(project => project.Threads))
            {
                if (ReferenceEquals(candidate, thread) ||
                    !SnapshotSatisfiesGitRefreshNeeds(
                        candidate.LiveSnapshot,
                        candidate.LiveSnapshotCapturedAt,
                        threadRootPath,
                        selectedPath,
                        includeSelectedDiff,
                        requiresCompleteSnapshot,
                        maxAge))
                {
                    continue;
                }

                if (newestThread is null || candidate.LiveSnapshotCapturedAt > newestCapturedAt)
                {
                    newestThread = candidate;
                    newestCapturedAt = candidate.LiveSnapshotCapturedAt;
                }
            }

            if (newestThread?.LiveSnapshot is null)
            {
                return false;
            }

            GitThreadSnapshot adoptedSnapshot = GitStatusService.CloneSnapshot(newestThread.LiveSnapshot);
            MergeCachedDiffTexts(thread.LiveSnapshot, adoptedSnapshot);
            GitStatusService.SelectDiffPath(adoptedSnapshot, selectedPath);
            CommitActiveGitSnapshot(adoptedSnapshot, newestCapturedAt, ensureBaselineCapture: false, logRefresh: false, updateHeader: true);
            return true;
        }

        private static void SetThreadLiveSnapshot(WorkspaceThread thread, GitThreadSnapshot snapshot, DateTimeOffset capturedAt)
        {
            if (thread is null)
            {
                return;
            }

            if (snapshot is null)
            {
                thread.LiveSnapshot = null;
                thread.LiveSnapshotCapturedAt = capturedAt;
                thread.ChangedFileCount = 0;
                thread.SelectedDiffPath = null;
                return;
            }

            thread.BranchName = snapshot.BranchName;
            thread.WorktreePath = string.IsNullOrWhiteSpace(thread.WorktreePath)
                ? snapshot.WorktreePath
                : thread.WorktreePath;
            thread.ChangedFileCount = snapshot.ChangedFiles.Count;
            thread.SelectedDiffPath = snapshot.SelectedPath;
            thread.LiveSnapshot = snapshot;
            thread.LiveSnapshotCapturedAt = capturedAt;
        }

        private void CommitActiveGitSnapshot(
            GitThreadSnapshot snapshot,
            DateTimeOffset capturedAt,
            bool ensureBaselineCapture,
            bool logRefresh,
            bool updateHeader = false)
        {
            if (_activeThread is null || snapshot is null)
            {
                return;
            }

            MergeCachedDiffTexts(_activeGitSnapshot, snapshot);
            _activeGitSnapshot = snapshot;
            SetThreadLiveSnapshot(_activeThread, snapshot, capturedAt);
            if (ensureBaselineCapture && EnableAutomaticBaselineCapture)
            {
                EnsureThreadBaselineCapture(_activeThread, _activeProject, snapshot);
            }
            ApplyGitSnapshotToUi();
            QueueProjectTreeRefresh();

            if (updateHeader)
            {
                UpdateHeader();
            }

            if (!logRefresh)
            {
                return;
            }

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

        private void QueueVisibleDiffHydrationIfNeeded(
            WorkspaceThread thread,
            WorkspaceProject project,
            GitThreadSnapshot snapshot = null)
        {
            if (thread is null ||
                project is null ||
                !ReferenceEquals(thread, _activeThread) ||
                !ReferenceEquals(project, _activeProject) ||
                _showingSettings ||
                thread.DiffReviewSource != DiffReviewSourceKind.Live)
            {
                return;
            }

            GitThreadSnapshot candidateSnapshot = snapshot ?? _activeGitSnapshot ?? thread.LiveSnapshot;
            bool requiresCompleteSnapshot = VisibleDiffPaneRequiresCompleteSnapshot(thread);
            string fallbackPath = candidateSnapshot?.SelectedPath ?? thread.SelectedDiffPath;
            List<string> visibleDiffPaths = GetVisibleFileCompareDiffPaths(thread, fallbackPath);
            string hydrationPath = !string.IsNullOrWhiteSpace(fallbackPath)
                ? fallbackPath
                : visibleDiffPaths.FirstOrDefault();
            if (!requiresCompleteSnapshot &&
                (visibleDiffPaths.Count == 0 || HasSelectedDiffAvailable(candidateSnapshot, hydrationPath)))
            {
                return;
            }

            DispatcherQueue.TryEnqueue(() => _ = RefreshActiveThreadGitStateAsync(
                hydrationPath,
                preserveSelection: true,
                includeSelectedDiff: !requiresCompleteSnapshot,
                preferFastRefresh: false));
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
                _ => _activeGitSnapshot ?? _activeThread.LiveSnapshot,
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

            if (_showingSettings || !_inspectorOpen)
            {
                DiffReviewSourceSection.Visibility = Visibility.Collapsed;
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
            SetInspectorSection(InspectorSection.Files, refreshFiles: false);
            RefreshInspectorFileBrowser();
        }

        private void OnInspectorNotesTabClicked(object sender, RoutedEventArgs e)
        {
            SetInspectorSection(InspectorSection.Notes, refreshFiles: false);
        }

        private void OnInspectorNotesThreadScopeClicked(object sender, RoutedEventArgs e)
        {
            SetNotesListScope(NotesListScope.Thread);
        }

        private void OnInspectorNotesProjectScopeClicked(object sender, RoutedEventArgs e)
        {
            SetNotesListScope(NotesListScope.Project);
        }

        private void OnInspectorAddNoteClicked(object sender, RoutedEventArgs e)
        {
            if (_activeThread is null)
            {
                return;
            }

            StartInspectorNoteDraft(_activeThread, scope: _activeNotesListScope);
        }

        private void OnInspectorDeleteNoteClicked(object sender, RoutedEventArgs e)
        {
            if (_activeThread is null)
            {
                return;
            }

            DeleteThreadNote(_activeThread, _activeThread.SelectedNoteId);
        }

        private void OnInspectorSaveNoteClicked(object sender, RoutedEventArgs e)
        {
            if (_activeThread is null)
            {
                return;
            }

            WorkspaceThreadNote note = ResolveSelectedThreadNote(_activeThread);
            if (note is null || note.IsArchived)
            {
                return;
            }

            CommitInspectorNoteDraft(_activeThread, note);
        }

        private void OnInspectorCollapseAllClicked(object sender, RoutedEventArgs e)
        {
            if (InspectorDirectoryTree is null)
            {
                return;
            }

            InspectorDirectoryTree.SelectedNode = null;
            foreach (TreeViewNode rootNode in InspectorDirectoryTree.RootNodes)
            {
                CollapseInspectorDirectoryNode(rootNode);
            }

            UpdateInspectorFileActionState();
        }

        private void OnInspectorDirectoryTreeExpanding(TreeView sender, TreeViewExpandingEventArgs args)
        {
            MaterializeInspectorDirectoryChildren(args?.Node);
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

        private void OnInspectorNoteCardTextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is not TextBox noteBox || noteBox.Tag is not InspectorNoteCardItem item || item.IsArchived)
            {
                return;
            }

            WorkspaceThread thread = FindThread(item.ThreadId);
            WorkspaceThreadNote note = thread?.NoteEntries.FirstOrDefault(candidate => string.Equals(candidate.Id, item.NoteId, StringComparison.Ordinal));
            if (thread is null || note is null)
            {
                return;
            }

            thread.SelectedNoteId = note.Id;
            UpdateInspectorNoteDraft(thread, note, text: noteBox.Text, updateText: true);
        }

        private void OnInspectorNoteCardTitleChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is not TextBox titleBox || titleBox.Tag is not InspectorNoteCardItem item || item.IsArchived)
            {
                return;
            }

            WorkspaceThread thread = FindThread(item.ThreadId);
            WorkspaceThreadNote note = thread?.NoteEntries.FirstOrDefault(candidate => string.Equals(candidate.Id, item.NoteId, StringComparison.Ordinal));
            if (thread is null || note is null)
            {
                return;
            }

            thread.SelectedNoteId = note.Id;
            UpdateInspectorNoteDraft(thread, note, title: titleBox.Text, updateTitle: true);
        }

        private void OnInspectorSaveNoteCardClicked(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement element || element.Tag is not InspectorNoteCardItem item)
            {
                return;
            }

            WorkspaceThread thread = FindThread(item.ThreadId);
            WorkspaceThreadNote note = thread?.NoteEntries.FirstOrDefault(candidate => string.Equals(candidate.Id, item.NoteId, StringComparison.Ordinal));
            if (thread is null || note is null || note.IsArchived)
            {
                return;
            }

            thread.SelectedNoteId = note.Id;
            CommitInspectorNoteDraft(thread, note);
        }

        private void OnInspectorNoteCardTapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element || element.Tag is not InspectorNoteCardItem item)
            {
                return;
            }

            WorkspaceThread thread = FindThread(item.ThreadId);
            if (thread is null || string.IsNullOrWhiteSpace(item.NoteId))
            {
                return;
            }

            bool selectionChanged = !string.Equals(thread.SelectedNoteId, item.NoteId, StringComparison.Ordinal);
            thread.SelectedNoteId = item.NoteId;

            if (selectionChanged)
            {
                RefreshInspectorNotes();
                QueueSessionSave();
            }
        }

        private void OnInspectorNoteCardDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element || element.Tag is not InspectorNoteCardItem item)
            {
                return;
            }

            WorkspaceThread thread = FindThread(item.ThreadId);
            if (thread is null || string.IsNullOrWhiteSpace(item.NoteId))
            {
                return;
            }

            bool selectionChanged = !string.Equals(thread.SelectedNoteId, item.NoteId, StringComparison.Ordinal);
            thread.SelectedNoteId = item.NoteId;
            if (selectionChanged)
            {
                RefreshInspectorNotes();
                QueueSessionSave();
            }

            if (!item.IsArchived)
            {
                FocusInspectorNoteEditor(item.NoteId);
            }

            e.Handled = true;
        }

        private void OnInspectorNoteCardTextBoxGotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBox noteBox || noteBox.Tag is not InspectorNoteCardItem item)
            {
                return;
            }

            ApplyInspectorNoteEditorFocusState(noteBox, item, focused: true);
            WorkspaceThread thread = FindThread(item.ThreadId);
            if (thread is null || string.IsNullOrWhiteSpace(item.NoteId))
            {
                return;
            }

            thread.SelectedNoteId = item.NoteId;
            QueueSessionSave();
        }

        private void OnInspectorNoteCardTextBoxLostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox noteBox && noteBox.Tag is InspectorNoteCardItem item)
            {
                ApplyInspectorNoteEditorFocusState(noteBox, item, focused: false);
                WorkspaceThread thread = FindThread(item.ThreadId);
                if (thread is not null && !string.IsNullOrWhiteSpace(item.NoteId))
                {
                    thread.SelectedNoteId = item.NoteId;
                }

                UpdateInspectorFileActionState();
                QueueSessionSave();
            }
        }

        private void OnInspectorNoteCardTextBoxKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (sender is not TextBox noteBox ||
                noteBox.Tag is not InspectorNoteCardItem item)
            {
                return;
            }

            WorkspaceThread thread = FindThread(item.ThreadId);
            WorkspaceThreadNote note = thread?.NoteEntries.FirstOrDefault(candidate => string.Equals(candidate.Id, item.NoteId, StringComparison.Ordinal));
            if (e.Key == Windows.System.VirtualKey.Enter &&
                noteBox.AcceptsReturn &&
                (InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down &&
                thread is not null &&
                note is not null)
            {
                thread.SelectedNoteId = item.NoteId;
                CommitInspectorNoteDraft(thread, note);
                e.Handled = true;
                return;
            }

            if (e.Key != Windows.System.VirtualKey.Escape)
            {
                return;
            }

            if (thread is not null && !string.IsNullOrWhiteSpace(item.NoteId))
            {
                thread.SelectedNoteId = item.NoteId;
            }

            DiscardInspectorNoteDraft(item.NoteId, refreshInspector: true);
            InspectorNotesThreadScopeButton?.Focus(FocusState.Programmatic);
            e.Handled = true;
        }

        private void OnInspectorNoteCardPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border card && card.Tag is InspectorNoteCardItem item)
            {
                ApplyInspectorNoteCardChrome(card, item, hovered: true);
            }
        }

        private void OnInspectorNoteCardPointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border card && card.Tag is InspectorNoteCardItem item)
            {
                ApplyInspectorNoteCardChrome(card, item, hovered: false);
            }
        }

        private void OnInspectorNoteScopeButtonClicked(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not InspectorNoteCardItem item)
            {
                return;
            }

            WorkspaceThread thread = FindThread(item.ThreadId);
            if (thread is null)
            {
                return;
            }

            thread.SelectedNoteId = item.NoteId;
            WorkspaceThreadNote note = thread.NoteEntries.FirstOrDefault(candidate => string.Equals(candidate.Id, item.NoteId, StringComparison.Ordinal));
            if (note is not null)
            {
                CommitInspectorNoteDraft(thread, note, refreshInspector: false);
            }
            MenuFlyout flyout = BuildInspectorNoteScopeFlyout(thread, item);
            flyout.ShowAt(button);
        }

        private void OnInspectorNoteScopeOptionClicked(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuFlyoutItem item || item.Tag is not NoteScopeOption option)
            {
                return;
            }

            WorkspaceThread thread = FindThread(option.ThreadId);
            WorkspaceThreadNote note = thread?.NoteEntries.FirstOrDefault(candidate => string.Equals(candidate.Id, option.NoteId, StringComparison.Ordinal));
            if (thread is null || note is null)
            {
                return;
            }

            thread.SelectedNoteId = note.Id;
            CommitInspectorNoteDraft(thread, note, refreshInspector: false);
            UpdateThreadNotePaneAttachment(thread, note, option.PaneId);
        }

        private void OnInspectorArchiveNoteButtonClicked(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement element || element.Tag is not InspectorNoteCardItem item)
            {
                return;
            }

            WorkspaceThread thread = FindThread(item.ThreadId);
            WorkspaceThreadNote note = thread?.NoteEntries.FirstOrDefault(candidate => string.Equals(candidate.Id, item.NoteId, StringComparison.Ordinal));
            if (thread is null || note is null)
            {
                return;
            }

            CommitInspectorNoteDraft(thread, note, refreshInspector: false);
            SetThreadNoteArchived(thread, note, archived: !note.IsArchived);
        }

        private void OnInspectorDeleteNoteCardClicked(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement element || element.Tag is not InspectorNoteCardItem item)
            {
                return;
            }

            WorkspaceThread thread = FindThread(item.ThreadId);
            if (thread is null)
            {
                return;
            }

            DeleteThreadNote(thread, item.NoteId);
        }

        private void OnInspectorArchivedNotesToggleClicked(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement element || element.Tag is not InspectorNoteGroupItem group || string.IsNullOrWhiteSpace(group.ThreadId))
            {
                return;
            }

            if (!_expandedArchivedNoteThreadIds.Add(group.ThreadId))
            {
                _expandedArchivedNoteThreadIds.Remove(group.ThreadId);
            }

            RefreshInspectorNotes();
        }

        private void SetInspectorSection(InspectorSection section, bool refreshFiles = true)
        {
            bool sectionChanged = _activeInspectorSection != section;
            _activeInspectorSection = section;
            UpdateInspectorSectionChrome();
            if (section == InspectorSection.Files && refreshFiles && sectionChanged)
            {
                RefreshInspectorFileBrowser(forceRebuild: sectionChanged);
            }
            else if (section == InspectorSection.Review && sectionChanged)
            {
                ApplyGitSnapshotToUi();
            }
            else if (section == InspectorSection.Notes)
            {
                RefreshInspectorNotes();
            }
        }

        private void SyncInspectorSectionWithSelectedPane()
        {
            if (_activeInspectorSection == InspectorSection.Notes)
            {
                return;
            }

            WorkspacePaneRecord selectedPane = GetSelectedPane(_activeThread);
            if (selectedPane?.Kind == WorkspacePaneKind.Editor)
            {
                SetInspectorSection(InspectorSection.Files, refreshFiles: false);
                return;
            }

            if (selectedPane?.Kind == WorkspacePaneKind.Diff)
            {
                SetInspectorSection(InspectorSection.Review, refreshFiles: false);
            }
        }

        private void UpdateInspectorSectionChrome()
        {
            if (InspectorReviewTabButton is null || InspectorFilesTabButton is null || InspectorNotesTabButton is null)
            {
                return;
            }

            ApplyInspectorTabButtonState(InspectorReviewTabButton, _activeInspectorSection == InspectorSection.Review);
            ApplyInspectorTabButtonState(InspectorFilesTabButton, _activeInspectorSection == InspectorSection.Files);
            ApplyInspectorTabButtonState(InspectorNotesTabButton, _activeInspectorSection == InspectorSection.Notes);
            ApplyInspectorNotesTabButtonAffordance();

            if (InspectorReviewContent is not null)
            {
                InspectorReviewContent.Visibility = _activeInspectorSection == InspectorSection.Review ? Visibility.Visible : Visibility.Collapsed;
            }

            if (InspectorFilesContent is not null)
            {
                InspectorFilesContent.Visibility = _activeInspectorSection == InspectorSection.Files ? Visibility.Visible : Visibility.Collapsed;
            }

            if (InspectorNotesContent is not null)
            {
                InspectorNotesContent.Visibility = _activeInspectorSection == InspectorSection.Notes ? Visibility.Visible : Visibility.Collapsed;
            }

            if (InspectorReviewActionsPanel is not null)
            {
                InspectorReviewActionsPanel.Visibility = _activeInspectorSection == InspectorSection.Review ? Visibility.Visible : Visibility.Collapsed;
            }

            if (InspectorFileActionsPanel is not null)
            {
                InspectorFileActionsPanel.Visibility = _activeInspectorSection == InspectorSection.Files ? Visibility.Visible : Visibility.Collapsed;
            }

            if (InspectorNotesActionsPanel is not null)
            {
                InspectorNotesActionsPanel.Visibility = _activeInspectorSection == InspectorSection.Notes ? Visibility.Visible : Visibility.Collapsed;
            }

            RefreshInspectorNotes();
            UpdateInspectorFileActionState();
        }

        private void ApplyInspectorNotesTabButtonAffordance()
        {
            if (InspectorNotesTabButton is null)
            {
                return;
            }

            int activeNoteCount = _activeThread?.NoteEntries.Count(candidate => !candidate.IsArchived) ?? 0;
            int archivedNoteCount = _activeThread?.NoteEntries.Count(candidate => candidate.IsArchived) ?? 0;

            ToolTipService.SetToolTip(
                InspectorNotesTabButton,
                _activeThread is null
                    ? "No thread selected"
                    : activeNoteCount == 0 && archivedNoteCount == 0
                        ? "Open project notes"
                        : $"{BuildNotesMeta(_activeProject, _activeThread, NotesListScope.Thread)} in this thread");
        }

        private void SetNotesListScope(NotesListScope scope)
        {
            if (_activeNotesListScope == scope)
            {
                return;
            }

            _activeNotesListScope = scope;
            RefreshInspectorNotes();
        }

        private void StartInspectorNoteDraft(WorkspaceThread thread, string paneId = null, NotesListScope? scope = null)
        {
            if (thread is null)
            {
                return;
            }

            OpenThreadNotes(thread, focusEditor: false, scope: scope);
            string resolvedPaneId = ResolveNotePaneId(thread, paneId, preferSelectedPane: true);
            WorkspaceThreadNote note = thread.NoteEntries
                .FirstOrDefault(candidate =>
                    !candidate.IsArchived &&
                    string.IsNullOrWhiteSpace(candidate.Text) &&
                    IsSystemGeneratedNoteTitle(candidate.Title));

            if (note is null)
            {
                note = AddThreadNote(thread, title: null, text: null, selectAfterCreate: true, paneId: resolvedPaneId);
            }
            else
            {
                thread.SelectedNoteId = note.Id;
                if (!string.IsNullOrWhiteSpace(resolvedPaneId) && !string.Equals(note.PaneId, resolvedPaneId, StringComparison.Ordinal))
                {
                    UpdateThreadNotePaneAttachment(thread, note, resolvedPaneId);
                }
                else
                {
                    RefreshInspectorNotes();
                }
            }

            FocusInspectorNoteEditor(note?.Id);
        }

        private void ClearInspectorNoteDraft()
        {
            if (_activeThread is not null && !string.IsNullOrWhiteSpace(_activeThread.SelectedNoteId))
            {
                _noteDraftsById.Remove(_activeThread.SelectedNoteId);
            }

            UpdateInspectorFileActionState();
        }

        private static string NormalizeNoteDraftTitle(string title)
        {
            return string.IsNullOrWhiteSpace(title) ? string.Empty : title.Trim();
        }

        private static string NormalizeNoteDraftText(string text)
        {
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        private NoteDraftState ResolveNoteDraftState(WorkspaceThreadNote note)
        {
            return note is not null && _noteDraftsById.TryGetValue(note.Id, out NoteDraftState draft)
                ? draft
                : null;
        }

        private NoteDraftState GetOrCreateNoteDraftState(WorkspaceThreadNote note)
        {
            if (note is null)
            {
                return null;
            }

            if (!_noteDraftsById.TryGetValue(note.Id, out NoteDraftState draft))
            {
                draft = new NoteDraftState
                {
                    EditableTitle = ResolveEditableNoteTitle(note),
                    Text = note.Text,
                };
                _noteDraftsById[note.Id] = draft;
            }

            draft.Dirty = IsNoteDraftDirty(note, draft);
            return draft;
        }

        private static bool IsNoteDraftDirty(WorkspaceThreadNote note, NoteDraftState draft)
        {
            if (note is null || draft is null)
            {
                return false;
            }

            return !string.Equals(ResolveEditableNoteTitle(note), draft.EditableTitle ?? string.Empty, StringComparison.Ordinal) ||
                !string.Equals(NormalizeNoteDraftText(note.Text), NormalizeNoteDraftText(draft.Text), StringComparison.Ordinal);
        }

        private void UpdateInspectorNoteDraft(WorkspaceThread thread, WorkspaceThreadNote note, string title = null, string text = null, bool updateTitle = false, bool updateText = false)
        {
            if (thread is null || note is null || note.IsArchived)
            {
                return;
            }

            NoteDraftState draft = GetOrCreateNoteDraftState(note);
            if (draft is null)
            {
                return;
            }

            if (updateTitle)
            {
                draft.EditableTitle = NormalizeNoteDraftTitle(title);
            }

            if (updateText)
            {
                draft.Text = NormalizeNoteDraftText(text);
            }

            draft.Dirty = IsNoteDraftDirty(note, draft);
            if (!draft.Dirty)
            {
                _noteDraftsById.Remove(note.Id);
            }

            thread.SelectedNoteId = note.Id;
            UpdateInspectorFileActionState();
        }

        private bool CommitInspectorNoteDraft(WorkspaceThread thread, WorkspaceThreadNote note, bool refreshInspector = true)
        {
            if (thread is null || note is null || note.IsArchived)
            {
                return false;
            }

            if (!_noteDraftsById.TryGetValue(note.Id, out NoteDraftState draft))
            {
                UpdateInspectorFileActionState();
                return false;
            }

            string nextTitle = string.IsNullOrWhiteSpace(draft.EditableTitle)
                ? BuildDefaultThreadNoteTitle(thread)
                : draft.EditableTitle.Trim();
            string nextText = NormalizeNoteDraftText(draft.Text);
            bool changed = !string.Equals(note.Title, nextTitle, StringComparison.Ordinal) ||
                !string.Equals(NormalizeNoteDraftText(note.Text), nextText, StringComparison.Ordinal);

            _noteDraftsById.Remove(note.Id);

            if (!changed)
            {
                if (refreshInspector)
                {
                    RefreshInspectorNotes();
                }
                else
                {
                    UpdateInspectorFileActionState();
                }

                return false;
            }

            note.Title = nextTitle;
            note.Text = nextText;
            note.UpdatedAt = DateTimeOffset.UtcNow;
            thread.NoteEntries.Remove(note);
            thread.NoteEntries.Insert(0, note);
            thread.SelectedNoteId = note.Id;
            AfterThreadNotesChanged(thread, refreshInspector);
            return true;
        }

        private void DiscardInspectorNoteDraft(string noteId, bool refreshInspector)
        {
            if (string.IsNullOrWhiteSpace(noteId))
            {
                return;
            }

            if (_noteDraftsById.Remove(noteId) && refreshInspector)
            {
                RefreshInspectorNotes();
                return;
            }

            UpdateInspectorFileActionState();
        }

        private void ApplyNotesListScopeButtonState(Button button, bool active)
        {
            if (button is null)
            {
                return;
            }

            Brush accentBrush = AppBrush(button, "ShellPaneActiveBorderBrush");
            bool lightTheme = ResolveTheme(SampleConfig.CurrentTheme) == ElementTheme.Light;
            button.Background = active
                ? CreateSidebarTintedBrush(accentBrush, lightTheme ? (byte)0x16 : (byte)0x12, Windows.UI.Color.FromArgb(0xFF, 0x33, 0x41, 0x55))
                : null;
            button.BorderBrush = null;
            button.BorderThickness = new Thickness(0);
            button.Foreground = active ? AppBrush(button, "ShellTextPrimaryBrush") : AppBrush(button, "ShellTextSecondaryBrush");
        }

        private void ApplyInspectorNoteCardChrome(Border card, InspectorNoteCardItem item, bool hovered)
        {
            if (card is null || item is null)
            {
                return;
            }

            if (!hovered)
            {
                card.Background = item.CardBackground;
                card.BorderBrush = item.CardBorderBrush;
                card.BorderThickness = new Thickness(0);
                return;
            }

            bool lightTheme = ResolveTheme(SampleConfig.CurrentTheme) == ElementTheme.Light;
            if (item.IsSelected)
            {
                byte backgroundAlpha = item.IsArchived
                    ? (byte)(lightTheme ? 0x0B : 0x0A)
                    : (byte)(lightTheme ? 0x14 : 0x12);
                card.Background = CreateSidebarTintedBrush(item.AccentBrush, backgroundAlpha, Windows.UI.Color.FromArgb(0xFF, 0x14, 0x16, 0x1B));
            }
            else
            {
                card.Background = AppBrush(card, "ShellNavHoverBrush");
            }

            card.BorderBrush = null;
            card.BorderThickness = new Thickness(0);
        }

        private void ApplyInspectorNoteEditorFocusState(TextBox editor, InspectorNoteCardItem item, bool focused)
        {
            if (editor is null || item is null)
            {
                return;
            }

            if (!focused)
            {
                editor.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00));
                editor.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00));
                return;
            }

            bool lightTheme = ResolveTheme(SampleConfig.CurrentTheme) == ElementTheme.Light;
            editor.Background = CreateSidebarTintedBrush(item.AccentBrush, lightTheme ? (byte)0x0A : (byte)0x08, Windows.UI.Color.FromArgb(0xFF, 0x14, 0x16, 0x1B));
            editor.BorderBrush = CreateSidebarTintedBrush(item.AccentBrush, lightTheme ? (byte)0x54 : (byte)0x44, Windows.UI.Color.FromArgb(0xFF, 0x33, 0x41, 0x55));
        }

        private MenuFlyout BuildInspectorNoteScopeFlyout(WorkspaceThread thread, InspectorNoteCardItem item)
        {
            MenuFlyout flyout = new();
            foreach (NoteScopeOption option in BuildNoteScopeOptions(thread, item?.NoteId))
            {
                MenuFlyoutItem menuItem = new()
                {
                    Text = option.Label,
                    Tag = option,
                };
                menuItem.Click += OnInspectorNoteScopeOptionClicked;
                flyout.Items.Add(menuItem);
            }

            return flyout;
        }

        private void FocusInspectorNoteEditor(string noteId)
        {
            if (string.IsNullOrWhiteSpace(noteId))
            {
                return;
            }

            string automationId = BuildNoteEditorAutomationId(noteId);
            DispatcherQueue.TryEnqueue(() =>
            {
                if (FindFirstElement(ShellRoot, candidate =>
                        candidate is TextBox textBox &&
                        string.Equals(AutomationProperties.GetAutomationId(textBox), automationId, StringComparison.Ordinal)) is not TextBox noteEditor)
                {
                    return;
                }

                noteEditor.Focus(FocusState.Programmatic);
                noteEditor.Select(noteEditor.Text?.Length ?? 0, 0);
            });
        }

        private void RefreshInspectorFileBrowser(bool forceRebuild = false)
        {
            if (InspectorDirectoryTree is null || InspectorDirectoryRootText is null || InspectorDirectoryMetaText is null || InspectorDirectoryEmptyText is null)
            {
                return;
            }

            if (_showingSettings || !_inspectorOpen || _activeThread is null)
            {
                CancelPendingInspectorDirectoryBuilds();
                UpdateInspectorFileActionState();
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
                _inspectorDirectoryModelsByNode.Clear();
                _inspectorDirectoryDepthByNode.Clear();
                _inspectorDirectoryModelsByPath.Clear();
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
            bool rootChanged = !string.Equals(_lastInspectorDirectoryRootPath, rootPath, StringComparison.OrdinalIgnoreCase);
            GitThreadSnapshot displayedSnapshot = ResolveDisplayedGitSnapshot();
            IReadOnlyList<GitChangedFile> liveFiles = displayedSnapshot?.ChangedFiles is { } changedFiles
                ? changedFiles
                : Array.Empty<GitChangedFile>();
            string renderKey = BuildInspectorDirectoryRenderKey(liveFiles);
            if (liveFiles.Count > 0)
            {
                if (!string.Equals(InspectorDirectoryTree.Tag as string, renderKey, StringComparison.Ordinal))
                {
                    shouldRebuild = true;
                }
            }

            if (shouldRebuild)
            {
                string cacheKey = BuildInspectorDirectoryCacheKey(rootPath, renderKey);
                if (!forceRebuild &&
                    _inspectorDirectoryUiCacheByKey.TryGetValue(cacheKey, out InspectorDirectoryUiCache cachedUi) &&
                    cachedUi.FileCount > 0)
                {
                    ApplyInspectorDirectoryUiCache(cachedUi);
                    shouldRebuild = false;
                }
                else if (!forceRebuild &&
                    rootChanged &&
                    (_activeGitSnapshot is null || liveFiles.Count == 0) &&
                    TryGetInspectorDirectoryUiForRoot(rootPath, out InspectorDirectoryUiCache cachedRootUi) &&
                    cachedRootUi.FileCount > 0)
                {
                    ApplyInspectorDirectoryUiCache(cachedRootUi);
                    shouldRebuild = false;
                }
            }

            if (shouldRebuild)
            {
                QueueInspectorDirectoryBuild(
                    rootPath,
                    liveFiles,
                    renderKey,
                    bypassCache: forceRebuild,
                    correlationId: NativeAutomationDiagnostics.CaptureCurrentCorrelationId());
            }

            EditorPaneRecord editorPane = ResolveInspectorEditorPane(createIfNeeded: false);
            string selectedPath = editorPane?.Editor.SelectedFilePath;
            InspectorDirectoryMetaText.Text = editorPane is null
                ? "Select a file to open it in a new editor pane."
                : editorPane.Editor.StatusText;
            UpdateInspectorDirectorySelection(selectedPath);
            UpdateInspectorFileActionState();
        }

        private void CancelPendingInspectorDirectoryBuilds()
        {
            _pendingInspectorDirectoryRootPath = null;
            _pendingInspectorDirectoryRenderKey = null;
            _latestInspectorDirectoryBuildRequestId++;
            _inspectorDirectoryBuildCancellation?.Cancel();
            _inspectorDirectoryBuildCancellation?.Dispose();
            _inspectorDirectoryBuildCancellation = null;
        }

        private void QueueInspectorDirectoryBuild(
            string rootPath,
            IReadOnlyList<GitChangedFile> changedFiles,
            string renderKey,
            bool bypassCache = false,
            string correlationId = null)
        {
            if (_showingSettings || !_inspectorOpen || _activeThread is null)
            {
                return;
            }

            if (string.Equals(_pendingInspectorDirectoryRootPath, rootPath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_pendingInspectorDirectoryRenderKey, renderKey, StringComparison.Ordinal) &&
                !bypassCache)
            {
                return;
            }

            _pendingInspectorDirectoryRootPath = rootPath;
            _pendingInspectorDirectoryRenderKey = renderKey;
            int requestId = ++_latestInspectorDirectoryBuildRequestId;
            _inspectorDirectoryBuildCancellation?.Cancel();
            _inspectorDirectoryBuildCancellation?.Dispose();
            _inspectorDirectoryBuildCancellation = new System.Threading.CancellationTokenSource();
            System.Threading.CancellationToken cancellationToken = _inspectorDirectoryBuildCancellation.Token;

            bool rootChanged = !string.Equals(_lastInspectorDirectoryRootPath, rootPath, StringComparison.OrdinalIgnoreCase);
            if (rootChanged)
            {
                _inspectorDirectoryNodesByPath.Clear();
                _inspectorDirectoryItemsByNode.Clear();
                _inspectorDirectoryModelsByNode.Clear();
                _inspectorDirectoryDepthByNode.Clear();
                _inspectorDirectoryModelsByPath.Clear();
                InspectorDirectoryTree.SelectedNode = null;
                InspectorDirectoryTree.RootNodes.Clear();
                InspectorDirectoryEmptyText.Visibility = Visibility.Collapsed;
            }

            _ = System.Threading.Tasks.Task.Run(
                () => BuildInspectorDirectoryTree(rootPath, changedFiles, renderKey, bypassCache, correlationId, cancellationToken),
                cancellationToken)
                .ContinueWith(task =>
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (requestId != _latestInspectorDirectoryBuildRequestId)
                        {
                            return;
                        }

                        if (task.IsCanceled)
                        {
                            return;
                        }

                        if (task.IsFaulted)
                        {
                            Exception failure = task.Exception?.GetBaseException();
                            LogAutomationEvent("inspector", "directory_build_failed", $"Failed to build project file tree: {failure?.Message ?? "Unknown error"}", new Dictionary<string, string>
                            {
                                ["rootPath"] = rootPath ?? string.Empty,
                                ["renderKey"] = renderKey ?? string.Empty,
                                ["threadId"] = _activeThread?.Id ?? string.Empty,
                                ["projectId"] = _activeProject?.Id ?? string.Empty,
                            });
                            _pendingInspectorDirectoryRootPath = null;
                            _pendingInspectorDirectoryRenderKey = null;
                            _lastInspectorDirectoryRootPath = null;
                            InspectorDirectoryTree.Tag = null;
                            InspectorDirectoryEmptyText.Visibility = Visibility.Visible;
                            UpdateInspectorFileActionState();
                            return;
                        }

                        string activeRootPath = ResolveThreadRootPath(_activeProject, _activeThread);
                        if (!string.Equals(activeRootPath, task.Result.RootPath, StringComparison.OrdinalIgnoreCase))
                        {
                            return;
                        }

                        ApplyInspectorDirectoryBuildResult(task.Result, correlationId);
                    });
                }, System.Threading.Tasks.TaskScheduler.Default);
        }

        private static InspectorDirectoryBuildResult BuildInspectorDirectoryTree(
            string rootPath,
            IReadOnlyList<GitChangedFile> changedFiles,
            string renderKey,
            bool bypassCache = false,
            string correlationId = null,
            System.Threading.CancellationToken cancellationToken = default)
        {
            using var perfScope = NativeAutomationDiagnostics.TrackOperation("inspector.build.background", correlationId, background: true, data: new Dictionary<string, string>
            {
                ["rootPath"] = rootPath ?? string.Empty,
            });
            NativeAutomationDiagnostics.IncrementCounter("inspectorFileScan.count");
            IReadOnlyList<EditorPaneFileEntry> files = EditorPaneControl.EnumerateProjectFilesForRoot(rootPath, bypassCache, cancellationToken);
            Dictionary<string, InspectorDirectoryDecoration> decorationsByPath = BuildInspectorDirectoryDecorations(changedFiles);
            Dictionary<string, InspectorDirectoryNodeModel> rootNodes = new(StringComparer.OrdinalIgnoreCase);
            foreach (EditorPaneFileEntry file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
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

            return new InspectorDirectoryBuildResult
            {
                RootPath = rootPath,
                RenderKey = renderKey,
                FileCount = files.Count,
                RootNodes = OrderInspectorDirectoryNodes(rootNodes.Values).ToList(),
            };
        }

        private static string BuildInspectorDirectoryCacheKey(string rootPath, string renderKey)
        {
            return $"{rootPath ?? string.Empty}|{renderKey ?? string.Empty}";
        }

        private bool TryGetInspectorDirectoryUiForRoot(string rootPath, out InspectorDirectoryUiCache uiCache)
        {
            return _inspectorDirectoryUiCacheByRootPath.TryGetValue(rootPath ?? string.Empty, out uiCache) &&
                uiCache is not null;
        }

        private void ApplyInspectorDirectoryBuildResult(InspectorDirectoryBuildResult result, string correlationId = null)
        {
            if (_showingSettings || !_inspectorOpen || _activeThread is null || _activeInspectorSection != InspectorSection.Files)
            {
                return;
            }

            using var perfScope = NativeAutomationDiagnostics.TrackOperation("inspector.build.apply", correlationId, background: true, data: new Dictionary<string, string>
            {
                ["rootPath"] = result?.RootPath ?? string.Empty,
            });
            if (result is not null && result.FileCount == 0)
            {
                LogAutomationEvent("inspector", "directory_build_empty", "Project file tree scan returned no editable files", new Dictionary<string, string>
                {
                    ["rootPath"] = result.RootPath ?? string.Empty,
                    ["renderKey"] = result.RenderKey ?? string.Empty,
                    ["threadId"] = _activeThread?.Id ?? string.Empty,
                    ["projectId"] = _activeProject?.Id ?? string.Empty,
                });
            }
            InspectorDirectoryUiCache uiCache = BuildInspectorDirectoryUiCache(result);
            CacheInspectorDirectoryUi(uiCache);
            ApplyInspectorDirectoryUiCache(uiCache);
        }

        private InspectorDirectoryUiCache BuildInspectorDirectoryUiCache(InspectorDirectoryBuildResult result)
        {
            InspectorDirectoryUiCache uiCache = new()
            {
                RootPath = result.RootPath,
                RenderKey = result.RenderKey,
                FileCount = result.FileCount,
                RootNodes = result.RootNodes,
            };

            foreach (InspectorDirectoryNodeModel node in result.RootNodes)
            {
                IndexInspectorDirectoryNodeModel(node, uiCache.ModelsByPath);
            }

            return uiCache;
        }

        private void CacheInspectorDirectoryUi(InspectorDirectoryUiCache uiCache)
        {
            if (uiCache is null)
            {
                return;
            }

            _inspectorDirectoryUiCacheByKey[BuildInspectorDirectoryCacheKey(uiCache.RootPath, uiCache.RenderKey)] = uiCache;
            _inspectorDirectoryUiCacheByRootPath[uiCache.RootPath ?? string.Empty] = uiCache;
            while (_inspectorDirectoryUiCacheByKey.Count > 6)
            {
                string oldestKey = _inspectorDirectoryUiCacheByKey.Keys.FirstOrDefault();
                if (string.IsNullOrWhiteSpace(oldestKey))
                {
                    break;
                }

                if (_inspectorDirectoryUiCacheByKey.TryGetValue(oldestKey, out InspectorDirectoryUiCache evictedUi) &&
                    _inspectorDirectoryUiCacheByRootPath.TryGetValue(evictedUi.RootPath ?? string.Empty, out InspectorDirectoryUiCache cachedRootUi) &&
                    ReferenceEquals(cachedRootUi, evictedUi))
                {
                    _inspectorDirectoryUiCacheByRootPath.Remove(evictedUi.RootPath ?? string.Empty);
                }

                _inspectorDirectoryUiCacheByKey.Remove(oldestKey);
            }
        }

        private void ApplyInspectorDirectoryUiCache(InspectorDirectoryUiCache uiCache)
        {
            _pendingInspectorDirectoryRootPath = null;
            _pendingInspectorDirectoryRenderKey = null;
            _inspectorDirectoryNodesByPath.Clear();
            _inspectorDirectoryItemsByNode.Clear();
            _inspectorDirectoryModelsByNode.Clear();
            _inspectorDirectoryDepthByNode.Clear();
            _inspectorDirectoryModelsByPath.Clear();
            InspectorDirectoryTree.SelectedNode = null;
            InspectorDirectoryTree.RootNodes.Clear();
            InspectorDirectoryTree.Tag = uiCache?.RenderKey;
            InspectorDirectoryEmptyText.Visibility = uiCache is null || uiCache.FileCount == 0 ? Visibility.Visible : Visibility.Collapsed;
            _lastInspectorDirectoryRootPath = uiCache?.RootPath;

            if (uiCache is null || uiCache.FileCount == 0)
            {
                UpdateInspectorFileActionState();
                return;
            }

            foreach ((string relativePath, InspectorDirectoryNodeModel model) in uiCache.ModelsByPath)
            {
                _inspectorDirectoryModelsByPath[relativePath] = model;
            }

            foreach (InspectorDirectoryNodeModel rootNode in uiCache.RootNodes)
            {
                InspectorDirectoryTree.RootNodes.Add(BuildInspectorDirectoryTreeNode(rootNode, depth: 0));
            }

            EditorPaneRecord editorPane = ResolveInspectorEditorPane(createIfNeeded: false);
            UpdateInspectorDirectorySelection(editorPane?.Editor.SelectedFilePath);
            UpdateInspectorFileActionState();
        }

        private static void IndexInspectorDirectoryNodeModel(
            InspectorDirectoryNodeModel node,
            Dictionary<string, InspectorDirectoryNodeModel> modelsByPath)
        {
            if (node is null || string.IsNullOrWhiteSpace(node.RelativePath))
            {
                return;
            }

            modelsByPath[node.RelativePath] = node;
            if (!node.IsDirectory)
            {
                return;
            }

            foreach (InspectorDirectoryNodeModel child in node.Children.Values)
            {
                IndexInspectorDirectoryNodeModel(child, modelsByPath);
            }
        }

        private TreeViewNode BuildInspectorDirectoryTreeNode(InspectorDirectoryNodeModel node, int depth)
        {
            InspectorDirectoryDecoration decoration = node.Decoration;
            FileIconInfo themeIcon = FileIconTheme.Resolve(node.RelativePath, node.IsDirectory);
            Brush defaultIconBrush = AppBrush(InspectorDirectoryTree, ResolveInspectorIconBrushKey(node.RelativePath, node.IsDirectory, decoration));
            Brush resolvedIconBrush = node.IsDirectory && decoration?.HasChangedDescendant == true
                ? defaultIconBrush
                : themeIcon?.Brush ?? defaultIconBrush;
            InspectorDirectoryTreeItem item = new()
            {
                Name = node.Name,
                RelativePath = node.RelativePath,
                IsDirectory = node.IsDirectory,
                IconGlyph = themeIcon?.Glyph ?? ResolveInspectorItemGlyph(node.RelativePath, node.IsDirectory),
                IconFontFamily = themeIcon?.FontFamily,
                IconFontSize = themeIcon?.FontSize ?? 11,
                UseGlyphBadge = node.IsDirectory || themeIcon is not null,
                IconBrush = resolvedIconBrush,
                KindText = themeIcon is null ? ResolveInspectorKindText(node.RelativePath, node.IsDirectory) : string.Empty,
                KindBrush = resolvedIconBrush,
                StatusText = ResolveInspectorChangeMarker(decoration?.File, hasChangedDescendant: decoration?.HasChangedDescendant == true),
                StatusBrush = AppBrush(InspectorDirectoryTree, ResolveInspectorChangeBrushKey(decoration?.File, hasChangedDescendant: decoration?.HasChangedDescendant == true)),
            };

            TreeViewNode treeNode = new()
            {
                Content = BuildInspectorDirectoryNodeContent(item),
                IsExpanded = node.IsDirectory && ShouldExpandInspectorDirectoryNode(node, depth),
            };

            _inspectorDirectoryItemsByNode[treeNode] = item;
            _inspectorDirectoryModelsByNode[treeNode] = node;
            _inspectorDirectoryDepthByNode[treeNode] = depth;
            _inspectorDirectoryNodesByPath[item.RelativePath] = treeNode;
            if (node.IsDirectory && node.Children.Count > 0)
            {
                treeNode.HasUnrealizedChildren = true;
                if (treeNode.IsExpanded)
                {
                    MaterializeInspectorDirectoryChildren(treeNode);
                }
            }

            return treeNode;
        }

        private void MaterializeInspectorDirectoryChildren(TreeViewNode node)
        {
            if (node is null ||
                !node.HasUnrealizedChildren ||
                !_inspectorDirectoryModelsByNode.TryGetValue(node, out InspectorDirectoryNodeModel model) ||
                !model.IsDirectory)
            {
                return;
            }

            int childDepth = (_inspectorDirectoryDepthByNode.TryGetValue(node, out int depth) ? depth : 0) + 1;
            foreach (InspectorDirectoryNodeModel child in OrderInspectorDirectoryNodes(model.Children.Values))
            {
                node.Children.Add(BuildInspectorDirectoryTreeNode(child, childDepth));
            }

            node.HasUnrealizedChildren = false;
        }

        private static IEnumerable<InspectorDirectoryNodeModel> OrderInspectorDirectoryNodes(IEnumerable<InspectorDirectoryNodeModel> nodes)
        {
            return nodes
                .OrderByDescending(node => node.IsDirectory)
                .ThenBy(node => node.Name, StringComparer.OrdinalIgnoreCase);
        }

        private static string BuildInspectorDirectoryRenderKey(IReadOnlyList<GitChangedFile> changedFiles)
        {
            if (changedFiles is null || changedFiles.Count == 0)
            {
                return string.Empty;
            }

            StringBuilder builder = new();
            foreach (GitChangedFile file in changedFiles)
            {
                if (file is null)
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.Append('|');
                }

                builder
                    .Append(file.Path ?? string.Empty)
                    .Append(':')
                    .Append(file.Status);
            }

            return builder.ToString();
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
                MinHeight = 24,
                ColumnSpacing = 6,
                Margin = new Thickness(-6, 1, 0, 1),
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ToolTipService.SetToolTip(row, item.RelativePath);

            Brush accentBrush = !string.IsNullOrWhiteSpace(item.StatusText)
                ? item.StatusBrush
                : item.IconBrush ?? item.KindBrush ?? AppBrush(InspectorDirectoryTree, "ShellPaneActiveBorderBrush");

            Border accent = new()
            {
                Width = 2,
                Margin = new Thickness(0, 1, 0, 1),
                CornerRadius = new CornerRadius(999),
                Background = accentBrush,
                Opacity = item.IsDirectory ? 0.45 : 0.82,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            row.Children.Add(accent);

            FrameworkElement glyph = BuildInspectorNodeGlyph(item);
            Grid.SetColumn(glyph, 1);
            row.Children.Add(glyph);

            TextBlock name = new()
            {
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11.1,
                FontWeight = item.IsDirectory
                    ? Microsoft.UI.Text.FontWeights.SemiBold
                    : Microsoft.UI.Text.FontWeights.Normal,
                Foreground = AppBrush(InspectorDirectoryTree, "ShellTextPrimaryBrush"),
                Text = item.Name,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(name, 2);
            row.Children.Add(name);

            StackPanel adornments = new()
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(adornments, 3);

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
            if (item.IsDirectory)
            {
                return new Border
                {
                    Width = 4,
                    Height = 4,
                    CornerRadius = new CornerRadius(2),
                    Background = item.StatusBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                };
            }

            return new TextBlock
            {
                Text = item.StatusText,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 10,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = item.StatusBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.9,
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
            if (item.UseGlyphBadge)
            {
                return new FontIcon
                {
                    Glyph = item.IconGlyph,
                    FontFamily = item.IconFontFamily,
                    FontSize = item.IconFontSize,
                    Foreground = item.IconBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                };
            }

            return new TextBlock
            {
                Text = item.KindText,
                FontFamily = new FontFamily("Consolas"),
                FontSize = item.KindText?.Length > 3 ? 8.8 : 9.4,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = item.KindBrush,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        private static FrameworkElement BuildInspectorPathBadge(string relativePath, bool isDirectory, Brush accentBrush)
        {
            InspectorDirectoryTreeItem item = new()
            {
                RelativePath = relativePath,
                IsDirectory = isDirectory,
                IconGlyph = ResolveInspectorItemGlyph(relativePath, isDirectory),
                IconBrush = accentBrush,
                KindText = ResolveInspectorKindText(relativePath, isDirectory),
                KindBrush = accentBrush,
                UseGlyphBadge = isDirectory,
            };

            if (item.UseGlyphBadge)
            {
                return new FontIcon
                {
                    Glyph = item.IconGlyph,
                    FontSize = 11,
                    Foreground = item.IconBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                };
            }

            return new TextBlock
            {
                Text = item.KindText,
                FontFamily = new FontFamily("Consolas"),
                FontSize = item.KindText?.Length > 3 ? 8.8 : 9.4,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = item.KindBrush,
                VerticalAlignment = VerticalAlignment.Center,
            };
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
                !TryEnsureInspectorDirectoryNode(selectedPath, out TreeViewNode node))
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
                MaterializeInspectorDirectoryChildren(current);
                current.IsExpanded = true;
                current = current.Parent as TreeViewNode;
            }

            InspectorDirectoryTree.SelectedNode = node;
        }

        private bool TryEnsureInspectorDirectoryNode(string relativePath, out TreeViewNode node)
        {
            if (_inspectorDirectoryNodesByPath.TryGetValue(relativePath, out node))
            {
                return true;
            }

            if (!_inspectorDirectoryModelsByPath.TryGetValue(relativePath, out InspectorDirectoryNodeModel model))
            {
                node = null;
                return false;
            }

            string parentPath = GetInspectorDirectoryParentPath(model.RelativePath);
            if (!string.IsNullOrWhiteSpace(parentPath))
            {
                if (!TryEnsureInspectorDirectoryNode(parentPath, out TreeViewNode parentNode))
                {
                    node = null;
                    return false;
                }

                MaterializeInspectorDirectoryChildren(parentNode);
                parentNode.IsExpanded = true;
            }

            return _inspectorDirectoryNodesByPath.TryGetValue(relativePath, out node);
        }

        private static string GetInspectorDirectoryParentPath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return null;
            }

            int separatorIndex = relativePath.LastIndexOf('/');
            return separatorIndex > 0 ? relativePath[..separatorIndex] : null;
        }

        private void UpdateInspectorFileActionState()
        {
            EditorPaneRecord editorPane = ResolveInspectorEditorPane(createIfNeeded: false);
            WorkspaceThreadNote selectedNote = ResolveSelectedThreadNote(_activeThread);
            bool canSaveSelectedNote = selectedNote is { IsArchived: false } &&
                ResolveNoteDraftState(selectedNote)?.Dirty == true;
            if (InspectorCollapseAllButton is not null)
            {
                InspectorCollapseAllButton.IsEnabled = InspectorDirectoryTree?.RootNodes.Count > 0;
            }

            if (InspectorSaveFileButton is not null)
            {
                InspectorSaveFileButton.IsEnabled = editorPane?.Editor.CanSave == true;
            }

            if (InspectorAddNoteButton is not null)
            {
                InspectorAddNoteButton.IsEnabled = _activeThread is not null;
            }

            if (InspectorSaveNoteButton is not null)
            {
                InspectorSaveNoteButton.IsEnabled = canSaveSelectedNote;
            }

            if (InspectorDeleteNoteButton is not null)
            {
                InspectorDeleteNoteButton.IsEnabled = selectedNote is not null;
            }

            if (InspectorInlineAddNoteButton is not null)
            {
                InspectorInlineAddNoteButton.IsEnabled = _activeThread is not null;
            }

            if (InspectorInlineSaveNoteButton is not null)
            {
                InspectorInlineSaveNoteButton.IsEnabled = canSaveSelectedNote;
            }

            if (InspectorInlineDeleteNoteButton is not null)
            {
                InspectorInlineDeleteNoteButton.IsEnabled = selectedNote is not null;
            }
        }

        private static void CollapseInspectorDirectoryNode(TreeViewNode node)
        {
            if (node is null)
            {
                return;
            }

            node.IsExpanded = false;
            foreach (TreeViewNode childNode in node.Children)
            {
                CollapseInspectorDirectoryNode(childNode);
            }
        }

        private void RefreshInspectorFileBrowserStatus()
        {
            if (InspectorDirectoryMetaText is null)
            {
                return;
            }

            EditorPaneRecord editorPane = ResolveInspectorEditorPane(createIfNeeded: false);
            InspectorDirectoryMetaText.Text = editorPane is null
                ? "Select a file to open it in a new editor pane."
                : editorPane.Editor.StatusText;
            UpdateInspectorFileActionState();
        }

        private void RefreshInspectorNotes()
        {
            if (InspectorNotesMetaText is null ||
                InspectorNotesGroupsItemsControl is null ||
                InspectorNotesEmptyText is null ||
                InspectorNotesThreadScopeButton is null ||
                InspectorNotesProjectScopeButton is null)
            {
                return;
            }

            List<InspectorNoteGroupItem> noteGroups = BuildInspectorNoteGroups(_activeProject, _activeThread, _activeNotesListScope).ToList();
            InspectorNotesGroupsItemsControl.ItemsSource = noteGroups;

            InspectorNotesEmptyText.Text = _activeNotesListScope == NotesListScope.Thread
                ? "No thread notes yet. Add a note to pin context here."
                : "No project notes yet.";
            InspectorNotesEmptyText.Visibility = noteGroups.Sum(candidate => candidate.ActiveNotes.Count + candidate.ArchivedNotes.Count) == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            InspectorNotesThreadScopeButton.IsEnabled = _activeThread is not null;
            InspectorNotesProjectScopeButton.IsEnabled = _activeProject is not null;
            ApplyNotesListScopeButtonState(InspectorNotesThreadScopeButton, _activeNotesListScope == NotesListScope.Thread);
            ApplyNotesListScopeButtonState(InspectorNotesProjectScopeButton, _activeNotesListScope == NotesListScope.Project);
            UpdateInspectorFileActionState();
            InspectorNotesMetaText.Text = BuildNotesMeta(_activeProject, _activeThread, _activeNotesListScope);
            InspectorNotesMetaText.Foreground = AppBrush(InspectorNotesMetaText, "ShellTextSecondaryBrush");
        }

        private void OpenThreadNotes(WorkspaceThread thread, bool focusEditor = true, NotesListScope? scope = null)
        {
            if (thread is null)
            {
                return;
            }

            WorkspaceThreadNote noteToFocus = focusEditor ? ResolveSelectedThreadNote(thread) : null;

            if (!ReferenceEquals(_activeThread, thread) || !ReferenceEquals(_activeProject, FindProjectForThread(thread)))
            {
                ClearInspectorNoteDraft();
                ActivateThread(thread);
            }

            ShowTerminalShellIfNeeded(queueGitRefresh: false);
            if (scope.HasValue)
            {
                _activeNotesListScope = scope.Value;
            }

            if (!_inspectorOpen)
            {
                _inspectorOpen = true;
                UpdateInspectorVisibility();
                QueueSessionSave();
            }

            SetInspectorSection(InspectorSection.Notes, refreshFiles: false);
            RefreshInspectorNotes();

            if (focusEditor)
            {
                FocusInspectorNoteEditor(noteToFocus?.Id);
            }
        }

        private bool UpdateThreadNotes(WorkspaceThread thread, string nextNotes, bool refreshInspector = true)
        {
            if (thread is null)
            {
                return false;
            }

            WorkspaceThreadNote note = ResolveSelectedThreadNote(thread);
            if (note is null && string.IsNullOrWhiteSpace(nextNotes))
            {
                if (refreshInspector && ReferenceEquals(thread, _activeThread))
                {
                    RefreshInspectorNotes();
                }

                return false;
            }

            if (note is null)
            {
                AddThreadNote(thread, title: null, text: nextNotes, selectAfterCreate: true);
                return true;
            }

            return UpdateThreadNoteText(thread, note, nextNotes, refreshInspector);
        }

        private WorkspaceThreadNote ResolveSelectedThreadNote(WorkspaceThread thread)
        {
            if (thread is null)
            {
                return null;
            }

            if (thread.NoteEntries.Count == 0)
            {
                thread.SelectedNoteId = null;
                return null;
            }

            WorkspaceThreadNote note = thread.NoteEntries.FirstOrDefault(candidate => string.Equals(candidate.Id, thread.SelectedNoteId, StringComparison.Ordinal))
                ?? ResolvePreferredThreadNote(thread);
            thread.SelectedNoteId = note.Id;
            return note;
        }

        private static WorkspaceThreadNote ResolvePreferredThreadNote(WorkspaceThread thread)
        {
            if (thread is null)
            {
                return null;
            }

            return thread.NoteEntries
                .Where(candidate => !candidate.IsArchived)
                .OrderByDescending(candidate => candidate.UpdatedAt)
                .FirstOrDefault()
                ?? thread.NoteEntries.OrderByDescending(candidate => candidate.UpdatedAt).FirstOrDefault();
        }

        private static string BuildDefaultThreadNoteTitle(WorkspaceThread thread)
        {
            int nextIndex = Math.Max(1, (thread?.NoteEntries.Count ?? 0) + 1);
            return nextIndex == 1 ? "Note" : $"Note {nextIndex}";
        }

        private string ResolveNotePaneId(WorkspaceThread thread, string requestedPaneId = null, bool preferSelectedPane = true)
        {
            if (thread is null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(requestedPaneId) &&
                thread.Panes.Any(candidate => string.Equals(candidate.Id, requestedPaneId, StringComparison.Ordinal)))
            {
                return requestedPaneId;
            }

            return preferSelectedPane
                ? GetSelectedPane(thread)?.Id
                : null;
        }

        private WorkspaceThreadNote AddThreadNote(WorkspaceThread thread, string title, string text, bool selectAfterCreate, string paneId = null)
        {
            if (thread is null)
            {
                return null;
            }

            WorkspaceThreadNote note = new(
                string.IsNullOrWhiteSpace(title) ? BuildDefaultThreadNoteTitle(thread) : title,
                text)
            {
                PaneId = ResolveNotePaneId(thread, paneId, preferSelectedPane: false),
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };

            thread.NoteEntries.Insert(0, note);
            if (selectAfterCreate)
            {
                thread.SelectedNoteId = note.Id;
            }

            AfterThreadNotesChanged(thread);
            return note;
        }

        private WorkspaceThreadNote UpsertThreadNote(WorkspaceThread thread, string noteId, string title, string text, bool selectAfterUpdate, string paneId = null)
        {
            if (thread is null)
            {
                return null;
            }

            WorkspaceThreadNote note = string.IsNullOrWhiteSpace(noteId)
                ? ResolveSelectedThreadNote(thread)
                : thread.NoteEntries.FirstOrDefault(candidate => string.Equals(candidate.Id, noteId, StringComparison.Ordinal));

            if (note is null)
            {
                return AddThreadNote(thread, title, text, selectAfterCreate: true, paneId);
            }

            bool changed = false;
            string resolvedTitle = string.IsNullOrWhiteSpace(title) ? note.Title : title.Trim();
            if (!string.Equals(note.Title, resolvedTitle, StringComparison.Ordinal))
            {
                note.Title = resolvedTitle;
                changed = true;
            }

            string normalizedText = string.IsNullOrWhiteSpace(text) ? null : text;
            if (!string.Equals(note.Text, normalizedText, StringComparison.Ordinal))
            {
                note.Text = normalizedText;
                changed = true;
            }

            string resolvedPaneId = paneId is null
                ? note.PaneId
                : ResolveNotePaneId(thread, paneId, preferSelectedPane: false);
            if (!string.Equals(note.PaneId, resolvedPaneId, StringComparison.Ordinal))
            {
                note.PaneId = resolvedPaneId;
                changed = true;
            }

            if (!changed)
            {
                if (selectAfterUpdate)
                {
                    thread.SelectedNoteId = note.Id;
                }

                return note;
            }

            note.UpdatedAt = DateTimeOffset.UtcNow;
            thread.NoteEntries.Remove(note);
            thread.NoteEntries.Insert(0, note);
            if (selectAfterUpdate)
            {
                thread.SelectedNoteId = note.Id;
            }

            AfterThreadNotesChanged(thread);
            return note;
        }

        private bool UpdateThreadNoteText(WorkspaceThread thread, WorkspaceThreadNote note, string text, bool refreshInspector)
        {
            if (thread is null || note is null)
            {
                return false;
            }

            string normalizedText = string.IsNullOrWhiteSpace(text) ? null : text;
            if (string.Equals(note.Text, normalizedText, StringComparison.Ordinal))
            {
                if (refreshInspector && ReferenceEquals(thread, _activeThread))
                {
                    RefreshInspectorNotes();
                }

                return false;
            }

            note.Text = normalizedText;
            note.UpdatedAt = DateTimeOffset.UtcNow;
            thread.NoteEntries.Remove(note);
            thread.NoteEntries.Insert(0, note);
            thread.SelectedNoteId = note.Id;
            AfterThreadNotesChanged(thread, refreshInspector);
            return true;
        }

        private bool UpdateThreadNoteTitle(WorkspaceThread thread, WorkspaceThreadNote note, string title, bool refreshInspector)
        {
            if (thread is null || note is null)
            {
                return false;
            }

            string normalizedTitle = string.IsNullOrWhiteSpace(title) ? BuildDefaultThreadNoteTitle(thread) : title.Trim();
            if (string.Equals(note.Title, normalizedTitle, StringComparison.Ordinal))
            {
                if (refreshInspector && ReferenceEquals(thread, _activeThread))
                {
                    RefreshInspectorNotes();
                }

                return false;
            }

            note.Title = normalizedTitle;
            note.UpdatedAt = DateTimeOffset.UtcNow;
            thread.NoteEntries.Remove(note);
            thread.NoteEntries.Insert(0, note);
            thread.SelectedNoteId = note.Id;
            AfterThreadNotesChanged(thread, refreshInspector);
            return true;
        }

        private bool DeleteThreadNote(WorkspaceThread thread, string noteId)
        {
            if (thread is null)
            {
                return false;
            }

            WorkspaceThreadNote note = string.IsNullOrWhiteSpace(noteId)
                ? ResolveSelectedThreadNote(thread)
                : thread.NoteEntries.FirstOrDefault(candidate => string.Equals(candidate.Id, noteId, StringComparison.Ordinal));
            if (note is null)
            {
                return false;
            }

            _noteDraftsById.Remove(note.Id);
            thread.NoteEntries.Remove(note);
            thread.SelectedNoteId = ResolvePreferredThreadNote(thread)?.Id;
            AfterThreadNotesChanged(thread);
            return true;
        }

        private bool SelectThreadNote(WorkspaceThread thread, string noteId, bool navigateToAttachment = true)
        {
            if (thread is null || string.IsNullOrWhiteSpace(noteId))
            {
                return false;
            }

            WorkspaceThreadNote note = thread.NoteEntries.FirstOrDefault(candidate => string.Equals(candidate.Id, noteId, StringComparison.Ordinal));
            if (note is null)
            {
                return false;
            }

            if (!ReferenceEquals(thread, _activeThread))
            {
                ActivateThread(thread);
            }

            thread.SelectedNoteId = note.Id;
            if (note.IsArchived)
            {
                _expandedArchivedNoteThreadIds.Add(thread.Id);
            }
            if (navigateToAttachment && !string.IsNullOrWhiteSpace(note.PaneId))
            {
                SelectTab(note.PaneId);
            }

            RefreshInspectorNotes();
            QueueProjectTreeRefresh();
            QueueSessionSave();
            return true;
        }

        private bool UpdateThreadNotePaneAttachment(WorkspaceThread thread, WorkspaceThreadNote note, string paneId)
        {
            if (thread is null || note is null)
            {
                return false;
            }

            string resolvedPaneId = !string.IsNullOrWhiteSpace(paneId) &&
                thread.Panes.Any(candidate => string.Equals(candidate.Id, paneId, StringComparison.Ordinal))
                ? paneId
                : null;
            if (string.Equals(note.PaneId, resolvedPaneId, StringComparison.Ordinal))
            {
                return false;
            }

            note.PaneId = resolvedPaneId;
            note.UpdatedAt = DateTimeOffset.UtcNow;
            thread.NoteEntries.Remove(note);
            thread.NoteEntries.Insert(0, note);
            thread.SelectedNoteId = note.Id;
            AfterThreadNotesChanged(thread);
            return true;
        }

        private bool SetThreadNoteArchived(WorkspaceThread thread, WorkspaceThreadNote note, bool archived)
        {
            if (thread is null || note is null || note.IsArchived == archived)
            {
                return false;
            }

            note.ArchivedAt = archived ? DateTimeOffset.UtcNow : null;
            note.UpdatedAt = DateTimeOffset.UtcNow;
            thread.NoteEntries.Remove(note);
            if (archived)
            {
                thread.NoteEntries.Add(note);
                _expandedArchivedNoteThreadIds.Add(thread.Id);
            }
            else
            {
                thread.NoteEntries.Insert(0, note);
            }

            thread.SelectedNoteId = archived
                ? ResolvePreferredThreadNote(thread)?.Id ?? note.Id
                : note.Id;
            AfterThreadNotesChanged(thread);
            return true;
        }

        private void ClearPaneNoteAttachments(WorkspaceThread thread, string paneId)
        {
            if (thread is null || string.IsNullOrWhiteSpace(paneId))
            {
                return;
            }

            bool changed = false;
            foreach (WorkspaceThreadNote note in thread.NoteEntries.Where(candidate => string.Equals(candidate.PaneId, paneId, StringComparison.Ordinal)))
            {
                note.PaneId = null;
                note.UpdatedAt = DateTimeOffset.UtcNow;
                changed = true;
            }

            if (changed)
            {
                AfterThreadNotesChanged(thread);
            }
        }

        private void AfterThreadNotesChanged(WorkspaceThread thread, bool refreshInspector = true)
        {
            if (thread is not null)
            {
                HashSet<string> liveNoteIds = thread.NoteEntries
                    .Select(note => note.Id)
                    .ToHashSet(StringComparer.Ordinal);
                foreach (string staleNoteId in _noteDraftsById.Keys.Where(noteId => !liveNoteIds.Contains(noteId)).ToList())
                {
                    _noteDraftsById.Remove(staleNoteId);
                }
            }

            QueueProjectTreeRefresh();
            QueueSessionSave();

            if (refreshInspector && ShouldRefreshInspectorNotesForThread(thread))
            {
                RefreshInspectorNotes();
            }
        }

        private bool ShouldRefreshInspectorNotesForThread(WorkspaceThread thread)
        {
            if (thread is null || _activeInspectorSection != InspectorSection.Notes)
            {
                return false;
            }

            return _activeNotesListScope == NotesListScope.Project
                ? ReferenceEquals(thread.Project, _activeProject)
                : ReferenceEquals(thread, _activeThread);
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

        private WorkspaceSessionSnapshot BuildSessionSnapshot(SessionSaveDetail detail)
        {
            bool includeGitSnapshots = detail == SessionSaveDetail.Full;
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
                        Notes = thread.Notes,
                        SelectedNoteId = thread.SelectedNoteId,
                        NoteEntries = thread.NoteEntries.Select(note => new ThreadNoteSessionSnapshot
                        {
                            Id = note.Id,
                            Title = note.Title,
                            Text = note.Text,
                            PaneId = note.PaneId,
                            CreatedAt = note.CreatedAt.ToString("O"),
                            UpdatedAt = note.UpdatedAt.ToString("O"),
                            ArchivedAt = note.ArchivedAt?.ToString("O"),
                        }).ToList(),
                        SelectedDiffPath = thread.SelectedDiffPath,
                        DiffReviewSource = FormatDiffReviewSource(thread.DiffReviewSource),
                        SelectedCheckpointId = thread.SelectedCheckpointId,
                        BaselineSnapshot = includeGitSnapshots ? CreateGitSnapshotSessionSnapshot(thread.BaselineSnapshot) : null,
                        LiveSnapshot = CreateGitSnapshotSessionSnapshot(thread.LiveSnapshot),
                        LiveSnapshotCapturedAt = thread.LiveSnapshotCapturedAt == default ? null : thread.LiveSnapshotCapturedAt.ToString("O"),
                        SelectedPaneId = thread.SelectedPaneId,
                        Layout = WorkspaceSessionStore.FormatLayout(thread.LayoutPreset),
                        PrimarySplitRatio = thread.PrimarySplitRatio,
                        SecondarySplitRatio = thread.SecondarySplitRatio,
                        AutoFitPaneContentLocked = thread.AutoFitPaneContentLocked,
                        DiffCheckpoints = includeGitSnapshots
                            ? thread.DiffCheckpoints.Select(checkpoint => new GitCheckpointSessionSnapshot
                            {
                                Id = checkpoint.Id,
                                Name = checkpoint.Name,
                                CapturedAt = checkpoint.CapturedAt.ToString("O"),
                                Snapshot = CreateGitSnapshotSessionSnapshot(checkpoint.Snapshot),
                            }).ToList()
                            : new List<GitCheckpointSessionSnapshot>(),
                        Panes = thread.Panes
                            .Where(ShouldPersistPane)
                            .Select(CreatePaneSessionSnapshot)
                            .Where(paneSnapshot => paneSnapshot is not null)
                            .ToList(),
                    }).ToList(),
                }).ToList(),
            };
        }

        private static bool ShouldPersistPane(WorkspacePaneRecord pane)
        {
            return pane is not null && (!pane.IsExited || pane.PersistExitedState);
        }

        private static PaneSessionSnapshot CreatePaneSessionSnapshot(WorkspacePaneRecord pane)
        {
            if (pane is null)
            {
                return null;
            }

            if (pane is DeferredPaneRecord deferredPane)
            {
                PaneSessionSnapshot snapshot = deferredPane.Snapshot;
                return new PaneSessionSnapshot
                {
                    Id = pane.Id,
                    Kind = pane.Kind.ToString().ToLowerInvariant(),
                    Title = pane.Title,
                    HasCustomTitle = pane.HasCustomTitle,
                    IsExited = pane.IsExited,
                    ReplayRestoreFailed = pane.ReplayRestoreFailed,
                    BrowserUri = deferredPane.BrowserUri,
                    SelectedBrowserTabId = deferredPane.SelectedBrowserTabId,
                    BrowserTabs = deferredPane.BrowserTabs.Select(tab => new BrowserTabSessionSnapshot
                    {
                        Id = tab.Id,
                        Title = tab.Title,
                        Uri = tab.Uri,
                    }).ToList(),
                    DiffPath = deferredPane.DiffPath,
                    EditorFilePath = deferredPane.EditorFilePath,
                    ReplayTool = pane.ReplayTool,
                    ReplaySessionId = pane.ReplaySessionId,
                    ReplayCommand = pane.ReplayCommand,
                    ReplayArguments = pane.ReplayArguments,
                };
            }

            return new PaneSessionSnapshot
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
                ReplayArguments = pane.ReplayArguments,
            };
        }

        private static string BuildBrowserPersistenceKey(BrowserPaneControl browser)
        {
            if (browser is null)
            {
                return string.Empty;
            }

            StringBuilder builder = new();
            builder.Append(browser.SelectedTabId ?? string.Empty)
                .Append('|')
                .Append(browser.CurrentUri ?? string.Empty)
                .Append('|');

            foreach (BrowserPaneControl.BrowserPaneTabSnapshot tab in browser.Tabs)
            {
                builder.Append(tab.Id ?? string.Empty)
                    .Append(':')
                    .Append(tab.Uri ?? string.Empty)
                    .Append('|');
            }

            return builder.ToString();
        }

        private static GitSnapshotSessionSnapshot CreateGitSnapshotSessionSnapshot(GitThreadSnapshot snapshot)
        {
            if (snapshot is null)
            {
                return null;
            }

            bool persistFullDiffText = ShouldPersistFullDiffText(snapshot);

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
                    DiffText = ShouldPersistDiffText(snapshot, file, persistFullDiffText) ? file.DiffText : null,
                }).ToList(),
            };
        }

        private static bool ShouldPersistFullDiffText(GitThreadSnapshot snapshot)
        {
            if (snapshot is null)
            {
                return false;
            }

            int diffFileCount = 0;
            int totalDiffChars = 0;
            foreach (GitChangedFile file in snapshot.ChangedFiles)
            {
                if (string.IsNullOrWhiteSpace(file?.DiffText))
                {
                    continue;
                }

                diffFileCount++;
                totalDiffChars += file.DiffText.Length;
                if (diffFileCount > MaxPersistedSnapshotDiffFiles || totalDiffChars > MaxPersistedSnapshotDiffChars)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool ShouldPersistDiffText(GitThreadSnapshot snapshot, GitChangedFile file, bool persistFullDiffText)
        {
            if (persistFullDiffText)
            {
                return true;
            }

            return !string.IsNullOrWhiteSpace(snapshot?.SelectedPath) &&
                string.Equals(file?.Path, snapshot.SelectedPath, StringComparison.Ordinal);
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

            return "terminal";
        }

        private static string FormatInspectorSection(InspectorSection section)
        {
            return section switch
            {
                InspectorSection.Files => "files",
                InspectorSection.Notes => "notes",
                _ => "review",
            };
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
                DisposeAllWorkspacePanes();
                _projects.Clear();
                _tabItemsById.Clear();
                _paneContainersById.Clear();

                SampleConfig.CurrentTheme = WorkspaceSessionStore.ParseTheme(snapshot.Theme);
                SampleConfig.DefaultShellProfileId = ShellProfiles.Resolve(snapshot.DefaultShellProfileId).Id;
                SampleConfig.MaxPaneCountPerThread = Math.Clamp(snapshot.MaxPaneCountPerThread, 2, 4);
                _threadSequence = Math.Max(1, snapshot.ThreadSequence);
                int skippedMissingProjectCount = 0;

                foreach (ProjectSessionSnapshot projectSnapshot in snapshot.Projects)
                {
                    if (!ShouldPersistProjectPath(projectSnapshot.RootPath))
                    {
                        continue;
                    }

                    if (!TryResolveRestorableProjectPath(projectSnapshot.RootPath, out string restorableProjectPath, out string unavailableProjectPath))
                    {
                        skippedMissingProjectCount++;
                        LogAutomationEvent("shell", "workspace.restore_project_skipped", "Skipped restoring a project because its root path is unavailable on this machine.", new Dictionary<string, string>
                        {
                            ["projectId"] = projectSnapshot.Id ?? string.Empty,
                            ["projectName"] = projectSnapshot.Name ?? string.Empty,
                            ["projectPath"] = projectSnapshot.RootPath ?? string.Empty,
                            ["unavailablePath"] = unavailableProjectPath ?? string.Empty,
                        });
                        continue;
                    }

                    WorkspaceProject project = new(restorableProjectPath, projectSnapshot.ShellProfileId, projectSnapshot.Name, projectSnapshot.Id);
                    _projects.Add(project);

                    foreach (ThreadSessionSnapshot threadSnapshot in projectSnapshot.Threads ?? new List<ThreadSessionSnapshot>())
                    {
                        WorkspaceThread thread = new(project, string.IsNullOrWhiteSpace(threadSnapshot.Name) ? $"Thread {_threadSequence++}" : threadSnapshot.Name, threadSnapshot.Id)
                        {
                            WorktreePath = ResolveRequestedPath(string.IsNullOrWhiteSpace(threadSnapshot.WorktreePath) ? project.RootPath : threadSnapshot.WorktreePath),
                            BranchName = threadSnapshot.BranchName,
                            ChangedFileCount = 0,
                            SelectedNoteId = threadSnapshot.SelectedNoteId,
                            SelectedDiffPath = threadSnapshot.SelectedDiffPath,
                            DiffReviewSource = ParseDiffReviewSource(threadSnapshot.DiffReviewSource),
                            SelectedCheckpointId = threadSnapshot.SelectedCheckpointId,
                            BaselineSnapshot = RestoreGitThreadSnapshot(threadSnapshot.BaselineSnapshot),
                            LiveSnapshot = RestoreGitThreadSnapshot(threadSnapshot.LiveSnapshot),
                            LiveSnapshotCapturedAt = DateTimeOffset.TryParse(threadSnapshot.LiveSnapshotCapturedAt, out DateTimeOffset liveSnapshotCapturedAt)
                                ? liveSnapshotCapturedAt
                                : default,
                            LayoutPreset = WorkspaceSessionStore.ParseLayout(threadSnapshot.Layout),
                            PrimarySplitRatio = ClampPaneSplitRatio(threadSnapshot.PrimarySplitRatio <= 0 ? 0.58 : threadSnapshot.PrimarySplitRatio),
                            SecondarySplitRatio = ClampPaneSplitRatio(threadSnapshot.SecondarySplitRatio <= 0 ? 0.5 : threadSnapshot.SecondarySplitRatio),
                            AutoFitPaneContentLocked = threadSnapshot.AutoFitPaneContentLocked,
                        };
                        if (thread.LiveSnapshot is not null)
                        {
                            thread.BranchName = string.IsNullOrWhiteSpace(thread.LiveSnapshot.BranchName)
                                ? thread.BranchName
                                : thread.LiveSnapshot.BranchName;
                            thread.ChangedFileCount = thread.LiveSnapshot.ChangedFiles.Count;
                            thread.SelectedDiffPath = string.IsNullOrWhiteSpace(thread.LiveSnapshot.SelectedPath)
                                ? thread.SelectedDiffPath
                                : thread.LiveSnapshot.SelectedPath;
                        }
                        foreach (ThreadNoteSessionSnapshot noteSnapshot in threadSnapshot.NoteEntries ?? new List<ThreadNoteSessionSnapshot>())
                        {
                            WorkspaceThreadNote note = new(noteSnapshot.Title, noteSnapshot.Text, noteSnapshot.Id)
                            {
                                PaneId = noteSnapshot.PaneId,
                                CreatedAt = DateTimeOffset.TryParse(noteSnapshot.CreatedAt, out DateTimeOffset createdAt)
                                    ? createdAt
                                    : DateTimeOffset.UtcNow,
                                UpdatedAt = DateTimeOffset.TryParse(noteSnapshot.UpdatedAt, out DateTimeOffset updatedAt)
                                    ? updatedAt
                                    : DateTimeOffset.UtcNow,
                                ArchivedAt = DateTimeOffset.TryParse(noteSnapshot.ArchivedAt, out DateTimeOffset archivedAt)
                                    ? archivedAt
                                    : null,
                            };
                            thread.NoteEntries.Add(note);
                        }

                        if (thread.NoteEntries.Count == 0 && !string.IsNullOrWhiteSpace(threadSnapshot.Notes))
                        {
                            thread.Notes = threadSnapshot.Notes;
                        }

                        if (thread.NoteEntries.Count > 0 &&
                            !thread.NoteEntries.Any(candidate => string.Equals(candidate.Id, thread.SelectedNoteId, StringComparison.Ordinal)))
                        {
                            thread.SelectedNoteId = ResolvePreferredThreadNote(thread)?.Id;
                        }

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
                                WorkspacePaneRecord pane = RestorePaneFromSnapshot(project, thread, paneSnapshot, materialize: false);
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

                if (_projects.Count == 0)
                {
                    if (skippedMissingProjectCount > 0)
                    {
                        LogAutomationEvent("shell", "workspace.restore_empty", "Skipped all saved projects because their roots were unavailable on this machine.", new Dictionary<string, string>
                        {
                            ["skippedProjectCount"] = skippedMissingProjectCount.ToString(),
                            ["sessionPath"] = WorkspaceSessionStore.GetSessionPath(),
                        });
                    }

                    return false;
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
                    EnsureThreadPanesMaterialized(_activeProject, _activeThread);
                    _activeProject.SelectedThreadId = _activeThread.Id;
                    _activeGitSnapshot = _activeThread.LiveSnapshot;
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
                DisposeAllWorkspacePanes();
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
            QueueProjectTreeRefresh(immediate: true);
            LogAutomationEvent("shell", "project.created", $"Created project {project.Name}", new Dictionary<string, string>
            {
                ["projectId"] = project.Id,
                ["projectPath"] = project.RootPath,
                ["shellProfileId"] = project.ShellProfileId,
            });
            QueueSessionSave();
            return project;
        }

        private WorkspaceThread CreateThread(WorkspaceProject project, string threadName = null, bool ensureInitialPane = true, WorkspaceThread inheritFromThread = null, bool deferUiWork = false)
        {
            project ??= _activeProject ?? GetOrCreateProject(Environment.CurrentDirectory);

            WorkspaceThread thread = new(project, string.IsNullOrWhiteSpace(threadName) ? $"Thread {_threadSequence++}" : threadName.Trim());
            WorkspaceThread inheritedSourceThread = ResolveInheritedSourceThread(project, inheritFromThread);
            if (TryResolveInheritedWorktreePath(project, inheritedSourceThread, out string inheritedWorktreePath))
            {
                thread.WorktreePath = inheritedWorktreePath;
            }

            if (inheritedSourceThread is not null)
            {
                thread.BranchName = inheritedSourceThread.BranchName;
                thread.ChangedFileCount = inheritedSourceThread.ChangedFileCount;
                thread.SelectedDiffPath = inheritedSourceThread.SelectedDiffPath;
                thread.LiveSnapshot = ReferenceEquals(inheritedSourceThread, _activeThread) && _activeGitSnapshot is not null
                    ? GitStatusService.CloneSnapshot(_activeGitSnapshot)
                    : GitStatusService.CloneSnapshot(inheritedSourceThread.LiveSnapshot);
                thread.LiveSnapshotCapturedAt = inheritedSourceThread.LiveSnapshotCapturedAt;
            }

            project.Threads.Add(thread);
            project.SelectedThreadId = thread.Id;
            if (ensureInitialPane)
            {
                EnsureThreadHasTab(project, thread);
            }
            QueueProjectTreeRefresh(immediate: true);
            LogAutomationEvent("shell", "thread.created", $"Created thread {thread.Name}", new Dictionary<string, string>
            {
                ["threadId"] = thread.Id,
                ["projectId"] = project.Id,
                ["threadName"] = thread.Name,
            });
            if (!deferUiWork)
            {
                QueueProjectTreeRefresh(immediate: true);
                QueueSessionSave();
            }

            return thread;
        }

        private WorkspaceThread ResolveInheritedSourceThread(WorkspaceProject project, WorkspaceThread explicitSourceThread)
        {
            if (project is null)
            {
                return null;
            }

            if (explicitSourceThread is not null)
            {
                return explicitSourceThread;
            }

            if (ReferenceEquals(project, _activeProject) && _activeThread is not null)
            {
                return _activeThread;
            }

            if (!string.IsNullOrWhiteSpace(project.SelectedThreadId))
            {
                return project.Threads.FirstOrDefault(thread => string.Equals(thread.Id, project.SelectedThreadId, StringComparison.Ordinal));
            }

            return null;
        }

        private bool TryResolveInheritedWorktreePath(WorkspaceProject project, WorkspaceThread sourceThread, out string worktreePath)
        {
            worktreePath = null;
            if (project is null)
            {
                return false;
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
            EnsureThreadPanesMaterialized(project, thread);

            if (paneKind == WorkspacePaneKind.Diff)
            {
                return thread;
            }

            if (thread.Panes.Count < thread.PaneLimit)
            {
                return thread;
            }

            WorkspaceThread overflowThread = CreateThread(project, ensureInitialPane: false, inheritFromThread: thread, deferUiWork: true);
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

            TerminalPaneRecord pane = CreateTerminalPane(project, thread, WorkspacePaneKind.Terminal, startupInput: null, initialTitle: "Terminal");
            thread.Panes.Add(pane);
            thread.SelectedPaneId = pane.Id;
            project.SelectedThreadId = thread.Id;
            PromoteLayoutForPaneCount(thread);

            if (thread == _activeThread)
            {
                if (!TryAppendSelectedPaneToActiveTabStrip(pane))
                {
                    RefreshTabView();
                }
                SetInspectorSection(InspectorSection.Files);
                RequestLayoutForVisiblePanes();
            }
            else if (thread.Panes.Count == 1)
            {
                ActivateThread(thread);
            }

            QueueProjectTreeRefresh();
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
                if (!TryAppendSelectedPaneToActiveTabStrip(pane))
                {
                    RefreshTabView();
                }
            }
            else if (thread.Panes.Count == 1)
            {
                ActivateThread(thread);
            }

            QueueProjectTreeRefresh();
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

        private bool TryAppendSelectedPaneToActiveTabStrip(WorkspacePaneRecord pane)
        {
            if (pane is null ||
                _activeThread is null ||
                !string.Equals(_activeThread.Id, _lastTabStripThreadId, StringComparison.Ordinal))
            {
                return false;
            }

            TabViewItem item = GetOrCreateTabViewItem(pane);
            if (FindTabViewIndex(item) < 0)
            {
                TerminalTabs.TabItems.Add(item);
            }

            int selectionGeneration = ++_tabSelectionChangeGeneration;
            bool previousSuppression = _suppressTabSelectionChanged;
            bool previousRefreshingTabView = _refreshingTabView;
            _refreshingTabView = true;
            _suppressTabSelectionChanged = true;
            try
            {
                if (!ReferenceEquals(TerminalTabs.SelectedItem, item))
                {
                    TerminalTabs.SelectedItem = item;
                }
            }
            finally
            {
                RestoreTabSelectionFlagsAsync(selectionGeneration, previousSuppression, previousRefreshingTabView);
            }

            RenderPaneWorkspace();
            UpdateWorkspaceVisibility();
            return true;
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
            bool wasActiveThread = ReferenceEquals(thread, _activeThread);
            bool wasSelected = pane is not null && string.Equals(thread.SelectedPaneId, pane.Id, StringComparison.Ordinal);
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

            project.SelectedThreadId = thread.Id;

            if (wasActiveThread)
            {
                if (created)
                {
                    thread.SelectedPaneId = pane.Id;
                    RefreshTabView();
                    SyncInspectorSectionWithSelectedPane();
                    RefreshInspectorFileBrowser();
                    RequestLayoutForVisiblePanes();
                }
                else
                {
                    UpdateTabViewItem(pane);
                    if (!wasSelected)
                    {
                        SelectPane(pane, focusPane: false);
                    }
                    else
                    {
                        thread.SelectedPaneId = pane.Id;
                    }
                }
            }
            else
            {
                thread.SelectedPaneId = pane.Id;
                ActivateThread(thread);
            }

            if (created || !wasActiveThread)
            {
                QueueProjectTreeRefresh();
            }
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
                if (!TryAppendSelectedPaneToActiveTabStrip(pane))
                {
                    RefreshTabView();
                }
                RequestLayoutForVisiblePanes();
            }
            else if (thread.Panes.Count == 1)
            {
                ActivateThread(thread);
            }

            QueueProjectTreeRefresh();
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
            browser.PreloadEnvironment();

            BrowserPaneRecord pane = new(initialTitle, browser, paneId);
            string lastPersistedBrowserState = BuildBrowserPersistenceKey(browser);
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
            };
            browser.StateChanged += (_, _) =>
            {
                string currentState = BuildBrowserPersistenceKey(browser);
                if (string.Equals(lastPersistedBrowserState, currentState, StringComparison.Ordinal))
                {
                    return;
                }

                lastPersistedBrowserState = currentState;
                QueueSessionSave();
            };
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
            string lastSelectedFilePath = editor.SelectedFilePath;
            int lastFileCount = editor.FileCount;
            bool lastCanSave = editor.CanSave;
            string lastStatusText = editor.StatusText;
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
            };
            editor.StateChanged += (_, _) =>
            {
                bool selectedFileChanged = !string.Equals(lastSelectedFilePath, editor.SelectedFilePath, StringComparison.OrdinalIgnoreCase);
                bool fileCountChanged = lastFileCount != editor.FileCount;
                bool canSaveChanged = lastCanSave != editor.CanSave;
                bool statusChanged = !string.Equals(lastStatusText, editor.StatusText, StringComparison.Ordinal);
                lastSelectedFilePath = editor.SelectedFilePath;
                lastFileCount = editor.FileCount;
                lastCanSave = editor.CanSave;
                lastStatusText = editor.StatusText;

                if (thread == _activeThread)
                {
                    if (selectedFileChanged || fileCountChanged)
                    {
                        RefreshInspectorFileBrowser();
                    }
                    else if (canSaveChanged || statusChanged)
                    {
                        RefreshInspectorFileBrowserStatus();
                    }
                }

                if (selectedFileChanged || fileCountChanged)
                {
                    QueueSessionSave();
                }
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
            DiffPaneDisplayMode resolvedMode = displayMode ?? pane.DiffPane.DisplayMode;
            GitChangedFile selectedFile = sourceSnapshot?.ChangedFiles
                .FirstOrDefault(file => string.Equals(file.Path, diffPath, StringComparison.Ordinal));
            string selectedDiffText = !string.IsNullOrWhiteSpace(selectedFile?.DiffText)
                ? selectedFile.DiffText
                : string.Equals(sourceSnapshot?.SelectedPath, diffPath, StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(sourceSnapshot?.SelectedDiff)
                    ? sourceSnapshot.SelectedDiff
                    : diffText;

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
            else if (resolvedMode == DiffPaneDisplayMode.FullPatchReview &&
                sourceSnapshot?.ChangedFiles.Any(file => file?.DiffText is null) == true)
            {
                pane.DiffPane.ShowLoading(null, "Loading full patch review…");
            }
            else if (selectedFile is not null && selectedFile.DiffText is null)
            {
                pane.DiffPane.ShowLoading(diffPath, "Loading patch…");
            }
            else if (!string.IsNullOrWhiteSpace(selectedDiffText))
            {
                if (selectedFile is not null)
                {
                    pane.DiffPane.ShowFileCompare(selectedFile, string.IsNullOrWhiteSpace(diffPath) ? "Patch review" : "Patch view");
                }
                else
                {
                    pane.DiffPane.ShowUnifiedDiff(diffPath, selectedDiffText);
                }
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
            };
            terminal.ReplayStateChanged += (_, _) =>
            {
                pane.ReplayTool = terminal.ReplayTool;
                pane.ReplaySessionId = terminal.ReplaySessionId;
                pane.ReplayCommand = terminal.ReplayCommand;
                pane.ReplayArguments = terminal.ReplayArguments;
                QueueProjectTreeRefresh();
                QueueSessionSave();
            };
            terminal.ToolSessionStateChanged += (_, _) => QueueProjectTreeRefresh();
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
                QueueProjectTreeRefresh();
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

                QueueProjectTreeRefresh();
                QueueSessionSave();
            };

            return pane;
        }

        private static IReadOnlyDictionary<string, string> BuildTerminalLaunchEnvironment(WorkspaceProject project, WorkspaceThread thread, string threadRootPath)
        {
            string repoRoot = Environment.CurrentDirectory;
            Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase)
            {
                ["WINMUX_REPO_ROOT"] = repoRoot,
                ["WINMUX_PROJECT_ROOT"] = project?.RootPath ?? string.Empty,
                ["WINMUX_THREAD_ROOT"] = threadRootPath ?? string.Empty,
                ["WINMUX_PROJECT_ID"] = project?.Id ?? string.Empty,
                ["WINMUX_THREAD_ID"] = thread?.Id ?? string.Empty,
                ["WINMUX_BROWSER_BRIDGE"] = Path.Combine(repoRoot, "tools", "winmux_browser_bridge.py"),
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

        private void EnsureThreadPanesMaterialized(WorkspaceProject project, WorkspaceThread thread)
        {
            if (project is null || thread is null || thread.Panes.Count == 0 || thread.Panes.All(pane => !pane.IsDeferred))
            {
                return;
            }

            string selectedPaneId = thread.SelectedPaneId;
            if (string.IsNullOrWhiteSpace(selectedPaneId))
            {
                selectedPaneId = thread.Panes.FirstOrDefault()?.Id;
            }

            HashSet<string> paneIdsToMaterialize = new(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(selectedPaneId))
            {
                paneIdsToMaterialize.Add(selectedPaneId);
            }
            if (!string.IsNullOrWhiteSpace(thread.ZoomedPaneId))
            {
                paneIdsToMaterialize.Add(thread.ZoomedPaneId);
            }

            for (int index = 0; index < thread.Panes.Count; index++)
            {
                if (thread.Panes[index] is not DeferredPaneRecord deferredPane ||
                    !paneIdsToMaterialize.Contains(deferredPane.Id))
                {
                    continue;
                }

                WorkspacePaneRecord materializedPane = RestorePaneFromSnapshot(project, thread, deferredPane.Snapshot, materialize: true);
                if (materializedPane is null)
                {
                    continue;
                }

                thread.Panes[index] = materializedPane;
            }

            if (thread.Panes.Count == 0)
            {
                EnsureThreadHasTab(project, thread);
            }

            if (thread.Panes.Any(candidate => string.Equals(candidate.Id, selectedPaneId, StringComparison.Ordinal)))
            {
                thread.SelectedPaneId = selectedPaneId;
            }
            else
            {
                thread.SelectedPaneId = thread.Panes.FirstOrDefault()?.Id;
            }
        }

        private void QueueVisibleDeferredPaneMaterialization(WorkspaceProject project, WorkspaceThread thread)
        {
            if (!EnableVisibleDeferredPaneMaterialization)
            {
                return;
            }

            int requestId = ++_visibleDeferredPaneMaterializationRequestId;
            if (project is null ||
                thread is null ||
                _showingSettings ||
                !ReferenceEquals(project, _activeProject) ||
                !ReferenceEquals(thread, _activeThread))
            {
                return;
            }

            List<string> deferredPaneIds = GetVisiblePanes(thread)
                .Where(candidate => candidate.IsDeferred &&
                    !string.Equals(candidate.Id, thread.SelectedPaneId, StringComparison.Ordinal))
                .Select(candidate => candidate.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToList();
            if (deferredPaneIds.Count == 0)
            {
                return;
            }

            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await System.Threading.Tasks.Task.Delay(60).ConfigureAwait(false);
                    foreach (string paneId in deferredPaneIds)
                    {
                        await EnqueueOnUiThreadAsync(() =>
                        {
                            if (requestId != _visibleDeferredPaneMaterializationRequestId ||
                                _showingSettings ||
                                !ReferenceEquals(project, _activeProject) ||
                                !ReferenceEquals(thread, _activeThread))
                            {
                                return;
                            }

                            if (!MaterializeDeferredPane(project, thread, paneId))
                            {
                                return;
                            }
                        }).ConfigureAwait(false);

                        if (requestId != _visibleDeferredPaneMaterializationRequestId)
                        {
                            return;
                        }

                        await System.Threading.Tasks.Task.Delay(35).ConfigureAwait(false);
                    }
                }
                catch
                {
                }
            });
        }

        private bool MaterializeDeferredPane(WorkspaceProject project, WorkspaceThread thread, string paneId)
        {
            if (project is null || thread is null || string.IsNullOrWhiteSpace(paneId))
            {
                return false;
            }

            int paneIndex = thread.Panes.FindIndex(candidate => string.Equals(candidate.Id, paneId, StringComparison.Ordinal));
            if (paneIndex < 0 || thread.Panes[paneIndex] is not DeferredPaneRecord deferredPane)
            {
                return false;
            }

            WorkspacePaneRecord materializedPane = RestorePaneFromSnapshot(project, thread, deferredPane.Snapshot, materialize: true);
            if (materializedPane is null)
            {
                return false;
            }

            thread.Panes[paneIndex] = materializedPane;
            UpdateTabViewItem(materializedPane);
            if (!ReferenceEquals(thread, _activeThread))
            {
                return true;
            }

            if (materializedPane.Kind == WorkspacePaneKind.Diff)
            {
                ApplyGitSnapshotToUi();
                QueueVisibleDiffHydrationIfNeeded(thread, project, _activeGitSnapshot ?? thread.LiveSnapshot);
            }

            _lastPaneWorkspaceRenderKey = null;
            RenderPaneWorkspace();
            RequestLayoutForVisiblePanes();
            return true;
        }

        private void EnsureThreadAllPanesMaterialized(WorkspaceProject project, WorkspaceThread thread)
        {
            if (project is null || thread is null || thread.Panes.Count == 0 || thread.Panes.All(pane => !pane.IsDeferred))
            {
                return;
            }

            string selectedPaneId = thread.SelectedPaneId;
            for (int index = 0; index < thread.Panes.Count; index++)
            {
                if (thread.Panes[index] is not DeferredPaneRecord deferredPane)
                {
                    continue;
                }

                WorkspacePaneRecord materializedPane = RestorePaneFromSnapshot(project, thread, deferredPane.Snapshot, materialize: true);
                if (materializedPane is null)
                {
                    continue;
                }

                thread.Panes[index] = materializedPane;
            }

            if (thread.Panes.Count == 0)
            {
                EnsureThreadHasTab(project, thread);
            }

            if (thread.Panes.Any(candidate => string.Equals(candidate.Id, selectedPaneId, StringComparison.Ordinal)))
            {
                thread.SelectedPaneId = selectedPaneId;
            }
            else
            {
                thread.SelectedPaneId = thread.Panes.FirstOrDefault()?.Id;
            }
        }

        private WorkspacePaneRecord RestorePaneFromSnapshot(WorkspaceProject project, WorkspaceThread thread, PaneSessionSnapshot snapshot, bool materialize = true)
        {
            if (snapshot is null)
            {
                return null;
            }

            if (!materialize)
            {
                return new DeferredPaneRecord(snapshot);
            }

            bool hasReplayMetadata = HasReplayRestoreMetadata(snapshot);
            bool replayRestoreRejected = false;
            string restoreReplayCommand = null;
            if (!snapshot.IsExited && hasReplayMetadata)
            {
                replayRestoreRejected = !TryResolveRestoreReplayCommand(snapshot, out restoreReplayCommand);
            }

            bool autoStartSession = !snapshot.IsExited && !replayRestoreRejected;
            string suspendedStatusText = snapshot.IsExited && snapshot.ReplayRestoreFailed
                ? "Replay restore failed last time. Close the tab or reopen the saved session manually."
                : replayRestoreRejected
                    ? "Saved replay metadata could not be restored automatically. Resume the saved session manually."
                    : null;

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
                    initialTitle: string.IsNullOrWhiteSpace(snapshot.Title) ? "Terminal" : snapshot.Title,
                    paneId: snapshot.Id,
                    restoreReplayCommand: restoreReplayCommand,
                    autoStartSession: autoStartSession,
                    suspendedStatusText: suspendedStatusText),
            };

            pane.HasCustomTitle = snapshot.HasCustomTitle;
            if (snapshot.HasCustomTitle && !string.IsNullOrWhiteSpace(snapshot.Title))
            {
                pane.Title = snapshot.Title;
            }
            pane.ReplayTool = snapshot.ReplayTool;
            pane.ReplaySessionId = snapshot.ReplaySessionId;
            pane.ReplayCommand = restoreReplayCommand;
            pane.ReplayArguments = snapshot.ReplayArguments;
            pane.RestoredFromSession = true;
            pane.ReplayRestoreFailed = snapshot.ReplayRestoreFailed || replayRestoreRejected;
            pane.PersistExitedState = snapshot.IsExited && snapshot.ReplayRestoreFailed || replayRestoreRejected;
            if (snapshot.IsExited && pane is TerminalPaneRecord exitedPane)
            {
                exitedPane.MarkExited();
            }
            else if (!string.IsNullOrWhiteSpace(restoreReplayCommand) && pane is TerminalPaneRecord replayPane)
            {
                replayPane.MarkReplayRestorePending();
            }
            else if (replayRestoreRejected && pane is TerminalPaneRecord rejectedReplayPane)
            {
                rejectedReplayPane.MarkExited();
                rejectedReplayPane.MarkReplayRestoreFailed();
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

        private static bool HasReplayRestoreMetadata(PaneSessionSnapshot snapshot)
        {
            return !string.IsNullOrWhiteSpace(snapshot?.ReplayTool) ||
                !string.IsNullOrWhiteSpace(snapshot?.ReplaySessionId) ||
                !string.IsNullOrWhiteSpace(snapshot?.ReplayCommand);
        }

        private static bool TryResolveRestoreReplayCommand(PaneSessionSnapshot snapshot, out string restoreReplayCommand)
        {
            restoreReplayCommand = null;
            if (snapshot is null)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(snapshot.ReplayArguments) &&
                TerminalControl.TryBuildReplayRestoreCommand(snapshot.ReplayTool, snapshot.ReplaySessionId, snapshot.ReplayArguments, out restoreReplayCommand))
            {
                return true;
            }

            if (!TerminalControl.TryExtractReplayCommandMetadata(snapshot.ReplayCommand, out string replayTool, out string replaySessionId, out string replayArguments))
            {
                return TerminalControl.TryBuildReplayRestoreCommand(snapshot.ReplayTool, snapshot.ReplaySessionId, snapshot.ReplayArguments, out restoreReplayCommand);
            }

            snapshot.ReplayTool = replayTool;
            snapshot.ReplaySessionId = replaySessionId;
            snapshot.ReplayArguments = replayArguments;
            return TerminalControl.TryBuildReplayRestoreCommand(replayTool, replaySessionId, replayArguments, out restoreReplayCommand);
        }

        private void EnsureThreadHasSelectedDiffPane(WorkspaceProject project, WorkspaceThread thread)
        {
            if (thread is null || string.IsNullOrWhiteSpace(thread.SelectedDiffPath))
            {
                return;
            }

            if (thread.Panes.Any(candidate => candidate.Kind == WorkspacePaneKind.Diff))
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
            QueueProjectTreeRefresh();
        }

        private void ClearPaneAttention(WorkspacePaneRecord pane)
        {
            if (pane is null || !pane.RequiresAttention)
            {
                return;
            }

            pane.RequiresAttention = false;
            QueueProjectTreeRefresh();
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
                    ClearPaneNoteAttachments(thread, pane.Id);

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
                        QueueSelectedPaneFocus();
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

            if (SettingsFrame?.Content is SettingsPage settingsPage && _showingSettings)
            {
                settingsPage.RefreshFromCurrentState(refreshCredentialVault: true);
            }
            else
            {
                _settingsPageNeedsRefresh = true;
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
            if (project is null)
            {
                throw new ArgumentNullException(nameof(project));
            }

            string previousProjectId = _activeProject?.Id;
            string previousThreadId = _activeThread?.Id;

            if (ReferenceEquals(_activeProject, project) &&
                (_activeThread is null || string.Equals(project.SelectedThreadId, _activeThread.Id, StringComparison.Ordinal)))
            {
                UpdateProjectTreeSelectionVisuals();
                return;
            }

            _activeProject = project;

            WorkspaceThread thread = project.Threads.FirstOrDefault(candidate => candidate.Id == project.SelectedThreadId)
                ?? project.Threads.FirstOrDefault();

            if (thread is null)
            {
                _activeThread = null;
                UpdateProjectTreeSelectionVisuals(previousProjectId, previousThreadId);
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

        private void ShowTerminalShellIfNeeded(bool queueGitRefresh = true)
        {
            if (_showingSettings)
            {
                ShowTerminalShell(queueGitRefresh);
            }
        }

        private void ActivateThread(WorkspaceThread thread)
        {
            if (thread is null)
            {
                throw new ArgumentNullException(nameof(thread));
            }

            string previousProjectId = _activeProject?.Id;
            string previousThreadId = _activeThread?.Id;

            if (ReferenceEquals(_activeThread, thread) && ReferenceEquals(_activeProject, FindProjectForThread(thread)))
            {
                UpdateProjectTreeSelectionVisuals();
                return;
            }

            WorkspaceThread previousThread = _activeThread;
            WorkspaceProject previousProject = previousThread?.Project;
            bool projectChanged = !ReferenceEquals(previousProject, thread.Project);
            bool deferWorkspaceRefresh = _showingSettings;
            InspectorSection previousInspectorSection = _activeInspectorSection;
            _activeThread = thread;
            NormalizeDiffReviewSource(_activeThread);
            _activeProject = FindProjectForThread(thread);
            _activeProject.SelectedThreadId = thread.Id;
            EnsureThreadPanesMaterialized(_activeProject, thread);
            EnsureThreadHasTab(_activeProject, thread);
            UpdateProjectTreeSelectionVisuals(previousProjectId, previousThreadId);
            string previousRootPath = ResolveThreadRootPath(previousProject, previousThread);
            string nextRootPath = ResolveThreadRootPath(_activeProject, _activeThread);
            bool rootChanged = !string.Equals(previousRootPath, nextRootPath, StringComparison.OrdinalIgnoreCase);
            if (!ReferenceEquals(previousThread, thread))
            {
                _activeGitSnapshot = thread.LiveSnapshot;
                if (!deferWorkspaceRefresh)
                {
                    ApplyGitSnapshotToUi();
                }
            }
            if (projectChanged)
            {
                QueueProjectTreeRefresh();
            }
            if (!deferWorkspaceRefresh)
            {
                RefreshTabView();
                UpdateHeader();
                if (_inspectorOpen)
                {
                    RefreshDiffReviewSourceControls();
                    RefreshInspectorNotes();
                    SyncInspectorSectionWithSelectedPane();
                    bool inspectorChangedToFiles = _activeInspectorSection == InspectorSection.Files &&
                        previousInspectorSection != _activeInspectorSection;
                    if (!inspectorChangedToFiles)
                    {
                        RefreshInspectorFileBrowser(forceRebuild: rootChanged && _activeInspectorSection == InspectorSection.Files);
                    }
                }
            }
            ClearPaneAttention(GetSelectedPane(_activeThread));
            if (!deferWorkspaceRefresh)
            {
                RequestLayoutForVisiblePanes();
                QueueSelectedPaneFocus();
                QueueVisibleDeferredPaneMaterialization(_activeProject, _activeThread);
            }
            QueueActiveThreadGitRefresh(
                thread.SelectedDiffPath,
                preserveSelection: true,
                includeSelectedDiff: false,
                preferFastRefresh: true);
            if (EnableBackgroundPaneWarmup)
            {
                QueueProjectPaneWarmup(_activeProject, _activeThread);
            }
            QueueSessionSave();
            LogAutomationEvent("shell", "thread.selected", $"Selected thread {thread.Name}", new Dictionary<string, string>
            {
                ["threadId"] = thread.Id,
                ["projectId"] = _activeProject.Id,
                ["threadName"] = thread.Name,
            });
        }

        private void QueueProjectPaneWarmup(WorkspaceProject project, WorkspaceThread activeThread)
        {
            if (!EnableBackgroundPaneWarmup || project is null)
            {
                return;
            }

            int requestId = ++_latestPaneWarmupRequestId;
            _ = WarmInactiveProjectPanesAsync(project, activeThread, requestId);
        }

        private async System.Threading.Tasks.Task WarmInactiveProjectPanesAsync(
            WorkspaceProject project,
            WorkspaceThread activeThread,
            int requestId)
        {
            await System.Threading.Tasks.Task.Delay(420).ConfigureAwait(true);
            WorkspaceThread threadToWarm = EnumerateWarmupThreads(project, activeThread).FirstOrDefault();
            if (threadToWarm is not null)
            {
                if (requestId != _latestPaneWarmupRequestId || !ReferenceEquals(project, _activeProject))
                {
                    return;
                }

                foreach (WorkspacePaneRecord pane in EnumerateWarmupPanes(threadToWarm, warmAllVisiblePanes: false))
                {
                    try
                    {
                        switch (pane)
                        {
                            case TerminalPaneRecord terminalPane:
                                await terminalPane.Terminal.WarmupAsync().ConfigureAwait(true);
                                break;
                            case BrowserPaneRecord browserPane:
                                await browserPane.Browser.EnsureInitializedAsync().ConfigureAwait(true);
                                break;
                            case EditorPaneRecord editorPane:
                                await editorPane.Editor.WarmupAsync().ConfigureAwait(true);
                                break;
                        }
                    }
                    catch
                    {
                    }
                }

                await WarmThreadGitSnapshotAsync(project, threadToWarm, requestId).ConfigureAwait(true);
            }
        }

        private async System.Threading.Tasks.Task WarmThreadGitSnapshotAsync(WorkspaceProject project, WorkspaceThread thread, int requestId)
        {
            if (thread is null || project is null || !RequiresThreadLiveSnapshotWarmup(thread))
            {
                return;
            }

            GitThreadSnapshot snapshot;
            try
            {
                string worktreePath = thread.WorktreePath ?? project.RootPath;
                string selectedPath = thread.SelectedDiffPath;
                snapshot = await System.Threading.Tasks.Task
                    .Run(() => GitStatusService.Capture(worktreePath, selectedPath))
                    .ConfigureAwait(true);
            }
            catch
            {
                return;
            }

            if (requestId != _latestPaneWarmupRequestId)
            {
                return;
            }

            SetThreadLiveSnapshot(thread, snapshot, DateTimeOffset.UtcNow);
        }

        private bool RequiresThreadLiveSnapshotWarmup(WorkspaceThread thread)
        {
            return thread?.LiveSnapshot is null ||
                DateTimeOffset.UtcNow - thread.LiveSnapshotCapturedAt > CachedThreadGitSnapshotMaxAge;
        }

        private IEnumerable<WorkspacePaneRecord> EnumerateWarmupPanes(WorkspaceThread thread, bool warmAllVisiblePanes)
        {
            if (thread is null)
            {
                yield break;
            }

            if (warmAllVisiblePanes)
            {
                foreach (WorkspacePaneRecord pane in GetVisiblePanes(thread))
                {
                    yield return pane;
                }

                yield break;
            }

            WorkspacePaneRecord selectedPane = GetSelectedPane(thread);
            if (selectedPane is not null)
            {
                yield return selectedPane;
            }
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

        private IEnumerable<WorkspaceThread> EnumerateWarmupThreads(WorkspaceProject project, WorkspaceThread activeThread)
        {
            if (project is null || activeThread is null || project.Threads.Count <= 1)
            {
                yield break;
            }

            foreach (WorkspaceThread thread in project.Threads
                         .Where(candidate => !ReferenceEquals(candidate, activeThread))
                         .OrderByDescending(candidate => string.Equals(candidate.Id, project.SelectedThreadId, StringComparison.Ordinal))
                         .ThenByDescending(candidate => candidate.Panes.Count)
                         .ThenByDescending(candidate => candidate.VisiblePaneCapacity)
                         .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
                         .Take(3))
            {
                yield return thread;
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
            EnsureThreadAllPanesMaterialized(project, source);
            WorkspaceThread duplicate = CreateThread(project, $"Copy of {source.Name}", inheritFromThread: source);

            duplicate.LayoutPreset = source.LayoutPreset;
            duplicate.NoteEntries.Clear();
            foreach (WorkspaceThreadNote note in source.NoteEntries)
            {
                WorkspaceThreadNote clonedNote = new(note.Title, note.Text)
                {
                    PaneId = null,
                    CreatedAt = note.CreatedAt,
                    UpdatedAt = note.UpdatedAt,
                    ArchivedAt = note.ArchivedAt,
                };
                duplicate.NoteEntries.Add(clonedNote);
            }
            duplicate.SelectedNoteId = ResolvePreferredThreadNote(duplicate)?.Id;
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
            foreach (WorkspaceThreadNote note in thread.NoteEntries)
            {
                note.PaneId = null;
            }

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
            using var perfScope = NativeAutomationDiagnostics.TrackOperation("render.project-tree");
            NativeAutomationDiagnostics.IncrementCounter("projectTree.refreshCount");
            string renderKey = BuildProjectTreeRenderKey();
            if (string.Equals(renderKey, _lastProjectTreeRenderKey, StringComparison.Ordinal))
            {
                return;
            }

            _lastProjectTreeRenderKey = renderKey;
            ProjectListPanel.Children.Clear();
            _projectButtonsById.Clear();
            _projectHeaderBordersById.Clear();
            _threadButtonsById.Clear();
            _threadActivitySummariesById.Clear();
            _hoveredProjectIds.Clear();
            _hoveredThreadIds.Clear();
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
                    Style = (Style)Application.Current.Resources["ShellSidebarProjectButtonStyle"],
                    Tag = project.Id,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                };
                AutomationProperties.SetAutomationId(projectButton, $"shell-project-{project.Id}");
                AutomationProperties.SetName(projectButton, project.Name);
                projectButton.Click += OnProjectButtonClicked;
                ToolTipService.SetToolTip(projectButton, FormatProjectPath(project));
                _projectButtonsById[project.Id] = projectButton;

                Grid projectLayout = new()
                {
                    ColumnSpacing = 5,
                };
                AutomationProperties.SetAutomationId(projectLayout, $"shell-project-layout-{project.Id}");
                projectLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                projectLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                FontIcon projectIcon = new()
                {
                    FontSize = 11.5,
                    Glyph = "\uE8B7",
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = AppBrush(projectLayout, ResolveProjectRailIconBrushKey(project)),
                };
                AutomationProperties.SetAutomationId(projectIcon, $"shell-project-icon-{project.Id}");
                projectLayout.Children.Add(projectIcon);

                if (isOpen)
                {
                    projectButton.Height = double.NaN;
                    projectButton.MinHeight = 28;
                    projectButton.Width = double.NaN;
                    projectButton.Padding = new Thickness(4, 4, 4, 4);
                    projectButton.HorizontalAlignment = HorizontalAlignment.Stretch;
                    projectButton.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                    StackPanel textStack = new()
                    {
                        Spacing = 0,
                    };
                    AutomationProperties.SetAutomationId(textStack, $"shell-project-text-{project.Id}");
                    Grid.SetColumn(textStack, 1);
                    TextBlock projectTitle = new()
                    {
                        Text = project.Name,
                        Style = (Style)Application.Current.Resources["ShellSidebarTitleTextStyle"],
                        TextWrapping = TextWrapping.NoWrap,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    };
                    AutomationProperties.SetAutomationId(projectTitle, $"shell-project-title-{project.Id}");
                    textStack.Children.Add(projectTitle);
                    TextBlock projectMeta = new()
                    {
                        Text = BuildProjectRailMeta(project),
                        Style = (Style)Application.Current.Resources["ShellSidebarMetaTextStyle"],
                        TextWrapping = TextWrapping.NoWrap,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    };
                    AutomationProperties.SetAutomationId(projectMeta, $"shell-project-meta-{project.Id}");
                    ToolTipService.SetToolTip(projectMeta, FormatProjectPath(project));
                    textStack.Children.Add(projectMeta);
                    projectLayout.Children.Add(textStack);
                    projectButton.Content = projectLayout;
                }
                else
                {
                    projectButton.MinHeight = 32;
                    projectButton.Height = 32;
                    projectButton.Width = 32;
                    projectButton.Padding = new Thickness(0);
                    projectButton.HorizontalAlignment = HorizontalAlignment.Left;
                    projectButton.HorizontalContentAlignment = HorizontalAlignment.Center;
                    projectButton.Margin = new Thickness(6, 0, 0, 0);
                    projectButton.PointerEntered += OnProjectHeaderPointerEntered;
                    projectButton.PointerExited += OnProjectHeaderPointerExited;
                    projectButton.Content = new Border
                    {
                        Width = 20,
                        Height = 20,
                        CornerRadius = new CornerRadius(4),
                        Background = AppBrush(projectButton, "ShellBrandMarkBackgroundBrush"),
                        BorderBrush = AppBrush(projectButton, "ShellBrandMarkBorderBrush"),
                        BorderThickness = new Thickness(1),
                        Child = new FontIcon
                        {
                            FontSize = 11.5,
                            Glyph = "\uE8B7",
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                            Foreground = AppBrush(projectButton, ResolveProjectRailIconBrushKey(project)),
                        },
                    };
                }
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
                    ApplyProjectRowState(project.Id, project == _activeProject && !_showingSettings);
                    group.Children.Add(projectButton);
                    ProjectListPanel.Children.Add(group);
                    continue;
                }

                Button addThreadButton = new()
                {
                    Style = (Style)Application.Current.Resources["ShellGhostToolbarButtonStyle"],
                    Tag = project.Id,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    Width = 18,
                    Height = 18,
                    Opacity = 0.62,
                };
                AutomationProperties.SetAutomationId(addThreadButton, $"shell-project-add-thread-{project.Id}");
                AutomationProperties.SetName(addThreadButton, $"Add thread to {project.Name}");
                addThreadButton.Click += OnProjectAddThreadClicked;
                addThreadButton.Foreground = AppBrush(addThreadButton, "ShellTextSecondaryBrush");
                ToolTipService.SetToolTip(addThreadButton, "Add thread");
                addThreadButton.Content = new FontIcon
                {
                    FontSize = 10.5,
                    Glyph = "\uE710",
                };

                Grid projectHeader = new()
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    ColumnDefinitions =
                    {
                        new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                        new ColumnDefinition { Width = GridLength.Auto },
                    },
                    ColumnSpacing = 4,
                };
                AutomationProperties.SetAutomationId(projectHeader, $"shell-project-header-{project.Id}");
                projectHeader.Children.Add(projectButton);
                Grid.SetColumn(projectButton, 0);
                Grid.SetColumn(addThreadButton, 1);
                projectHeader.Children.Add(addThreadButton);

                Border projectHeaderChrome = new()
                {
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00)),
                    CornerRadius = new CornerRadius(2),
                    Child = projectHeader,
                    Tag = project.Id,
                };
                AutomationProperties.SetAutomationId(projectHeaderChrome, $"shell-project-header-chrome-{project.Id}");
                projectHeaderChrome.PointerEntered += OnProjectHeaderPointerEntered;
                projectHeaderChrome.PointerExited += OnProjectHeaderPointerExited;
                _projectHeaderBordersById[project.Id] = projectHeaderChrome;
                ApplyProjectRowState(project.Id, project == _activeProject && !_showingSettings);
                group.Children.Add(projectHeaderChrome);

                if (showProjectThreads)
                {
                    StackPanel threadStack = new()
                    {
                        Spacing = 2,
                        Margin = new Thickness(6, 2, 0, 2),
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
                            MinHeight = 28,
                            Padding = new Thickness(3, 3, 3, 3),
                        };
                        AutomationProperties.SetAutomationId(threadButton, $"shell-thread-{thread.Id}");
                        AutomationProperties.SetName(threadButton, BuildThreadAutomationLabel(project, thread, activitySummary));
                        threadButton.Click += OnThreadButtonClicked;
                        threadButton.DoubleTapped += OnThreadButtonDoubleTapped;
                        threadButton.PointerEntered += OnThreadButtonPointerEntered;
                        threadButton.PointerExited += OnThreadButtonPointerExited;
                        ToolTipService.SetToolTip(threadButton, BuildThreadButtonToolTip(project, thread, BuildOverviewPaneSummary(thread)));
                        _threadButtonsById[thread.Id] = threadButton;
                        _threadActivitySummariesById[thread.Id] = activitySummary;

                        MenuFlyout threadMenu = new();
                        MenuFlyoutItem renameItem = new()
                        {
                            Text = "Rename",
                            Tag = thread.Id,
                        };
                        AutomationProperties.SetAutomationId(renameItem, $"shell-thread-rename-{thread.Id}");
                        renameItem.Click += OnRenameThreadMenuClicked;
                        MenuFlyoutItem editNoteItem = new()
                        {
                            Text = "Notes",
                            Tag = thread.Id,
                        };
                        AutomationProperties.SetAutomationId(editNoteItem, $"shell-thread-note-{thread.Id}");
                        editNoteItem.Click += OnEditThreadNotesMenuClicked;
                        MenuFlyoutItem newNoteItem = new()
                        {
                            Text = "New note",
                            Tag = thread.Id,
                        };
                        AutomationProperties.SetAutomationId(newNoteItem, $"shell-thread-note-new-{thread.Id}");
                        newNoteItem.Click += OnNewThreadNoteMenuClicked;
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
                        threadMenu.Items.Add(editNoteItem);
                        threadMenu.Items.Add(newNoteItem);
                        threadMenu.Items.Add(duplicateItem);
                        threadMenu.Items.Add(deleteItem);
                        threadButton.ContextFlyout = threadMenu;

                        Grid threadLayout = new()
                        {
                            ColumnSpacing = 5,
                        };
                        AutomationProperties.SetAutomationId(threadLayout, $"shell-thread-layout-{thread.Id}");
                        threadLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        threadLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                        FontIcon threadIcon = new()
                        {
                            FontSize = 10.8,
                            Glyph = "\uE8BD",
                            VerticalAlignment = VerticalAlignment.Center,
                            Foreground = AppBrush(threadLayout, ResolveThreadRailIconBrushKey(thread, activitySummary)),
                        };
                        AutomationProperties.SetAutomationId(threadIcon, $"shell-thread-icon-{thread.Id}");
                        threadLayout.Children.Add(threadIcon);

                        StackPanel threadText = new()
                        {
                            Spacing = 1,
                        };
                        AutomationProperties.SetAutomationId(threadText, $"shell-thread-text-{thread.Id}");
                        Grid.SetColumn(threadText, 1);
                        TextBlock threadTitle = new()
                        {
                            Text = thread.Name,
                            Style = (Style)Application.Current.Resources["ShellSidebarTitleTextStyle"],
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
                            ColumnSpacing = 4,
                        };
                        TextBlock threadMeta = new()
                        {
                            Text = BuildThreadRailMeta(project, thread),
                            Style = (Style)Application.Current.Resources["ShellSidebarMetaTextStyle"],
                            TextWrapping = TextWrapping.NoWrap,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            VerticalAlignment = VerticalAlignment.Center,
                        };
                        AutomationProperties.SetAutomationId(threadMeta, $"shell-thread-meta-{thread.Id}");
                        threadFooter.Children.Add(threadMeta);

                        bool showPaneStrip = thread.Panes.Count > 0;
                        if (showPaneStrip)
                        {
                            StackPanel threadAdornments = new()
                            {
                                Orientation = Orientation.Horizontal,
                                Spacing = 6,
                                VerticalAlignment = VerticalAlignment.Center,
                            };
                            AutomationProperties.SetAutomationId(threadAdornments, $"shell-thread-adornments-{thread.Id}");

                            FrameworkElement paneStrip = BuildThreadPaneStrip(thread);
                            AutomationProperties.SetAutomationId(paneStrip, $"shell-thread-panes-{thread.Id}");
                            threadAdornments.Children.Add(paneStrip);

                            Grid.SetColumn(threadAdornments, 1);
                            threadFooter.Children.Add(threadAdornments);
                        }

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
                        ApplySidebarThreadButtonState(
                            threadButton,
                            thread,
                            thread == _activeThread && !_showingSettings,
                            activitySummary,
                            _hoveredThreadIds.Contains(thread.Id));
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

        private void UpdateProjectTreeSelectionVisuals()
        {
            UpdateProjectTreeSelectionVisuals(previousProjectId: null, previousThreadId: null, forceAll: true);
        }

        private void UpdateProjectTreeSelectionVisuals(string previousProjectId, string previousThreadId, bool forceAll = false)
        {
            bool activeShellView = !_showingSettings;
            if (forceAll || (string.IsNullOrWhiteSpace(previousProjectId) && string.IsNullOrWhiteSpace(previousThreadId)))
            {
                foreach (string projectId in _projectButtonsById.Keys)
                {
                    ApplyProjectRowState(projectId, activeShellView && string.Equals(projectId, _activeProject?.Id, StringComparison.Ordinal));
                }

                foreach ((string threadId, Button threadButton) in _threadButtonsById)
                {
                    _threadActivitySummariesById.TryGetValue(threadId, out ThreadActivitySummary summary);
                    WorkspaceThread thread = FindThread(threadId);
                    ApplySidebarThreadButtonState(
                        threadButton,
                        thread,
                        activeShellView && string.Equals(threadId, _activeThread?.Id, StringComparison.Ordinal),
                        summary,
                        _hoveredThreadIds.Contains(threadId));
                }

                return;
            }

            UpdateProjectTreeSelectionVisual(previousProjectId, activeShellView);
            if (!string.Equals(previousProjectId, _activeProject?.Id, StringComparison.Ordinal))
            {
                UpdateProjectTreeSelectionVisual(_activeProject?.Id, activeShellView);
            }

            UpdateThreadTreeSelectionVisual(previousThreadId, activeShellView);
            if (!string.Equals(previousThreadId, _activeThread?.Id, StringComparison.Ordinal))
            {
                UpdateThreadTreeSelectionVisual(_activeThread?.Id, activeShellView);
            }
        }

        private void UpdateProjectTreeSelectionVisual(string projectId, bool activeShellView)
        {
            if (string.IsNullOrWhiteSpace(projectId) ||
                !_projectButtonsById.ContainsKey(projectId))
            {
                return;
            }

            ApplyProjectRowState(projectId, activeShellView && string.Equals(projectId, _activeProject?.Id, StringComparison.Ordinal));
        }

        private void UpdateThreadTreeSelectionVisual(string threadId, bool activeShellView)
        {
            if (string.IsNullOrWhiteSpace(threadId) ||
                !_threadButtonsById.TryGetValue(threadId, out Button threadButton))
            {
                return;
            }

            _threadActivitySummariesById.TryGetValue(threadId, out ThreadActivitySummary summary);
            WorkspaceThread thread = FindThread(threadId);
            ApplySidebarThreadButtonState(
                threadButton,
                thread,
                activeShellView && string.Equals(threadId, _activeThread?.Id, StringComparison.Ordinal),
                summary,
                _hoveredThreadIds.Contains(threadId));
        }

        private void OnProjectHeaderPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element || element.Tag is not string projectId)
            {
                return;
            }

            _hoveredProjectIds.Add(projectId);
            ApplyProjectRowState(projectId, !_showingSettings && string.Equals(projectId, _activeProject?.Id, StringComparison.Ordinal));
        }

        private void OnProjectHeaderPointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element || element.Tag is not string projectId)
            {
                return;
            }

            _hoveredProjectIds.Remove(projectId);
            ApplyProjectRowState(projectId, !_showingSettings && string.Equals(projectId, _activeProject?.Id, StringComparison.Ordinal));
        }

        private void OnThreadButtonPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element || element.Tag is not string threadId)
            {
                return;
            }

            _hoveredThreadIds.Add(threadId);
            UpdateThreadTreeSelectionVisual(threadId, !_showingSettings);
        }

        private void OnThreadButtonPointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element || element.Tag is not string threadId)
            {
                return;
            }

            _hoveredThreadIds.Remove(threadId);
            UpdateThreadTreeSelectionVisual(threadId, !_showingSettings);
        }

        private void ApplyProjectRowState(string projectId, bool active)
        {
            if (!_projectButtonsById.TryGetValue(projectId, out Button projectButton))
            {
                return;
            }

            bool hovered = _hoveredProjectIds.Contains(projectId);

            if (_projectHeaderBordersById.TryGetValue(projectId, out Border projectHeaderChrome))
            {
                projectHeaderChrome.Background = active
                    ? CreateSidebarTintedBrush(AppBrush(projectHeaderChrome, "ShellPaneActiveBorderBrush"), hovered ? (byte)0x18 : (byte)0x12, Windows.UI.Color.FromArgb(0xFF, 0x33, 0x41, 0x55))
                    : hovered
                        ? AppBrush(projectHeaderChrome, "ShellNavHoverBrush")
                        : new SolidColorBrush(Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00));
                projectHeaderChrome.BorderThickness = new Thickness(0);
                projectHeaderChrome.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00));
                projectButton.Background = null;
                projectButton.BorderBrush = null;
                projectButton.Foreground = AppBrush(projectButton, "ShellTextPrimaryBrush");
                return;
            }

            ApplyProjectButtonState(projectButton, active, hovered);
        }

        private void RefreshTabView()
        {
            using var perfScope = NativeAutomationDiagnostics.TrackOperation("render.tab-strip");
            NativeAutomationDiagnostics.IncrementCounter("tabStripRefresh.count");
            if (_activeThread is null)
            {
                bool previousSuppressSelection = _suppressTabSelectionChanged;
                _refreshingTabView = true;
                _suppressTabSelectionChanged = true;
                try
                {
                    _lastTabStripThreadId = null;
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

            List<TabViewItem> desiredItems = new(_activeThread.Panes.Count);
            HashSet<string> desiredPaneIds = new(StringComparer.Ordinal);
            foreach (WorkspacePaneRecord pane in _activeThread.Panes)
            {
                desiredPaneIds.Add(pane.Id);
                desiredItems.Add(PrepareTabViewItem(pane));
            }

            int selectionGeneration = ++_tabSelectionChangeGeneration;
            bool previousSuppression = _suppressTabSelectionChanged;
            bool previousRefreshingTabView = _refreshingTabView;
            _refreshingTabView = true;
            _suppressTabSelectionChanged = true;
            try
            {
                bool threadChanged = !string.Equals(_lastTabStripThreadId, _activeThread.Id, StringComparison.Ordinal);
                if (threadChanged)
                {
                    TerminalTabs.TabItems.Clear();
                    foreach (TabViewItem desiredItem in desiredItems)
                    {
                        TerminalTabs.TabItems.Add(desiredItem);
                    }
                }
                else
                {
                    for (int index = TerminalTabs.TabItems.Count - 1; index >= 0; index--)
                    {
                        if (TerminalTabs.TabItems[index] is not TabViewItem existingItem ||
                            existingItem.Tag is not WorkspacePaneRecord existingPane ||
                            desiredPaneIds.Contains(existingPane.Id))
                        {
                            continue;
                        }

                        TerminalTabs.TabItems.RemoveAt(index);
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

                _lastTabStripThreadId = _activeThread.Id;
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

            return item;
        }

        private TabViewItem PrepareTabViewItem(WorkspacePaneRecord pane)
        {
            TabViewItem item = GetOrCreateTabViewItem(pane);
            UpdateTabViewItem(item, pane);
            return item;
        }

        private void UpdateTabViewItem(WorkspacePaneRecord pane)
        {
            if (pane is null)
            {
                return;
            }

            UpdateTabViewItem(GetOrCreateTabViewItem(pane), pane);
        }

        private void UpdateTabViewItem(TabViewItem item, WorkspacePaneRecord pane)
        {
            if (item is null || pane is null)
            {
                return;
            }

            item.Tag = pane;
            AutomationProperties.SetName(item, FormatTabHeader(pane.Title, pane.Kind, pane.IsExited));
            item.ContextFlyout ??= BuildPaneContextMenu(pane);
            if (!TryUpdatePaneTabHeader(item, pane))
            {
                item.Header = BuildPaneTabHeader(pane);
            }

            UpdatePaneTabChrome(item, pane);
            item.IsClosable = true;
        }

        private bool TryUpdatePaneTabHeader(TabViewItem item, WorkspacePaneRecord pane)
        {
            if (item is null || pane is null)
            {
                return false;
            }

            if (string.Equals(_inlineRenamingPaneId, pane.Id, StringComparison.Ordinal))
            {
                if (item.Header is TextBox editor)
                {
                    editor.Text = pane.Title;
                    return true;
                }

                return false;
            }

            if (!TryGetPaneTabHeaderParts(item, out Grid header, out Border accentDot, out FontIcon kindIcon, out TextBlock titleText))
            {
                return false;
            }

            kindIcon.Glyph = ResolvePaneKindGlyph(pane.Kind);
            Brush accentBrush = AppBrush(header, ResolvePaneAccentBrushKey(pane.Kind));
            kindIcon.Foreground = accentBrush;
            accentDot.Background = CreateSidebarTintedBrush(accentBrush, 0xD4, Windows.UI.Color.FromArgb(0xFF, 0x33, 0x41, 0x55));
            titleText.Text = FormatTabHeader(pane.Title, pane.Kind, pane.IsExited);
            return true;
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
            menu.Items.Add(new MenuFlyoutSeparator());
            MenuFlyoutItem noteItem = new()
            {
                Text = "Note",
                Tag = pane.Id,
            };
            AutomationProperties.SetAutomationId(noteItem, $"shell-tab-pane-note-{pane.Id}");
            noteItem.Click += OnEditPaneThreadNotesMenuClicked;
            menu.Items.Add(noteItem);
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
                    MinWidth = 0,
                    Margin = new Thickness(6, 3, 6, 3),
                };
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                AutomationProperties.SetAutomationId(header, $"shell-tab-header-{pane.Id}");

                Border accentDot = new()
                {
                    Width = 6,
                    Height = 6,
                    CornerRadius = new CornerRadius(3),
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = CreateSidebarTintedBrush(AppBrush(header, ResolvePaneAccentBrushKey(pane.Kind)), 0xD4, Windows.UI.Color.FromArgb(0xFF, 0x33, 0x41, 0x55)),
                };
                AutomationProperties.SetAutomationId(accentDot, $"shell-tab-accent-{pane.Id}");
                header.Children.Add(accentDot);

                FontIcon kindIcon = new()
                {
                    Glyph = ResolvePaneKindGlyph(pane.Kind),
                    FontSize = 10.5,
                    Foreground = AppBrush(header, ResolvePaneAccentBrushKey(pane.Kind)),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                AutomationProperties.SetAutomationId(kindIcon, $"shell-tab-kind-{pane.Id}");
                Grid.SetColumn(kindIcon, 1);
                header.Children.Add(kindIcon);

                TextBlock titleText = new()
                {
                    Text = title,
                    FontSize = 10.9,
                    FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                    Foreground = AppBrush(header, "ShellTextSecondaryBrush"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                AutomationProperties.SetAutomationId(titleText, $"shell-tab-title-{pane.Id}");
                Grid.SetColumn(titleText, 2);
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

        private bool TryGetPaneTabHeaderParts(TabViewItem item, out Grid header, out Border accentDot, out FontIcon kindIcon, out TextBlock titleText)
        {
            header = null;
            accentDot = null;
            kindIcon = null;
            titleText = null;

            if (item?.Header is not Grid headerGrid || headerGrid.Children.Count < 3)
            {
                return false;
            }

            if (headerGrid.Children[0] is not Border accent ||
                headerGrid.Children[1] is not FontIcon icon ||
                headerGrid.Children[2] is not TextBlock title)
            {
                return false;
            }

            header = headerGrid;
            accentDot = accent;
            kindIcon = icon;
            titleText = title;
            return true;
        }

        private void UpdatePaneTabChrome(TabViewItem item, WorkspacePaneRecord pane)
        {
            if (item is null || pane is null || !TryGetPaneTabHeaderParts(item, out Grid header, out Border accentDot, out FontIcon kindIcon, out TextBlock titleText))
            {
                return;
            }

            bool selected = string.Equals(_activeThread?.SelectedPaneId, pane.Id, StringComparison.Ordinal);
            Brush accentBrush = AppBrush(header, ResolvePaneAccentBrushKey(pane.Kind));
            accentDot.Background = selected
                ? accentBrush
                : CreateSidebarTintedBrush(accentBrush, 0xD4, Windows.UI.Color.FromArgb(0xFF, 0x33, 0x41, 0x55));
            kindIcon.Foreground = accentBrush;
            kindIcon.Opacity = selected ? 1 : 0.84;
            titleText.Foreground = AppBrush(header, selected ? "ShellTextPrimaryBrush" : "ShellTextSecondaryBrush");
            titleText.FontWeight = selected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Medium;
        }

        private static string ResolvePaneKindGlyph(WorkspacePaneKind kind)
        {
            return kind switch
            {
                WorkspacePaneKind.Browser => "\uE774",
                WorkspacePaneKind.Editor => "\uE70F",
                WorkspacePaneKind.Diff => "\uE8A5",
                _ => "\uE756",
            };
        }

        private static string ResolvePaneAccentBrushKey(WorkspacePaneKind kind)
        {
            return kind switch
            {
                WorkspacePaneKind.Browser => "ShellInfoBrush",
                WorkspacePaneKind.Editor => "ShellSuccessBrush",
                WorkspacePaneKind.Diff => "ShellWarningBrush",
                _ => "ShellTerminalBrush",
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
            using var perfScope = NativeAutomationDiagnostics.TrackOperation("render.pane-workspace");
            NativeAutomationDiagnostics.IncrementCounter("paneWorkspaceRenderCount");
            string renderKey = BuildPaneWorkspaceRenderKey();
            if (string.Equals(renderKey, _lastPaneWorkspaceRenderKey, StringComparison.Ordinal))
            {
                UpdatePaneSelectionChrome();
                return;
            }

            _lastPaneWorkspaceRenderKey = renderKey;
            RemovePaneSplitters();
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
                    BorderThickness = new Thickness(1, 0, 1, 1),
                    CornerRadius = new CornerRadius(5),
                    Child = containerContent,
                    Margin = new Thickness(1, 0, 1, 1),
                    Tag = pane,
                };
                AutomationProperties.SetAutomationId(border, $"shell-pane-{pane.Id}");
                border.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPaneContainerPointerPressed), true);
                _paneContainersById[pane.Id] = border;
            }
            else
            {
                if (!ReferenceEquals(border.Tag, pane))
                {
                    border.Child = BuildPaneContainerContent(pane);
                }

                border.Tag = pane;
            }

            border.Background = AppBrush(border, "ShellSurfaceBackgroundBrush");
            border.Visibility = Visibility.Visible;
            Grid.SetRow(border, row);
            Grid.SetColumn(border, column);
            Grid.SetRowSpan(border, rowSpan);
            Grid.SetColumnSpan(border, columnSpan);
            if (!ReferenceEquals(border.Parent, PaneWorkspaceGrid))
            {
                PaneWorkspaceGrid.Children.Add(border);
            }
            UpdatePaneZoomButtonState(border, pane);

        }

        private void RemovePaneSplitters()
        {
            foreach (Border splitter in PaneWorkspaceGrid.Children
                         .OfType<Border>()
                         .Where(candidate => candidate.Tag is string direction &&
                             (string.Equals(direction, "vertical", StringComparison.Ordinal) ||
                              string.Equals(direction, "horizontal", StringComparison.Ordinal) ||
                              string.Equals(direction, "both", StringComparison.Ordinal)))
                         .ToList())
            {
                PaneWorkspaceGrid.Children.Remove(splitter);
            }
        }

        private Grid BuildPaneContainerContent(WorkspacePaneRecord pane)
        {
            Grid content = new();
            content.Children.Add(pane.View);

            Button zoomButton = new()
            {
                Width = 20,
                Height = 20,
                Padding = new Thickness(0),
                Margin = ResolvePaneZoomButtonMargin(pane),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Style = (Style)Application.Current.Resources["ShellGhostToolbarButtonStyle"],
                Tag = pane,
                Opacity = 0.84,
            };
            zoomButton.Click += OnPaneZoomButtonClicked;
            content.Children.Add(zoomButton);
            return content;
        }

        private static Thickness ResolvePaneZoomButtonMargin(WorkspacePaneRecord pane)
        {
            return new Thickness(0, 0, 1, 0);
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
            zoomButton.Margin = ResolvePaneZoomButtonMargin(pane);
            bool isZoomed = string.Equals(_activeThread?.ZoomedPaneId, pane.Id, StringComparison.Ordinal);
            bool isSelected = string.Equals(_activeThread?.SelectedPaneId, pane.Id, StringComparison.Ordinal);
            bool lightTheme = ResolveTheme(SampleConfig.CurrentTheme) == ElementTheme.Light;
            Brush selectionBrush = AppBrush(border, ResolvePaneAccentBrushKey(pane.Kind));
            zoomButton.Content = new FontIcon
            {
                FontSize = 9.25,
                Glyph = isZoomed ? "\uE73F" : "\uE740",
            };
            zoomButton.Opacity = isZoomed ? 1.0 : 0.78;
            zoomButton.Background = isZoomed
                ? CreateSidebarTintedBrush(selectionBrush, lightTheme ? (byte)0x10 : (byte)0x12, Windows.UI.Color.FromArgb(0xFF, 0x33, 0x41, 0x55))
                : isSelected
                    ? CreateSidebarTintedBrush(selectionBrush, lightTheme ? (byte)0x08 : (byte)0x0C, Windows.UI.Color.FromArgb(0xFF, 0x33, 0x41, 0x55))
                : new SolidColorBrush(Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00));
            zoomButton.BorderBrush = isZoomed
                ? CreateSidebarTintedBrush(selectionBrush, lightTheme ? (byte)0x50 : (byte)0x46, Windows.UI.Color.FromArgb(0xFF, 0x33, 0x41, 0x55))
                : isSelected
                    ? CreateSidebarTintedBrush(selectionBrush, lightTheme ? (byte)0x24 : (byte)0x2E, Windows.UI.Color.FromArgb(0xFF, 0x33, 0x41, 0x55))
                : new SolidColorBrush(Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00));
            zoomButton.Foreground = isZoomed
                ? AppBrush(border, "ShellTextPrimaryBrush")
                : isSelected
                    ? selectionBrush
                    : AppBrush(border, "ShellTextTertiaryBrush");
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
                Tag = "vertical",
            };
            ApplyPaneSplitterVisual(splitter, emphasized: false);
            AutomationProperties.SetAutomationId(splitter, $"shell-pane-splitter-vertical-{row}-{column}");
            ToolTipService.SetToolTip(splitter, "Drag to resize. Hold Shift to resize both axes. Double-click to fit panes.");
            splitter.PointerPressed += OnPaneSplitterPointerPressed;
            splitter.PointerMoved += OnPaneSplitterPointerMoved;
            splitter.PointerReleased += OnPaneSplitterPointerReleased;
            splitter.PointerCanceled += OnPaneSplitterPointerCanceled;
            splitter.PointerCaptureLost += OnPaneSplitterPointerCaptureLost;
            splitter.PointerEntered += OnPaneSplitterPointerEntered;
            splitter.PointerExited += OnPaneSplitterPointerExited;
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
                Tag = "horizontal",
            };
            ApplyPaneSplitterVisual(splitter, emphasized: false);
            AutomationProperties.SetAutomationId(splitter, $"shell-pane-splitter-horizontal-{row}-{column}");
            ToolTipService.SetToolTip(splitter, "Drag to resize. Hold Shift to resize both axes. Double-click to fit panes.");
            splitter.PointerPressed += OnPaneSplitterPointerPressed;
            splitter.PointerMoved += OnPaneSplitterPointerMoved;
            splitter.PointerReleased += OnPaneSplitterPointerReleased;
            splitter.PointerCanceled += OnPaneSplitterPointerCanceled;
            splitter.PointerCaptureLost += OnPaneSplitterPointerCaptureLost;
            splitter.PointerEntered += OnPaneSplitterPointerEntered;
            splitter.PointerExited += OnPaneSplitterPointerExited;
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
                Width = 12,
                Height = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(-4),
                Tag = "both",
            };
            ApplyPaneSplitterVisual(splitter, emphasized: false);
            AutomationProperties.SetAutomationId(splitter, $"shell-pane-splitter-both-{row}-{column}");
            ToolTipService.SetToolTip(splitter, "Drag to resize both axes. Double-click to fit panes.");
            splitter.PointerPressed += OnPaneSplitterPointerPressed;
            splitter.PointerMoved += OnPaneSplitterPointerMoved;
            splitter.PointerReleased += OnPaneSplitterPointerReleased;
            splitter.PointerCanceled += OnPaneSplitterPointerCanceled;
            splitter.PointerCaptureLost += OnPaneSplitterPointerCaptureLost;
            splitter.PointerEntered += OnPaneSplitterPointerEntered;
            splitter.PointerExited += OnPaneSplitterPointerExited;
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
            _splitterPreviewPrimaryRatio = _splitterStartPrimaryRatio;
            _splitterPreviewSecondaryRatio = _splitterStartSecondaryRatio;
            ResetPaneSplitPreview();
            ShowPaneSplitPreview();
            UpdatePaneSplitPreviewVisuals();
            ApplyPaneSplitterVisual(splitter, emphasized: true);
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
                UpdatePaneSplitPreviewFromPointer(point, adjustPrimary: true, adjustSecondary: resizeBothAxes);
                e.Handled = true;
                return;
            }

            if (string.Equals(_activeSplitterDirection, "both", StringComparison.Ordinal))
            {
                UpdatePaneSplitPreviewFromPointer(point, adjustPrimary: true, adjustSecondary: true);
                e.Handled = true;
                return;
            }

            if (string.Equals(_activeSplitterDirection, "horizontal", StringComparison.Ordinal))
            {
                UpdatePaneSplitPreviewFromPointer(point, adjustPrimary: resizeBothAxes, adjustSecondary: true);
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
                CompletePaneSplitterInteraction(splitter, commitPreview: true);
                splitter.ReleasePointerCapture(e.Pointer);
                e.Handled = true;
            }
        }

        private void OnPaneSplitterPointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border splitter && ReferenceEquals(splitter, _activeSplitter) && _activeSplitterPointerId == e.Pointer.PointerId)
            {
                CompletePaneSplitterInteraction(splitter, commitPreview: false);
                splitter.ReleasePointerCaptures();
                e.Handled = true;
            }
        }

        private void OnPaneSplitterPointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border splitter &&
                ReferenceEquals(splitter, _activeSplitter) &&
                (!_activeSplitterPointerId.HasValue || _activeSplitterPointerId == e.Pointer.PointerId))
            {
                CompletePaneSplitterInteraction(splitter, commitPreview: false);
                e.Handled = true;
            }
        }

        private void OnPaneSplitterPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border splitter && !ReferenceEquals(splitter, _activeSplitter))
            {
                ApplyPaneSplitterVisual(splitter, emphasized: true);
            }
        }

        private void OnPaneSplitterPointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border splitter && !ReferenceEquals(splitter, _activeSplitter))
            {
                ApplyPaneSplitterVisual(splitter, emphasized: false);
            }
        }

        private void ApplyPaneSplitterVisual(Border splitter, bool emphasized)
        {
            if (splitter is null || PaneWorkspaceGrid is null)
            {
                return;
            }

            bool isCenterSplitter = string.Equals(splitter.Tag as string, "both", StringComparison.Ordinal);
            if (isCenterSplitter)
            {
                splitter.Background = emphasized
                    ? CreateSidebarTintedBrush(AppBrush(PaneWorkspaceGrid, "ShellPaneActiveBorderBrush"), 0x10, Windows.UI.Color.FromArgb(0xFF, 0x2C, 0x2D, 0x31))
                    : AppBrush(PaneWorkspaceGrid, "ShellSurfaceBackgroundBrush");
                splitter.BorderBrush = emphasized
                    ? CreateSidebarTintedBrush(AppBrush(PaneWorkspaceGrid, "ShellPaneActiveBorderBrush"), 0x40, Windows.UI.Color.FromArgb(0xFF, 0x2C, 0x2D, 0x31))
                    : CreateSidebarTintedBrush(AppBrush(PaneWorkspaceGrid, "ShellBorderBrush"), 0x30, Windows.UI.Color.FromArgb(0xFF, 0x7E, 0x86, 0x91));
                return;
            }

            splitter.Background = emphasized
                ? CreateSidebarTintedBrush(AppBrush(PaneWorkspaceGrid, "ShellPaneActiveBorderBrush"), 0x20, Windows.UI.Color.FromArgb(0xFF, 0x2C, 0x2D, 0x31))
                : CreateSidebarTintedBrush(AppBrush(PaneWorkspaceGrid, "ShellBorderBrush"), 0x20, Windows.UI.Color.FromArgb(0xFF, 0x7E, 0x86, 0x91));
        }

        private void SetLiveResizeModeForVisiblePanes(bool enabled)
        {
            if (_activeThread is null)
            {
                return;
            }

            foreach (WorkspacePaneRecord pane in GetVisiblePanes(_activeThread))
            {
                switch (pane)
                {
                    case TerminalPaneRecord terminalPane:
                        terminalPane.Terminal.SetLiveResizeMode(enabled);
                        break;
                    case BrowserPaneRecord browserPane:
                        browserPane.Browser.SetLiveResizeMode(enabled);
                        break;
                    case EditorPaneRecord editorPane:
                        editorPane.Editor.SetLiveResizeMode(enabled);
                        break;
                    case DiffPaneRecord diffPane:
                        diffPane.DiffPane.SetLiveResizeMode(enabled);
                        break;
                }
            }
        }

        private void ClearActiveSplitterTracking()
        {
            _activeSplitter = null;
            _activeSplitterDirection = null;
            _activeSplitterPointerId = null;
            _splitterPreviewPrimaryRatio = null;
            _splitterPreviewSecondaryRatio = null;
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

        private void UpdatePaneSplitPreviewFromPointer(Point point, bool adjustPrimary, bool adjustSecondary)
        {
            if (_activeThread is null)
            {
                return;
            }

            double horizontalOffset = 0;
            double verticalOffset = 0;

            if (adjustPrimary && PaneWorkspaceGrid.ColumnDefinitions.Count >= 3)
            {
                double leftWidth = PaneWorkspaceGrid.ColumnDefinitions[0].ActualWidth;
                double rightWidth = PaneWorkspaceGrid.ColumnDefinitions[2].ActualWidth;
                double totalWidth = leftWidth + rightWidth;
                if (totalWidth > 0)
                {
                    double nextLeftWidth = Math.Clamp((totalWidth * _splitterStartPrimaryRatio) + (point.X - _splitterDragOriginX), totalWidth * MinPaneSplitRatio, totalWidth * MaxPaneSplitRatio);
                    _splitterPreviewPrimaryRatio = ClampPaneSplitRatio(nextLeftWidth / totalWidth);
                    horizontalOffset = nextLeftWidth - (totalWidth * _splitterStartPrimaryRatio);
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
                    _splitterPreviewSecondaryRatio = ClampPaneSplitRatio(nextTopHeight / totalHeight);
                    verticalOffset = nextTopHeight - (totalHeight * _splitterStartSecondaryRatio);
                }
            }

            ApplyPaneSplitPreview(horizontalOffset, verticalOffset);
            UpdatePaneSplitPreviewVisuals();
        }

        private void ApplyPaneSplitPreview(double horizontalOffset, double verticalOffset)
        {
            foreach (Border splitter in PaneWorkspaceGrid.Children.OfType<Border>())
            {
                string direction = splitter.Tag as string;
                switch (direction)
                {
                    case "vertical":
                        SetPaneSplitPreviewTransform(splitter, horizontalOffset, 0);
                        break;
                    case "horizontal":
                        SetPaneSplitPreviewTransform(splitter, 0, verticalOffset);
                        break;
                    case "both":
                        SetPaneSplitPreviewTransform(splitter, horizontalOffset, verticalOffset);
                        break;
                }
            }
        }

        private void ShowPaneSplitPreview()
        {
            if (PaneSplitPreviewCanvas is null)
            {
                return;
            }

            PaneSplitPreviewCanvas.Children.Clear();
            _paneSplitPreviewItems.Clear();
            PaneSplitPreviewCanvas.Visibility = Visibility.Visible;
        }

        private void HidePaneSplitPreview()
        {
            if (PaneSplitPreviewCanvas is null)
            {
                return;
            }

            PaneSplitPreviewCanvas.Children.Clear();
            _paneSplitPreviewItems.Clear();
            PaneSplitPreviewCanvas.Visibility = Visibility.Collapsed;
        }

        private void UpdatePaneSplitPreviewVisuals()
        {
            if (PaneSplitPreviewCanvas is null || _activeThread is null || PaneSplitPreviewCanvas.Visibility != Visibility.Visible)
            {
                return;
            }

            List<(WorkspacePaneRecord Pane, Rect Bounds)> previewRects = BuildPaneSplitPreviewRects();
            EnsurePaneSplitPreviewItems(previewRects.Count);

            for (int index = 0; index < _paneSplitPreviewItems.Count; index++)
            {
                Border previewBorder = _paneSplitPreviewItems[index];
                if (index >= previewRects.Count)
                {
                    previewBorder.Visibility = Visibility.Collapsed;
                    continue;
                }

                (WorkspacePaneRecord pane, Rect bounds) = previewRects[index];
                Rect insetBounds = InsetPreviewRect(bounds, 6);
                previewBorder.Width = Math.Max(0, insetBounds.Width);
                previewBorder.Height = Math.Max(0, insetBounds.Height);
                Canvas.SetLeft(previewBorder, insetBounds.X);
                Canvas.SetTop(previewBorder, insetBounds.Y);
                previewBorder.Visibility = Visibility.Visible;

                Brush accentBrush = AppBrush(PaneWorkspaceGrid, ResolvePaneAccentBrushKey(pane.Kind));
                previewBorder.BorderBrush = CreateSidebarTintedBrush(accentBrush, 0x7C, Windows.UI.Color.FromArgb(0xFF, 0x33, 0x41, 0x55));
                previewBorder.Background = CreateSidebarTintedBrush(accentBrush, 0x14, Windows.UI.Color.FromArgb(0xFF, 0x33, 0x41, 0x55));
                if (previewBorder.Child is Grid previewContent)
                {
                    previewContent.Background = null;
                    if (previewContent.Children.OfType<TextBlock>().FirstOrDefault() is TextBlock label)
                    {
                        label.Text = BuildOverviewPaneLabel(pane);
                        label.Foreground = accentBrush;
                    }
                }
            }
        }

        private void EnsurePaneSplitPreviewItems(int count)
        {
            if (PaneSplitPreviewCanvas is null)
            {
                return;
            }

            while (_paneSplitPreviewItems.Count < count)
            {
                TextBlock label = new()
                {
                    FontSize = 11,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(8, 6, 8, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                };

                Grid previewContent = new();
                previewContent.Children.Add(label);

                Border previewBorder = new()
                {
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    IsHitTestVisible = false,
                    Opacity = 1,
                    Child = previewContent,
                };

                previewBorder.Visibility = Visibility.Collapsed;
                _paneSplitPreviewItems.Add(previewBorder);
                PaneSplitPreviewCanvas.Children.Add(previewBorder);
            }
        }

        private List<(WorkspacePaneRecord Pane, Rect Bounds)> BuildPaneSplitPreviewRects()
        {
            List<(WorkspacePaneRecord Pane, Rect Bounds)> result = new();
            if (_activeThread is null || PaneWorkspaceGrid is null)
            {
                return result;
            }

            List<WorkspacePaneRecord> visiblePanes = GetVisiblePanes(_activeThread).ToList();
            if (visiblePanes.Count == 0)
            {
                return result;
            }

            double totalWidth = Math.Max(0, PaneWorkspaceGrid.ActualWidth);
            double totalHeight = Math.Max(0, PaneWorkspaceGrid.ActualHeight);
            if (totalWidth <= 1 || totalHeight <= 1)
            {
                return result;
            }

            double primaryRatio = _splitterPreviewPrimaryRatio ?? _activeThread.PrimarySplitRatio;
            double secondaryRatio = _splitterPreviewSecondaryRatio ?? _activeThread.SecondarySplitRatio;
            double splitWidth = Math.Max(0, totalWidth - PaneDividerThickness);
            double splitHeight = Math.Max(0, totalHeight - PaneDividerThickness);
            double leftWidth = splitWidth * primaryRatio;
            double rightWidth = splitWidth - leftWidth;
            double topHeight = splitHeight * secondaryRatio;
            double bottomHeight = splitHeight - topHeight;

            switch (visiblePanes.Count)
            {
                case 1:
                    result.Add((visiblePanes[0], new Rect(0, 0, totalWidth, totalHeight)));
                    break;
                case 2:
                    result.Add((visiblePanes[0], new Rect(0, 0, leftWidth, totalHeight)));
                    result.Add((visiblePanes[1], new Rect(leftWidth + PaneDividerThickness, 0, rightWidth, totalHeight)));
                    break;
                case 3:
                    result.Add((visiblePanes[0], new Rect(0, 0, leftWidth, totalHeight)));
                    result.Add((visiblePanes[1], new Rect(leftWidth + PaneDividerThickness, 0, rightWidth, topHeight)));
                    result.Add((visiblePanes[2], new Rect(leftWidth + PaneDividerThickness, topHeight + PaneDividerThickness, rightWidth, bottomHeight)));
                    break;
                default:
                    result.Add((visiblePanes[0], new Rect(0, 0, leftWidth, topHeight)));
                    result.Add((visiblePanes[1], new Rect(leftWidth + PaneDividerThickness, 0, rightWidth, topHeight)));
                    result.Add((visiblePanes[2], new Rect(0, topHeight + PaneDividerThickness, leftWidth, bottomHeight)));
                    result.Add((visiblePanes[3], new Rect(leftWidth + PaneDividerThickness, topHeight + PaneDividerThickness, rightWidth, bottomHeight)));
                    break;
            }

            return result;
        }

        private static Rect InsetPreviewRect(Rect bounds, double inset)
        {
            double safeInsetX = Math.Min(inset, bounds.Width / 2);
            double safeInsetY = Math.Min(inset, bounds.Height / 2);
            return new Rect(
                bounds.X + safeInsetX,
                bounds.Y + safeInsetY,
                Math.Max(0, bounds.Width - (safeInsetX * 2)),
                Math.Max(0, bounds.Height - (safeInsetY * 2)));
        }

        private static void SetPaneSplitPreviewTransform(UIElement element, double x, double y)
        {
            if (element is null)
            {
                return;
            }

            if (Math.Abs(x) < 0.01 && Math.Abs(y) < 0.01)
            {
                element.RenderTransform = null;
                return;
            }

            if (element.RenderTransform is not TranslateTransform transform)
            {
                transform = new TranslateTransform();
                element.RenderTransform = transform;
            }

            transform.X = x;
            transform.Y = y;
        }

        private void ResetPaneSplitPreview()
        {
            ApplyPaneSplitPreview(0, 0);
        }

        private void CompletePaneSplitterInteraction(Border splitter, bool commitPreview)
        {
            ApplyPaneSplitterVisual(splitter, emphasized: false);

            if (commitPreview)
            {
                CommitPaneSplitPreviewRatios();
            }

            ResetPaneSplitPreview();
            HidePaneSplitPreview();
            ClearActiveSplitterTracking();
            RequestLayoutForVisiblePanes();
        }

        private void CommitPaneSplitPreviewRatios()
        {
            if (_activeThread is null)
            {
                return;
            }

            bool changed = false;

            if (_splitterPreviewPrimaryRatio is double previewPrimary &&
                Math.Abs(previewPrimary - _activeThread.PrimarySplitRatio) > 0.0005)
            {
                _activeThread.PrimarySplitRatio = ClampPaneSplitRatio(previewPrimary);
                changed = true;
            }

            if (_splitterPreviewSecondaryRatio is double previewSecondary &&
                Math.Abs(previewSecondary - _activeThread.SecondarySplitRatio) > 0.0005)
            {
                _activeThread.SecondarySplitRatio = ClampPaneSplitRatio(previewSecondary);
                changed = true;
            }

            ApplyCommittedPaneSplitRatios();

            if (!changed)
            {
                return;
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

        private void ApplyCommittedPaneSplitRatios()
        {
            if (_activeThread is null)
            {
                return;
            }

            if (PaneWorkspaceGrid.ColumnDefinitions.Count >= 3)
            {
                PaneWorkspaceGrid.ColumnDefinitions[0].Width = new GridLength(_activeThread.PrimarySplitRatio, GridUnitType.Star);
                PaneWorkspaceGrid.ColumnDefinitions[2].Width = new GridLength(1 - _activeThread.PrimarySplitRatio, GridUnitType.Star);
            }

            if (PaneWorkspaceGrid.RowDefinitions.Count >= 3)
            {
                PaneWorkspaceGrid.RowDefinitions[0].Height = new GridLength(_activeThread.SecondarySplitRatio, GridUnitType.Star);
                PaneWorkspaceGrid.RowDefinitions[2].Height = new GridLength(1 - _activeThread.SecondarySplitRatio, GridUnitType.Star);
            }

            PaneWorkspaceGrid.UpdateLayout();
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
            EnsureThreadPanesMaterialized(_activeProject, _activeThread);
            pane = GetSelectedPane(_activeThread) ?? pane;
            UpdateTabViewItem(pane);
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
            QueueVisibleDeferredPaneMaterialization(_activeProject, _activeThread);
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
            bool lightTheme = ResolveTheme(SampleConfig.CurrentTheme) == ElementTheme.Light;
            foreach ((string paneId, Border border) in _paneContainersById)
            {
                if (border.Tag is WorkspacePaneRecord taggedPane)
                {
                    UpdatePaneZoomButtonState(border, taggedPane);
                }

                bool isSelected = string.Equals(selectedPane?.Id, paneId, StringComparison.Ordinal);
                string accentKey = border.Tag is WorkspacePaneRecord chromePane
                    ? ResolvePaneAccentBrushKey(chromePane.Kind)
                    : "ShellPaneActiveBorderBrush";
                Brush accentBrush = AppBrush(border, accentKey);
                border.Background = isSelected
                    ? CreateSidebarTintedBrush(accentBrush, lightTheme ? (byte)0x18 : (byte)0x12, Windows.UI.Color.FromArgb(0xFF, 0x33, 0x41, 0x55))
                    : AppBrush(border, "ShellSurfaceBackgroundBrush");
                border.BorderBrush = isSelected
                    ? CreateSidebarTintedBrush(accentBrush, lightTheme ? (byte)0x86 : (byte)0x70, Windows.UI.Color.FromArgb(0xFF, 0x33, 0x41, 0x55))
                    : AppBrush(border, "ShellBorderBrush");
                border.BorderThickness = new Thickness(1, 0, 1, 1);
            }

            foreach ((_, TabViewItem item) in _tabItemsById)
            {
                if (item.Tag is WorkspacePaneRecord pane)
                {
                    UpdatePaneTabChrome(item, pane);
                }
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
            if (ShouldSuppressSettingsNavigation())
            {
                return;
            }

            SettingsPage existingSettingsPage = SettingsFrame?.Content as SettingsPage;
            bool needsSettingsRefresh = _settingsPageNeedsRefresh;

            if (_showingSettings)
            {
                if (existingSettingsPage is not null)
                {
                    if (needsSettingsRefresh)
                    {
                        existingSettingsPage.RefreshFromCurrentState(refreshCredentialVault: false);
                        _settingsPageNeedsRefresh = false;
                    }

                    existingSettingsPage.QueueBrowserCredentialVaultRefresh();
                }
                else
                {
                    EnsureSettingsPageLoaded(refreshExisting: false, preloadOnly: true);
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_showingSettings && SettingsFrame?.Content is SettingsPage visibleSettingsPage)
                        {
                            visibleSettingsPage.QueueBrowserCredentialVaultRefresh();
                        }
                    });
                }

                return;
            }

            _showingSettings = true;
            CancelPendingInspectorDirectoryBuilds();
            if (existingSettingsPage is not null)
            {
                if (needsSettingsRefresh)
                {
                    existingSettingsPage.RefreshFromCurrentState(refreshCredentialVault: false);
                    _settingsPageNeedsRefresh = false;
                }
            }
            else
            {
                EnsureSettingsPageLoaded(refreshExisting: false, preloadOnly: true);
            }
            UpdateProjectTreeSelectionVisuals();
            UpdateWorkspaceVisibility();
            UpdateSidebarActions();
            UpdateHeader();
            QueueSessionSave();
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_showingSettings && SettingsFrame?.Content is SettingsPage visibleSettingsPage)
                {
                    visibleSettingsPage.QueueBrowserCredentialVaultRefresh();
                }
            });
            LogAutomationEvent("shell", "view.settings", "Opened preferences");
        }

        private void QueueSettingsPagePreload()
        {
            if (_settingsPagePreloadStarted)
            {
                return;
            }

            _settingsPagePreloadStarted = true;
            if (DispatcherQueue?.HasThreadAccess == true)
            {
                if (!_showingSettings)
                {
                    EnsureSettingsPageLoaded(refreshExisting: false, preloadOnly: true);
                }

                return;
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                if (_showingSettings)
                {
                    return;
                }

                EnsureSettingsPageLoaded(refreshExisting: false, preloadOnly: true);
            });
        }

        private void EnsureSettingsPageLoaded(bool refreshExisting, bool preloadOnly = false)
        {
            if (SettingsFrame is null)
            {
                return;
            }

            if (SettingsFrame.Content is SettingsPage settingsPage)
            {
                if (refreshExisting)
                {
                    settingsPage.RefreshFromCurrentState(refreshCredentialVault: !preloadOnly);
                    _settingsPageNeedsRefresh = false;
                }

                return;
            }

            SettingsFrame.Navigate(typeof(SettingsPage), preloadOnly);
            _settingsPageNeedsRefresh = false;
        }

        private void ShowTerminalShell(bool queueGitRefresh = true)
        {
            bool wasShowingSettings = _showingSettings;
            _showingSettings = false;
            if (wasShowingSettings)
            {
                // Ignore one stale settings reopen after leaving the preferences view.
                _suppressSettingsUntil = DateTimeOffset.UtcNow.AddSeconds(2);
            }
            EnsureThreadPanesMaterialized(_activeProject, _activeThread);
            UpdateProjectTreeSelectionVisuals();
            if (wasShowingSettings)
            {
                _lastPaneWorkspaceRenderKey = null;
                RefreshTabView();
            }
            else
            {
                UpdateWorkspaceVisibility();
            }
            UpdateSidebarActions();
            QueueSelectedPaneFocus();
            RequestLayoutForVisiblePanes();
            QueueVisibleDeferredPaneMaterialization(_activeProject, _activeThread);
            if (_activeGitSnapshot is null &&
                _activeThread?.DiffReviewSource == DiffReviewSourceKind.Live &&
                _activeThread.LiveSnapshot is not null)
            {
                _activeGitSnapshot = _activeThread.LiveSnapshot;
                ApplyGitSnapshotToUi();
            }
            UpdateHeader();
            if (queueGitRefresh)
            {
                string selectedDiffPath = _activeThread?.SelectedDiffPath ?? _activeGitSnapshot?.SelectedPath;
                QueueActiveThreadGitRefresh(
                    selectedDiffPath,
                    preserveSelection: true,
                    includeSelectedDiff: false,
                    preferFastRefresh: true);
            }

            QueueSessionSave();
            LogAutomationEvent("shell", "view.terminal", _activeThread is null ? "Showing empty project state" : "Showing pane workspace", new Dictionary<string, string>
            {
                ["projectId"] = _activeProject?.Id ?? string.Empty,
                ["threadId"] = _activeThread?.Id ?? string.Empty,
            });
        }

        private bool ShouldSuppressSettingsNavigation()
        {
            return !_showingSettings && _suppressSettingsUntil > DateTimeOffset.UtcNow;
        }

        private async System.Threading.Tasks.Task RefreshActiveThreadGitStateAsync(
            string selectedPath = null,
            bool preserveSelection = false,
            bool includeSelectedDiff = true,
            bool preferFastRefresh = false)
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
            string correlationId = _pendingGitCorrelationId;
            _pendingGitCorrelationId = null;
            string worktreePath = thread.WorktreePath ?? project?.RootPath;
            bool captureComplete = !preferFastRefresh && VisibleDiffPaneRequiresCompleteSnapshot(thread);
            Stopwatch stopwatch = Stopwatch.StartNew();
            NativeAutomationDiagnostics.IncrementCounter("gitRefresh.count");

            GitThreadSnapshot snapshot = await System.Threading.Tasks.Task
                .Run(() =>
                {
                    using var perfScope = NativeAutomationDiagnostics.TrackOperation("git.refresh.active", correlationId, background: true);
                    return captureComplete
                        ? GitStatusService.CaptureComplete(worktreePath, targetPath)
                        : includeSelectedDiff
                            ? GitStatusService.Capture(worktreePath, targetPath)
                            : preferFastRefresh
                                ? GitStatusService.CaptureStatusOnly(worktreePath, targetPath)
                                : GitStatusService.CaptureMetadata(worktreePath, targetPath);
                })
                .ConfigureAwait(true);

            if (requestId != _latestGitRefreshRequestId ||
                !ReferenceEquals(thread, _activeThread) ||
                !ReferenceEquals(project, _activeProject))
            {
                return;
            }

            ApplyActiveGitSnapshot(snapshot);
            QueueVisibleDiffHydrationIfNeeded(thread, project, snapshot);
            LogAutomationEvent("performance", "git.snapshot_ready", "Refreshed active thread git state", new Dictionary<string, string>
            {
                ["selectedPath"] = targetPath ?? string.Empty,
                ["durationMs"] = stopwatch.ElapsedMilliseconds.ToString(),
                ["mode"] = captureComplete
                    ? "complete"
                    : includeSelectedDiff
                        ? "selected-diff"
                        : preferFastRefresh
                            ? "status-only"
                            : "metadata",
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
                    thread.BaselineSnapshot = checkpointSnapshot;
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
                QueueSessionSave(SessionSaveDetail.Full);
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
            CommitActiveGitSnapshot(snapshot, DateTimeOffset.UtcNow, ensureBaselineCapture: true, logRefresh: true);
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
                            thread.BaselineSnapshot = task.Result;
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
            bool renderDiffReviewUi = ShouldRenderDiffReviewUi();
            bool refreshFilesInspector = _inspectorOpen && _activeInspectorSection == InspectorSection.Files;
            if (displayedSnapshot is null)
            {
                if (ShouldRefreshReviewInspectorUi())
                {
                    DiffBranchText.Text = "No git context";
                    DiffWorktreeText.Text = string.Empty;
                    DiffSummaryText.Text = "No working tree changes";
                }
                if (renderDiffReviewUi)
                {
                    PopulateDiffFileList(null, null, null);
                    foreach (DiffPaneRecord emptyDiffPane in GetVisiblePanes(_activeThread).OfType<DiffPaneRecord>())
                    {
                        UpdateDiffPane(emptyDiffPane, emptyDiffPane.DiffPath, null);
                    }
                }

                return;
            }

            int totalAddedLines = 0;
            int totalRemovedLines = 0;
            foreach (GitChangedFile file in displayedSnapshot.ChangedFiles)
            {
                totalAddedLines += file.AddedLines;
                totalRemovedLines += file.RemovedLines;
            }
            if (ShouldRefreshReviewInspectorUi())
            {
                DiffBranchText.Text = string.IsNullOrWhiteSpace(displayedSnapshot.BranchName)
                    ? "Git metadata unavailable"
                    : displayedSnapshot.BranchName;
                DiffWorktreeText.Text = displayedSnapshot.WorktreePath ?? string.Empty;
                DiffSummaryText.Text = string.IsNullOrWhiteSpace(displayedSnapshot.Error)
                    ? FormatGitSummary(displayedSnapshot.StatusSummary, totalAddedLines, totalRemovedLines)
                    : displayedSnapshot.Error;
            }

            if (!renderDiffReviewUi)
            {
                if (refreshFilesInspector)
                {
                    RefreshInspectorFileBrowser();
                }

                return;
            }

            PopulateDiffFileList(displayedSnapshot.ChangedFiles, displayedSnapshot.SelectedPath, displayedSnapshot);

            foreach (DiffPaneRecord diffPane in GetVisiblePanes(_activeThread).OfType<DiffPaneRecord>())
            {
                DiffPaneDisplayMode displayMode = diffPane.DiffPane.DisplayMode;
                string panePath = displayMode == DiffPaneDisplayMode.FullPatchReview
                    ? (string.IsNullOrWhiteSpace(diffPane.DiffPath) ? displayedSnapshot.SelectedPath : diffPane.DiffPath)
                    : ResolveVisibleDiffPanePath(diffPane, displayedSnapshot.SelectedPath);
                UpdateDiffPane(
                    diffPane,
                    panePath,
                    null,
                    displayedSnapshot.ChangedFiles.Count > 0 ? displayedSnapshot : null,
                    displayMode);
            }

            if (refreshFilesInspector)
            {
                RefreshInspectorFileBrowser();
            }
        }

        private bool ShouldRenderDiffReviewUi()
        {
            if (_activeThread is null)
            {
                return false;
            }

            if (_inspectorOpen && _activeInspectorSection == InspectorSection.Review)
            {
                return true;
            }

            foreach (WorkspacePaneRecord pane in GetVisiblePanes(_activeThread))
            {
                if (pane is DiffPaneRecord)
                {
                    return true;
                }
            }

            return false;
        }

        private bool ShouldRefreshReviewInspectorUi()
        {
            return _inspectorOpen && _activeInspectorSection == InspectorSection.Review;
        }

        private void PopulateDiffFileList(IReadOnlyList<GitChangedFile> changedFiles, string selectedPath, GitThreadSnapshot sourceSnapshot)
        {
            IReadOnlyList<GitChangedFile> files = changedFiles ?? Array.Empty<GitChangedFile>();
            string renderKey = BuildDiffFileListRenderKey(files);
            if (!string.Equals(renderKey, _lastDiffFileListRenderKey, StringComparison.Ordinal))
            {
                using var perfScope = NativeAutomationDiagnostics.TrackOperation("render.diff-file-list");
                _diffFileButtonsByPath.Clear();
                _diffFileListItems.Clear();
                _diffFileListItems.AddRange(files.Select(BuildDiffFileListItem));
                if (DiffFileListView is not null)
                {
                    DiffFileListView.ItemsSource = null;
                    DiffFileListView.ItemsSource = _diffFileListItems;
                }

                _lastDiffFileListRenderKey = renderKey;
            }

            DiffEmptyText.Visibility = files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

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

        private DiffFileListItem BuildDiffFileListItem(GitChangedFile changedFile)
        {
            Brush statusBrush = AppBrush(this, ResolveGitStatusBrushKey(changedFile?.Status));
            return new DiffFileListItem
            {
                File = changedFile,
                AutomationId = $"shell-diff-file-{BuildAutomationKey(changedFile?.Path)}",
                AutomationName = changedFile?.DisplayName,
                StatusSymbol = ResolveGitStatusSymbol(changedFile?.Status),
                StatusBrush = statusBrush,
                FileName = Path.GetFileName(changedFile?.DisplayName ?? string.Empty),
                MetaText = BuildDiffFileMeta(changedFile),
                AddedText = changedFile?.AddedLines > 0 ? $"+{changedFile.AddedLines}" : string.Empty,
                AddedVisibility = changedFile?.AddedLines > 0 ? Visibility.Visible : Visibility.Collapsed,
                RemovedText = changedFile?.RemovedLines > 0 ? $"-{changedFile.RemovedLines}" : string.Empty,
                RemovedVisibility = changedFile?.RemovedLines > 0 ? Visibility.Visible : Visibility.Collapsed,
            };
        }

        private async System.Threading.Tasks.Task SelectDiffPathInCurrentReviewAsync(string selectedPath)
        {
            if (_activeThread is null || string.IsNullOrWhiteSpace(selectedPath))
            {
                return;
            }

            try
            {
                GitThreadSnapshot displayedSnapshot = ResolveDisplayedGitSnapshot();
                if (displayedSnapshot is null)
                {
                    return;
                }

                GitStatusService.SelectDiffPath(displayedSnapshot, selectedPath);
                _activeThread.SelectedDiffPath = displayedSnapshot.SelectedPath;
                _hoveredDiffFilePaths.Clear();
                if (ReferenceEquals(displayedSnapshot, _activeGitSnapshot))
                {
                    UpdateDiffFileSelection();
                    if (HasSelectedDiffAvailable(displayedSnapshot, displayedSnapshot.SelectedPath))
                    {
                        AddOrSelectDiffPane(_activeProject, _activeThread, displayedSnapshot.SelectedPath, null, displayedSnapshot, DiffPaneDisplayMode.FileCompare);
                    }
                    else
                    {
                        AddOrSelectDiffPane(_activeProject, _activeThread, displayedSnapshot.SelectedPath, null, displayedSnapshot, DiffPaneDisplayMode.FileCompare);
                        await EnsureSelectedDiffReadyAsync(_activeThread, _activeProject, displayedSnapshot.SelectedPath).ConfigureAwait(true);
                    }
                }
                else
                {
                    UpdateDiffFileSelection();
                    AddOrSelectDiffPane(_activeProject, _activeThread, displayedSnapshot.SelectedPath, displayedSnapshot.SelectedDiff, displayedSnapshot, DiffPaneDisplayMode.FileCompare);
                }
            }
            catch (Exception ex)
            {
                LogAutomationEvent("shell", "diff.select_failed", $"Could not select diff path {selectedPath}: {ex.Message}", new Dictionary<string, string>
                {
                    ["threadId"] = _activeThread?.Id ?? string.Empty,
                    ["projectId"] = _activeProject?.Id ?? string.Empty,
                    ["selectedPath"] = selectedPath ?? string.Empty,
                });
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

            foreach (GitChangedFile file in snapshot.ChangedFiles)
            {
                if (string.Equals(file.Path, resolvedPath, StringComparison.Ordinal))
                {
                    return !string.IsNullOrWhiteSpace(file.DiffText);
                }
            }

            return false;
        }

        private static bool HasCompleteDiffSet(GitThreadSnapshot snapshot)
        {
            if (snapshot is null || snapshot.ChangedFiles.Count <= 1)
            {
                return false;
            }

            foreach (GitChangedFile file in snapshot.ChangedFiles)
            {
                if (string.IsNullOrWhiteSpace(file.DiffText))
                {
                    return false;
                }
            }

            return true;
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
            if (cachedSnapshot is null ||
                nextSnapshot is null ||
                ReferenceEquals(cachedSnapshot, nextSnapshot) ||
                nextSnapshot.ChangedFiles.Count == 0)
            {
                return;
            }

            string cachedOrigin = string.IsNullOrWhiteSpace(cachedSnapshot.WorktreePath)
                ? cachedSnapshot.RepositoryRootPath
                : cachedSnapshot.WorktreePath;
            string nextOrigin = string.IsNullOrWhiteSpace(nextSnapshot.WorktreePath)
                ? nextSnapshot.RepositoryRootPath
                : nextSnapshot.WorktreePath;
            if (!string.IsNullOrWhiteSpace(cachedOrigin) &&
                !string.IsNullOrWhiteSpace(nextOrigin) &&
                !string.Equals(cachedOrigin, nextOrigin, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Dictionary<string, GitChangedFile> cachedFilesByPath = new(cachedSnapshot.ChangedFiles.Count, StringComparer.Ordinal);
            foreach (GitChangedFile cachedFile in cachedSnapshot.ChangedFiles)
            {
                if (!string.IsNullOrWhiteSpace(cachedFile?.Path))
                {
                    cachedFilesByPath[cachedFile.Path] = cachedFile;
                }
            }

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

                if (string.IsNullOrWhiteSpace(changedFile.OriginalPath))
                {
                    changedFile.OriginalPath = cachedFile.OriginalPath;
                }

            }

            if (string.IsNullOrWhiteSpace(nextSnapshot.SelectedDiff) && !string.IsNullOrWhiteSpace(nextSnapshot.SelectedPath))
            {
                foreach (GitChangedFile file in nextSnapshot.ChangedFiles)
                {
                    if (string.Equals(file.Path, nextSnapshot.SelectedPath, StringComparison.Ordinal))
                    {
                        nextSnapshot.SelectedDiff = file.DiffText;
                        break;
                    }
                }
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
            TextBlock statusBadge = new()
            {
                Text = ResolveGitStatusSymbol(changedFile.Status),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 10.25,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = statusBrush,
                Opacity = 0.92,
                VerticalAlignment = VerticalAlignment.Top,
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
            string status = ResolveGitStatusDescription(changedFile?.Status);
            string relativePath = changedFile?.DisplayName ?? changedFile?.Path;
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return status;
            }

            string normalizedPath = relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            string directory = Path.GetDirectoryName(normalizedPath)?.Trim();
            if (string.IsNullOrWhiteSpace(directory))
            {
                return $"root · {status}";
            }

            return $"{FormatCompactDiffPathContext(directory)} · {status}";
        }

        private static string FormatCompactDiffPathContext(string directoryPath, int maxSegments = 2)
        {
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                return "root";
            }

            string normalized = directoryPath
                .Replace(Path.DirectorySeparatorChar, '/')
                .Trim()
                .Trim('/');
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return "root";
            }

            string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                return "root";
            }

            if (segments.Length <= maxSegments)
            {
                return string.Join("/", segments);
            }

            return $".../{string.Join("/", segments[^maxSegments..])}";
        }

        private void UpdateDiffFileSelection()
        {
            string selectedPath = ResolveDisplayedGitSnapshot()?.SelectedPath;
            foreach (Button button in _diffFileButtonsByPath.Values)
            {
                GitChangedFile changedFile = ResolveSelectedDiffFile(button);
                ApplyDiffFileButtonState(
                    button,
                    changedFile,
                    string.Equals(changedFile?.Path, selectedPath, StringComparison.Ordinal),
                    changedFile is not null && _hoveredDiffFilePaths.Contains(changedFile.Path));
            }
        }

        private string BuildDiffFileListRenderKey(IReadOnlyList<GitChangedFile> changedFiles)
        {
            StringBuilder builder = new();
            builder.Append(ResolveTheme(SampleConfig.CurrentTheme).ToString());
            builder.Append('|');
            IReadOnlyList<GitChangedFile> files = changedFiles ?? Array.Empty<GitChangedFile>();
            builder.Append(files.Count);

            foreach (GitChangedFile changedFile in files)
            {
                builder.Append('|');
                builder.Append(changedFile?.Status ?? string.Empty);
                builder.Append(':');
                builder.Append(changedFile?.Path ?? string.Empty);
                builder.Append(':');
                builder.Append(changedFile?.OriginalPath ?? string.Empty);
                builder.Append(':');
                builder.Append(changedFile?.AddedLines ?? 0);
                builder.Append(':');
                builder.Append(changedFile?.RemovedLines ?? 0);
            }

            return builder.ToString();
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

        private void QueueSelectedPaneFocus()
        {
            if (GetSelectedPane(_activeThread) is not TerminalPaneRecord)
            {
                return;
            }

            int requestId = ++_paneFocusRequestId;
            _ = System.Threading.Tasks.Task.Delay(45)
                .ContinueWith(_ =>
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (requestId != _paneFocusRequestId || _showingSettings)
                        {
                            return;
                        }

                        FocusSelectedPane();
                    });
                }, System.Threading.Tasks.TaskScheduler.Default);
        }

        private System.Threading.Tasks.Task EnqueueOnUiThreadAsync(Action action)
        {
            if (action is null)
            {
                return System.Threading.Tasks.Task.CompletedTask;
            }

            var completion = new System.Threading.Tasks.TaskCompletionSource<object>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
            if (!DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        action();
                        completion.TrySetResult(null);
                    }
                    catch (Exception ex)
                    {
                        completion.TrySetException(ex);
                    }
                }))
            {
                completion.TrySetCanceled();
            }

            return completion.Task;
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
            using var perfScope = NativeAutomationDiagnostics.TrackOperation("pane.layout.apply");
            NativeAutomationDiagnostics.IncrementCounter("paneLayout.count");
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

        private void OnPaneStripButtonPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Button button && !string.IsNullOrWhiteSpace(button.Name))
            {
                _hoveredPaneStripButtonNames.Add(button.Name);
                UpdateSidebarActions();
            }
        }

        private void OnPaneStripButtonPointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Button button && !string.IsNullOrWhiteSpace(button.Name))
            {
                _hoveredPaneStripButtonNames.Remove(button.Name);
                UpdateSidebarActions();
            }
        }

        private bool IsPaneStripButtonHovered(Button button)
        {
            return button is not null &&
                !string.IsNullOrWhiteSpace(button.Name) &&
                _hoveredPaneStripButtonNames.Contains(button.Name);
        }

        private void UpdateSidebarActions()
        {
            ApplyActionButtonState(SettingsNavButton, SettingsNavText, _showingSettings);
            ApplyActionButtonState(NewProjectButton, NewProjectText, false);
            ApplyPaneStripButtonState(AddBrowserPaneButton, "ShellInfoBrush", hovered: IsPaneStripButtonHovered(AddBrowserPaneButton));
            ApplyPaneStripButtonState(AddEditorPaneButton, "ShellSuccessBrush", hovered: IsPaneStripButtonHovered(AddEditorPaneButton));
            ApplyPaneStripButtonState(
                FitPanesButton,
                _activeThread?.AutoFitPaneContentLocked == true
                    ? ResolvePaneAccentBrushKey(GetSelectedPane(_activeThread)?.Kind ?? WorkspacePaneKind.Terminal)
                    : "ShellTextSecondaryBrush",
                _activeThread?.AutoFitPaneContentLocked == true,
                IsPaneStripButtonHovered(FitPanesButton));
            ApplyPaneStripButtonState(
                ToggleInspectorButton,
                _inspectorOpen ? "ShellTextSecondaryBrush" : "ShellTextTertiaryBrush",
                hovered: IsPaneStripButtonHovered(ToggleInspectorButton));
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
            if (!_inspectorOpen)
            {
                CancelPendingInspectorDirectoryBuilds();
            }
            else
            {
                SyncInspectorSectionWithSelectedPane();
                ApplyGitSnapshotToUi();
            }

            UpdateInspectorVisibility();
            _lastPaneWorkspaceRenderKey = null;
            QueueSessionSave();
            PlayShellLayoutTransition(includeSidebar: false, includeInspector: true);
            LogAutomationEvent("shell", "inspector.toggled", _inspectorOpen ? "Inspector opened" : "Inspector collapsed", new Dictionary<string, string>
            {
                ["inspectorOpen"] = _inspectorOpen.ToString(),
            });
        }

        private void UpdateInspectorVisibility()
        {
            if (InspectorSplitView is null || InspectorSidebar is null || ToggleInspectorButton is null)
            {
                return;
            }

            bool showInspector = _inspectorOpen && !_showingSettings && _activeThread is not null;

            InspectorSplitView.IsPaneOpen = showInspector;
            InspectorSidebar.IsHitTestVisible = showInspector;
            if (showInspector)
            {
                RefreshInspectorNotes();
            }

            ToolTipService.SetToolTip(ToggleInspectorButton, _inspectorOpen ? "Hide inspector" : "Show inspector");
            if (ToggleInspectorButton.Content is FontIcon icon)
            {
                icon.Glyph = _inspectorOpen ? "\uE7F8" : "\uE7F7";
            }

            ApplyPaneStripButtonState(
                ToggleInspectorButton,
                _inspectorOpen ? "ShellTextSecondaryBrush" : "ShellTextTertiaryBrush",
                hovered: IsPaneStripButtonHovered(ToggleInspectorButton));
        }

        private void UpdatePaneLayout()
        {
            bool isOpen = ShellSplitView.IsPaneOpen;

            PaneBrandMark.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
            PaneBrandTextStack.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
            ProjectSectionText.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
            NewProjectText.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
            SettingsNavText.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
            ApplySidebarActionLayout(NewProjectButton, isOpen);
            ApplySidebarActionLayout(SettingsNavButton, isOpen);
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
                    string settingsContext = "Theme, shell, pane limit, and vault";
                    ActiveDirectoryText.Text = settingsContext;
                    ActiveDirectoryText.Visibility = Visibility.Visible;
                    if (ActiveDirectorySeparator is not null)
                    {
                        ActiveDirectorySeparator.Visibility = Visibility.Visible;
                    }
                    return;
                }

                ThreadNameBox.IsReadOnly = _activeThread is null;
                ThreadNameBox.Text = _activeThread?.Name ?? "No thread selected";
                string context = _activeProject is null ? string.Empty : BuildHeaderContext(_activeProject, _activeThread);
                bool hasContext = !string.IsNullOrWhiteSpace(context);
                ActiveDirectoryText.Text = context;
                ActiveDirectoryText.Visibility = hasContext ? Visibility.Visible : Visibility.Collapsed;
                if (ActiveDirectorySeparator is not null)
                {
                    ActiveDirectorySeparator.Visibility = hasContext ? Visibility.Visible : Visibility.Collapsed;
                }
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
            thread.LiveSnapshot = null;
            thread.LiveSnapshotCapturedAt = default;
            thread.BaselineSnapshot = null;
            thread.DiffCheckpoints.Clear();
            thread.SelectedCheckpointId = null;
            thread.DiffReviewSource = DiffReviewSourceKind.Live;

            if (thread == _activeThread)
            {
                UpdateHeader();
                QueueActiveThreadGitRefresh(includeSelectedDiff: true);
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
                PaneWorkspaceShell.Visibility = Visibility.Visible;
                PaneWorkspaceShell.Opacity = 0;
                PaneWorkspaceShell.IsHitTestVisible = false;
                EmptyThreadStatePanel.Visibility = Visibility.Collapsed;
                UpdateInspectorVisibility();
                return;
            }

            SettingsFrame.Visibility = Visibility.Collapsed;

            bool showEmptyState = _activeProject is not null && _activeThread is null;
            PaneWorkspaceShell.Opacity = 1;
            PaneWorkspaceShell.IsHitTestVisible = true;
            PaneWorkspaceShell.Visibility = showEmptyState ? Visibility.Collapsed : Visibility.Visible;
            EmptyThreadStatePanel.Visibility = showEmptyState ? Visibility.Visible : Visibility.Collapsed;
            UpdateInspectorVisibility();
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
                parts.Add($"{liveCount} active");
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

            int attentionCount = 0;
            int activeToolCount = 0;
            TerminalPaneRecord representativeAttentionToolPane = null;
            TerminalPaneRecord representativeActiveToolPane = null;

            foreach (WorkspacePaneRecord pane in thread.Panes)
            {
                bool isAttentionPane = pane.RequiresAttention;
                if (isAttentionPane)
                {
                    attentionCount++;
                }

                if (pane is not TerminalPaneRecord terminalPane)
                {
                    continue;
                }

                bool hasLiveTool = terminalPane.Terminal?.HasLiveToolSession == true &&
                    !terminalPane.IsExited &&
                    !terminalPane.ReplayRestoreFailed;
                if (hasLiveTool)
                {
                    activeToolCount++;
                    representativeActiveToolPane ??= terminalPane;
                }

                if (isAttentionPane &&
                    representativeAttentionToolPane is null &&
                    (!string.IsNullOrWhiteSpace(terminalPane.Terminal?.ActiveToolSession) ||
                     !string.IsNullOrWhiteSpace(terminalPane.ReplayTool)))
                {
                    representativeAttentionToolPane = terminalPane;
                }
            }

            if (attentionCount > 0)
            {
                TerminalPaneRecord attentionToolPane = representativeAttentionToolPane ?? representativeActiveToolPane;
                string toolName = ResolveToolName(attentionToolPane?.Terminal?.ActiveToolSession)
                    ?? ResolveToolName(attentionToolPane?.ReplayTool);
                return new ThreadActivitySummary
                {
                    Label = string.IsNullOrWhiteSpace(toolName)
                        ? (attentionCount == 1 ? "Ready" : $"{attentionCount} ready")
                        : toolName,
                    ToolTip = string.IsNullOrWhiteSpace(toolName)
                        ? $"{attentionCount} pane{(attentionCount == 1 ? string.Empty : "s")} have unread activity."
                        : $"{toolName} has unread activity.",
                    RequiresAttention = true,
                };
            }

            if (activeToolCount > 0)
            {
                string toolName = activeToolCount == 1
                    ? ResolveToolName(representativeActiveToolPane?.Terminal?.ActiveToolSession) ?? ResolveToolName(representativeActiveToolPane?.ReplayTool) ?? "Agent"
                    : $"{activeToolCount} live";
                return new ThreadActivitySummary
                {
                    Label = toolName,
                    ToolTip = activeToolCount == 1
                        ? $"{toolName} session is active."
                        : $"{activeToolCount} agent sessions are active.",
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

            int noteCount = thread?.NoteEntries.Count ?? 0;
            if (noteCount > 0)
            {
                builder.Append(' ')
                    .Append(noteCount)
                    .Append(" note");
                if (noteCount != 1)
                {
                    builder.Append('s');
                }
            }

            return builder.ToString();
        }

        private static string BuildThreadRailMeta(WorkspaceProject project, WorkspaceThread thread)
        {
            string worktreeName = ShellProfiles.DeriveName(thread.WorktreePath ?? project.RootPath);
            string location = string.IsNullOrWhiteSpace(thread.BranchName)
                ? worktreeName
                : thread.BranchName;

            if (location?.Length > 20)
            {
                location = location[..17] + "...";
            }

            string meta = thread.ChangedFileCount <= 0
                ? location
                : $"{location} · {thread.ChangedFileCount} files";
            int noteCount = thread?.NoteEntries.Count ?? 0;
            if (noteCount > 0)
            {
                meta += $" · {noteCount} note{(noteCount == 1 ? string.Empty : "s")}";
            }

            return meta;
        }

        private static string BuildThreadButtonToolTip(WorkspaceProject project, WorkspaceThread thread, string paneSummary)
        {
            StringBuilder builder = new();
            builder.Append(FormatThreadPath(project, thread))
                .Append(" · ")
                .Append(paneSummary);

            int noteCount = thread?.NoteEntries.Count ?? 0;
            string notePreview = BuildThreadNotePreview(ResolvePreferredThreadNote(thread)?.Text, maxLength: 120);
            if (noteCount > 0)
            {
                builder.Append(Environment.NewLine)
                    .Append(noteCount == 1 ? "Note: " : $"{noteCount} notes: ");
                if (!string.IsNullOrWhiteSpace(notePreview))
                {
                    builder.Append(notePreview);
                }
            }

            return builder.ToString();
        }

        private IEnumerable<InspectorNoteGroupItem> BuildInspectorNoteGroups(WorkspaceProject project, WorkspaceThread thread, NotesListScope scope)
        {
            if (scope == NotesListScope.Thread)
            {
                InspectorNoteGroupItem group = BuildInspectorNoteGroup(thread, scope);
                if (group is not null)
                {
                    yield return group;
                }

                yield break;
            }

            if (project is null)
            {
                yield break;
            }

            foreach (WorkspaceThread ownerThread in project.Threads
                         .Where(candidate => candidate.NoteEntries.Count > 0)
                         .OrderByDescending(candidate => ReferenceEquals(candidate, _activeThread))
                         .ThenByDescending(GetLatestThreadNoteActivity))
            {
                InspectorNoteGroupItem group = BuildInspectorNoteGroup(ownerThread, scope);
                if (group is not null)
                {
                    yield return group;
                }
            }
        }

        private InspectorNoteGroupItem BuildInspectorNoteGroup(WorkspaceThread thread, NotesListScope scope)
        {
            if (thread is null)
            {
                return null;
            }

            List<WorkspaceThreadNote> activeNotes = thread.NoteEntries
                .Where(candidate => !candidate.IsArchived)
                .OrderByDescending(candidate => candidate.UpdatedAt)
                .ToList();
            List<WorkspaceThreadNote> archivedNotes = thread.NoteEntries
                .Where(candidate => candidate.IsArchived)
                .OrderByDescending(candidate => candidate.ArchivedAt ?? candidate.UpdatedAt)
                .ToList();
            if (activeNotes.Count == 0 && archivedNotes.Count == 0)
            {
                return null;
            }

            bool showHeader = scope == NotesListScope.Project || archivedNotes.Count > 0;
            bool archivedExpanded = _expandedArchivedNoteThreadIds.Contains(thread.Id) || (scope == NotesListScope.Thread && activeNotes.Count == 0);
            return new InspectorNoteGroupItem
            {
                ThreadId = thread.Id,
                Title = thread.Name,
                Meta = showHeader ? BuildNoteGroupMeta(thread, activeNotes.Count, archivedNotes.Count, scope) : string.Empty,
                HeaderAccentBrush = ResolveNoteGroupAccentBrush(thread, activeNotes, archivedNotes),
                HeaderVisibility = showHeader ? Visibility.Visible : Visibility.Collapsed,
                ArchivedToggleText = archivedExpanded
                    ? "Hide archived"
                    : $"Archived ({archivedNotes.Count})",
                ArchivedSectionVisibility = archivedNotes.Count > 0 ? Visibility.Visible : Visibility.Collapsed,
                ArchivedItemsVisibility = archivedExpanded ? Visibility.Visible : Visibility.Collapsed,
                ActiveNotes = activeNotes.Select(note => BuildInspectorNoteCardItem(thread, note)).ToList(),
                ArchivedNotes = archivedNotes.Select(note => BuildInspectorNoteCardItem(thread, note)).ToList(),
            };
        }

        private string BuildNoteGroupMeta(WorkspaceThread thread, int activeCount, int archivedCount, NotesListScope scope)
        {
            int totalCount = activeCount + archivedCount;
            List<string> parts = new();
            parts.Add(totalCount == 1 ? "1 note" : $"{totalCount} notes");

            if (archivedCount > 0)
            {
                parts.Add($"{archivedCount} archived");
            }

            if (scope == NotesListScope.Project && ReferenceEquals(thread, _activeThread))
            {
                parts.Add("Current");
            }

            return string.Join(" · ", parts);
        }

        private static string ResolveNoteAccentBrushKey(WorkspacePaneRecord attachedPane)
        {
            return attachedPane?.Kind switch
            {
                WorkspacePaneKind.Browser => "ShellInfoBrush",
                WorkspacePaneKind.Editor => "ShellSuccessBrush",
                WorkspacePaneKind.Diff => "ShellConfigBrush",
                WorkspacePaneKind.Terminal => "ShellTerminalBrush",
                _ => "ShellPaneActiveBorderBrush",
            };
        }

        private Brush ResolveNoteAccentBrush(WorkspacePaneRecord attachedPane)
        {
            return AppBrush(InspectorNotesGroupsItemsControl, ResolveNoteAccentBrushKey(attachedPane));
        }

        private Brush ResolveNoteGroupAccentBrush(
            WorkspaceThread thread,
            IReadOnlyList<WorkspaceThreadNote> activeNotes,
            IReadOnlyList<WorkspaceThreadNote> archivedNotes)
        {
            WorkspaceThreadNote sampleNote = activeNotes?.FirstOrDefault() ?? archivedNotes?.FirstOrDefault();
            WorkspacePaneRecord attachedPane = thread?.Panes.FirstOrDefault(candidate => string.Equals(candidate.Id, sampleNote?.PaneId, StringComparison.Ordinal))
                ?? thread?.Panes.FirstOrDefault(candidate => string.Equals(candidate.Id, thread.SelectedPaneId, StringComparison.Ordinal))
                ?? thread?.Panes.FirstOrDefault();
            return ResolveNoteAccentBrush(attachedPane);
        }

        private InspectorNoteCardItem BuildInspectorNoteCardItem(WorkspaceThread thread, WorkspaceThreadNote note)
        {
            WorkspacePaneRecord attachedPane = thread?.Panes.FirstOrDefault(candidate => string.Equals(candidate.Id, note?.PaneId, StringComparison.Ordinal));
            Brush accentBrush = ResolveNoteAccentBrush(attachedPane);
            bool selected = string.Equals(thread?.SelectedNoteId, note?.Id, StringComparison.Ordinal);
            bool archived = note?.IsArchived == true;
            NoteDraftState draft = ResolveNoteDraftState(note);
            bool dirty = draft?.Dirty == true;
            string noteText = draft?.Text ?? note?.Text;
            bool lightTheme = ResolveTheme(SampleConfig.CurrentTheme) == ElementTheme.Light;
            return new InspectorNoteCardItem
            {
                NoteId = note?.Id,
                ThreadId = thread?.Id,
                Title = ResolveNoteListTitle(note),
                EditableTitle = draft?.EditableTitle ?? ResolveEditableNoteTitle(note),
                TitlePlaceholderText = archived ? string.Empty : "Optional title",
                TitleEditorAutomationId = BuildNoteTitleEditorAutomationId(note?.Id),
                Text = noteText,
                Meta = BuildNoteCardMeta(attachedPane),
                StatusText = archived ? "Read only" : dirty ? "Unsaved changes" : string.Empty,
                TimestampText = archived
                    ? $"Archived {FormatNoteTimestamp(note?.ArchivedAt ?? note?.UpdatedAt ?? DateTimeOffset.UtcNow)}"
                    : dirty
                        ? $"Last saved {FormatNoteTimestamp(note?.UpdatedAt ?? DateTimeOffset.UtcNow)}"
                        : $"Saved {FormatNoteTimestamp(note?.UpdatedAt ?? DateTimeOffset.UtcNow)}",
                ScopeButtonLabel = BuildNoteScopeLabel(attachedPane),
                ScopeToolTip = "Attach this note to the thread or a pane",
                ArchiveButtonLabel = archived ? "Restore" : "Archive",
                ArchiveToolTip = archived ? "Restore this note to the active list" : "Archive this note",
                DeleteToolTip = "Delete this note",
                PlaceholderText = archived ? string.Empty : "Write note",
                EditorAutomationId = BuildNoteEditorAutomationId(note?.Id),
                AccentBrush = accentBrush,
                CardBackground = selected
                    ? CreateSidebarTintedBrush(
                        accentBrush,
                        archived ? (byte)(lightTheme ? 0x06 : 0x07) : (byte)(lightTheme ? 0x0E : 0x0C),
                        Windows.UI.Color.FromArgb(0xFF, 0x14, 0x16, 0x1B))
                    : null,
                CardBorderBrush = null,
                ArchiveButtonVisibility = string.IsNullOrWhiteSpace(noteText) && string.IsNullOrWhiteSpace(draft?.EditableTitle) && !archived
                    ? Visibility.Collapsed
                    : Visibility.Visible,
                IsArchived = archived,
                IsSelected = selected,
            };
        }

        private string BuildNoteCardMeta(WorkspacePaneRecord attachedPane)
        {
            return attachedPane is null
                ? _activeNotesListScope == NotesListScope.Project ? "Thread note" : "Current thread"
                : BuildPaneContextTitle(attachedPane);
        }

        private static string BuildNoteScopeLabel(WorkspacePaneRecord attachedPane)
        {
            return attachedPane?.Kind switch
            {
                WorkspacePaneKind.Browser => "WEB",
                WorkspacePaneKind.Editor => "EDIT",
                WorkspacePaneKind.Diff => "DIFF",
                WorkspacePaneKind.Terminal => "TERM",
                _ => "THREAD",
            };
        }

        private static string BuildPaneContextTitle(WorkspacePaneRecord pane)
        {
            if (pane is null)
            {
                return string.Empty;
            }

            string title = string.IsNullOrWhiteSpace(pane.Title) ? string.Empty : pane.Title.Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                return BuildOverviewPaneLabel(pane);
            }

            string normalized = pane.Kind switch
            {
                WorkspacePaneKind.Browser when title.StartsWith("Web ", StringComparison.OrdinalIgnoreCase) => title[4..],
                WorkspacePaneKind.Editor when title.StartsWith("Edit ", StringComparison.OrdinalIgnoreCase) => title[5..],
                WorkspacePaneKind.Diff when title.StartsWith("Diff ", StringComparison.OrdinalIgnoreCase) => title[5..],
                _ => title,
            };

            normalized = normalized.Trim().TrimEnd('\\', '/');
            int separatorIndex = Math.Max(normalized.LastIndexOf('\\'), normalized.LastIndexOf('/'));
            if (separatorIndex >= 0 && separatorIndex < normalized.Length - 1)
            {
                normalized = normalized[(separatorIndex + 1)..];
            }

            return normalized.Length > 32
                ? normalized[..29] + "..."
                : normalized;
        }

        private static string BuildNotesMeta(WorkspaceProject project, WorkspaceThread thread, NotesListScope scope)
        {
            IEnumerable<WorkspaceThreadNote> notes = scope == NotesListScope.Thread
                ? thread?.NoteEntries ?? Enumerable.Empty<WorkspaceThreadNote>()
                : project?.Threads.SelectMany(candidate => candidate.NoteEntries) ?? Enumerable.Empty<WorkspaceThreadNote>();
            int activeCount = notes.Count(candidate => !candidate.IsArchived);
            int archivedCount = notes.Count(candidate => candidate.IsArchived);
            if (activeCount == 0 && archivedCount == 0)
            {
                return "No notes";
            }

            List<string> parts = new();
            if (activeCount > 0)
            {
                parts.Add($"{activeCount} active");
            }

            if (archivedCount > 0)
            {
                parts.Add($"{archivedCount} archived");
            }

            return string.Join(" · ", parts);
        }

        private static IEnumerable<NoteScopeOption> BuildNoteScopeOptions(WorkspaceThread thread, string noteId)
        {
            yield return new NoteScopeOption
            {
                ThreadId = thread?.Id,
                NoteId = noteId,
                PaneId = null,
                Label = "Thread",
            };

            if (thread is null)
            {
                yield break;
            }

            foreach (WorkspacePaneRecord pane in thread.Panes)
            {
                yield return new NoteScopeOption
                {
                    ThreadId = thread.Id,
                    NoteId = noteId,
                    PaneId = pane.Id,
                    Label = FormatTabHeader(pane.Title, pane.Kind),
                };
            }
        }

        private static DateTimeOffset GetLatestThreadNoteActivity(WorkspaceThread thread)
        {
            return thread?.NoteEntries.Count > 0
                ? thread.NoteEntries.Max(candidate => candidate.UpdatedAt)
                : DateTimeOffset.MinValue;
        }

        private static string FormatNoteTimestamp(DateTimeOffset timestamp)
        {
            return timestamp.ToLocalTime().ToString("MMM d · h:mm tt");
        }

        private static string BuildNoteEditorAutomationId(string noteId)
        {
            return string.IsNullOrWhiteSpace(noteId)
                ? "shell-thread-note-editor"
                : $"shell-thread-note-editor-{noteId}";
        }

        private static string BuildNoteTitleEditorAutomationId(string noteId)
        {
            return string.IsNullOrWhiteSpace(noteId)
                ? "shell-thread-note-title"
                : $"shell-thread-note-title-{noteId}";
        }

        private static string BuildThreadNotePreview(string notes, int maxLength = 72)
        {
            if (string.IsNullOrWhiteSpace(notes))
            {
                return string.Empty;
            }

            StringBuilder builder = new();
            bool lastWasWhitespace = false;
            bool truncated = false;
            foreach (char character in notes)
            {
                if (char.IsWhiteSpace(character))
                {
                    if (builder.Length == 0 || lastWasWhitespace)
                    {
                        continue;
                    }

                    builder.Append(' ');
                    lastWasWhitespace = true;
                }
                else
                {
                    builder.Append(character);
                    lastWasWhitespace = false;
                }

                if (builder.Length >= maxLength)
                {
                    truncated = true;
                    break;
                }
            }

            string preview = builder.ToString().Trim();
            if (preview.Length == 0)
            {
                return string.Empty;
            }

            return truncated
                ? preview[..Math.Max(0, maxLength - 3)].TrimEnd() + "..."
                : preview;
        }

        private static string ResolveNoteListTitle(WorkspaceThreadNote note)
        {
            string normalizedTitle = note?.Title?.Trim();
            if (!IsSystemGeneratedNoteTitle(normalizedTitle))
            {
                return normalizedTitle;
            }

            return "Untitled note";
        }

        private static string ResolveEditableNoteTitle(WorkspaceThreadNote note)
        {
            string normalizedTitle = note?.Title?.Trim();
            return IsSystemGeneratedNoteTitle(normalizedTitle)
                ? string.Empty
                : normalizedTitle;
        }

        private static bool IsSystemGeneratedNoteTitle(string title)
        {
            string normalized = title?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return true;
            }

            if (string.Equals(normalized, "Handoff", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "Note", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!normalized.StartsWith("Note ", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return int.TryParse(normalized["Note ".Length..], out _);
        }

        private static string ExtractFirstNoteLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            foreach (string line in normalized.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0)
                {
                    return trimmed;
                }
            }

            return string.Empty;
        }

        private static string RemoveFirstNoteLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            bool removed = false;
            StringBuilder builder = new();
            foreach (string line in normalized.Split('\n'))
            {
                if (!removed)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    removed = true;
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.AppendLine();
                }

                builder.Append(line);
            }

            return builder.ToString().Trim();
        }

        private string ResolveProjectRailIconBrushKey(WorkspaceProject project)
        {
            if (ReferenceEquals(project, _activeProject))
            {
                return "ShellTextPrimaryBrush";
            }

            bool requiresAttention = false;
            bool isRunning = false;
            bool hasChanges = false;
            foreach (WorkspaceThread thread in project?.Threads ?? Enumerable.Empty<WorkspaceThread>())
            {
                ThreadActivitySummary summary = ResolveThreadActivitySummary(thread);
                requiresAttention |= summary?.RequiresAttention == true;
                isRunning |= summary?.IsRunning == true;
                hasChanges |= thread.ChangedFileCount > 0;
            }

            if (requiresAttention)
            {
                return "ShellSuccessBrush";
            }

            if (isRunning)
            {
                return "ShellInfoBrush";
            }

            if (hasChanges)
            {
                return "ShellWarningBrush";
            }

            return "ShellTextTertiaryBrush";
        }

        private static string ResolveThreadRailIconBrushKey(WorkspaceThread thread, ThreadActivitySummary summary)
        {
            if (summary?.RequiresAttention == true)
            {
                return "ShellSuccessBrush";
            }

            if (summary?.IsRunning == true)
            {
                return "ShellInfoBrush";
            }

            if (thread?.ChangedFileCount > 0)
            {
                return "ShellWarningBrush";
            }

            if ((thread?.NoteEntries.Count ?? 0) > 0)
            {
                return "ShellWarningBrush";
            }

            return "ShellTextTertiaryBrush";
        }

        private FrameworkElement BuildThreadPaneStrip(WorkspaceThread thread)
        {
            StackPanel strip = new()
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
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
                TextBlock overflowBadge = new()
                {
                    Text = $"+{hiddenPaneCount}",
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 9.4,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = AppBrush(strip, "ShellTextSecondaryBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                ToolTipService.SetToolTip(overflowBadge, $"{hiddenPaneCount} additional pane{(hiddenPaneCount == 1 ? string.Empty : "s")} are hidden in this layout.");
                strip.Children.Add(overflowBadge);
            }

            return strip;
        }

        private static FrameworkElement BuildThreadPaneBadge(WorkspacePaneRecord pane, bool selected)
        {
            string accentKey = ResolvePaneAccentBrushKey(pane.Kind);
            Brush accentBrush = AppBrush(null, accentKey);
            FontIcon badge = new()
            {
                Glyph = ResolvePaneKindGlyph(pane.Kind),
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 9.8,
                Foreground = accentBrush,
                Opacity = selected ? 1 : 0.76,
                Margin = new Thickness(0, 0, 4, 0),
            };
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
            TextBlock label = new()
            {
                Text = summary.Label,
                FontSize = 9.9,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = AppBrush(null, accentKey),
                VerticalAlignment = VerticalAlignment.Center,
            };

            ToolTipService.SetToolTip(label, summary.ToolTip);
            return label;
        }

        private static string ResolveThreadSelectionBrushKey(WorkspaceThread thread, ThreadActivitySummary summary)
        {
            WorkspacePaneRecord selectedPane = thread?.Panes.FirstOrDefault(candidate => string.Equals(candidate.Id, thread.SelectedPaneId, StringComparison.Ordinal))
                ?? thread?.Panes.FirstOrDefault();
            if (selectedPane is not null)
            {
                return ResolvePaneAccentBrushKey(selectedPane.Kind);
            }

            if (summary?.RequiresAttention == true)
            {
                return "ShellSuccessBrush";
            }

            if (summary?.IsRunning == true)
            {
                return "ShellInfoBrush";
            }

            return "ShellPaneActiveBorderBrush";
        }

        private static void ApplySidebarThreadButtonState(Button button, WorkspaceThread thread, bool active, ThreadActivitySummary summary, bool hovered)
        {
            string accentKey = ResolveThreadSelectionBrushKey(thread, summary);
            Brush accentBrush = AppBrush(button, accentKey);
            button.BorderThickness = new Thickness(0);
            button.BorderBrush = null;

            if (active)
            {
                button.Background = CreateSidebarTintedBrush(accentBrush, hovered ? (byte)0x18 : (byte)0x12, Windows.UI.Color.FromArgb(0xFF, 0x33, 0x41, 0x55));
                button.Foreground = AppBrush(button, "ShellTextPrimaryBrush");
                return;
            }

            if (hovered)
            {
                button.Background = AppBrush(button, "ShellNavHoverBrush");
                button.Foreground = AppBrush(button, "ShellTextPrimaryBrush");
                return;
            }

            if (summary?.RequiresAttention == true)
            {
                button.Background = null;
                button.BorderBrush = null;
                button.BorderThickness = new Thickness(0);
                button.Foreground = AppBrush(button, "ShellTextPrimaryBrush");
                return;
            }

            if (summary?.IsRunning == true)
            {
                button.Background = null;
                button.BorderBrush = null;
                button.BorderThickness = new Thickness(0);
                button.Foreground = AppBrush(button, "ShellTextPrimaryBrush");
                return;
            }

            button.Background = null;
            button.BorderBrush = null;
            button.BorderThickness = new Thickness(0);
            button.Foreground = AppBrush(button, "ShellTextPrimaryBrush");
        }

        private static Brush CreateSidebarTintedBrush(Brush source, byte alpha, Windows.UI.Color fallbackBaseColor)
        {
            Windows.UI.Color baseColor = source is SolidColorBrush solid
                ? solid.Color
                : fallbackBaseColor;
            return new SolidColorBrush(Windows.UI.Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B));
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
                if (ShouldSuppressSettingsNavigation())
                {
                    return true;
                }

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

            if (automationId.StartsWith("shell-tab-thread-note-", StringComparison.Ordinal))
            {
                switch (action)
                {
                    case "notes":
                    case "edit thread note":
                    case "open notes":
                    case "note":
                    {
                        WorkspaceThread thread = FindThreadForPane(targetId);
                        if (thread is null)
                        {
                            return false;
                        }

                        WorkspaceThreadNote note = thread.NoteEntries.FirstOrDefault(candidate => string.Equals(candidate.PaneId, targetId, StringComparison.Ordinal));
                        if (note is not null)
                        {
                            thread.SelectedNoteId = note.Id;
                            ClearInspectorNoteDraft();
                            OpenThreadNotes(thread, scope: NotesListScope.Thread);
                            return true;
                        }

                        StartInspectorNoteDraft(thread, targetId, scope: NotesListScope.Thread);
                        return true;
                    }
                    default:
                        return false;
                }
            }

            if (automationId.StartsWith("shell-tab-pane-note-", StringComparison.Ordinal))
            {
                switch (action)
                {
                    case "note":
                    case "notes":
                    case "attach note":
                    case "new note":
                    case "new note for this pane":
                    {
                        WorkspaceThread thread = FindThreadForPane(targetId);
                        if (thread is null)
                        {
                            return false;
                        }

                        WorkspaceThreadNote note = thread.NoteEntries.FirstOrDefault(candidate => string.Equals(candidate.PaneId, targetId, StringComparison.Ordinal));
                        if (note is not null)
                        {
                            thread.SelectedNoteId = note.Id;
                            ClearInspectorNoteDraft();
                            OpenThreadNotes(thread, scope: NotesListScope.Thread);
                            return true;
                        }

                        StartInspectorNoteDraft(thread, targetId, scope: NotesListScope.Thread);
                        return true;
                    }
                    default:
                        return false;
                }
            }

            switch (action)
            {
                case "rename":
                    _ = BeginRenameThreadAsync(targetId);
                    return true;
                case "notes":
                case "open notes":
                case "edit note":
                case "note":
                {
                    WorkspaceThread thread = FindThread(targetId);
                    if (thread is null)
                    {
                        return false;
                    }

                    if (thread.NoteEntries.Count == 0)
                    {
                        StartInspectorNoteDraft(thread, scope: NotesListScope.Thread);
                        return true;
                    }

                    ClearInspectorNoteDraft();
                    OpenThreadNotes(thread, scope: NotesListScope.Thread);
                    return true;
                }
                case "duplicate":
                    DuplicateThread(targetId);
                    return true;
                case "clear thread":
                case "delete":
                    DeleteThread(targetId);
                    return true;
                case "new note":
                {
                    WorkspaceThread thread = FindThread(targetId);
                    if (thread is null)
                    {
                        return false;
                    }

                    StartInspectorNoteDraft(thread, scope: NotesListScope.Thread);
                    return true;
                }
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
                case "notes":
                case "open notes":
                case "edit note":
                case "note":
                {
                    WorkspaceThread thread = FindThread(threadId);
                    if (thread is null)
                    {
                        return false;
                    }

                    if (thread.NoteEntries.Count == 0)
                    {
                        StartInspectorNoteDraft(thread, scope: NotesListScope.Thread);
                        return true;
                    }

                    ClearInspectorNoteDraft();
                    OpenThreadNotes(thread, scope: NotesListScope.Thread);
                    return true;
                }
                case "new note":
                {
                    WorkspaceThread thread = FindThread(threadId);
                    if (thread is null)
                    {
                        return false;
                    }

                    StartInspectorNoteDraft(thread, scope: NotesListScope.Thread);
                    return true;
                }
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
            List<NativeAutomationTabState> tabStates = thread.Panes.Select(tab => new NativeAutomationTabState
            {
                Id = tab.Id,
                Kind = tab.Kind.ToString().ToLowerInvariant(),
                Title = FormatTabHeader(tab.Title, tab.Kind),
                Exited = tab.IsExited,
            }).ToList();
            List<NativeAutomationThreadNoteState> noteStates = BuildThreadNoteStates(thread).ToList();

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
                AutoFitPaneContentLocked = thread.AutoFitPaneContentLocked,
                ZoomedPaneId = thread.ZoomedPaneId,
                PaneLimit = thread.PaneLimit,
                VisiblePaneCapacity = thread.VisiblePaneCapacity,
                PrimarySplitRatio = thread.PrimarySplitRatio,
                SecondarySplitRatio = thread.SecondarySplitRatio,
                ChangedFileCount = thread.ChangedFileCount,
                HasNotes = thread.NoteEntries.Count > 0,
                NoteCount = thread.NoteEntries.Count,
                SelectedNoteId = thread.SelectedNoteId,
                NotePreview = BuildThreadNotePreview(ResolvePreferredThreadNote(thread)?.Text),
                DiffReviewSource = FormatDiffReviewSource(thread.DiffReviewSource),
                SelectedCheckpointId = thread.SelectedCheckpointId,
                CheckpointCount = thread.DiffCheckpoints.Count,
                Tabs = tabStates,
                Panes = tabStates,
                Notes = noteStates,
            };
        }

        private static IEnumerable<NativeAutomationThreadNoteState> BuildThreadNoteStates(WorkspaceThread thread)
        {
            if (thread is null)
            {
                yield break;
            }

            foreach (WorkspaceThreadNote note in thread.NoteEntries)
            {
                WorkspacePaneRecord attachedPane = thread.Panes.FirstOrDefault(candidate => string.Equals(candidate.Id, note.PaneId, StringComparison.Ordinal));
                yield return new NativeAutomationThreadNoteState
                {
                    Id = note.Id,
                    Title = note.Title,
                    Text = note.Text,
                    Preview = BuildThreadNotePreview(note.Text),
                    ProjectId = thread.Project?.Id,
                    ProjectName = thread.Project?.Name,
                    ThreadId = thread.Id,
                    ThreadName = thread.Name,
                    PaneId = note.PaneId,
                    PaneTitle = attachedPane is null ? null : FormatTabHeader(attachedPane.Title, attachedPane.Kind),
                    Selected = string.Equals(thread.SelectedNoteId, note.Id, StringComparison.Ordinal),
                    Archived = note.IsArchived,
                    UpdatedAt = note.UpdatedAt.ToString("O"),
                    ArchivedAt = note.ArchivedAt?.ToString("O"),
                };
            }
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
                (ElementTheme.Light, "ShellPageBackgroundBrush") => Windows.UI.Color.FromArgb(0xFF, 0xF2, 0xF5, 0xF8),
                (ElementTheme.Light, "ShellPaneBackgroundBrush") => Windows.UI.Color.FromArgb(0xFF, 0xF2, 0xF5, 0xF8),
                (ElementTheme.Light, "ShellSurfaceBackgroundBrush") => Windows.UI.Color.FromArgb(0xFF, 0xF9, 0xFB, 0xFD),
                (ElementTheme.Light, "ShellMutedSurfaceBrush") => Windows.UI.Color.FromArgb(0xFF, 0xF2, 0xF6, 0xFA),
                (ElementTheme.Light, "ShellBrandMarkBackgroundBrush") => Windows.UI.Color.FromArgb(0xFF, 0xF9, 0xFB, 0xFD),
                (ElementTheme.Light, "ShellBrandMarkBorderBrush") => Windows.UI.Color.FromArgb(0xFF, 0xCC, 0xD6, 0xE0),
                (ElementTheme.Light, "ShellPaneDividerBrush") => Windows.UI.Color.FromArgb(0xFF, 0xD3, 0xDE, 0xE8),
                (ElementTheme.Light, "ShellNavHoverBrush") => Windows.UI.Color.FromArgb(0xFF, 0xE7, 0xEE, 0xF5),
                (ElementTheme.Light, "ShellNavActiveBrush") => Windows.UI.Color.FromArgb(0xFF, 0xDF, 0xE8, 0xF1),
                (ElementTheme.Light, "ShellBorderBrush") => Windows.UI.Color.FromArgb(0xFF, 0xC7, 0xD2, 0xDD),
                (ElementTheme.Light, "ShellPaneActiveBorderBrush") => Windows.UI.Color.FromArgb(0xFF, 0x33, 0x41, 0x55),
                (ElementTheme.Light, "ShellTextPrimaryBrush") => Windows.UI.Color.FromArgb(0xFF, 0x1A, 0x1E, 0x23),
                (ElementTheme.Light, "ShellTextSecondaryBrush") => Windows.UI.Color.FromArgb(0xFF, 0x39, 0x42, 0x4D),
                (ElementTheme.Light, "ShellTextTertiaryBrush") => Windows.UI.Color.FromArgb(0xFF, 0x55, 0x60, 0x6D),
                (ElementTheme.Light, "ShellSuccessBrush") => Windows.UI.Color.FromArgb(0xFF, 0x16, 0xA3, 0x4A),
                (ElementTheme.Light, "ShellWarningBrush") => Windows.UI.Color.FromArgb(0xFF, 0xCA, 0x8A, 0x04),
                (ElementTheme.Light, "ShellDangerBrush") => Windows.UI.Color.FromArgb(0xFF, 0xDC, 0x26, 0x26),
                (ElementTheme.Light, "ShellInfoBrush") => Windows.UI.Color.FromArgb(0xFF, 0x25, 0x63, 0xEB),
                (ElementTheme.Light, "ShellTerminalBrush") => Windows.UI.Color.FromArgb(0xFF, 0x0F, 0x76, 0x6E),
                (ElementTheme.Light, "ShellConfigBrush") => Windows.UI.Color.FromArgb(0xFF, 0x47, 0x55, 0x69),
                (ElementTheme.Dark, "ShellPageBackgroundBrush") => Windows.UI.Color.FromArgb(0xFF, 0x0C, 0x0D, 0x10),
                (ElementTheme.Dark, "ShellPaneBackgroundBrush") => Windows.UI.Color.FromArgb(0xFF, 0x0C, 0x0D, 0x10),
                (ElementTheme.Dark, "ShellSurfaceBackgroundBrush") => Windows.UI.Color.FromArgb(0xFF, 0x10, 0x12, 0x16),
                (ElementTheme.Dark, "ShellMutedSurfaceBrush") => Windows.UI.Color.FromArgb(0xFF, 0x14, 0x16, 0x1B),
                (ElementTheme.Dark, "ShellBrandMarkBackgroundBrush") => Windows.UI.Color.FromArgb(0xFF, 0x13, 0x16, 0x1B),
                (ElementTheme.Dark, "ShellBrandMarkBorderBrush") => Windows.UI.Color.FromArgb(0xFF, 0x26, 0x2B, 0x33),
                (ElementTheme.Dark, "ShellPaneDividerBrush") => Windows.UI.Color.FromArgb(0xFF, 0x1A, 0x1D, 0x23),
                (ElementTheme.Dark, "ShellNavHoverBrush") => Windows.UI.Color.FromArgb(0xFF, 0x13, 0x16, 0x1B),
                (ElementTheme.Dark, "ShellNavActiveBrush") => Windows.UI.Color.FromArgb(0xFF, 0x17, 0x1A, 0x20),
                (ElementTheme.Dark, "ShellBorderBrush") => Windows.UI.Color.FromArgb(0xFF, 0x1E, 0x21, 0x27),
                (ElementTheme.Dark, "ShellPaneActiveBorderBrush") => Windows.UI.Color.FromArgb(0xFF, 0xC9, 0xCD, 0xD4),
                (ElementTheme.Dark, "ShellTextPrimaryBrush") => Windows.UI.Color.FromArgb(0xFF, 0xF3, 0xF4, 0xF6),
                (ElementTheme.Dark, "ShellTextSecondaryBrush") => Windows.UI.Color.FromArgb(0xFF, 0xA7, 0xAD, 0xB7),
                (ElementTheme.Dark, "ShellTextTertiaryBrush") => Windows.UI.Color.FromArgb(0xFF, 0x7A, 0x80, 0x8B),
                (ElementTheme.Dark, "ShellSuccessBrush") => Windows.UI.Color.FromArgb(0xFF, 0x4A, 0xDE, 0x80),
                (ElementTheme.Dark, "ShellWarningBrush") => Windows.UI.Color.FromArgb(0xFF, 0xFB, 0xBF, 0x24),
                (ElementTheme.Dark, "ShellDangerBrush") => Windows.UI.Color.FromArgb(0xFF, 0xF8, 0x71, 0x71),
                (ElementTheme.Dark, "ShellInfoBrush") => Windows.UI.Color.FromArgb(0xFF, 0x60, 0xA5, 0xFA),
                (ElementTheme.Dark, "ShellTerminalBrush") => Windows.UI.Color.FromArgb(0xFF, 0x2D, 0xD4, 0xBF),
                (ElementTheme.Dark, "ShellConfigBrush") => Windows.UI.Color.FromArgb(0xFF, 0x94, 0xA3, 0xB8),
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
                DiffFileListItem item => item.File,
                Button button when button.Tag is GitChangedFile changedFile => changedFile,
                Button button when button.Tag is DiffFileListItem item => item.File,
                ListViewItem item when item.Tag is GitChangedFile changedFile => changedFile,
                _ => null,
            };
        }

        private void ApplyDiffFileButtonState(Button button, GitChangedFile changedFile, bool active, bool hovered = false)
        {
            if (button is null)
            {
                return;
            }

            bool lightTheme = ResolveTheme(SampleConfig.CurrentTheme) == ElementTheme.Light;
            Brush accentBrush = changedFile is not null
                ? AppBrush(button, ResolveInspectorIconBrushKey(changedFile.Path, isDirectory: false, decoration: null))
                : AppBrush(button, "ShellPaneActiveBorderBrush");
            button.Background = active
                ? CreateSidebarTintedBrush(accentBrush, lightTheme ? (byte)0x16 : (byte)0x12, Windows.UI.Color.FromArgb(0xFF, 0x33, 0x41, 0x55))
                : hovered
                    ? AppBrush(button, "ShellNavHoverBrush")
                    : null;
            button.BorderBrush = null;
            button.BorderThickness = new Thickness(0);
            button.Foreground = active || hovered ? AppBrush(button, "ShellTextPrimaryBrush") : AppBrush(button, "ShellTextSecondaryBrush");
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
            button.Background = active
                ? CreateSidebarTintedBrush(AppBrush(button, "ShellPaneActiveBorderBrush"), 0x10, Windows.UI.Color.FromArgb(0xFF, 0x2C, 0x2D, 0x31))
                : null;
            button.BorderBrush = null;
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

        private static void ApplyPaneStripButtonState(Button button, string accentKey, bool active = false, bool hovered = false)
        {
            if (button is null)
            {
                return;
            }

            Brush accentBrush = AppBrush(button, accentKey);
            button.Background = active
                ? CreateSidebarTintedBrush(accentBrush, 0x12, Windows.UI.Color.FromArgb(0xFF, 0x33, 0x41, 0x55))
                : hovered
                    ? CreateSidebarTintedBrush(accentBrush, 0x10, Windows.UI.Color.FromArgb(0xFF, 0x33, 0x41, 0x55))
                    : null;
            button.BorderBrush = active || hovered
                ? CreateSidebarTintedBrush(accentBrush, hovered && !active ? (byte)0x2E : (byte)0x2A, Windows.UI.Color.FromArgb(0xFF, 0x33, 0x41, 0x55))
                : AppBrush(button, "ShellBorderBrush");
            button.BorderThickness = new Thickness(1);
            button.Foreground = hovered ? AppBrush(button, "ShellTextPrimaryBrush") : accentBrush;
            button.Opacity = hovered || active ? 1.0 : 0.92;
            if (button.Content is FontIcon icon)
            {
                icon.ClearValue(IconElement.ForegroundProperty);
                icon.Opacity = hovered || active ? 1.0 : 0.92;
            }
        }

        private static void ApplySidebarActionLayout(Button button, bool isOpen)
        {
            if (button is null)
            {
                return;
            }

            button.Width = isOpen ? double.NaN : 32;
            button.Padding = isOpen ? new Thickness(5, 3, 5, 3) : new Thickness(0);
            button.HorizontalAlignment = isOpen ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
            button.HorizontalContentAlignment = isOpen ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
            button.Margin = isOpen ? new Thickness(0) : new Thickness(6, 0, 0, 0);
        }

        private static void ApplyProjectButtonState(Button button, bool active, bool hovered)
        {
            if (active)
            {
                button.Background = CreateSidebarTintedBrush(AppBrush(button, "ShellPaneActiveBorderBrush"), hovered ? (byte)0x14 : (byte)0x0E, Windows.UI.Color.FromArgb(0xFF, 0x33, 0x41, 0x55));
                button.BorderBrush = null;
                button.BorderThickness = new Thickness(0);
            }
            else if (hovered)
            {
                button.Background = AppBrush(button, "ShellNavHoverBrush");
                button.BorderBrush = null;
                button.BorderThickness = new Thickness(0);
            }
            else
            {
                button.Background = null;
                button.BorderBrush = null;
                button.BorderThickness = new Thickness(0);
            }
            button.Foreground = AppBrush(button, "ShellTextPrimaryBrush");
        }

        private static void ApplyInspectorTabButtonState(Button button, bool active)
        {
            if (button is null)
            {
                return;
            }

            Brush accentBrush = AppBrush(button, "ShellPaneActiveBorderBrush");
            if (active)
            {
                button.Background = CreateSidebarTintedBrush(accentBrush, 0x14, Windows.UI.Color.FromArgb(0xFF, 0x33, 0x41, 0x55));
                button.BorderBrush = CreateSidebarTintedBrush(accentBrush, 0x2E, Windows.UI.Color.FromArgb(0xFF, 0x33, 0x41, 0x55));
                button.BorderThickness = new Thickness(1);
                button.Foreground = AppBrush(button, "ShellTextPrimaryBrush");
                return;
            }

            button.Background = null;
            button.BorderBrush = AppBrush(button, "ShellBorderBrush");
            button.BorderThickness = new Thickness(1);
            button.Foreground = AppBrush(button, "ShellTextSecondaryBrush");
        }

        private static void ApplyThreadButtonState(Button button, bool active)
        {
            button.Background = active ? AppBrush(button, "ShellNavActiveBrush") : null;
            button.BorderBrush = null;
            button.BorderThickness = new Thickness(0);
            button.Foreground = active ? AppBrush(button, "ShellTextPrimaryBrush") : AppBrush(button, "ShellTextSecondaryBrush");
        }

        private static string ResolveRequestedPath(string rootPath)
        {
            return ShellProfiles.NormalizeProjectPath(rootPath);
        }

        private static bool TryResolveRestorableProjectPath(string rootPath, out string normalizedPath, out string unavailablePath)
        {
            normalizedPath = ResolveRequestedPath(rootPath);
            unavailablePath = null;
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                unavailablePath = rootPath;
                return false;
            }

            string pathToCheck = normalizedPath;
            if (ShellProfiles.TryResolveLocalStoragePath(normalizedPath, out string localStoragePath))
            {
                pathToCheck = localStoragePath;
            }
            else if (normalizedPath.StartsWith("/", StringComparison.Ordinal))
            {
                // WSL Linux paths are valid project roots even though Win32 Directory.Exists
                // cannot verify them directly from the host process.
                return true;
            }

            if (Directory.Exists(pathToCheck))
            {
                return true;
            }

            unavailablePath = pathToCheck;
            return false;
        }

        private static string ResolveWorkspaceBootstrapPath()
        {
            string currentDirectory = ResolveRequestedPath(Environment.CurrentDirectory);
            if (!LooksLikeInstalledAppDirectory(currentDirectory))
            {
                return currentDirectory;
            }

            string documentsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrWhiteSpace(documentsDirectory) && Directory.Exists(documentsDirectory))
            {
                return ResolveRequestedPath(documentsDirectory);
            }

            string homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(homeDirectory) && Directory.Exists(homeDirectory))
            {
                return ResolveRequestedPath(homeDirectory);
            }

            return currentDirectory;
        }

        private static bool LooksLikeInstalledAppDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            string normalizedPath = ResolveRequestedPath(path);
            string baseDirectory = ResolveRequestedPath(AppContext.BaseDirectory);
            if (string.Equals(normalizedPath, baseDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return File.Exists(Path.Combine(normalizedPath, "WinMux.exe")) ||
                File.Exists(Path.Combine(normalizedPath, "SelfContainedDeployment.exe"));
        }

        private static bool LooksLikePath(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && (value.Contains('\\') || value.Contains('/') || value.Contains(':'));
        }

        private static string FormatProjectPath(WorkspaceProject project)
        {
            return project.DisplayPath;
        }

        private static bool IsFeatureEnabled(string environmentVariable)
        {
            string raw = Environment.GetEnvironmentVariable(environmentVariable);
            return string.Equals(raw, "1", StringComparison.Ordinal) ||
                string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
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
                    .Append(project.Threads.Count)
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
                    List<WorkspacePaneRecord> visiblePanes = GetVisiblePanes(thread).ToList();
                    builder.Append(thread.Id)
                        .Append(':')
                        .Append(thread.Name)
                        .Append(':')
                        .Append(thread.BranchName)
                        .Append(':')
                        .Append(thread.WorktreePath)
                        .Append(':')
                        .Append(thread.ChangedFileCount)
                        .Append(':')
                        .Append(thread.SelectedPaneId)
                        .Append(':')
                        .Append(thread.ZoomedPaneId)
                        .Append(':')
                        .Append(thread.VisiblePaneCapacity)
                        .Append(':')
                        .Append(thread.NoteEntries.Count)
                        .Append('|');

                    ThreadActivitySummary summary = ResolveThreadActivitySummary(thread);
                    AppendThreadActivitySummaryKey(builder, summary);
                    builder.Append('|');

                    foreach (WorkspacePaneRecord pane in visiblePanes)
                    {
                        builder.Append(pane.Id)
                            .Append(',')
                            .Append(pane.Kind)
                            .Append(',')
                            .Append(string.Equals(thread.SelectedPaneId, pane.Id, StringComparison.Ordinal) ? '1' : '0')
                            .Append('|');
                    }

                    int hiddenPaneCount = Math.Max(0, thread.Panes.Count - visiblePanes.Count);
                    builder.Append("hidden:")
                        .Append(hiddenPaneCount)
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
                .Append(_activeThread?.Id)
                .Append('|');

            if (_activeThread is null)
            {
                return builder.ToString();
            }

            builder.Append(_activeThread.ZoomedPaneId)
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
                    .Append(pane.IsDeferred ? '1' : '0')
                    .Append('|');
            }

            return builder.ToString();
        }

        private static void AppendThreadActivitySummaryKey(StringBuilder builder, ThreadActivitySummary summary)
        {
            if (summary is null)
            {
                builder.Append("none");
                return;
            }

            builder.Append(summary.Label)
                .Append(':')
                .Append(summary.RequiresAttention ? '1' : '0')
                .Append(':')
                .Append(summary.IsRunning ? '1' : '0');
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
                    _ => "terminal",
                };
            }
            else
            {
                string trimmed = title.Trim().TrimEnd('\\', '/');
                if (kind == WorkspacePaneKind.Terminal && LooksLikePath(trimmed))
                {
                    nextTitle = "terminal";
                }
                else
                {
                    int slashIndex = Math.Max(trimmed.LastIndexOf('\\'), trimmed.LastIndexOf('/'));
                    nextTitle = slashIndex >= 0 && slashIndex < trimmed.Length - 1
                        ? trimmed[(slashIndex + 1)..]
                        : trimmed;
                }
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
