using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Dispatching;
using Microsoft.Web.WebView2.Core;
using SelfContainedDeployment.Automation;
using SelfContainedDeployment.Browser;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Linq;
using Windows.System;
using Windows.Storage.Streams;

namespace SelfContainedDeployment.Panes
{
    public sealed partial class BrowserPaneControl : UserControl
    {
        private const string IsolatedBrowserProfileFolderName = "browser-profile-isolated";
        private const double CompactPaneWidthThreshold = 560;
        private const double CompactPaneHeightThreshold = 320;
        private const double CompactBrowserZoomFloor = 0.72;

        private readonly struct BrowserExtensionSnapshot
        {
            public string Id { get; init; }

            public string Name { get; init; }
        }

        public sealed class BrowserPaneTabSnapshot
        {
            public string Id { get; init; }

            public string Title { get; init; }

            public string Uri { get; init; }
        }

        private sealed class BrowserTabSession
        {
            public string Id { get; init; }

            public string Title { get; set; }

            public string Uri { get; set; }
        }

        private static readonly Dictionary<string, Task<CoreWebView2Environment>> EnvironmentTasks = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object EnvironmentSync = new();
        private Task _initializationTask;
        private bool _initialized;
        private bool _disposed;
        private string _currentUri;
        private string _currentTitle = "Preview";
        private ElementTheme _themePreference = ElementTheme.Default;
        private string _profileSeedStatus = "Isolated WinMux browser profile";
        private string _extensionImportStatus = "No browser extensions are installed in this WinMux profile.";
        private string _credentialAutofillStatus = "No imported WinMux credentials.";
        private readonly List<BrowserTabSession> _browserTabs = new();
        private readonly Dictionary<string, Button> _browserTabButtonsById = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Border> _browserTabDotsById = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TextBlock> _browserTabTitlesById = new(StringComparer.Ordinal);
        private readonly List<BrowserExtensionSnapshot> _installedExtensions = new();
        private readonly Dictionary<int, BrowserCredentialMatch> _credentialContextMenuMatches = new();
        private Task _deferredSetupTask;
        private string _selectedBrowserTabId;
        private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _layoutTimer;
        private bool _lastCompactLayout;
        private double _lastBrowserTabTitleWidth = -1;
        private string _lastBrowserTabStripStructureSignature;
        private string _lastStateChangeSignature;
        private double _lastQueuedLayoutWidth = -1;
        private double _lastQueuedLayoutHeight = -1;
        private int _lastResizeNotificationWidth;
        private int _lastResizeNotificationHeight;
        private double _lastAppliedZoomFactor = double.NaN;

        private sealed class BrowserAutofillResult
        {
            public bool FilledUsername { get; set; }

            public bool FilledPassword { get; set; }

            public bool HasUsernameField { get; set; }

            public bool HasPasswordField { get; set; }

            public string BlockedReason { get; set; }
        }

        public BrowserPaneControl()
        {
            InitializeComponent();
            ActualThemeChanged += OnActualThemeChanged;
            PointerPressed += (_, _) => RaiseInteractionRequested();
            GotFocus += OnInteractionRequested;
            SizeChanged += OnBrowserPaneSizeChanged;
            AddressBox.GotFocus += OnAddressBoxGotFocus;
            _layoutTimer = DispatcherQueue.CreateTimer();
            _layoutTimer.IsRepeating = false;
            _layoutTimer.Interval = TimeSpan.FromMilliseconds(45);
            _layoutTimer.Tick += OnLayoutTimerTick;
            RefreshBrowserTabStrip();
            UpdateNavigationButtons();
            UpdateAdaptiveChromeLayout();
        }

        internal static void PreloadBrowserEnvironmentIfAvailable()
        {
            string key = GetBrowserProfileRootPath();
            try
            {
                if (!Directory.Exists(key) || !Directory.EnumerateFileSystemEntries(key).Any())
                {
                    return;
                }
            }
            catch
            {
                return;
            }

            Task<CoreWebView2Environment> environmentTask;
            lock (EnvironmentSync)
            {
                if (!EnvironmentTasks.TryGetValue(key, out environmentTask))
                {
                    environmentTask = CreateEnvironmentAsync(key);
                    EnvironmentTasks[key] = environmentTask;
                }
            }

            _ = environmentTask.ContinueWith(_ => { }, TaskScheduler.Default);
        }

        public event EventHandler<string> TitleChanged;
        public event EventHandler InteractionRequested;
        public event EventHandler<string> OpenPaneRequested;
        public event EventHandler StateChanged;

        public string ProjectId { get; set; }

        public string ProjectName { get; set; }

        public string ProjectPath { get; set; }

        public string ProjectRootPath { get; set; }

        public string InitialUri { get; set; }

        public string CurrentUri => _currentUri;

        public string CurrentTitle => _currentTitle;

        public string AddressText => AddressBox.Text ?? string.Empty;

        public bool IsInitialized => _initialized;

        public string SelectedTabId => _selectedBrowserTabId;

        public int TabCount => _browserTabs.Count;

        public IReadOnlyList<BrowserPaneTabSnapshot> Tabs => _browserTabs
            .Select(tab => new BrowserPaneTabSnapshot
            {
                Id = tab.Id,
                Title = tab.Title,
                Uri = tab.Uri,
            })
            .ToList();

        public string ProfileSeedStatus => _profileSeedStatus;

        public string ExtensionImportStatus => _extensionImportStatus;

        public string CredentialAutofillStatus => _credentialAutofillStatus;

        public IReadOnlyList<string> InstalledExtensionNames => _installedExtensions
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Name)
            .ToList();

        private void LogBrowserEvent(string name, string message = null, IReadOnlyDictionary<string, string> data = null)
        {
            NativeAutomationEventLog.Record("browser", name, message, data ?? new Dictionary<string, string>
            {
                ["projectId"] = ProjectId ?? string.Empty,
                ["projectPath"] = ProjectPath ?? string.Empty,
                ["uri"] = _currentUri ?? string.Empty,
                ["title"] = _currentTitle ?? string.Empty,
            });
        }

        public void ApplyTheme(ElementTheme theme)
        {
            _themePreference = theme;
            RequestedTheme = theme;

            if (_initialized && string.Equals(_currentUri, "winmux://start", StringComparison.OrdinalIgnoreCase))
            {
                _ = RefreshStartPageForThemeChangeAsync();
            }
        }

        private async Task RefreshStartPageForThemeChangeAsync()
        {
            try
            {
                await NavigateToStartPageAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                HandleBrowserInitializationFailure(ex);
            }
        }

        public void FocusPane()
        {
            if (!_initialized || BrowserView.CoreWebView2 is null)
            {
                AddressBox.Focus(FocusState.Programmatic);
                return;
            }

            BrowserView.Focus(FocusState.Programmatic);
        }

        public void RequestLayout()
        {
            QueueLayoutRefresh();
        }

        public void RefreshCredentialAutofillState()
        {
            UpdateCredentialAutofillStatus();
        }

        public void DisposePane()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _layoutTimer.Stop();
            ActualThemeChanged -= OnActualThemeChanged;
            GotFocus -= OnInteractionRequested;
            SizeChanged -= OnBrowserPaneSizeChanged;
            AddressBox.GotFocus -= OnAddressBoxGotFocus;
            BrowserView.GotFocus -= OnInteractionRequested;
            if (BrowserView.CoreWebView2 is not null)
            {
                BrowserView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
                BrowserView.CoreWebView2.DocumentTitleChanged -= OnDocumentTitleChanged;
                BrowserView.CoreWebView2.SourceChanged -= OnSourceChanged;
                BrowserView.CoreWebView2.ContextMenuRequested -= OnContextMenuRequested;
                BrowserView.CoreWebView2.NewWindowRequested -= OnNewWindowRequested;
            }

