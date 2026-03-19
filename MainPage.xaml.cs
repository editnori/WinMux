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
        private static readonly bool DisableBrowserPaneWarmup = IsFeatureEnabled("WINMUX_DISABLE_BROWSER_PANE_WARMUP");
        private static readonly bool EnableBrowserPaneWarmup = IsFeatureEnabled("WINMUX_ENABLE_BROWSER_PANE_WARMUP") || !DisableBrowserPaneWarmup;
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
        private sealed class BrowserPaneBinding
        {
            public EventHandler InteractionRequested { get; init; }

            public EventHandler<string> OpenPaneRequested { get; init; }

            public EventHandler<string> TitleChanged { get; init; }

            public EventHandler StateChanged { get; init; }
        }

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
        private static readonly TimeSpan CrossPaneInteractionSuppressionWindow = TimeSpan.FromMilliseconds(140);
        private DateTimeOffset _ignoreNonSelectedPaneInteractionUntil;
        private DateTimeOffset _suppressSettingsUntil;
        private bool _settingsPageNeedsRefresh = true;
        private bool _settingsPagePreloadStarted;
        private bool _browserPaneWarmupStarted;
        private string _inlineRenamingPaneId;
        private bool _restoringSession;
        private const double InspectorCompactPaneWidth = 40;
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
        private int _latestSelectedDiffRequestId;
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
        private string _lastHeaderRenderKey;
        private string _lastInspectorNotesRenderKey;
        private bool _projectTreeRefreshEnqueued;
        private bool _suppressDiffReviewSourceSelectionChanged;
        private bool _capturingDiffCheckpoint;
        private NotesListScope _activeNotesListScope = NotesListScope.Thread;
        private InspectorSection _activeInspectorSection = InspectorSection.Review;
        private string _lastInspectorDirectoryRootPath;
        private string _pendingInspectorDirectoryRootPath;
        private string _pendingInspectorDirectoryRenderKey;
        private string _pendingInspectorDirectoryWarmupRootPath;
        private string _pendingInspectorDirectoryWarmupRenderKey;
        private int _latestInspectorDirectoryBuildRequestId;
        private int _latestInspectorDirectoryWarmupRequestId;
        private System.Threading.CancellationTokenSource _inspectorDirectoryBuildCancellation;
        private int _latestPaneWarmupRequestId;
        private readonly Dictionary<string, Button> _projectButtonsById = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Border> _projectHeaderBordersById = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Button> _threadButtonsById = new(StringComparer.Ordinal);
        private readonly Dictionary<BrowserPaneControl, BrowserPaneBinding> _browserPaneBindings = new();
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
        private BrowserPaneControl _browserWarmupPane;

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
            InspectorFilesView.DirectoryItemInvoked += OnInspectorDirectoryItemInvoked;
            InspectorReviewView.ReviewSourceSelectionChanged += OnDiffReviewSourceSelectionChanged;
            InspectorReviewView.DiffFileButtonClicked += OnDiffFileButtonClicked;
            InspectorReviewView.DiffFileItemButtonPointerEntered += OnDiffFileItemButtonPointerEntered;
            InspectorReviewView.DiffFileItemButtonPointerExited += OnDiffFileItemButtonPointerExited;
            InspectorReviewView.DiffFileItemButtonLoaded += OnDiffFileItemButtonLoaded;
            InspectorReviewView.DiffFileItemButtonUnloaded += OnDiffFileItemButtonUnloaded;
            InspectorNotesView.ThreadScopeClicked += OnInspectorNotesThreadScopeClicked;
            InspectorNotesView.ProjectScopeClicked += OnInspectorNotesProjectScopeClicked;
            InspectorNotesView.AddNoteClicked += OnInspectorAddNoteClicked;
            InspectorNotesView.SaveNoteClicked += OnInspectorSaveNoteClicked;
            InspectorNotesView.DeleteNoteClicked += OnInspectorDeleteNoteClicked;
            InspectorNotesView.ArchivedNotesToggleClicked += OnInspectorArchivedNotesToggleClicked;
            InspectorNotesView.NoteCardTapped += OnInspectorNoteCardTapped;
            InspectorNotesView.NoteCardDoubleTapped += OnInspectorNoteCardDoubleTapped;
            InspectorNotesView.NoteCardPointerEntered += OnInspectorNoteCardPointerEntered;
            InspectorNotesView.NoteCardPointerExited += OnInspectorNoteCardPointerExited;
            InspectorNotesView.NoteCardTitleChanged += OnInspectorNoteCardTitleChanged;
            InspectorNotesView.NoteCardTextChanged += OnInspectorNoteCardTextChanged;
            InspectorNotesView.NoteCardTextBoxGotFocus += OnInspectorNoteCardTextBoxGotFocus;
            InspectorNotesView.NoteCardTextBoxLostFocus += OnInspectorNoteCardTextBoxLostFocus;
            InspectorNotesView.NoteCardTextBoxKeyDown += OnInspectorNoteCardTextBoxKeyDown;
            InspectorNotesView.NoteScopeButtonClicked += OnInspectorNoteScopeButtonClicked;
            InspectorNotesView.SaveNoteCardClicked += OnInspectorSaveNoteCardClicked;
            InspectorNotesView.ArchiveNoteButtonClicked += OnInspectorArchiveNoteButtonClicked;
            InspectorNotesView.DeleteNoteCardClicked += OnInspectorDeleteNoteCardClicked;
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
            InspectorFilesView.DirectoryItemInvoked -= OnInspectorDirectoryItemInvoked;
            InspectorReviewView.ReviewSourceSelectionChanged -= OnDiffReviewSourceSelectionChanged;
            InspectorReviewView.DiffFileButtonClicked -= OnDiffFileButtonClicked;
            InspectorReviewView.DiffFileItemButtonPointerEntered -= OnDiffFileItemButtonPointerEntered;
            InspectorReviewView.DiffFileItemButtonPointerExited -= OnDiffFileItemButtonPointerExited;
            InspectorReviewView.DiffFileItemButtonLoaded -= OnDiffFileItemButtonLoaded;
            InspectorReviewView.DiffFileItemButtonUnloaded -= OnDiffFileItemButtonUnloaded;
            InspectorNotesView.ThreadScopeClicked -= OnInspectorNotesThreadScopeClicked;
            InspectorNotesView.ProjectScopeClicked -= OnInspectorNotesProjectScopeClicked;
            InspectorNotesView.AddNoteClicked -= OnInspectorAddNoteClicked;
            InspectorNotesView.SaveNoteClicked -= OnInspectorSaveNoteClicked;
            InspectorNotesView.DeleteNoteClicked -= OnInspectorDeleteNoteClicked;
            InspectorNotesView.ArchivedNotesToggleClicked -= OnInspectorArchivedNotesToggleClicked;
            InspectorNotesView.NoteCardTapped -= OnInspectorNoteCardTapped;
            InspectorNotesView.NoteCardDoubleTapped -= OnInspectorNoteCardDoubleTapped;
            InspectorNotesView.NoteCardPointerEntered -= OnInspectorNoteCardPointerEntered;
            InspectorNotesView.NoteCardPointerExited -= OnInspectorNoteCardPointerExited;
            InspectorNotesView.NoteCardTitleChanged -= OnInspectorNoteCardTitleChanged;
            InspectorNotesView.NoteCardTextChanged -= OnInspectorNoteCardTextChanged;
            InspectorNotesView.NoteCardTextBoxGotFocus -= OnInspectorNoteCardTextBoxGotFocus;
            InspectorNotesView.NoteCardTextBoxLostFocus -= OnInspectorNoteCardTextBoxLostFocus;
            InspectorNotesView.NoteCardTextBoxKeyDown -= OnInspectorNoteCardTextBoxKeyDown;
            InspectorNotesView.NoteScopeButtonClicked -= OnInspectorNoteScopeButtonClicked;
            InspectorNotesView.SaveNoteCardClicked -= OnInspectorSaveNoteCardClicked;
            InspectorNotesView.ArchiveNoteButtonClicked -= OnInspectorArchiveNoteButtonClicked;
            InspectorNotesView.DeleteNoteCardClicked -= OnInspectorDeleteNoteCardClicked;
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
            _latestInspectorDirectoryWarmupRequestId++;
            _paneFocusRequestId++;
            _gitRefreshPending = false;
            _gitRefreshInFlight = false;
            _pendingGitCorrelationId = null;
            _pendingInspectorDirectoryWarmupRootPath = null;
            _pendingInspectorDirectoryWarmupRenderKey = null;
            _inspectorDirectoryBuildCancellation?.Cancel();
            _inspectorDirectoryBuildCancellation?.Dispose();
            _inspectorDirectoryBuildCancellation = null;
            if (_browserWarmupPane is not null)
            {
                BrowserWarmupHost?.Children.Clear();
                _browserWarmupPane.DisposePane();
                _browserWarmupPane = null;
            }
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
                    if (pane is BrowserPaneRecord browserPane)
                    {
                        DetachBrowserPane(browserPane.Browser);
                    }

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

            _browserPaneBindings.Clear();
        }

        private static void LogAutomationEvent(string category, string name, string message = null, IReadOnlyDictionary<string, string> data = null)
        {
            NativeAutomationEventLog.Record(category, name, message, data);
        }

        public void ApplyTheme(ElementTheme theme)
        {
            if (SampleConfig.CurrentTheme == theme && ShellRoot.RequestedTheme == theme)
            {
                ShellTheme.ApplyThemePackResources(SampleConfig.CurrentThemePackId);
                ((App)Application.Current).MainWindowInstance?.ApplyChromeTheme(theme);
                return;
            }

            SampleConfig.CurrentTheme = theme;
            ApplyShellAppearance(queueProjectTreeRefresh: false);
            ElementTheme effectiveTheme = ResolveTheme(theme);
            LogAutomationEvent("shell", "theme.changed", $"Theme mode set to {WorkspaceSessionStore.FormatTheme(theme)}", new Dictionary<string, string>
            {
                ["themeMode"] = WorkspaceSessionStore.FormatTheme(theme),
                ["theme"] = effectiveTheme.ToString().ToLowerInvariant(),
                ["themePack"] = SampleConfig.CurrentThemePackId,
            });
            QueueSessionSave();
        }

        public void ApplyThemePack(string packId)
        {
            string normalizedPackId = ShellTheme.NormalizePackId(packId);
            if (string.Equals(SampleConfig.CurrentThemePackId, normalizedPackId, StringComparison.OrdinalIgnoreCase))
            {
                ShellTheme.ApplyThemePackResources(normalizedPackId);
                ((App)Application.Current).MainWindowInstance?.ApplyChromeTheme(SampleConfig.CurrentTheme);
                return;
            }

            SampleConfig.CurrentThemePackId = normalizedPackId;
            ApplyShellAppearance(queueProjectTreeRefresh: false);
            ElementTheme effectiveTheme = ResolveTheme(SampleConfig.CurrentTheme);
            LogAutomationEvent("shell", "theme.pack.changed", $"Theme pack set to {normalizedPackId}", new Dictionary<string, string>
            {
                ["themeMode"] = WorkspaceSessionStore.FormatTheme(SampleConfig.CurrentTheme),
                ["theme"] = effectiveTheme.ToString().ToLowerInvariant(),
                ["themePack"] = normalizedPackId,
            });
            QueueSessionSave();
        }

        private void ApplyShellAppearance(bool queueProjectTreeRefresh)
        {
            ElementTheme effectiveTheme = ResolveTheme(SampleConfig.CurrentTheme);
            ShellTheme.ApplyThemePackResources(SampleConfig.CurrentThemePackId);
            ShellRoot.RequestedTheme = SampleConfig.CurrentTheme;
            SettingsFrame.RequestedTheme = SampleConfig.CurrentTheme;
            ((App)Application.Current).MainWindowInstance?.ApplyChromeTheme(SampleConfig.CurrentTheme);
            _settingsPageNeedsRefresh = !_showingSettings;

            if (queueProjectTreeRefresh)
            {
                QueueProjectTreeRefresh();
            }
            else
            {
                RefreshProjectTree();
            }

            UpdateSidebarActions();
            UpdateHeader();
            ApplyThemeToAllTerminals(effectiveTheme);
            ApplyGitSnapshotToUi();
            UpdateInspectorSectionChrome();
            RefreshInspectorFileBrowser(forceRebuild: true);
            _lastPaneWorkspaceRenderKey = null;
            RenderPaneWorkspace();
            PaneWorkspaceGrid.Background = AppBrush(PaneWorkspaceGrid, "ShellPaneDividerBrush");
            StartShellThemeTransition();
            PlayShellLayoutTransition(includeSidebar: ShellSplitView?.IsPaneOpen == true, includeInspector: _inspectorOpen);
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
                ThemeMode = WorkspaceSessionStore.FormatTheme(SampleConfig.CurrentTheme),
                ThemePack = SampleConfig.CurrentThemePackId,
                PaneOpen = ShellSplitView.IsPaneOpen,
                InspectorOpen = _inspectorOpen,
                InspectorSection = FormatInspectorSection(_activeInspectorSection),
                NotesScope = _activeNotesListScope == NotesListScope.Project ? "project" : "thread",
                ShellProfileId = _activeProject?.ShellProfileId,
                BrowserCredentialCount = BrowserCredentialStore.GetCredentialCount(),
                GitBranch = displayedSnapshot?.BranchName ?? _activeThread?.BranchName,
                WorktreePath = displayedSnapshot?.WorktreePath ?? _activeThread?.WorktreePath,
                ChangedFileCount = displayedSnapshot?.EffectiveChangedFileCount ?? _activeThread?.ChangedFileCount ?? 0,
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
                        ShowSettings(bypassSuppression: true);
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
                        if (ShellTheme.IsKnownPack(request.Value))
                        {
                            ApplyThemePack(request.Value);
                        }
                        else
                        {
                            ApplyTheme(ParseTheme(request.Value));
                        }
                        break;
                    case "setthemepack":
                        ApplyThemePack(request.Value);
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
                ApplyShellAppearance(queueProjectTreeRefresh: true);
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
                ApplySelectedPaneUiState(pane, focusPane: true, logSelection: true);
            }
            QueueSessionSave();
        }

        private void ApplySelectedPaneUiState(WorkspacePaneRecord pane, bool focusPane, bool logSelection)
        {
            if (_activeThread is null || pane is null)
            {
                return;
            }

            _ignoreNonSelectedPaneInteractionUntil = DateTimeOffset.UtcNow.Add(CrossPaneInteractionSuppressionWindow);
            EnsureThreadPanesMaterialized(_activeProject, _activeThread);
            WorkspacePaneRecord selectedPane = GetSelectedPane(_activeThread) ?? pane;
            if (selectedPane is not TerminalPaneRecord)
            {
                CancelPendingTerminalFocusRequests(_activeThread, selectedPane.Id);
            }

            UpdateTabViewItem(selectedPane);
            if (logSelection)
            {
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
            if (focusPane)
            {
                FocusSelectedPane();
            }

            RequestLayoutForVisiblePanes();
            QueueVisibleDeferredPaneMaterialization(_activeProject, _activeThread);
            QueueProjectTreeRefresh();
            UpdateHeader();
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
                IReadOnlyList<GitChangedFile> warmupFiles = (_activeGitSnapshot ?? _activeThread.LiveSnapshot)?.ChangedFiles;
                _pendingGitCorrelationId = null;
                _pendingGitIncludeSelectedDiff = true;
                _pendingGitPreferFastRefresh = false;
                QueueInspectorDirectoryWarmup(
                    ResolveThreadRootPath(_activeProject, _activeThread),
                    warmupFiles ?? Array.Empty<GitChangedFile>());
                QueueVisibleDiffHydrationIfNeeded(_activeThread, _activeProject, _activeGitSnapshot ?? _activeThread.LiveSnapshot);
                return false;
            }

            if (ShouldAttemptPeerThreadGitSnapshot(_activeThread, _pendingGitSelectedPath, _pendingGitIncludeSelectedDiff, _pendingGitPreferFastRefresh) &&
                TryUsePeerThreadGitSnapshot(_activeThread, _pendingGitSelectedPath, _pendingGitIncludeSelectedDiff, _pendingGitPreferFastRefresh))
            {
                IReadOnlyList<GitChangedFile> warmupFiles = (_activeGitSnapshot ?? _activeThread.LiveSnapshot)?.ChangedFiles;
                _pendingGitCorrelationId = null;
                _pendingGitIncludeSelectedDiff = true;
                _pendingGitPreferFastRefresh = false;
                QueueInspectorDirectoryWarmup(
                    ResolveThreadRootPath(_activeProject, _activeThread),
                    warmupFiles ?? Array.Empty<GitChangedFile>());
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
            return thread is not null;
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

            EditorPaneRecord editorPane = ResolveInspectorEditorPane(createIfNeeded: false);
            bool createdForInspectorSelection = false;
            if (editorPane is null)
            {
                // Open the requested file directly instead of seeding from the thread's review selection
                // and then immediately reopening another file.
                editorPane = AddEditorPane(_activeProject, _activeThread, relativePath, seedFromSelectedDiffIfUnset: false);
                createdForInspectorSelection = editorPane is not null;
            }

            if (editorPane is null)
            {
                return;
            }

            if (!createdForInspectorSelection)
            {
                await OpenEditorFileWithInlineDiffAsync(editorPane.Editor, _activeProject, _activeThread, relativePath).ConfigureAwait(true);
            }

            SelectPane(editorPane);
            RefreshInspectorFileBrowser();
        }

        private async System.Threading.Tasks.Task OpenEditorFileWithInlineDiffAsync(
            EditorPaneControl editor,
            WorkspaceProject project,
            WorkspaceThread thread,
            string relativePath)
        {
            if (editor is null || thread is null || project is null || string.IsNullOrWhiteSpace(relativePath))
            {
                return;
            }

            GitChangedFile compareFile = await TryBuildInlineCompareFileAsync(project, thread, relativePath).ConfigureAwait(true);
            await editor.OpenFilePathAsync(relativePath, compareFile).ConfigureAwait(true);
        }

        private async System.Threading.Tasks.Task<GitChangedFile> TryBuildInlineCompareFileAsync(
            WorkspaceProject project,
            WorkspaceThread thread,
            string relativePath)
        {
            GitChangedFile changedFile = ResolveInlineCompareChangedFile(thread, relativePath);
            if (changedFile is null)
            {
                return null;
            }

            string worktreePath = thread.WorktreePath ?? project.RootPath;
            if (string.IsNullOrWhiteSpace(worktreePath))
            {
                return null;
            }

            GitChangedFile compareFile = CloneGitChangedFile(changedFile);
            await System.Threading.Tasks.Task
                .Run(() => GitStatusService.EnsureCompareTexts(worktreePath, compareFile))
                .ConfigureAwait(true);
            return HasInlineCompareContent(compareFile) ? compareFile : null;
        }

        private GitChangedFile ResolveInlineCompareChangedFile(WorkspaceThread thread, string relativePath)
        {
            if (thread is null || string.IsNullOrWhiteSpace(relativePath))
            {
                return null;
            }

            string normalizedPath = NormalizeEditorComparePath(relativePath);
            GitThreadSnapshot liveSnapshot = ReferenceEquals(thread, _activeThread)
                ? _activeGitSnapshot ?? thread.LiveSnapshot
                : thread.LiveSnapshot;
            if (liveSnapshot?.ChangedFiles is null)
            {
                return null;
            }

            foreach (GitChangedFile changedFile in liveSnapshot.ChangedFiles)
            {
                if (string.Equals(NormalizeEditorComparePath(changedFile?.Path), normalizedPath, StringComparison.OrdinalIgnoreCase))
                {
                    return changedFile;
                }
            }

            return null;
        }

        private static GitChangedFile CloneGitChangedFile(GitChangedFile changedFile)
        {
            if (changedFile is null)
            {
                return null;
            }

            return new GitChangedFile
            {
                Status = changedFile.Status,
                Path = changedFile.Path,
                OriginalPath = changedFile.OriginalPath,
                AddedLines = changedFile.AddedLines,
                RemovedLines = changedFile.RemovedLines,
                DiffText = changedFile.DiffText,
                OriginalText = changedFile.OriginalText,
                ModifiedText = changedFile.ModifiedText,
            };
        }

        private static bool HasInlineCompareContent(GitChangedFile changedFile)
        {
            return !string.IsNullOrWhiteSpace(changedFile?.OriginalText) ||
                !string.IsNullOrWhiteSpace(changedFile?.ModifiedText);
        }

        private static string NormalizeEditorComparePath(string path)
        {
            return string.IsNullOrWhiteSpace(path)
                ? string.Empty
                : path.Replace('\\', '/').Trim();
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
                ApplySelectedPaneUiState(pane, focusPane: true, logSelection: true);
                UpdateWorkspaceVisibility();
                SetInspectorSection(InspectorSection.Files);
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
                ApplySelectedPaneUiState(pane, focusPane: true, logSelection: true);
                UpdateWorkspaceVisibility();
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

            TabViewItem item = PrepareTabViewItem(pane);
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

        private EditorPaneRecord AddEditorPane(
            WorkspaceProject project,
            WorkspaceThread thread,
            string initialFilePath = null,
            bool seedFromSelectedDiffIfUnset = true)
        {
            project ??= _activeProject ?? GetOrCreateProject(Environment.CurrentDirectory);
            thread = ResolveTargetThreadForNewPane(project, thread, WorkspacePaneKind.Editor);
            string preferredFilePath = initialFilePath;
            if (string.IsNullOrWhiteSpace(preferredFilePath) && seedFromSelectedDiffIfUnset)
            {
                preferredFilePath = thread?.SelectedDiffPath;
            }

            EditorPaneRecord pane = CreateEditorPane(project, thread, preferredFilePath, "Editor");
            GitChangedFile inlineCompareFile = ResolveInlineCompareChangedFile(thread, preferredFilePath);
            if (inlineCompareFile is not null)
            {
                pane.Editor.InitialCompareFile = CloneGitChangedFile(inlineCompareFile);
                pane.Editor.InitialCompareWorkingPath = thread.WorktreePath ?? project.RootPath;
            }

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
                ApplySelectedPaneUiState(pane, focusPane: true, logSelection: true);
                UpdateWorkspaceVisibility();
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

        private void QueueBrowserPaneWarmup()
        {
            if (!EnableBrowserPaneWarmup ||
                _browserPaneWarmupStarted ||
                BrowserWarmupHost is null)
            {
                return;
            }

            _browserPaneWarmupStarted = true;
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, async () =>
            {
                try
                {
                    if (_lifetimeResourcesReleased ||
                        _browserWarmupPane is not null ||
                        _projects.SelectMany(project => project.Threads).SelectMany(thread => thread.Panes).Any(pane => pane.Kind == WorkspacePaneKind.Browser))
                    {
                        return;
                    }

                    WorkspaceProject project = _activeProject ?? _projects.FirstOrDefault();
                    WorkspaceThread thread = _activeThread ?? project?.Threads.FirstOrDefault();
                    if (project is null || thread is null)
                    {
                        return;
                    }

                    BrowserPaneControl browser = BuildBrowserPaneControl(project, thread, initialUri: null);
                    _browserWarmupPane = browser;
                    BrowserWarmupHost.Children.Clear();
                    BrowserWarmupHost.Children.Add(browser);
                    await browser.EnsureInitializedAsync().ConfigureAwait(true);
                }
                catch
                {
                    if (_browserWarmupPane is not null)
                    {
                        BrowserPaneControl browser = _browserWarmupPane;
                        BrowserWarmupHost.Children.Clear();
                        _browserWarmupPane = null;
                        browser.DisposePane();
                    }
                }
                finally
                {
                    _browserPaneWarmupStarted = false;
                }
            });
        }

        private BrowserPaneControl BuildBrowserPaneControl(WorkspaceProject project, WorkspaceThread thread, string initialUri)
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
            return browser;
        }

        private BrowserPaneControl TakePrewarmedBrowserPane(WorkspaceProject project, WorkspaceThread thread, string initialUri)
        {
            BrowserPaneControl browser = _browserWarmupPane;
            if (browser is null)
            {
                return null;
            }

            _browserWarmupPane = null;
            BrowserWarmupHost?.Children.Clear();
            browser.UpdateWorkspaceContext(
                project.Id,
                project.Name,
                FormatThreadPath(project, thread),
                ResolveThreadRootPath(project, thread),
                initialUri);
            browser.ApplyTheme(ResolveTheme(SampleConfig.CurrentTheme));
            return browser;
        }

        private void AttachBrowserPane(WorkspaceProject project, WorkspaceThread thread, BrowserPaneRecord pane, string initialTitle)
        {
            BrowserPaneControl browser = pane?.Browser;
            if (browser is null)
            {
                return;
            }

            DetachBrowserPane(browser);
            string lastPersistedBrowserState = BuildBrowserPersistenceKey(browser);

            EventHandler interactionRequested = (_, _) =>
            {
                if (ShouldIgnorePaneInteractionRequest(thread, pane))
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
            };

            EventHandler<string> openPaneRequested = (_, uri) =>
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

            EventHandler<string> titleChanged = (_, title) =>
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

            EventHandler stateChanged = (_, _) =>
            {
                string currentState = BuildBrowserPersistenceKey(browser);
                if (string.Equals(lastPersistedBrowserState, currentState, StringComparison.Ordinal))
                {
                    return;
                }

                lastPersistedBrowserState = currentState;
                QueueSessionSave();
            };

            browser.InteractionRequested += interactionRequested;
            browser.OpenPaneRequested += openPaneRequested;
            browser.TitleChanged += titleChanged;
            browser.StateChanged += stateChanged;
            _browserPaneBindings[browser] = new BrowserPaneBinding
            {
                InteractionRequested = interactionRequested,
                OpenPaneRequested = openPaneRequested,
                TitleChanged = titleChanged,
                StateChanged = stateChanged,
            };
        }

        private void DetachBrowserPane(BrowserPaneControl browser)
        {
            if (browser is null || !_browserPaneBindings.TryGetValue(browser, out BrowserPaneBinding binding))
            {
                return;
            }

            browser.InteractionRequested -= binding.InteractionRequested;
            browser.OpenPaneRequested -= binding.OpenPaneRequested;
            browser.TitleChanged -= binding.TitleChanged;
            browser.StateChanged -= binding.StateChanged;
            _browserPaneBindings.Remove(browser);
        }

        private bool TryParkBrowserPaneForReuse(WorkspaceProject project, WorkspaceThread thread, BrowserPaneRecord pane)
        {
            if (BrowserWarmupHost is null ||
                _lifetimeResourcesReleased ||
                pane?.Browser is null ||
                _browserWarmupPane is not null)
            {
                return false;
            }

            BrowserPaneControl browser = pane.Browser;
            browser.UpdateWorkspaceContext(
                project?.Id ?? string.Empty,
                project?.Name ?? string.Empty,
                FormatThreadPath(project, thread),
                ResolveThreadRootPath(project, thread),
                initialUri: null);
            BrowserWarmupHost.Children.Clear();
            BrowserWarmupHost.Children.Add(browser);
            _browserWarmupPane = browser;
            return true;
        }

        private void ReleasePane(WorkspaceProject project, WorkspaceThread thread, WorkspacePaneRecord pane)
        {
            if (pane is BrowserPaneRecord browserPane)
            {
                DetachBrowserPane(browserPane.Browser);
            }

            pane.DisposePane();
        }

        private BrowserPaneRecord CreateBrowserPane(WorkspaceProject project, WorkspaceThread thread, string initialUri, string initialTitle, string paneId = null)
        {
            BrowserPaneControl browser = TakePrewarmedBrowserPane(project, thread, initialUri)
                ?? BuildBrowserPaneControl(project, thread, initialUri);

            BrowserPaneRecord pane = new(initialTitle, browser, paneId);
            AttachBrowserPane(project, thread, pane, initialTitle);
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
                ["STARSHIP_CONFIG"] = Path.Combine(repoRoot, "tools", "winmux-starship.toml"),
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
                if (ShouldIgnorePaneInteractionRequest(thread, pane))
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

        private bool ShouldIgnorePaneInteractionRequest(WorkspaceThread thread, WorkspacePaneRecord pane)
        {
            if (_suppressPaneInteractionRequests || _refreshingTabView)
            {
                return true;
            }

            if (!ReferenceEquals(_activeThread, thread))
            {
                return false;
            }

            if (!IsPaneCurrentlyVisible(thread, pane))
            {
                return true;
            }

            return DateTimeOffset.UtcNow < _ignoreNonSelectedPaneInteractionUntil &&
                !string.Equals(thread?.SelectedPaneId, pane?.Id, StringComparison.Ordinal);
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
                    ReleasePane(project, thread, pane);
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

        private bool IsPaneCurrentlyVisible(WorkspaceThread thread, WorkspacePaneRecord pane)
        {
            if (thread is null || pane is null)
            {
                return false;
            }

            foreach (WorkspacePaneRecord visiblePane in GetVisiblePanes(thread))
            {
                if (ReferenceEquals(visiblePane, pane) ||
                    string.Equals(visiblePane.Id, pane.Id, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
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

            var perfData = new Dictionary<string, string>
            {
                ["targetThreadId"] = thread.Id,
                ["targetProjectId"] = thread.Project?.Id ?? string.Empty,
                ["previousThreadId"] = _activeThread?.Id ?? string.Empty,
                ["previousProjectId"] = _activeProject?.Id ?? string.Empty,
            };
            using var perfScope = NativeAutomationDiagnostics.TrackOperation("thread.activate", data: perfData);

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
            perfData["projectChanged"] = projectChanged.ToString();
            perfData["rootChanged"] = rootChanged.ToString();
            perfData["deferWorkspaceRefresh"] = deferWorkspaceRefresh.ToString();
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
                    SyncInspectorSectionWithSelectedPane();
                    if (ShouldRefreshInspectorNotesUi())
                    {
                        RefreshInspectorNotes();
                    }
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
                    .Run(() => GitStatusService.CaptureStatusOnly(worktreePath, selectedPath, thread.BranchName))
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
            if (thread?.LiveSnapshot is null)
            {
                return true;
            }

            TimeSpan maxAge = thread.LiveSnapshot.HasEnumeratedFiles
                ? CachedThreadGitSnapshotMaxAge
                : CachedShellOnlyGitSnapshotMaxAge;
            return DateTimeOffset.UtcNow - thread.LiveSnapshotCapturedAt > maxAge;
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
                ReleasePane(project, thread, pane);
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

        private void ShowSettings(bool bypassSuppression = false)
        {
            if (!bypassSuppression && ShouldSuppressSettingsNavigation())
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
            ActiveGitRefreshMode refreshMode = ResolveActiveGitRefreshMode(thread, targetPath, includeSelectedDiff, preferFastRefresh);
            string refreshModeName = FormatActiveGitRefreshMode(refreshMode);
            Stopwatch stopwatch = Stopwatch.StartNew();
            NativeAutomationDiagnostics.IncrementCounter("gitRefresh.count");
            var perfData = new Dictionary<string, string>
            {
                ["projectId"] = project?.Id ?? string.Empty,
                ["threadId"] = thread.Id,
                ["selectedPath"] = targetPath ?? string.Empty,
                ["mode"] = refreshModeName,
                ["preferFastRefresh"] = preferFastRefresh.ToString(),
            };

            GitThreadSnapshot snapshot = await System.Threading.Tasks.Task
                .Run(() =>
                {
                    using var perfScope = NativeAutomationDiagnostics.TrackOperation("git.refresh.active", correlationId, background: true, data: perfData);
                    return refreshMode switch
                    {
                        ActiveGitRefreshMode.Complete => GitStatusService.CaptureComplete(worktreePath, targetPath),
                        ActiveGitRefreshMode.MetadataFast => GitStatusService.CaptureMetadataFast(worktreePath, targetPath, thread.BranchName),
                        ActiveGitRefreshMode.SelectedDiff => GitStatusService.Capture(worktreePath, targetPath),
                        ActiveGitRefreshMode.StatusOnly => GitStatusService.CaptureStatusOnly(worktreePath, targetPath, thread.BranchName),
                        _ => GitStatusService.CaptureMetadata(worktreePath, targetPath),
                    };
                })
                .ConfigureAwait(true);

            if (requestId != _latestGitRefreshRequestId ||
                !ReferenceEquals(thread, _activeThread) ||
                !ReferenceEquals(project, _activeProject))
            {
                return;
            }

            ApplyActiveGitSnapshot(snapshot);
            QueueGitMetadataHydrationIfNeeded(thread, project, snapshot, targetPath, refreshMode);
            QueueInspectorDirectoryWarmup(worktreePath, snapshot.ChangedFiles, correlationId);
            QueueVisibleDiffHydrationIfNeeded(thread, project, snapshot);
            if (!_projects.SelectMany(candidateProject => candidateProject.Threads).SelectMany(candidateThread => candidateThread.Panes).Any(pane => pane.Kind == WorkspacePaneKind.Browser))
            {
                QueueBrowserPaneWarmup();
            }
            LogAutomationEvent("performance", "git.snapshot_ready", "Refreshed active thread git state", new Dictionary<string, string>
            {
                ["selectedPath"] = targetPath ?? string.Empty,
                ["durationMs"] = stopwatch.ElapsedMilliseconds.ToString(),
                ["mode"] = refreshModeName,
            });
        }

        private void QueueGitMetadataHydrationIfNeeded(
            WorkspaceThread thread,
            WorkspaceProject project,
            GitThreadSnapshot snapshot,
            string selectedPath,
            ActiveGitRefreshMode refreshMode)
        {
            if (refreshMode != ActiveGitRefreshMode.MetadataFast ||
                snapshot is null ||
                snapshot.HasLineStats ||
                !snapshot.HasEnumeratedFiles ||
                !ShouldRefreshReviewInspectorUi() ||
                thread is null ||
                project is null ||
                !ReferenceEquals(thread, _activeThread) ||
                !ReferenceEquals(project, _activeProject))
            {
                return;
            }

            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                QueueActiveThreadGitRefresh(
                    selectedPath,
                    preserveSelection: true,
                    includeSelectedDiff: false,
                    preferFastRefresh: false);
            });
        }

        private void ApplyActiveGitSnapshot(GitThreadSnapshot snapshot)
        {
            CommitActiveGitSnapshot(snapshot, DateTimeOffset.UtcNow, ensureBaselineCapture: true, logRefresh: true);
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
                    displayedSnapshot.HasEnumeratedFiles && displayedSnapshot.ChangedFiles.Count > 0 ? displayedSnapshot : null,
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
            if (snapshot is null ||
                !snapshot.HasEnumeratedFiles ||
                snapshot.ChangedFiles.Count <= 1)
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

            int requestId = ++_latestSelectedDiffRequestId;
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

                if (requestId != _latestSelectedDiffRequestId ||
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

            if (requestId != _latestSelectedDiffRequestId ||
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
                !nextSnapshot.HasEnumeratedFiles ||
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
            builder.Append(SampleConfig.CurrentThemePackId);
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
                _paneFocusRequestId++;
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

            bool hasInspectorContext = !_showingSettings && _activeThread is not null;
            bool showInspector = _inspectorOpen && hasInspectorContext;

            InspectorSplitView.CompactPaneLength = showInspector ? InspectorCompactPaneWidth : 0;
            InspectorSplitView.IsPaneOpen = showInspector;
            InspectorSidebar.Visibility = hasInspectorContext ? Visibility.Visible : Visibility.Collapsed;
            InspectorSidebar.IsHitTestVisible = hasInspectorContext;

            if (InspectorHeaderTabsPanel is not null)
            {
                InspectorHeaderTabsPanel.Visibility = showInspector ? Visibility.Visible : Visibility.Collapsed;
            }

            if (InspectorHeaderActionsHost is not null)
            {
                InspectorHeaderActionsHost.Visibility = showInspector ? Visibility.Visible : Visibility.Collapsed;
            }

            if (InspectorBodyHost is not null)
            {
                InspectorBodyHost.Visibility = showInspector ? Visibility.Visible : Visibility.Collapsed;
            }

            if (showInspector && ShouldRefreshInspectorNotesUi())
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
                string threadNameText;
                bool threadNameReadOnly;
                string contextText;
                Visibility contextVisibility;
                if (_showingSettings)
                {
                    threadNameText = "Preferences";
                    threadNameReadOnly = true;
                    contextText = "Theme, shell, pane limit, and vault";
                    contextVisibility = Visibility.Visible;
                }
                else
                {
                    threadNameReadOnly = _activeThread is null;
                    threadNameText = _activeThread?.Name ?? "No thread selected";
                    contextText = _activeProject is null ? string.Empty : BuildHeaderContext(_activeProject, _activeThread);
                    contextVisibility = string.IsNullOrWhiteSpace(contextText) ? Visibility.Collapsed : Visibility.Visible;
                }

                string renderKey = string.Join(
                    "\u001F",
                    _showingSettings.ToString(),
                    threadNameReadOnly.ToString(),
                    threadNameText ?? string.Empty,
                    contextText ?? string.Empty,
                    contextVisibility.ToString());
                if (string.Equals(renderKey, _lastHeaderRenderKey, StringComparison.Ordinal))
                {
                    return;
                }

                ThreadNameBox.IsReadOnly = threadNameReadOnly;
                ThreadNameBox.Text = threadNameText;
                ActiveDirectoryText.Text = contextText;
                ActiveDirectoryText.Visibility = contextVisibility;
                if (ActiveDirectorySeparator is not null)
                {
                    ActiveDirectorySeparator.Visibility = contextVisibility;
                }

                _lastHeaderRenderKey = renderKey;
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
                PaneWorkspaceShell.Visibility = Visibility.Collapsed;
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

        private static ElementTheme ParseTheme(string value)
        {
            return value?.Trim().ToLowerInvariant() switch
            {
                "light" => ElementTheme.Light,
                "dark" => ElementTheme.Dark,
                "system" => ElementTheme.Default,
                "default" => ElementTheme.Default,
                _ => throw new InvalidOperationException($"Unknown theme '{value}'. Expected light, dark, or system."),
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
            return ShellTheme.ResolveBrushForTheme(effectiveTheme, key);
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
            ContentDialogResult result = await ShowShellContentDialogAsync(
                dialog,
                "Dialog open",
                "Finish the dialog to return to the live panes.");
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

            ContentDialogResult result = await ShowShellContentDialogAsync(
                dialog,
                "Dialog open",
                "Finish the dialog to return to the live panes.");
            return result == ContentDialogResult.Primary ? nameBox.Text?.Trim() : null;
        }

        private async System.Threading.Tasks.Task<ContentDialogResult> ShowShellContentDialogAsync(
            ContentDialog dialog,
            string suppressionTitle,
            string suppressionMessage)
        {
            SetVisibleHostedPaneSuppression(true, suppressionTitle, suppressionMessage);
            try
            {
                return await dialog.ShowAsync();
            }
            finally
            {
                SetVisibleHostedPaneSuppression(false, null, null);
            }
        }

        private void SetVisibleHostedPaneSuppression(bool suppressed, string title, string message)
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
                        terminalPane.Terminal.SetHostedSurfaceSuppressed(suppressed, title, message);
                        break;
                    case BrowserPaneRecord browserPane:
                        browserPane.Browser.SetHostedSurfaceSuppressed(suppressed);
                        break;
                    case EditorPaneRecord editorPane:
                        editorPane.Editor.SetHostedSurfaceSuppressed(suppressed, title, message);
                        break;
                }
            }
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