            _browserTabs.Clear();
            _browserTabButtonsById.Clear();
            _browserTabDotsById.Clear();
            _browserTabTitlesById.Clear();
            _installedExtensions.Clear();
            _credentialContextMenuMatches.Clear();
            _deferredSetupTask = null;
            BrowserTabStripPanel?.Children.Clear();
        }

        public void Navigate(string target)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                _lastAppliedZoomFactor = double.NaN;
                _ = NavigateToStartPageAsync();
                return;
            }

            string normalized = NormalizeUri(target);
            AddressBox.Text = normalized;
            RaiseInteractionRequested();
            EnsureBrowserTabExists();
            if (_initialized && BrowserView.CoreWebView2 is not null)
            {
                SyncSelectedBrowserTab(uri: normalized);
                _lastAppliedZoomFactor = double.NaN;
                TryNavigateCore(BrowserView.CoreWebView2, normalized, "navigate.requested", _selectedBrowserTabId);
            }
            else
            {
                BrowserTabSession tab = GetSelectedBrowserTab();
                if (tab is not null)
                {
                    tab.Uri = normalized;
                    tab.Title = string.IsNullOrWhiteSpace(tab.Title) ? "New tab" : tab.Title;
                    _currentUri = normalized;
                }
                else
                {
                    InitialUri = normalized;
                }

                RefreshBrowserTabStrip();
                RaiseStateChangedIfNeeded();
            }
        }

        public void RestoreTabSession(IReadOnlyList<BrowserPaneTabSnapshot> tabs, string selectedTabId)
        {
            _browserTabs.Clear();
            foreach (BrowserPaneTabSnapshot tab in tabs ?? Array.Empty<BrowserPaneTabSnapshot>())
            {
                _browserTabs.Add(new BrowserTabSession
                {
                    Id = string.IsNullOrWhiteSpace(tab.Id) ? Guid.NewGuid().ToString("N") : tab.Id,
                    Title = string.IsNullOrWhiteSpace(tab.Title) ? "New tab" : tab.Title.Trim(),
                    Uri = string.IsNullOrWhiteSpace(tab.Uri) ? "winmux://start" : tab.Uri.Trim(),
                });
            }

            if (_browserTabs.Count == 0)
            {
                _selectedBrowserTabId = null;
                if (!string.IsNullOrWhiteSpace(InitialUri))
                {
                    EnsureBrowserTabExists(InitialUri);
                }
            }
            else
            {
                _selectedBrowserTabId = _browserTabs.Any(tab => string.Equals(tab.Id, selectedTabId, StringComparison.Ordinal))
                    ? selectedTabId
                    : _browserTabs[0].Id;
                BrowserTabSession selected = GetSelectedBrowserTab();
                _currentTitle = selected?.Title ?? _currentTitle;
                _currentUri = selected?.Uri ?? _currentUri;
            }

            RefreshBrowserTabStrip();
            UpdateNavigationButtons();
            RaiseStateChangedIfNeeded();
        }

        public async Task EnsureInitializedAsync()
        {
            if (_initialized || _disposed)
            {
                return;
            }

            Task initializationTask = _initializationTask;
            if (initializationTask is null)
            {
                initializationTask = InitializeBrowserAsync();
                _initializationTask = initializationTask;
            }

            try
            {
                await initializationTask.ConfigureAwait(true);
            }
            catch
            {
                if (ReferenceEquals(_initializationTask, initializationTask))
                {
                    _initializationTask = null;
                }

                throw;
            }
        }

        internal void PreloadEnvironment()
        {
            if (_disposed || _initialized)
            {
                return;
            }

            _ = PreloadEnvironmentAsync();
        }

        private async Task PreloadEnvironmentAsync()
        {
            try
            {
                await GetEnvironmentAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                LogBrowserEvent("webview.preload_failed", $"Browser environment preload failed: {ex.Message}", new Dictionary<string, string>
                {
                    ["error"] = ex.Message,
                });
            }
        }

        private async Task InitializeBrowserAsync()
        {
            using var perfScope = NativeAutomationDiagnostics.TrackOperation("browser.webview.init");
            NativeAutomationDiagnostics.IncrementCounter("browserWebViewInit.count");
            try
            {
                CoreWebView2Environment environment;
                using (NativeAutomationDiagnostics.TrackOperation("browser.environment.get"))
                {
                    environment = await GetEnvironmentAsync();
                }
                if (_disposed)
                {
                    return;
                }

                using (NativeAutomationDiagnostics.TrackOperation("browser.core.ensure"))
                {
                    await BrowserView.EnsureCoreWebView2Async(environment);
                }
                if (_disposed)
                {
                    return;
                }

                LogBrowserEvent("webview.ready", "Browser WebView2 initialized with isolated WinMux profile");
            }
            catch (Exception ex)
            {
                LogBrowserEvent("webview.profile_failed", $"WinMux browser profile failed: {ex.Message}", new Dictionary<string, string>
                {
                    ["error"] = ex.Message,
                });

                await BrowserView.EnsureCoreWebView2Async();
                if (_disposed)
                {
                    return;
                }

                _profileSeedStatus = "Fell back to default WebView2 profile";
                LogBrowserEvent("webview.ready", "Browser WebView2 initialized with default profile");
            }

            CoreWebView2 core = await WaitForCoreWebView2Async().ConfigureAwait(true);
            if (_disposed)
            {
                return;
            }

            using (NativeAutomationDiagnostics.TrackOperation("browser.configure"))
            {
                await ConfigureInitializedBrowserAsync(core).ConfigureAwait(true);
            }
        }

        private async Task ConfigureInitializedBrowserAsync(CoreWebView2 core)
        {
            try
            {
                if (_disposed)
                {
                    return;
                }

                if (core is null)
                {
                    throw new InvalidOperationException("Browser CoreWebView2 was null after initialization.");
                }

                LogBrowserEvent("webview.configure", "Configuring browser pane settings", new Dictionary<string, string>
                {
                    ["step"] = "settings.begin",
                });
                bool devToolsEnabled = IsBrowserDevToolsEnabled();
                core.Settings.AreDevToolsEnabled = devToolsEnabled;
                core.Settings.AreBrowserAcceleratorKeysEnabled = true;
                core.Settings.IsStatusBarEnabled = false;
                LogBrowserEvent("webview.configure", "Configured basic browser settings", new Dictionary<string, string>
                {
                    ["step"] = "settings.basic",
                    ["devToolsEnabled"] = devToolsEnabled.ToString(),
                });
                core.Settings.IsPasswordAutosaveEnabled = false;
                LogBrowserEvent("webview.configure", "Disabled native browser password autosave in favor of WinMux vault", new Dictionary<string, string>
                {
                    ["step"] = "settings.passwordAutosave",
                });
                core.Settings.IsGeneralAutofillEnabled = false;
                LogBrowserEvent("webview.configure", "Disabled native browser general autofill in favor of WinMux vault", new Dictionary<string, string>
                {
                    ["step"] = "settings.generalAutofill",
                });
                core.Profile.IsPasswordAutosaveEnabled = false;
                core.Profile.IsGeneralAutofillEnabled = false;
                core.DocumentTitleChanged += OnDocumentTitleChanged;
                core.NavigationCompleted += OnNavigationCompleted;
                core.SourceChanged += OnSourceChanged;
                core.ContextMenuRequested += OnContextMenuRequested;
                core.NewWindowRequested += OnNewWindowRequested;
                BrowserView.GotFocus += OnInteractionRequested;
                _initialized = true;
                LogBrowserEvent("webview.configure", "Attached browser event handlers", new Dictionary<string, string>
                {
                    ["step"] = "events.attached",
                });

                ApplyTheme(_themePreference == ElementTheme.Default ? ActualTheme : _themePreference);
                LogBrowserEvent("webview.configure", "Applied browser theme", new Dictionary<string, string>
                {
                    ["step"] = "theme.applied",
                });

                LogBrowserEvent("webview.configure", "Browser credential capture is disabled in WinMux-managed profiles", new Dictionary<string, string>
                {
                    ["step"] = "credentials.captureDisabled",
                });

                UpdateCredentialAutofillStatus();
                EnsureBrowserTabExists(!string.IsNullOrWhiteSpace(InitialUri) ? NormalizeUri(InitialUri) : null);
                BrowserTabSession selectedTab = GetSelectedBrowserTab();

                if (!string.IsNullOrWhiteSpace(selectedTab?.Uri) &&
                    !string.Equals(selectedTab.Uri, "about:blank", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(selectedTab.Uri, "winmux://start", StringComparison.OrdinalIgnoreCase))
                {
                    string selectedUri = NormalizeUri(selectedTab.Uri);
                    AddressBox.Text = selectedUri;
                    _currentUri = selectedUri;
                    LogBrowserEvent("webview.configure", $"Restoring browser tab to {selectedUri}", new Dictionary<string, string>
                    {
                        ["step"] = "navigate.selectedTab",
                        ["uri"] = selectedUri,
                        ["tabId"] = selectedTab.Id,
                    });
                    TryNavigateCore(core, selectedUri, "restore.selectedTab", selectedTab.Id);
                }
                else if (!string.IsNullOrWhiteSpace(_currentUri) &&
                    !string.Equals(_currentUri, "about:blank", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(_currentUri, "winmux://start", StringComparison.OrdinalIgnoreCase))
                {
                    LogBrowserEvent("webview.configure", $"Preserving in-flight browser navigation to {_currentUri}", new Dictionary<string, string>
                    {
                        ["step"] = "navigate.preserveCurrent",
                        ["uri"] = _currentUri,
                    });
                }
                else if (!string.IsNullOrWhiteSpace(AddressBox.Text) &&
                    !string.Equals(AddressBox.Text, "about:blank", StringComparison.OrdinalIgnoreCase))
                {
                    string pendingUri = NormalizeUri(AddressBox.Text);
                    SyncSelectedBrowserTab(uri: pendingUri);
                    LogBrowserEvent("webview.configure", $"Preserving pending browser navigation to {pendingUri}", new Dictionary<string, string>
                    {
                        ["step"] = "navigate.preserveAddress",
                        ["uri"] = pendingUri,
                    });
                    TryNavigateCore(core, pendingUri, "restore.pendingAddress");
                }
                else if (string.IsNullOrWhiteSpace(InitialUri))
                {
                    LogBrowserEvent("webview.configure", "Navigating to browser start page", new Dictionary<string, string>
                    {
                        ["step"] = "navigate.startPage",
                    });
                    await NavigateToStartPageAsync();
                }
                else
                {
                    string initialUri = NormalizeUri(InitialUri);
                    AddressBox.Text = initialUri;
                    SyncSelectedBrowserTab(uri: initialUri);
                    LogBrowserEvent("webview.configure", $"Navigating browser pane to {initialUri}", new Dictionary<string, string>
                    {
                        ["step"] = "navigate.initialUri",
                        ["uri"] = initialUri,
                    });
                    TryNavigateCore(core, initialUri, "restore.initialUri");
                }

                RefreshBrowserTabStrip();
                UpdateNavigationButtons();
                StartDeferredSetup(core);
            }
            catch (Exception ex)
            {
                LogBrowserEvent("webview.init_failed", $"Browser pane post-init failed: {ex.Message}", new Dictionary<string, string>
                {
                    ["error"] = ex.ToString(),
                });
                throw;
            }
        }

        private async Task<CoreWebView2> WaitForCoreWebView2Async()
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                if (BrowserView.CoreWebView2 is not null)
                {
                    return BrowserView.CoreWebView2;
                }

                await Task.Delay(50).ConfigureAwait(true);
            }

            throw new InvalidOperationException("Browser CoreWebView2 was null after initialization.");
        }

        private void EnsureBrowserTabExists(string initialUri = null)
        {
            if (_browserTabs.Count == 0)
            {
                string normalizedUri = string.IsNullOrWhiteSpace(initialUri)
                    ? "winmux://start"
                    : initialUri.Trim();
                BrowserTabSession tab = new()
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Title = string.Equals(normalizedUri, "winmux://start", StringComparison.OrdinalIgnoreCase) ? "Start" : "New tab",
                    Uri = normalizedUri,
                };
                _browserTabs.Add(tab);
                _selectedBrowserTabId = tab.Id;
                _currentUri = tab.Uri;
                _currentTitle = tab.Title;
            }

            if (!_browserTabs.Any(tab => string.Equals(tab.Id, _selectedBrowserTabId, StringComparison.Ordinal)))
            {
                _selectedBrowserTabId = _browserTabs[0].Id;
            }
        }

        private BrowserTabSession GetSelectedBrowserTab()
        {
            EnsureBrowserTabExists();
            return _browserTabs.FirstOrDefault(tab => string.Equals(tab.Id, _selectedBrowserTabId, StringComparison.Ordinal))
                ?? _browserTabs[0];
        }

        private void SyncSelectedBrowserTab(string uri = null, string title = null)
        {
            BrowserTabSession tab = GetSelectedBrowserTab();
            bool titleChanged = false;
            bool uriChanged = false;
            if (!string.IsNullOrWhiteSpace(uri))
            {
                string normalizedUri = uri.Trim();
                uriChanged = !string.Equals(tab.Uri, normalizedUri, StringComparison.Ordinal);
                tab.Uri = normalizedUri;
                _currentUri = tab.Uri;
            }

            if (!string.IsNullOrWhiteSpace(title))
            {
                string normalizedTitle = title.Trim();
                titleChanged = !string.Equals(tab.Title, normalizedTitle, StringComparison.Ordinal);
                tab.Title = normalizedTitle;
                _currentTitle = tab.Title;
            }

            if (titleChanged)
            {
                UpdateBrowserTabStripTitles();
            }

            UpdateBrowserTabSelectionVisuals();
            if (titleChanged || uriChanged)
            {
                _lastStateChangeSignature = null;
            }
        }

        private async Task SelectBrowserTabAsync(string tabId)
        {
            BrowserTabSession nextTab = _browserTabs.FirstOrDefault(tab => string.Equals(tab.Id, tabId, StringComparison.Ordinal));
            if (nextTab is null)
            {
                return;
            }

            _selectedBrowserTabId = nextTab.Id;
            _currentTitle = string.IsNullOrWhiteSpace(nextTab.Title) ? "Preview" : nextTab.Title;
            _currentUri = string.IsNullOrWhiteSpace(nextTab.Uri) ? "winmux://start" : nextTab.Uri;
            AddressBox.Text = string.Equals(_currentUri, "winmux://start", StringComparison.OrdinalIgnoreCase) ? string.Empty : _currentUri;
            _lastStateChangeSignature = null;
            UpdateBrowserTabSelectionVisuals();
            UpdateNavigationButtons();
            RaiseStateChangedIfNeeded();

            if (!_initialized || BrowserView.CoreWebView2 is null)
            {
                InitialUri = _currentUri;
                return;
            }

            if (string.Equals(_currentUri, "winmux://start", StringComparison.OrdinalIgnoreCase))
            {
                _lastAppliedZoomFactor = double.NaN;
                await NavigateToStartPageAsync().ConfigureAwait(true);
            }
            else
            {
                _lastAppliedZoomFactor = double.NaN;
                TryNavigateCore(BrowserView.CoreWebView2, _currentUri, "tab.selected", nextTab.Id);
            }
        }

        private async Task AddBrowserTabAsync(string initialUri = null, string title = null, string tabId = null)
        {
            string normalizedUri = string.IsNullOrWhiteSpace(initialUri)
                ? "winmux://start"
                : NormalizeUri(initialUri);

            BrowserTabSession tab = new()
            {
                Id = string.IsNullOrWhiteSpace(tabId) ? Guid.NewGuid().ToString("N") : tabId,
                Title = string.IsNullOrWhiteSpace(title)
                    ? (string.Equals(normalizedUri, "winmux://start", StringComparison.OrdinalIgnoreCase) ? "Start" : "New tab")
                    : title.Trim(),
                Uri = normalizedUri,
            };

            _browserTabs.Add(tab);
            await SelectBrowserTabAsync(tab.Id).ConfigureAwait(true);
        }

        private async Task CloseBrowserTabAsync(string tabId)
        {
            if (_browserTabs.Count <= 1)
            {
                BrowserTabSession onlyTab = GetSelectedBrowserTab();
                onlyTab.Uri = "winmux://start";
                onlyTab.Title = "Start";
                await SelectBrowserTabAsync(onlyTab.Id).ConfigureAwait(true);
                return;
            }

            int index = _browserTabs.FindIndex(tab => string.Equals(tab.Id, tabId, StringComparison.Ordinal));
            if (index < 0)
            {
                return;
            }

            _browserTabs.RemoveAt(index);
            BrowserTabSession fallback = _browserTabs[Math.Clamp(index - 1, 0, _browserTabs.Count - 1)];
            await SelectBrowserTabAsync(fallback.Id).ConfigureAwait(true);
        }

        private void RefreshBrowserTabStrip()
        {
            if (BrowserTabStripPanel is null)
            {
                return;
            }

            double targetTitleWidth = ResolveBrowserTabTitleWidth();
            bool compact = IsCompactPaneLayout();
            bool showInlineCloseButtons = !compact || _browserTabs.Count <= 2;
            string structureSignature = BuildBrowserTabStripStructureSignature(targetTitleWidth, compact, showInlineCloseButtons);
            if (string.Equals(structureSignature, _lastBrowserTabStripStructureSignature, StringComparison.Ordinal) &&
                _browserTabButtonsById.Count == _browserTabs.Count &&
                _browserTabDotsById.Count == _browserTabs.Count &&
                _browserTabTitlesById.Count == _browserTabs.Count)
            {
                UpdateBrowserTabStripTitles();
                UpdateBrowserTabSelectionVisuals();
                return;
            }

            BrowserTabStripPanel.Children.Clear();
            _browserTabButtonsById.Clear();
            _browserTabDotsById.Clear();
            _browserTabTitlesById.Clear();
            foreach (BrowserTabSession tab in _browserTabs)
            {
                bool isActive = string.Equals(tab.Id, _selectedBrowserTabId, StringComparison.Ordinal);
                Grid tabLayout = new()
                {
                    ColumnSpacing = showInlineCloseButtons ? 4 : 0,
                    Width = targetTitleWidth + (showInlineCloseButtons ? (compact ? 20 : 26) : 0),
                };
                tabLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                if (showInlineCloseButtons)
                {
                    tabLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                }

                Button tabButton = new()
                {
                    Style = (Style)Application.Current.Resources["ShellBrowserTabButtonStyle"],
                    Tag = tab.Id,
                    Width = targetTitleWidth,
                    MinWidth = compact ? 68 : 90,
                    MaxWidth = compact ? 164 : 208,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Padding = compact ? new Thickness(6, 1, 6, 1) : new Thickness(8, 1, 8, 1),
                };
                AutomationProperties.SetAutomationId(tabButton, $"browser-pane-tab-{tab.Id}");
                AutomationProperties.SetName(tabButton, string.IsNullOrWhiteSpace(tab.Title) ? "Browser tab" : tab.Title);
                tabButton.Click += OnBrowserTabClicked;

                TextBlock tabTitle = new()
                {
                    Text = string.IsNullOrWhiteSpace(tab.Title) ? "New tab" : tab.Title,
                    FontSize = compact ? 10.4 : 10.8,
                    Opacity = isActive ? 1.0 : 0.82,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    FontWeight = isActive
                        ? Microsoft.UI.Text.FontWeights.SemiBold
                        : Microsoft.UI.Text.FontWeights.Normal,
                };
                AutomationProperties.SetAutomationId(tabTitle, $"browser-pane-tab-title-{tab.Id}");

                Border dot = new()
                {
                    Width = 6,
                    Height = 6,
                    CornerRadius = new CornerRadius(3),
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = ResolveBrowserTabDotBrush(tab, isActive),
                };

                StackPanel tabContent = new()
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                tabContent.Children.Add(dot);
                tabContent.Children.Add(tabTitle);

                tabButton.Content = tabContent;
                ApplyBrowserTabButtonState(tabButton, isActive);
                _browserTabButtonsById[tab.Id] = tabButton;
                _browserTabDotsById[tab.Id] = dot;
                _browserTabTitlesById[tab.Id] = tabTitle;
                tabLayout.Children.Add(tabButton);

                if (showInlineCloseButtons)
                {
                    Button closeButton = new()
                    {
                        Style = (Style)Application.Current.Resources["ShellGhostToolbarButtonStyle"],
                        Tag = tab.Id,
                        Width = compact ? 20 : 22,
                        Height = compact ? 20 : 22,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Opacity = 0.74,
                        Content = new FontIcon
                        {
                            FontSize = compact ? 8.5 : 9,
                            Glyph = "\uE711",
                        },
                    };
                    AutomationProperties.SetAutomationId(closeButton, $"browser-pane-tab-close-{tab.Id}");
                    closeButton.Click += OnBrowserTabCloseClicked;
                    Grid.SetColumn(closeButton, 1);
                    tabLayout.Children.Add(closeButton);
                }

                BrowserTabStripPanel.Children.Add(tabLayout);
            }

            _lastBrowserTabStripStructureSignature = structureSignature;
        }
        private void UpdateBrowserTabStripTitles()
        {
            if (_browserTabTitlesById.Count != _browserTabs.Count || _browserTabDotsById.Count != _browserTabs.Count)
            {
                RefreshBrowserTabStrip();
                return;
            }

            foreach (BrowserTabSession tab in _browserTabs)
            {
                if (_browserTabTitlesById.TryGetValue(tab.Id, out TextBlock title))
                {
                    string nextTitle = string.IsNullOrWhiteSpace(tab.Title) ? "New tab" : tab.Title;
                    if (!string.Equals(title.Text, nextTitle, StringComparison.Ordinal))
                    {
                        title.Text = nextTitle;
                    }
                }

                if (_browserTabButtonsById.TryGetValue(tab.Id, out Button button))
                {
                    AutomationProperties.SetName(button, string.IsNullOrWhiteSpace(tab.Title) ? "Browser tab" : tab.Title);
                }

                if (_browserTabDotsById.TryGetValue(tab.Id, out Border dot))
                {
                    dot.Background = ResolveBrowserTabDotBrush(tab, string.Equals(tab.Id, _selectedBrowserTabId, StringComparison.Ordinal));
                }
            }
        }

        private void UpdateBrowserTabSelectionVisuals()
        {
            if (_browserTabButtonsById.Count != _browserTabs.Count ||
                _browserTabDotsById.Count != _browserTabs.Count ||
                _browserTabTitlesById.Count != _browserTabs.Count)
            {
                RefreshBrowserTabStrip();
                return;
            }

            foreach (BrowserTabSession tab in _browserTabs)
            {
                bool isActive = string.Equals(tab.Id, _selectedBrowserTabId, StringComparison.Ordinal);
                if (_browserTabButtonsById.TryGetValue(tab.Id, out Button button))
                {
                    ApplyBrowserTabButtonState(button, isActive);
                }

                if (_browserTabTitlesById.TryGetValue(tab.Id, out TextBlock title))
                {
                    title.FontWeight = isActive
                        ? Microsoft.UI.Text.FontWeights.SemiBold
                        : Microsoft.UI.Text.FontWeights.Normal;
                    title.Opacity = isActive ? 1.0 : 0.82;
                }

                if (_browserTabDotsById.TryGetValue(tab.Id, out Border dot))
                {
                    dot.Background = ResolveBrowserTabDotBrush(tab, isActive);
                }
            }
        }

        private bool IsCompactPaneLayout()
        {
            double width = BrowserRoot?.ActualWidth > 0 ? BrowserRoot.ActualWidth : ActualWidth;
            double height = BrowserRoot?.ActualHeight > 0 ? BrowserRoot.ActualHeight : ActualHeight;
            return width > 0 && height > 0 &&
                (width < CompactPaneWidthThreshold || height < CompactPaneHeightThreshold);
        }

        private double ResolveBrowserTabTitleWidth()
        {
            double width = BrowserRoot?.ActualWidth > 0 ? BrowserRoot.ActualWidth : ActualWidth;
            if (width <= 0)
            {
                return 132;
            }

            bool compact = IsCompactPaneLayout();
            double usableWidth = Math.Max(220, width - (compact ? 32 : 64));
            int divisor = Math.Max(1, Math.Min(_browserTabs.Count, compact ? 3 : 4));
            double slotWidth = usableWidth / divisor;
            return Math.Clamp(slotWidth - (compact ? 10 : 34), compact ? 90 : 96, compact ? 176 : 192);
        }

        private bool UpdateAdaptiveChromeLayout()
        {
            if (BrowserChromeBorder is null || BrowserChromeGrid is null || AddressBox is null)
            {
                return false;
            }

            bool compact = IsCompactPaneLayout();
            double targetTitleWidth = ResolveBrowserTabTitleWidth();
            bool refreshTabStrip = compact != _lastCompactLayout ||
                Math.Abs(targetTitleWidth - _lastBrowserTabTitleWidth) >= 2;
            BrowserChromeBorder.Padding = compact ? new Thickness(6, 4, 6, 4) : new Thickness(6, 5, 6, 4);
            BrowserChromeGrid.RowSpacing = compact ? 4 : 5;
            BrowserTabStripPanel.Spacing = compact ? 3 : 4;
            BrowserTabStripScroller.VerticalAlignment = VerticalAlignment.Center;

            double chromeButtonSize = compact ? 28 : 28;
            AddressBox.Height = chromeButtonSize;
            AddressBox.MinWidth = compact ? 120 : 180;

            ApplyChromeButtonMetrics(AddBrowserTabButton, compact ? 22 : 24);
            ApplyChromeButtonMetrics(BrowserBackButton, chromeButtonSize);
            ApplyChromeButtonMetrics(BrowserForwardButton, chromeButtonSize);
            ApplyChromeButtonMetrics(BrowserHomeButton, chromeButtonSize);
            ApplyChromeButtonMetrics(BrowserReloadButton, chromeButtonSize);
            ApplyChromeButtonMetrics(BrowserPagesButton, chromeButtonSize);
            ApplyChromeButtonMetrics(BrowserExtensionsButton, chromeButtonSize);
            ApplyChromeButtonMetrics(BrowserNewPaneButton, chromeButtonSize);

            _lastCompactLayout = compact;
            _lastBrowserTabTitleWidth = targetTitleWidth;
            if (refreshTabStrip)
            {
                RefreshBrowserTabStrip();
            }

            return refreshTabStrip;
        }

        private void ApplyChromeButtonMetrics(Button button, double size)
        {
            if (button is null)
            {
                return;
            }

            button.Width = size;
            button.Height = size;
            switch (button.Content)
            {
                case FontIcon icon:
                    icon.FontSize = size <= 22 ? 9.5 : 10.5;
                    break;
                case TextBlock text:
                    text.FontSize = size <= 22 ? 10.5 : 12;
                    break;
            }
        }

        private void UpdateAdaptiveZoomFactor()
        {
            if (!_initialized || BrowserView?.CoreWebView2 is null)
            {
                return;
            }

            double width = BrowserRoot?.ActualWidth > 0 ? BrowserRoot.ActualWidth : ActualWidth;
            double height = BrowserRoot?.ActualHeight > 0 ? BrowserRoot.ActualHeight : ActualHeight;
            if (width <= 0 || height <= 0)
            {
                return;
            }

            double widthScale = Math.Min(1.0, width / 640d);
            double heightScale = Math.Min(1.0, height / 420d);
            double zoomFactor = Math.Min(widthScale, heightScale);
            zoomFactor = zoomFactor >= 0.96 ? 1.0 : Math.Clamp(zoomFactor, CompactBrowserZoomFloor, 1.0);
            if (!double.IsNaN(_lastAppliedZoomFactor) && Math.Abs(_lastAppliedZoomFactor - zoomFactor) < 0.01)
            {
                return;
            }

            string zoomText = zoomFactor.ToString("0.###", CultureInfo.InvariantCulture);
            _lastAppliedZoomFactor = zoomFactor;
            _ = BrowserView.CoreWebView2.ExecuteScriptAsync(
                $"(() => {{ document.documentElement.style.zoom = '{zoomText}'; if (document.body) document.body.style.zoom = '{zoomText}'; }})()").AsTask();
        }

        private Brush ResolveBrowserTabDotBrush(BrowserTabSession tab, bool active)
        {
            string brushKey = ResolveBrowserTabDotBrushKey(tab?.Uri);
            Brush accent = ResolveShellBrush(brushKey);
            if (active)
            {
                return accent;
            }

            Windows.UI.Color fallbackBaseColor = brushKey switch
            {
                "ShellSuccessBrush" => Windows.UI.Color.FromArgb(0xFF, 0x16, 0xA3, 0x4A),
                "ShellWarningBrush" => Windows.UI.Color.FromArgb(0xFF, 0xCA, 0x8A, 0x04),
                "ShellInfoBrush" => Windows.UI.Color.FromArgb(0xFF, 0x25, 0x63, 0xEB),
                _ => Windows.UI.Color.FromArgb(0xFF, 0x7E, 0x86, 0x91),
            };
            return CreateTintedBrush(accent, 0xB8, fallbackBaseColor);
        }

        private static string ResolveBrowserTabDotBrushKey(string uri)
        {
            if (string.IsNullOrWhiteSpace(uri))
            {
                return "ShellTextTertiaryBrush";
            }

            if (string.Equals(uri, "winmux://start", StringComparison.OrdinalIgnoreCase))
            {
                return "ShellWarningBrush";
            }

            if (Uri.TryCreate(uri, UriKind.Absolute, out Uri absoluteUri))
            {
                if (absoluteUri.IsLoopback)
                {
                    return "ShellSuccessBrush";
                }

                if (string.Equals(absoluteUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(absoluteUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    return "ShellInfoBrush";
                }
            }

            return "ShellTextTertiaryBrush";
        }

        private Brush ResolveShellBrush(string key)
        {
            ElementTheme effectiveTheme = _themePreference == ElementTheme.Default
                ? (ActualTheme == ElementTheme.Light ? ElementTheme.Light : ElementTheme.Dark)
                : _themePreference;

            Windows.UI.Color color = (effectiveTheme, key) switch
            {
                (ElementTheme.Light, "ShellPageBackgroundBrush") => Windows.UI.Color.FromArgb(0xFF, 0xF5, 0xF6, 0xF8),
                (ElementTheme.Light, "ShellSurfaceBackgroundBrush") => Windows.UI.Color.FromArgb(0xFF, 0xFB, 0xFC, 0xFD),
                (ElementTheme.Light, "ShellMutedSurfaceBrush") => Windows.UI.Color.FromArgb(0xFF, 0xEE, 0xF1, 0xF4),
                (ElementTheme.Light, "ShellNavActiveBrush") => Windows.UI.Color.FromArgb(0xFF, 0xE7, 0xEB, 0xF0),
                (ElementTheme.Light, "ShellBorderBrush") => Windows.UI.Color.FromArgb(0xFF, 0xE1, 0xE5, 0xEA),
                (ElementTheme.Light, "ShellPaneActiveBorderBrush") => Windows.UI.Color.FromArgb(0xFF, 0x2C, 0x2D, 0x31),
                (ElementTheme.Light, "ShellTextPrimaryBrush") => Windows.UI.Color.FromArgb(0xFF, 0x1B, 0x1F, 0x24),
                (ElementTheme.Light, "ShellTextSecondaryBrush") => Windows.UI.Color.FromArgb(0xFF, 0x59, 0x60, 0x6A),
                (ElementTheme.Light, "ShellTextTertiaryBrush") => Windows.UI.Color.FromArgb(0xFF, 0x7E, 0x86, 0x91),
                (ElementTheme.Light, "ShellSuccessBrush") => Windows.UI.Color.FromArgb(0xFF, 0x16, 0xA3, 0x4A),
                (ElementTheme.Light, "ShellWarningBrush") => Windows.UI.Color.FromArgb(0xFF, 0xCA, 0x8A, 0x04),
                (ElementTheme.Light, "ShellInfoBrush") => Windows.UI.Color.FromArgb(0xFF, 0x25, 0x63, 0xEB),
                (ElementTheme.Dark, "ShellPageBackgroundBrush") => Windows.UI.Color.FromArgb(0xFF, 0x0C, 0x0D, 0x10),
                (ElementTheme.Dark, "ShellSurfaceBackgroundBrush") => Windows.UI.Color.FromArgb(0xFF, 0x10, 0x12, 0x16),
                (ElementTheme.Dark, "ShellMutedSurfaceBrush") => Windows.UI.Color.FromArgb(0xFF, 0x14, 0x16, 0x1B),
                (ElementTheme.Dark, "ShellNavActiveBrush") => Windows.UI.Color.FromArgb(0xFF, 0x17, 0x1A, 0x20),
                (ElementTheme.Dark, "ShellBorderBrush") => Windows.UI.Color.FromArgb(0xFF, 0x1E, 0x21, 0x27),
                (ElementTheme.Dark, "ShellPaneActiveBorderBrush") => Windows.UI.Color.FromArgb(0xFF, 0xC9, 0xCD, 0xD4),
                (ElementTheme.Dark, "ShellTextPrimaryBrush") => Windows.UI.Color.FromArgb(0xFF, 0xF3, 0xF4, 0xF6),
                (ElementTheme.Dark, "ShellTextSecondaryBrush") => Windows.UI.Color.FromArgb(0xFF, 0xA7, 0xAD, 0xB7),
                (ElementTheme.Dark, "ShellTextTertiaryBrush") => Windows.UI.Color.FromArgb(0xFF, 0x7A, 0x80, 0x8B),
                (ElementTheme.Dark, "ShellSuccessBrush") => Windows.UI.Color.FromArgb(0xFF, 0x4A, 0xDE, 0x80),
                (ElementTheme.Dark, "ShellWarningBrush") => Windows.UI.Color.FromArgb(0xFF, 0xFB, 0xBF, 0x24),
                (ElementTheme.Dark, "ShellInfoBrush") => Windows.UI.Color.FromArgb(0xFF, 0x60, 0xA5, 0xFA),
                _ => default,
            };

            if (color != default)
            {
                return new SolidColorBrush(color);
            }

            return (Brush)Application.Current.Resources[key];
        }

        private string ResolveCssColor(string key, Windows.UI.Color fallbackColor)
        {
            Windows.UI.Color color = ResolveShellBrush(key) is SolidColorBrush solid
                ? solid.Color
                : fallbackColor;
            return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        }

        private string ResolveCssColor(string key, byte alpha, Windows.UI.Color fallbackColor)
        {
            Windows.UI.Color color = ResolveShellBrush(key) is SolidColorBrush solid
                ? solid.Color
                : fallbackColor;
            return $"rgba({color.R}, {color.G}, {color.B}, {(alpha / 255d).ToString("0.###", CultureInfo.InvariantCulture)})";
        }

        private static Brush CreateTintedBrush(Brush source, byte alpha, Windows.UI.Color fallbackBaseColor)
        {
            Windows.UI.Color baseColor = source is SolidColorBrush solid
                ? solid.Color
                : fallbackBaseColor;
            return new SolidColorBrush(Windows.UI.Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B));
        }

        private void ApplyBrowserTabButtonState(Button button, bool active)
        {
            if (button is null)
            {
                return;
            }

            if (active)
            {
                button.Background = CreateTintedBrush(ResolveShellBrush("ShellPaneActiveBorderBrush"), 0x08, Windows.UI.Color.FromArgb(0xFF, 0x2C, 0x2D, 0x31));
                button.BorderBrush = CreateTintedBrush(ResolveShellBrush("ShellPaneActiveBorderBrush"), 0x2C, Windows.UI.Color.FromArgb(0xFF, 0x2C, 0x2D, 0x31));
                button.Foreground = ResolveShellBrush("ShellTextPrimaryBrush");
                button.BorderThickness = new Thickness(1);
                return;
            }

            button.Background = CreateTintedBrush(ResolveShellBrush("ShellBorderBrush"), 0x03, Windows.UI.Color.FromArgb(0xFF, 0x7E, 0x86, 0x91));
            button.BorderBrush = CreateTintedBrush(ResolveShellBrush("ShellBorderBrush"), 0x12, Windows.UI.Color.FromArgb(0xFF, 0x7E, 0x86, 0x91));
            button.Foreground = ResolveShellBrush("ShellTextSecondaryBrush");
            button.BorderThickness = new Thickness(1);
        }

        private void UpdateNavigationButtons()
        {
            bool canNavigate = _initialized && BrowserView.CoreWebView2 is not null;
            if (BrowserBackButton is not null)
            {
                BrowserBackButton.IsEnabled = canNavigate && BrowserView.CoreWebView2.CanGoBack;
            }

            if (BrowserForwardButton is not null)
            {
                BrowserForwardButton.IsEnabled = canNavigate && BrowserView.CoreWebView2.CanGoForward;
            }
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_disposed || _initialized)
            {
                return;
            }

            await Task.Yield();
            if (_disposed)
            {
                return;
            }

            try
            {
                await EnsureInitializedAsync();
            }
            catch (Exception ex)
            {
                HandleBrowserInitializationFailure(ex);
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
        }

        private void OnActualThemeChanged(FrameworkElement sender, object args)
        {
            if (_themePreference == ElementTheme.Default && string.Equals(_currentUri, "winmux://start", StringComparison.OrdinalIgnoreCase))
            {
                _ = NavigateToStartPageAsync();
            }

            UpdateBrowserTabSelectionVisuals();
        }

        private void QueueLayoutRefresh()
        {
            _layoutTimer.Stop();
            _layoutTimer.Start();
        }

        private void OnLayoutTimerTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
        {
            _layoutTimer.Stop();
            UpdateAdaptiveChromeLayout();
            UpdateAdaptiveZoomFactor();
            _ = NotifyBrowserResizedAsync();
        }

        private async Task NavigateToStartPageAsync()
        {
            RaiseInteractionRequested();
            EnsureBrowserTabExists();
            _currentUri = "winmux://start";
            _currentTitle = "Start";
            AddressBox.Text = string.Empty;
            SyncSelectedBrowserTab(uri: _currentUri, title: _currentTitle);
            UpdateCredentialAutofillStatus();
            if (_initialized && BrowserView.CoreWebView2 is not null)
            {
                _lastAppliedZoomFactor = double.NaN;
                TryNavigateToMarkup(BrowserView.CoreWebView2, BuildStartPageHtml(), "start-page", _currentUri);
            }

            UpdateNavigationButtons();
            RaiseStateChangedIfNeeded();
            await Task.CompletedTask;
        }

        private async void OnAddBrowserTabClicked(object sender, RoutedEventArgs e)
        {
            RaiseInteractionRequested();
            await AddBrowserTabAsync().ConfigureAwait(true);
        }

        private async void OnBrowserTabClicked(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string tabId)
            {
                return;
            }

            RaiseInteractionRequested();
            await SelectBrowserTabAsync(tabId).ConfigureAwait(true);
        }

        private async void OnBrowserTabCloseClicked(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string tabId)
            {
                return;
            }

            RaiseInteractionRequested();
            await CloseBrowserTabAsync(tabId).ConfigureAwait(true);
        }

        private void OnAddressBoxGotFocus(object sender, RoutedEventArgs e)
        {
            AddressBox.SelectAll();
        }

        private void OnBackClicked(object sender, RoutedEventArgs e)
        {
            if (!_initialized || BrowserView.CoreWebView2 is null || !BrowserView.CoreWebView2.CanGoBack)
            {
                return;
            }

            RaiseInteractionRequested();
            BrowserView.CoreWebView2.GoBack();
        }

        private void OnForwardClicked(object sender, RoutedEventArgs e)
        {
            if (!_initialized || BrowserView.CoreWebView2 is null || !BrowserView.CoreWebView2.CanGoForward)
            {
                return;
            }

            RaiseInteractionRequested();
            BrowserView.CoreWebView2.GoForward();
        }

        private void OnHomeClicked(object sender, RoutedEventArgs e)
        {
            _ = NavigateToStartPageAsync();
        }

        private void OnReloadClicked(object sender, RoutedEventArgs e)
        {
            if (!_initialized || BrowserView.CoreWebView2 is null)
            {
                return;
            }

            RaiseInteractionRequested();
            if (string.Equals(_currentUri, "winmux://start", StringComparison.OrdinalIgnoreCase))
            {
                _ = NavigateToStartPageAsync();
                return;
            }

            BrowserView.CoreWebView2.Reload();
        }

        private void OnOpenCurrentPageInPaneClicked(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(_currentUri) && !string.Equals(_currentUri, "winmux://start", StringComparison.OrdinalIgnoreCase))
            {
                OpenPaneRequested?.Invoke(this, _currentUri);
                return;
            }

            OpenPaneRequested?.Invoke(this, null);
        }

        private void OnAddressBoxKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Enter)
            {
                return;
            }

            RaiseInteractionRequested();
            Navigate(AddressBox.Text);
            e.Handled = true;
        }

        private void OnPagesClicked(object sender, RoutedEventArgs e)
        {
            RaiseInteractionRequested();
            EnsureBrowserTabExists();
            BrowserTabSession selectedTab = GetSelectedBrowserTab();

            MenuFlyout flyout = new();
            flyout.Items.Add(new MenuFlyoutItem
            {
                Text = string.IsNullOrWhiteSpace(_currentTitle) ? "Current page" : _currentTitle,
                IsEnabled = false,
            });
            flyout.Items.Add(new MenuFlyoutItem
            {
                Text = string.IsNullOrWhiteSpace(_currentUri) ? "No page loaded yet" : _currentUri,
                IsEnabled = false,
            });
            flyout.Items.Add(new MenuFlyoutSeparator());

            MenuFlyoutItem duplicateCurrentPageItem = new()
            {
                Text = "Open this page in another pane",
                IsEnabled = !string.IsNullOrWhiteSpace(_currentUri) && !string.Equals(_currentUri, "winmux://start", StringComparison.OrdinalIgnoreCase),
            };
            duplicateCurrentPageItem.Click += (_, _) => OpenPaneRequested?.Invoke(this, _currentUri);
            flyout.Items.Add(duplicateCurrentPageItem);

            MenuFlyoutItem newTabItem = new()
            {
                Text = "Open a new browser tab",
            };
            newTabItem.Click += async (_, _) => await AddBrowserTabAsync().ConfigureAwait(true);
            flyout.Items.Add(newTabItem);

            MenuFlyoutItem closeTabItem = new()
            {
                Text = "Close this tab",
                IsEnabled = _browserTabs.Count > 1,
            };
            closeTabItem.Click += async (_, _) => await CloseBrowserTabAsync(selectedTab.Id).ConfigureAwait(true);
            flyout.Items.Add(closeTabItem);

            MenuFlyoutItem blankPaneItem = new()
            {
                Text = "Open a blank browser pane",
            };
            blankPaneItem.Click += (_, _) => OpenPaneRequested?.Invoke(this, null);
            flyout.Items.Add(blankPaneItem);

            MenuFlyoutItem autofillItem = new()
            {
                Text = "Autofill this page",
                IsEnabled = BrowserCredentialStore.GetCredentialCount() > 0
                    && !string.IsNullOrWhiteSpace(_currentUri)
                    && !string.Equals(_currentUri, "winmux://start", StringComparison.OrdinalIgnoreCase),
            };
            autofillItem.Click += async (_, _) => await ManualAutofillCurrentPageAsync().ConfigureAwait(true);
            flyout.Items.Add(autofillItem);

            IReadOnlyList<BrowserCredentialMatch> matches = BrowserCredentialStore.ResolveMatchesForUri(_currentUri);
            if (matches.Count > 0)
            {
                flyout.Items.Add(new MenuFlyoutSeparator());
                foreach (BrowserCredentialMatch match in matches.Take(6))
                {
                    MenuFlyoutItem credentialItem = new()
                    {
                        Text = string.IsNullOrWhiteSpace(match.Username)
                            ? $"Autofill {match.Host}"
                            : $"Autofill {match.Username}",
                    };
                    credentialItem.Click += async (_, _) => await AutofillCredentialAsync(match).ConfigureAwait(true);
                    flyout.Items.Add(credentialItem);
                }
            }

            if (_browserTabs.Count > 0)
            {
                flyout.Items.Add(new MenuFlyoutSeparator());
                flyout.Items.Add(new MenuFlyoutItem
                {
                    Text = "Tabs in this pane",
                    IsEnabled = false,
                });

                foreach (BrowserTabSession tab in _browserTabs.Take(8))
                {
                    MenuFlyoutItem tabItem = new()
                    {
                        Text = string.IsNullOrWhiteSpace(tab.Title) ? "New tab" : tab.Title,
                        Tag = tab.Id,
                    };
                    tabItem.Click += async (_, _) => await SelectBrowserTabAsync(tab.Id).ConfigureAwait(true);
                    flyout.Items.Add(tabItem);
                }
            }

            flyout.Items.Add(new MenuFlyoutItem
            {
                Text = $"{_browserTabs.Count} tab{(_browserTabs.Count == 1 ? string.Empty : "s")} in this browser pane.",
                IsEnabled = false,
            });
            flyout.ShowAt(BrowserPagesButton);
        }

        private void OnExtensionsClicked(object sender, RoutedEventArgs e)
        {
            RaiseInteractionRequested();

            MenuFlyout flyout = new();
            flyout.Items.Add(new MenuFlyoutItem
            {
                Text = _profileSeedStatus,
                IsEnabled = false,
            });
            flyout.Items.Add(new MenuFlyoutItem
            {
                Text = _extensionImportStatus,
                IsEnabled = false,
            });
            flyout.Items.Add(new MenuFlyoutSeparator());

            if (_installedExtensions.Count == 0)
            {
                flyout.Items.Add(new MenuFlyoutItem
                {
                    Text = "No browser extensions are installed in this WinMux profile.",
                    IsEnabled = false,
                });
            }
            else
            {
                foreach (BrowserExtensionSnapshot extension in _installedExtensions.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
                {
                    flyout.Items.Add(new MenuFlyoutItem
                    {
                        Text = extension.Name,
                        IsEnabled = false,
                    });
                }
            }

            flyout.ShowAt(BrowserExtensionsButton);
        }

        private void OnInteractionRequested(object sender, RoutedEventArgs e)
        {
            RaiseInteractionRequested();
        }

        private void OnBrowserPaneSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.NewSize.Width <= 1 || e.NewSize.Height <= 1)
            {
                return;
            }

            double width = Math.Round(e.NewSize.Width);
            double height = Math.Round(e.NewSize.Height);
            if (Math.Abs(_lastQueuedLayoutWidth - width) < 1 &&
                Math.Abs(_lastQueuedLayoutHeight - height) < 1)
            {
                return;
            }

            _lastQueuedLayoutWidth = width;
            _lastQueuedLayoutHeight = height;
            QueueLayoutRefresh();
        }

        private void RaiseInteractionRequested()
        {
            InteractionRequested?.Invoke(this, EventArgs.Empty);
        }

        public async Task<string> ExecuteBrowserScriptAsync(string script)
        {
            await EnsureInitializedAsync().ConfigureAwait(true);
            CoreWebView2 core = await WaitForCoreWebView2Async().ConfigureAwait(true);
            string effectiveScript = string.IsNullOrWhiteSpace(script) ? "document.title" : script;
            return await core.ExecuteScriptAsync(effectiveScript).AsTask().ConfigureAwait(true);
        }

        public Task AddTabAsync(string initialUri = null) => AddBrowserTabAsync(initialUri);

        public Task SelectTabAsync(string tabId) => SelectBrowserTabAsync(tabId);

        public Task CloseTabAsync(string tabId = null) => CloseBrowserTabAsync(string.IsNullOrWhiteSpace(tabId) ? _selectedBrowserTabId : tabId);

        public async Task ManualAutofillCurrentPageAsync()
        {
            await EnsureInitializedAsync().ConfigureAwait(true);
            await TryAutofillMatchingCredentialsAsync().ConfigureAwait(true);
        }

        public async Task<(string Path, int Width, int Height)> CaptureBrowserPreviewAsync(string outputPath)
        {
            await EnsureInitializedAsync().ConfigureAwait(true);
            CoreWebView2 core = await WaitForCoreWebView2Async().ConfigureAwait(true);

            string finalPath = string.IsNullOrWhiteSpace(outputPath)
                ? Path.Combine(Path.GetTempPath(), $"winmux-browser-{Environment.ProcessId}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.png")
                : outputPath;

            string targetDirectory = Path.GetDirectoryName(finalPath);
            if (!string.IsNullOrWhiteSpace(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            using FileStream stream = new(finalPath, FileMode.Create, FileAccess.Write, FileShare.None);
            using IRandomAccessStream randomAccessStream = stream.AsRandomAccessStream();
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, randomAccessStream).AsTask().ConfigureAwait(true);
            await stream.FlushAsync().ConfigureAwait(true);
            return (finalPath, (int)Math.Round(BrowserView.ActualWidth), (int)Math.Round(BrowserView.ActualHeight));
        }

        private void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (!args.IsSuccess && !string.Equals(_currentUri, "winmux://start", StringComparison.OrdinalIgnoreCase))
            {
                UpdateCredentialAutofillStatus();
                TryNavigateToMarkup(sender, BuildErrorPageHtml(args.WebErrorStatus.ToString(), _currentUri), "navigate.error-page", _currentUri);
                LogBrowserEvent("navigate.failed", $"Browser navigation failed for {_currentUri}", new Dictionary<string, string>
                {
                    ["error"] = args.WebErrorStatus.ToString(),
                    ["uri"] = _currentUri ?? string.Empty,
                });
                RaiseStateChangedIfNeeded();
                return;
            }

            if (!string.Equals(_currentUri, "winmux://start", StringComparison.OrdinalIgnoreCase))
            {
                AddressBox.Text = BrowserView.Source?.ToString() ?? _currentUri ?? string.Empty;
            }

            _lastAppliedZoomFactor = double.NaN;
            UpdateAdaptiveZoomFactor();
            UpdateCredentialAutofillStatus();
            UpdateNavigationButtons();
            LogBrowserEvent("navigate.completed", $"Browser navigation completed for {_currentUri}", new Dictionary<string, string>
            {
                ["uri"] = _currentUri ?? string.Empty,
                ["success"] = args.IsSuccess.ToString(),
            });
            RaiseStateChangedIfNeeded();
        }

        private void HandleBrowserInitializationFailure(Exception ex)
        {
            _profileSeedStatus = "Browser pane failed to initialize";
            LogBrowserEvent("webview.load_failed", $"Browser pane load failed: {ex.Message}", new Dictionary<string, string>
            {
                ["error"] = ex.ToString(),
                ["initialUri"] = InitialUri ?? string.Empty,
                ["selectedTabId"] = _selectedBrowserTabId ?? string.Empty,
            });
            TryNavigateToMarkup(
                BrowserView?.CoreWebView2,
                BuildErrorPageHtml(ex.Message, InitialUri ?? _currentUri),
                "webview.load_failed",
                InitialUri ?? _currentUri);
            UpdateNavigationButtons();
            RaiseStateChangedIfNeeded();
        }

        private bool TryNavigateCore(CoreWebView2 core, string uri, string reason, string tabId = null)
        {
            if (_disposed || core is null || string.IsNullOrWhiteSpace(uri))
            {
                return false;
            }

            try
            {
                core.Navigate(uri);
                LogBrowserEvent("navigate.requested", $"Navigating browser pane to {uri}", new Dictionary<string, string>
                {
                    ["reason"] = reason ?? string.Empty,
                    ["uri"] = uri,
                    ["tabId"] = tabId ?? string.Empty,
                });
                return true;
            }
            catch (Exception ex)
            {
                LogBrowserEvent("navigate.exception", $"Browser navigation threw for {uri}: {ex.Message}", new Dictionary<string, string>
                {
                    ["reason"] = reason ?? string.Empty,
                    ["uri"] = uri,
                    ["tabId"] = tabId ?? string.Empty,
                    ["error"] = ex.ToString(),
                });
                TryNavigateToMarkup(core, BuildErrorPageHtml(ex.Message, uri), "navigate.exception", uri);
                return false;
            }
        }

        private bool TryNavigateToMarkup(CoreWebView2 core, string markup, string reason, string uriContext = null)
        {
            if (_disposed || core is null || string.IsNullOrWhiteSpace(markup))
            {
                return false;
            }

            try
            {
                core.NavigateToString(markup);
                if (string.Equals(reason, "start-page", StringComparison.Ordinal))
                {
                    LogBrowserEvent("start-page.shown", "Browser start page rendered");
                }

                return true;
            }
            catch (Exception ex)
            {
                LogBrowserEvent("navigate.markup_failed", $"Browser markup navigation failed: {ex.Message}", new Dictionary<string, string>
                {
                    ["reason"] = reason ?? string.Empty,
                    ["uri"] = uriContext ?? string.Empty,
                    ["error"] = ex.ToString(),
                });
                return false;
            }
        }

        private void OnDocumentTitleChanged(CoreWebView2 sender, object args)
        {
            string title = sender.DocumentTitle;
            if (string.IsNullOrWhiteSpace(title))
            {
                title = string.Equals(_currentUri, "winmux://start", StringComparison.OrdinalIgnoreCase)
                    ? "Preview"
                    : ProjectName ?? "Preview";
            }

            string normalizedTitle = title.Trim();
            bool titleChanged = !string.Equals(_currentTitle, normalizedTitle, StringComparison.Ordinal);
            _currentTitle = normalizedTitle;
            SyncSelectedBrowserTab(title: _currentTitle);
            if (titleChanged)
            {
                TitleChanged?.Invoke(this, _currentTitle);
            }

            RaiseStateChangedIfNeeded();
        }

        private void OnSourceChanged(CoreWebView2 sender, CoreWebView2SourceChangedEventArgs args)
        {
            if (sender.Source is null)
            {
                return;
            }

            _currentUri = sender.Source;
            if (!string.Equals(_currentUri, "about:blank", StringComparison.OrdinalIgnoreCase))
            {
                AddressBox.Text = _currentUri;
            }

            _lastAppliedZoomFactor = double.NaN;
            SyncSelectedBrowserTab(uri: _currentUri);
            UpdateNavigationButtons();
            LogBrowserEvent("source.changed", $"Browser source changed to {_currentUri}");
            RaiseStateChangedIfNeeded();
        }

        private string BuildBrowserTabStripStructureSignature(double targetTitleWidth, bool compact, bool showInlineCloseButtons)
        {
            StringBuilder builder = new();
            builder.Append(compact ? '1' : '0')
                .Append('|')
                .Append(showInlineCloseButtons ? '1' : '0')
                .Append('|')
                .Append(Math.Round(targetTitleWidth).ToString(CultureInfo.InvariantCulture))
                .Append('|');

            foreach (BrowserTabSession tab in _browserTabs)
            {
                builder.Append(tab.Id)
                    .Append('|');
            }

            return builder.ToString();
        }

        private string BuildStateChangeSignature()
        {
            StringBuilder builder = new();
            builder.Append(_selectedBrowserTabId ?? string.Empty)
                .Append('|')
                .Append(_currentUri ?? string.Empty)
                .Append('|')
                .Append(_currentTitle ?? string.Empty)
                .Append('|');

            foreach (BrowserTabSession tab in _browserTabs)
            {
                builder.Append(tab.Id)
                    .Append(':')
                    .Append(tab.Title ?? string.Empty)
                    .Append(':')
                    .Append(tab.Uri ?? string.Empty)
                    .Append('|');
            }

            return builder.ToString();
        }

        private void RaiseStateChangedIfNeeded()
        {
            string signature = BuildStateChangeSignature();
            if (string.Equals(signature, _lastStateChangeSignature, StringComparison.Ordinal))
            {
                return;
            }

            _lastStateChangeSignature = signature;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        private async void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
        {
            string targetUri = args.Uri;
            if (string.IsNullOrWhiteSpace(targetUri))
            {
                return;
            }

            args.Handled = true;
            LogBrowserEvent("popup.redirected", $"Redirected popup to internal browser tab {targetUri}", new Dictionary<string, string>
            {
                ["uri"] = targetUri,
            });
            await AddBrowserTabAsync(targetUri).ConfigureAwait(true);
        }

        private void OnContextMenuRequested(CoreWebView2 sender, CoreWebView2ContextMenuRequestedEventArgs args)
        {
            IReadOnlyList<BrowserCredentialMatch> matches = BrowserCredentialStore.ResolveMatchesForUri(_currentUri);
            if (matches.Count == 0)
            {
                return;
            }

            _credentialContextMenuMatches.Clear();
            CoreWebView2ContextMenuItem submenu = sender.Environment.CreateContextMenuItem(
                "Autofill with WinMux",
                null,
                CoreWebView2ContextMenuItemKind.Submenu);

            foreach (BrowserCredentialMatch match in matches.Take(6))
            {
                CoreWebView2ContextMenuItem item = sender.Environment.CreateContextMenuItem(
                    string.IsNullOrWhiteSpace(match.Username) ? match.Host : $"{match.Username}  •  {match.Host}",
                    null,
                    CoreWebView2ContextMenuItemKind.Command);
                _credentialContextMenuMatches[item.CommandId] = match;
                item.CustomItemSelected += OnCredentialContextMenuItemSelected;
                submenu.Children.Add(item);
            }

            args.MenuItems.Insert(0, submenu);
        }

        private async void OnCredentialContextMenuItemSelected(CoreWebView2ContextMenuItem sender, object args)
        {
            if (!_credentialContextMenuMatches.TryGetValue(sender.CommandId, out BrowserCredentialMatch match))
            {
                return;
            }

            await AutofillCredentialAsync(match).ConfigureAwait(true);
        }

        private void UpdateCredentialAutofillStatus()
        {
            if (BrowserCredentialStore.GetCredentialCount() == 0)
            {
                _credentialAutofillStatus = "No imported WinMux credentials.";
                return;
            }

            BrowserCredentialMatch match = BrowserCredentialStore.ResolveForUri(_currentUri);
            _credentialAutofillStatus = match is null
                ? $"Imported WinMux credentials available ({BrowserCredentialStore.GetCredentialCount()} entries), but none exactly match this page."
                : string.Equals(match.MatchScope, "origin", StringComparison.OrdinalIgnoreCase)
                    ? $"Imported WinMux credentials matched this page origin ({match.Origin})."
                    : $"Imported WinMux credentials matched this host ({match.Host}).";
        }

        private async Task TryAutofillMatchingCredentialsAsync()
        {
            if (!_initialized || BrowserView.CoreWebView2 is null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(_currentUri) ||
                string.Equals(_currentUri, "winmux://start", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(_currentUri, "about:blank", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            BrowserCredentialMatch match = BrowserCredentialStore.ResolveForUri(_currentUri);
            if (match is null)
            {
                UpdateCredentialAutofillStatus();
                return;
            }

            await AutofillCredentialAsync(match).ConfigureAwait(true);
        }

        private async Task AutofillCredentialAsync(BrowserCredentialMatch match)
        {
            if (!_initialized || BrowserView.CoreWebView2 is null || match is null)
            {
                return;
            }

            string username = JsonSerializer.Serialize(match.Username ?? string.Empty);
            string password = JsonSerializer.Serialize(match.Password ?? string.Empty);
            string host = JsonSerializer.Serialize(match.Host ?? string.Empty);
            string script = $$"""
(() => {
  const usernameValue = {{username}};
  const passwordValue = {{password}};
  const matchedHost = {{host}};

  const isVisible = (element) => {
    if (!element) return false;
    const style = window.getComputedStyle(element);
    const rect = element.getBoundingClientRect();
    return style.display !== 'none' && style.visibility !== 'hidden' && rect.width > 0 && rect.height > 0;
  };

  const setValue = (element, value) => {
    if (!element || !value || element.value) return false;
    element.focus();
    element.value = value;
    element.dispatchEvent(new Event('input', { bubbles: true }));
    element.dispatchEvent(new Event('change', { bubbles: true }));
    return true;
  };

  const findCandidate = (selectors) => {
    for (const selector of selectors) {
      const candidates = Array.from(document.querySelectorAll(selector));
      for (const candidate of candidates) {
        if (isVisible(candidate) && !candidate.disabled && !candidate.readOnly) {
          return candidate;
        }
      }
    }

    return null;
  };

  const usernameSelectors = [
    'input[type="email"]',
    'input[autocomplete="username"]',
    'input[name*="user" i]',
    'input[name*="email" i]',
    'input[id*="user" i]',
    'input[id*="email" i]',
    'input[type="text"]'
  ];
  const passwordSelectors = [
    'input[type="password"]',
    'input[autocomplete="current-password"]'
  ];

  const apply = () => {
    const passwordCandidates = Array.from(document.querySelectorAll('input[type="password"], input[autocomplete="current-password"], input[autocomplete="new-password"]'))
      .filter(candidate => isVisible(candidate) && !candidate.disabled && !candidate.readOnly);
    const hasNewPasswordField = passwordCandidates.some(candidate => (candidate.autocomplete || '').toLowerCase() === 'new-password');
    const currentPasswordField = passwordCandidates.find(candidate => (candidate.autocomplete || '').toLowerCase() === 'current-password') || null;
    if (hasNewPasswordField || (passwordCandidates.length > 1 && !currentPasswordField)) {
      return {
        matchedHost,
        filledUsername: false,
        filledPassword: false,
        hasUsernameField: false,
        hasPasswordField: passwordCandidates.length > 0,
        blockedReason: hasNewPasswordField ? 'new-password-form' : 'multi-password-form'
      };
    }

    const passwordField = currentPasswordField || findCandidate(passwordSelectors);
    const scope = passwordField?.form || document;
    const usernameField = Array.from(scope.querySelectorAll(usernameSelectors.join(',')))
      .find(candidate => isVisible(candidate) && !candidate.disabled && !candidate.readOnly) || null;
    const filledUsername = setValue(usernameField, usernameValue);
    const filledPassword = setValue(passwordField, passwordValue);
    return {
      matchedHost,
      filledUsername,
      filledPassword,
      hasUsernameField: !!usernameField,
      hasPasswordField: !!passwordField,
      blockedReason: null
    };
  };

  const initial = apply();
  let attempts = 0;
  const timer = window.setInterval(() => {
    attempts += 1;
    const result = apply();
    if (result.blockedReason ||
        ((result.filledUsername || !result.hasUsernameField) &&
        (result.filledPassword || !result.hasPasswordField || !passwordValue) ||
        attempts >= 20)) {
      window.clearInterval(timer);
    }
  }, 500);

  return initial;
})()
""";

            try
            {
                string result = await BrowserView.CoreWebView2.ExecuteScriptAsync(script).AsTask().ConfigureAwait(true);
                BrowserAutofillResult payload = JsonSerializer.Deserialize<BrowserAutofillResult>(result, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                });
                if (!string.IsNullOrWhiteSpace(payload?.BlockedReason))
                {
                    _credentialAutofillStatus = payload.BlockedReason switch
                    {
                        "new-password-form" => "WinMux blocked autofill on a sign-up or password-reset form.",
                        "multi-password-form" => "WinMux blocked autofill on a multi-password form.",
                        _ => "WinMux blocked autofill on an unsafe form.",
                    };
                }
                else
                {
                    _credentialAutofillStatus = string.IsNullOrWhiteSpace(match.Username)
                        ? $"Imported WinMux credentials autofilled for {match.Host}."
                        : $"Imported WinMux credentials autofilled using {match.Username}.";
                }

                LogBrowserEvent("credentials.autofill_attempted", $"Attempted WinMux credential autofill for {match.Host}", new Dictionary<string, string>
                {
                    ["credentialId"] = match.Id ?? string.Empty,
                    ["host"] = match.Host ?? string.Empty,
                    ["origin"] = match.Origin ?? string.Empty,
                    ["matchScope"] = match.MatchScope ?? string.Empty,
                    ["hasUsername"] = (!string.IsNullOrWhiteSpace(match.Username)).ToString(),
                    ["result"] = result ?? string.Empty,
                });
            }
            catch (Exception ex)
            {
                _credentialAutofillStatus = $"Imported WinMux credentials failed to autofill for {match.Host}.";
                LogBrowserEvent("credentials.autofill_failed", $"WinMux credential autofill failed for {match.Host}: {ex.Message}", new Dictionary<string, string>
                {
                    ["host"] = match.Host ?? string.Empty,
                    ["error"] = ex.Message,
                });
            }
        }

        private void StartDeferredSetup(CoreWebView2 core)
        {
            if (_disposed || core is null)
            {
                return;
            }

            Task existingTask = _deferredSetupTask;
            if (existingTask is not null)
            {
                return;
            }

            _deferredSetupTask = RunDeferredSetupAsync(core);
        }

        private async Task RunDeferredSetupAsync(CoreWebView2 core)
        {
            try
            {
                await Task.Delay(150).ConfigureAwait(true);
                if (_disposed || !_initialized)
                {
                    return;
                }

                await RefreshInstalledExtensionsAsync(core).ConfigureAwait(true);
                if (_disposed)
                {
                    return;
                }

                UpdateCredentialAutofillStatus();
                RaiseStateChangedIfNeeded();
            }
            catch (Exception ex)
            {
                LogBrowserEvent("webview.deferred_setup_failed", $"Deferred browser setup failed: {ex.Message}", new Dictionary<string, string>
                {
                    ["error"] = ex.Message,
                });
            }
        }

        private async Task NotifyBrowserResizedAsync()
        {
            if (!_initialized || BrowserView.CoreWebView2 is null)
            {
                return;
            }

            int width = (int)Math.Round(BrowserView.ActualWidth);
            int height = (int)Math.Round(BrowserView.ActualHeight);
            if (width <= 0 || height <= 0)
            {
                return;
            }

            if (width == _lastResizeNotificationWidth && height == _lastResizeNotificationHeight)
            {
                return;
            }

            _lastResizeNotificationWidth = width;
            _lastResizeNotificationHeight = height;

            try
            {
                await BrowserView.CoreWebView2.ExecuteScriptAsync("window.dispatchEvent(new Event('resize'));").AsTask().ConfigureAwait(true);
            }
            catch
            {
            }
        }

        private async Task<CoreWebView2Environment> GetEnvironmentAsync()
        {
            string key = GetBrowserProfileRootPath();
            Task<CoreWebView2Environment> environmentTask;

            lock (EnvironmentSync)
            {
                if (!EnvironmentTasks.TryGetValue(key, out environmentTask))
                {
                    environmentTask = CreateEnvironmentForProfileRootAsync(key);
                    EnvironmentTasks[key] = environmentTask;
                }
            }

            return await environmentTask.ConfigureAwait(true);
        }

        private static string GetBrowserProfileRootPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinMux",
                IsolatedBrowserProfileFolderName);
        }

        private async Task<CoreWebView2Environment> CreateEnvironmentForProfileRootAsync(string userDataFolder)
        {
            string resolvedRoot = await Task.Run(() =>
            {
                Directory.CreateDirectory(userDataFolder);
                _profileSeedStatus = "Isolated WinMux browser profile";
                return userDataFolder;
            }).ConfigureAwait(true);

            return await CreateEnvironmentAsync(resolvedRoot).ConfigureAwait(true);
        }

        private static async Task<CoreWebView2Environment> CreateEnvironmentAsync(string userDataFolder)
        {
            CoreWebView2EnvironmentOptions options = new();
            string additionalArguments = Environment.GetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS");
            if (!string.IsNullOrWhiteSpace(additionalArguments))
            {
                options.AdditionalBrowserArguments = additionalArguments;
            }
            options.AreBrowserExtensionsEnabled = true;

            return await CoreWebView2Environment.CreateWithOptionsAsync(null, userDataFolder, options);
        }

        private string ResolveProfileRoot()
        {
            string root = GetBrowserProfileRootPath();
            Directory.CreateDirectory(root);
            return root;
        }

        private static bool IsBrowserDevToolsEnabled()
        {
            string value = Environment.GetEnvironmentVariable("WINMUX_ENABLE_BROWSER_DEVTOOLS");
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
        }

        private async Task RefreshInstalledExtensionsAsync(CoreWebView2 core)
        {
            _installedExtensions.Clear();

            IReadOnlyList<CoreWebView2BrowserExtension> installedExtensions = await core.Profile.GetBrowserExtensionsAsync();
            foreach (CoreWebView2BrowserExtension extension in installedExtensions)
            {
                if (!string.IsNullOrWhiteSpace(extension?.Name))
                {
                    _installedExtensions.Add(new BrowserExtensionSnapshot
                    {
                        Id = extension.Id,
                        Name = extension.Name,
                    });
                }
            }

            _extensionImportStatus = _installedExtensions.Count == 0
                ? "No browser extensions are installed in this WinMux profile."
                : $"{_installedExtensions.Count} browser extension{(_installedExtensions.Count == 1 ? string.Empty : "s")} installed in this WinMux profile.";
        }

        private string BuildStartPageHtml()
        {
            bool darkTheme = (_themePreference == ElementTheme.Default ? ActualTheme : _themePreference) != ElementTheme.Light;
            string projectName = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(ProjectName) ? "Project preview" : ProjectName);
            string projectPath = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(ProjectPath) ? "No active path yet." : ProjectPath);
            string rootLabel = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(ProjectRootPath)
                ? (string.IsNullOrWhiteSpace(ProjectPath) ? "No project root yet." : ProjectPath)
                : ProjectRootPath);
            string profileStatus = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(_profileSeedStatus) ? "Isolated WinMux browser profile reused across panes and projects" : _profileSeedStatus);
            string extensionStatus = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(_extensionImportStatus) ? "No browser extensions are installed in this WinMux profile." : _extensionImportStatus);
            string credentialStatus = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(_credentialAutofillStatus) ? "No imported WinMux credentials." : _credentialAutofillStatus);
            string background = ResolveCssColor("ShellPageBackgroundBrush", Windows.UI.Color.FromArgb(0xFF, 0xF5, 0xF6, 0xF8));
            string surface = ResolveCssColor("ShellSurfaceBackgroundBrush", Windows.UI.Color.FromArgb(0xFF, 0xFB, 0xFC, 0xFD));
            string mutedSurface = ResolveCssColor("ShellMutedSurfaceBrush", Windows.UI.Color.FromArgb(0xFF, 0xEE, 0xF1, 0xF4));
            string border = ResolveCssColor("ShellBorderBrush", Windows.UI.Color.FromArgb(0xFF, 0xE1, 0xE5, 0xEA));
            string borderSoft = ResolveCssColor("ShellBorderBrush", 0x88, Windows.UI.Color.FromArgb(0xFF, 0xE1, 0xE5, 0xEA));
            string text = ResolveCssColor("ShellTextPrimaryBrush", Windows.UI.Color.FromArgb(0xFF, 0x1B, 0x1F, 0x24));
            string subtext = ResolveCssColor("ShellTextSecondaryBrush", Windows.UI.Color.FromArgb(0xFF, 0x59, 0x60, 0x6A));
            string faint = ResolveCssColor("ShellTextTertiaryBrush", Windows.UI.Color.FromArgb(0xFF, 0x7E, 0x86, 0x91));
            string accent = ResolveCssColor("ShellPaneActiveBorderBrush", Windows.UI.Color.FromArgb(0xFF, 0x2C, 0x2D, 0x31));
            string success = ResolveCssColor("ShellSuccessBrush", Windows.UI.Color.FromArgb(0xFF, 0x16, 0xA3, 0x4A));
            string warning = ResolveCssColor("ShellWarningBrush", Windows.UI.Color.FromArgb(0xFF, 0xCA, 0x8A, 0x04));
            string info = ResolveCssColor("ShellInfoBrush", Windows.UI.Color.FromArgb(0xFF, 0x25, 0x63, 0xEB));

            string[] previewUrls =
            {
                "http://127.0.0.1:3000",
                "http://127.0.0.1:5173",
                "http://127.0.0.1:8000",
                "http://127.0.0.1:8080",
            };

            StringBuilder links = new();
            foreach (string previewUrl in previewUrls)
            {
                string label = previewUrl.Contains("3000", StringComparison.Ordinal) ? "Common app dev server"
                    : previewUrl.Contains("5173", StringComparison.Ordinal) ? "Vite preview"
                    : previewUrl.Contains("8000", StringComparison.Ordinal) ? "API or docs server"
                    : "Alternate local preview";
                string note = previewUrl.Contains("5173", StringComparison.Ordinal) ? "hot reload"
                    : previewUrl.Contains("8000", StringComparison.Ordinal) ? "docs"
                    : previewUrl.Contains("8080", StringComparison.Ordinal) ? "alt"
                    : "open";
                links.Append($"""
<a class="launch" href="{previewUrl}">
  <span class="launch-copy">
    <span class="launch-kicker">{label}</span>
    <span class="launch-url">{previewUrl}</span>
  </span>
  <span class="launch-note">{note}</span>
</a>
""");
            }

            return $$"""
<!doctype html>
<html>
<head>
  <meta charset="utf-8" />
  <title>{{projectName}}</title>
  <style>
    :root {
      color-scheme: {{(darkTheme ? "dark" : "light")}};
    }
    body {
      margin: 0;
      background: {{background}};
      color: {{text}};
      font-family: "Segoe UI Variable Text", "Segoe UI", sans-serif;
    }
    .wrap {
      padding: 24px 26px 30px;
      display: grid;
      gap: 18px;
      max-width: 940px;
    }
    .hero {
      display: grid;
      gap: 10px;
      padding-bottom: 12px;
      border-bottom: 1px solid {{border}};
    }
    .hero-top {
      display: flex;
      align-items: center;
      gap: 10px;
    }
    .mark {
      width: 24px;
      height: 24px;
      display: grid;
      place-items: center;
      border: 1px solid {{border}};
      border-radius: 4px;
      background: {{surface}};
      font-size: 8.5px;
      font-weight: 700;
      letter-spacing: 0.09em;
      color: {{text}};
    }
    .hero-copy,
    .hero-summary {
      display: grid;
      gap: 4px;
      min-width: 0;
    }
    .eyebrow {
      font-size: 10.5px;
      font-weight: 600;
      letter-spacing: 0.08em;
      text-transform: uppercase;
      color: {{faint}};
    }
    h1 {
      margin: 0;
      font-size: 22px;
      line-height: 1.08;
      font-weight: 650;
    }
    p {
      margin: 0;
      color: {{subtext}};
      line-height: 1.45;
      max-width: 720px;
    }
    .grid {
      display: grid;
      grid-template-columns: minmax(0, 1.1fr) minmax(280px, 0.9fr);
      gap: 24px;
    }
    .aside {
      display: grid;
      gap: 18px;
      align-content: start;
    }
    .section {
      display: grid;
      gap: 10px;
      min-width: 0;
    }
    .section-header {
      display: grid;
      gap: 3px;
    }
    .section-label {
      font-size: 10.5px;
      font-weight: 600;
      letter-spacing: 0.08em;
      text-transform: uppercase;
      color: {{faint}};
    }
    .section-title {
      font-size: 13px;
      font-weight: 600;
      color: {{text}};
    }
    .section-copy {
      font-size: 12px;
      color: {{subtext}};
      line-height: 1.45;
    }
    .launches,
    .facts {
      display: grid;
      gap: 4px;
    }
    .launch {
      display: grid;
      grid-template-columns: minmax(0, 1fr) auto;
      align-items: center;
      gap: 10px;
      padding: 10px;
      border: 1px solid {{border}};
      border-radius: 4px;
      background: {{surface}};
      color: {{text}};
      text-decoration: none;
      transition: border-color 120ms ease, background 120ms ease, color 120ms ease;
    }
    .launch:hover {
      border-color: {{accent}};
      background: {{mutedSurface}};
    }
    .launch-copy {
      display: grid;
      gap: 2px;
      min-width: 0;
    }
    .launch-kicker {
      font-size: 10.5px;
      color: {{subtext}};
    }
    .launch-url {
      font-size: 12.5px;
      font-weight: 600;
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
    }
    .launch-note {
      font-size: 10px;
      font-weight: 600;
      letter-spacing: 0.08em;
      text-transform: uppercase;
      color: {{faint}};
    }
    .facts {
      border-top: 1px solid {{borderSoft}};
    }
    .fact {
      display: grid;
      gap: 3px;
      padding: 9px 0;
      border-bottom: 1px solid {{borderSoft}};
    }
    .fact-title {
      font-size: 10.5px;
      font-weight: 600;
      letter-spacing: 0.08em;
      text-transform: uppercase;
      color: {{faint}};
    }
    .fact-value {
      font-size: 12px;
      color: {{text}};
      line-height: 1.45;
      word-break: break-word;
    }
    .fact-value.path {
      font-family: Consolas, monospace;
      font-size: 11.5px;
    }
    .status-strip {
      display: flex;
      flex-wrap: wrap;
      gap: 8px 14px;
      align-items: center;
      color: {{subtext}};
      font-size: 11px;
    }
    .status-item {
      display: inline-flex;
      gap: 6px;
      align-items: center;
    }
    .status-dot {
      width: 5px;
      height: 5px;
      border-radius: 2.5px;
      background: {{success}};
    }
    .status-dot.warning {
      background: {{warning}};
    }
    .status-dot.info {
      background: {{info}};
    }
    code {
      font-family: Consolas, monospace;
      color: {{text}};
    }
    @media (max-width: 760px) {
      .wrap {
        padding: 18px 16px 22px;
      }
      .grid {
        grid-template-columns: 1fr;
      }
    }
  </style>
</head>
<body>
  <div class="wrap">
    <div class="hero">
      <div class="hero-top">
        <div class="mark">WM</div>
        <div class="hero-copy">
          <div class="eyebrow">Browser workspace</div>
          <h1>{{projectName}}</h1>
        </div>
      </div>
      <div class="hero-summary">
        <p>Open previews, docs, and live project context without leaving the current thread. This pane stays aligned with the same shell palette while reusing one WinMux-managed browser profile across panes and projects.</p>
      </div>
    </div>

    <div class="status-strip">
      <span class="status-item"><span class="status-dot"></span>WinMux-managed browser profile reused across projects</span>
      <span class="status-item"><span class="status-dot info"></span>Local preview shortcuts for this workspace</span>
      <span class="status-item"><span class="status-dot warning"></span>Use the address field above for any URL</span>
    </div>

    <div class="grid">
      <section class="section">
        <div class="section-header">
          <div class="section-label">Quick launch</div>
          <div class="section-title">Common local previews</div>
          <div class="section-copy">Keep the high-frequency local routes one click away. These links are intentionally sparse so the pane stays useful instead of noisy.</div>
        </div>
        <div class="launches">{{links}}</div>
      </section>

      <div class="aside">
        <section class="section">
          <div class="section-header">
            <div class="section-label">Workspace</div>
            <div class="section-title">Current project context</div>
          </div>
          <div class="facts">
            <div class="fact">
              <div class="fact-title">Project root</div>
              <div class="fact-value path"><code>{{rootLabel}}</code></div>
            </div>
            <div class="fact">
              <div class="fact-title">Active path</div>
              <div class="fact-value path"><code>{{projectPath}}</code></div>
            </div>
            <div class="fact">
              <div class="fact-title">Suggested use</div>
              <div class="fact-value">Run local apps, open docs, compare external references, or keep web context beside the terminal and diff panes.</div>
            </div>
          </div>
        </section>

        <section class="section">
          <div class="section-header">
            <div class="section-label">Browser state</div>
            <div class="section-title">WinMux profile and helpers</div>
          </div>
          <div class="facts">
            <div class="fact">
              <div class="fact-title">Profile</div>
              <div class="fact-value">{{profileStatus}}</div>
            </div>
            <div class="fact">
              <div class="fact-title">Extensions</div>
              <div class="fact-value">{{extensionStatus}}</div>
            </div>
            <div class="fact">
              <div class="fact-title">Autofill</div>
              <div class="fact-value">{{credentialStatus}}</div>
            </div>
          </div>
        </section>
      </div>
    </div>
  </div>
</body>
</html>
""";
        }

        private string BuildErrorPageHtml(string error, string uri)
        {
            string safeUri = WebUtility.HtmlEncode(uri ?? string.Empty);
            string safeError = WebUtility.HtmlEncode(error ?? "Navigation failed");
            string background = ResolveCssColor("ShellPageBackgroundBrush", Windows.UI.Color.FromArgb(0xFF, 0x0C, 0x0D, 0x10));
            string surface = ResolveCssColor("ShellSurfaceBackgroundBrush", Windows.UI.Color.FromArgb(0xFF, 0x10, 0x12, 0x16));
            string border = ResolveCssColor("ShellBorderBrush", Windows.UI.Color.FromArgb(0xFF, 0x1E, 0x21, 0x27));
            string borderSoft = ResolveCssColor("ShellBorderBrush", 0x88, Windows.UI.Color.FromArgb(0xFF, 0x1E, 0x21, 0x27));
            string text = ResolveCssColor("ShellTextPrimaryBrush", Windows.UI.Color.FromArgb(0xFF, 0xF3, 0xF4, 0xF6));
            string subtext = ResolveCssColor("ShellTextSecondaryBrush", Windows.UI.Color.FromArgb(0xFF, 0xA7, 0xAD, 0xB7));
            string faint = ResolveCssColor("ShellTextTertiaryBrush", Windows.UI.Color.FromArgb(0xFF, 0x7A, 0x80, 0x8B));
            return $$"""
<!doctype html>
<html>
<head>
  <meta charset="utf-8" />
  <title>Preview unavailable</title>
  <style>
    body {
      margin: 0;
      background: {{background}};
      color: {{text}};
      font-family: "Segoe UI Variable Text", "Segoe UI", sans-serif;
    }
    .wrap {
      padding: 24px 26px;
      display: grid;
      gap: 18px;
      max-width: 760px;
    }
    .hero {
      display: grid;
      gap: 10px;
      padding-bottom: 12px;
      border-bottom: 1px solid {{border}};
    }
    .hero-top {
      display: flex;
      align-items: center;
      gap: 10px;
    }
    .mark {
      width: 24px;
      height: 24px;
      display: grid;
      place-items: center;
      border: 1px solid {{border}};
      border-radius: 4px;
      background: {{surface}};
      font-size: 8.5px;
      font-weight: 700;
      letter-spacing: 0.09em;
      color: {{text}};
    }
    .hero-copy {
      display: grid;
      gap: 4px;
    }
    .label {
      font-size: 10.5px;
      font-weight: 600;
      letter-spacing: 0.08em;
      text-transform: uppercase;
      color: {{faint}};
    }
    h1 {
      margin: 0;
      font-size: 20px;
      line-height: 1.1;
    }
    p {
      margin: 0;
      color: {{subtext}};
      line-height: 1.5;
    }
    .facts {
      display: grid;
      border-top: 1px solid {{borderSoft}};
    }
    .fact {
      display: grid;
      gap: 4px;
      padding: 10px 0;
      border-bottom: 1px solid {{borderSoft}};
    }
    .fact-title {
      font-size: 10.5px;
      font-weight: 600;
      letter-spacing: 0.08em;
      text-transform: uppercase;
      color: {{faint}};
    }
    .fact-value {
      color: {{text}};
      line-height: 1.45;
      word-break: break-word;
    }
    code {
      color: {{text}};
      font-family: Consolas, monospace;
    }
  </style>
</head>
<body>
  <div class="wrap">
    <div class="hero">
      <div class="hero-top">
        <div class="mark">WM</div>
        <div class="hero-copy">
          <div class="label">Browser pane</div>
          <h1>Preview unavailable</h1>
        </div>
      </div>
      <p>The current pane could not load <code>{{safeUri}}</code>. The browser surface stayed native, but this destination returned an error.</p>
    </div>

    <div class="facts">
      <div class="fact">
        <div class="fact-title">Requested address</div>
        <div class="fact-value"><code>{{safeUri}}</code></div>
      </div>
      <div class="fact">
        <div class="fact-title">Error detail</div>
        <div class="fact-value"><code>{{safeError}}</code></div>
      </div>
    </div>
  </div>
</body>
</html>
""";
        }

        private static string NormalizeUri(string value)
        {
            string trimmed = value?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return "about:blank";
            }

            if (trimmed.Contains("://", StringComparison.Ordinal))
            {
                return trimmed;
            }

            if (trimmed.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("127.0.0.1", StringComparison.OrdinalIgnoreCase))
            {
                return $"http://{trimmed}";
            }

            return $"https://{trimmed}";
        }
    }
}
