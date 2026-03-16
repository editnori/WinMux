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
using System.Security.Cryptography;
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
        private const string SharedBrowserProfileFolderName = "browser-profile-shared";
        private const string LegacyBrowserProfilesFolderName = "browser-profiles";
        private const string ProfileSeedMetadataFileName = "profile-source.json";
        private const int CurrentProfileSeedFormatVersion = 1;
        private const double CompactPaneWidthThreshold = 560;
        private const double CompactPaneHeightThreshold = 320;
        private const double CompactBrowserZoomFloor = 0.72;

        private static readonly string[] PreferredChromiumExtensionIds =
        {
            "cjpalhdlnbpafiamejdnhcphjbkeiagm", // uBlock Origin
            "fcoeoabgfenejglbffodgkkbkcdhcgfn", // Claude
        };

        private readonly struct BrowserExtensionSnapshot
        {
            public string Id { get; init; }

            public string Name { get; init; }
        }

        private readonly struct SeedCopySummary
        {
            public int CopiedFiles { get; init; }

            public int SkippedFiles { get; init; }
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

        private sealed class ChromiumProfileSeedSource
        {
            public string BrowserName { get; init; }

            public string UserDataRoot { get; init; }

            public string ProfileDirectoryName { get; init; }

            public string ProfilePath { get; init; }

            public string ProfileDisplayName { get; init; }

            public string ExtensionsPath => Path.Combine(ProfilePath ?? string.Empty, "Extensions");
        }

        private sealed class ProfileSeedMetadata
        {
            public string BrowserName { get; set; }

            public string UserDataRoot { get; set; }

            public string ProfileDirectoryName { get; set; }

            public string ProfileDisplayName { get; set; }

            public string ImportedAtUtc { get; set; }

            public int SeedFormatVersion { get; set; }

            public bool RepairComplete { get; set; }
        }

        private static readonly string[] ChromiumProfileFilesToSeed =
        {
            "Bookmarks",
            "Bookmarks.bak",
            "Preferences",
            "Secure Preferences",
        };

        private static readonly string[] ChromiumProfileDirectoriesToSeed =
        {
            "Extensions",
            "Extension State",
            "IndexedDB",
            "Local Extension Settings",
            "Local Storage",
            "Service Worker",
            "Session Storage",
            "Storage",
            "Sync Extension Settings",
        };

        private static readonly Dictionary<string, Task<CoreWebView2Environment>> EnvironmentTasks = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object EnvironmentSync = new();
        private Task _initializationTask;
        private bool _initialized;
        private bool _disposed;
        private string _currentUri;
        private string _currentTitle = "Preview";
        private ElementTheme _themePreference = ElementTheme.Default;
        private string _profileSeedStatus = "Shared WinMux browser profile";
        private string _extensionImportStatus = "No browser extensions imported yet.";
        private string _credentialAutofillStatus = "No imported WinMux credentials.";
        private readonly List<BrowserTabSession> _browserTabs = new();
        private readonly Dictionary<string, Button> _browserTabButtonsById = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TextBlock> _browserTabTitlesById = new(StringComparer.Ordinal);
        private readonly List<BrowserExtensionSnapshot> _installedExtensions = new();
        private readonly Dictionary<int, BrowserCredentialMatch> _credentialContextMenuMatches = new();
        private bool _credentialCaptureScriptInjected;
        private string _selectedBrowserTabId;
        private ChromiumProfileSeedSource _profileSeedSource;
        private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _layoutTimer;
        private bool _lastCompactLayout;
        private double _lastBrowserTabTitleWidth = -1;
        private int _lastResizeNotificationWidth;
        private int _lastResizeNotificationHeight;
        private double _lastAppliedZoomFactor = double.NaN;

        private sealed class BrowserCredentialCaptureMessage
        {
            public string Type { get; set; }

            public string Url { get; set; }

            public string Username { get; set; }

            public string Password { get; set; }

            public string CaptureKind { get; set; }
        }

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

        public async void ApplyTheme(ElementTheme theme)
        {
            _themePreference = theme;
            RequestedTheme = theme;

            if (_initialized && string.Equals(_currentUri, "winmux://start", StringComparison.OrdinalIgnoreCase))
            {
                await NavigateToStartPageAsync();
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
            if (BrowserView.CoreWebView2 is not null)
            {
                BrowserView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
                BrowserView.CoreWebView2.DocumentTitleChanged -= OnDocumentTitleChanged;
                BrowserView.CoreWebView2.SourceChanged -= OnSourceChanged;
                BrowserView.CoreWebView2.ContextMenuRequested -= OnContextMenuRequested;
                BrowserView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                BrowserView.CoreWebView2.NewWindowRequested -= OnNewWindowRequested;
            }
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
                BrowserView.CoreWebView2.Navigate(normalized);
                LogBrowserEvent("navigate.requested", $"Navigating browser pane to {normalized}");
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
                StateChanged?.Invoke(this, EventArgs.Empty);
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

        private async Task InitializeBrowserAsync()
        {
            try
            {
                CoreWebView2Environment environment = await GetEnvironmentAsync();
                await BrowserView.EnsureCoreWebView2Async(environment);
                LogBrowserEvent("webview.ready", "Browser WebView2 initialized with shared browser profile");
            }
            catch (Exception ex)
            {
                LogBrowserEvent("webview.profile_failed", $"Shared browser profile failed: {ex.Message}", new Dictionary<string, string>
                {
                    ["error"] = ex.Message,
                });

                await BrowserView.EnsureCoreWebView2Async();
                _profileSeedStatus = "Fell back to default WebView2 profile";
                LogBrowserEvent("webview.ready", "Browser WebView2 initialized with default profile");
            }

            await ConfigureInitializedBrowserAsync(await WaitForCoreWebView2Async().ConfigureAwait(true));
        }

        private async Task ConfigureInitializedBrowserAsync(CoreWebView2 core)
        {
            try
            {
                if (core is null)
                {
                    throw new InvalidOperationException("Browser CoreWebView2 was null after initialization.");
                }

                LogBrowserEvent("webview.configure", "Configuring browser pane settings", new Dictionary<string, string>
                {
                    ["step"] = "settings.begin",
                });
                core.Settings.AreDevToolsEnabled = true;
                core.Settings.AreBrowserAcceleratorKeysEnabled = true;
                core.Settings.IsStatusBarEnabled = false;
                LogBrowserEvent("webview.configure", "Configured basic browser settings", new Dictionary<string, string>
                {
                    ["step"] = "settings.basic",
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
                core.WebMessageReceived += OnWebMessageReceived;
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

                await ImportSeededExtensionsAsync(core).ConfigureAwait(true);
                await RefreshInstalledExtensionsAsync(core).ConfigureAwait(true);
                await EnsureCredentialCaptureScriptAsync(core).ConfigureAwait(true);
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
                    core.Navigate(selectedUri);
                    LogBrowserEvent("navigate.requested", $"Navigating browser pane to {selectedUri}");
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
                    core.Navigate(pendingUri);
                    LogBrowserEvent("navigate.requested", $"Navigating browser pane to {pendingUri}");
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
                    core.Navigate(initialUri);
                    LogBrowserEvent("navigate.requested", $"Navigating browser pane to {initialUri}");
                }

                RefreshBrowserTabStrip();
                UpdateNavigationButtons();
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
            if (!string.IsNullOrWhiteSpace(uri))
            {
                tab.Uri = uri.Trim();
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
            UpdateBrowserTabSelectionVisuals();
            UpdateNavigationButtons();
            StateChanged?.Invoke(this, EventArgs.Empty);

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
                BrowserView.CoreWebView2.Navigate(_currentUri);
                LogBrowserEvent("tab.selected", $"Selected browser tab {nextTab.Id}", new Dictionary<string, string>
                {
                    ["tabId"] = nextTab.Id,
                    ["uri"] = _currentUri ?? string.Empty,
                });
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

            BrowserTabStripPanel.Children.Clear();
            _browserTabButtonsById.Clear();
            _browserTabTitlesById.Clear();
            double targetTitleWidth = ResolveBrowserTabTitleWidth();
            bool compact = IsCompactPaneLayout();
            bool showInlineCloseButtons = !compact || _browserTabs.Count <= 2;
            foreach (BrowserTabSession tab in _browserTabs)
            {
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
                    MinWidth = compact ? 72 : 96,
                    MaxWidth = compact ? 152 : 192,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Padding = compact ? new Thickness(6, 1, 6, 1) : new Thickness(8, 2, 8, 2),
                };
                AutomationProperties.SetAutomationId(tabButton, $"browser-pane-tab-{tab.Id}");
                AutomationProperties.SetName(tabButton, string.IsNullOrWhiteSpace(tab.Title) ? "Browser tab" : tab.Title);
                tabButton.Click += OnBrowserTabClicked;

                TextBlock tabTitle = new()
                {
                    Text = string.IsNullOrWhiteSpace(tab.Title) ? "New tab" : tab.Title,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    FontWeight = string.Equals(tab.Id, _selectedBrowserTabId, StringComparison.Ordinal)
                        ? Microsoft.UI.Text.FontWeights.SemiBold
                        : Microsoft.UI.Text.FontWeights.Normal,
                };
                AutomationProperties.SetAutomationId(tabTitle, $"browser-pane-tab-title-{tab.Id}");
                tabButton.Content = tabTitle;
                ApplyBrowserTabButtonState(tabButton, string.Equals(tab.Id, _selectedBrowserTabId, StringComparison.Ordinal));
                _browserTabButtonsById[tab.Id] = tabButton;
                _browserTabTitlesById[tab.Id] = tabTitle;
                tabLayout.Children.Add(tabButton);

                if (showInlineCloseButtons)
                {
                    Button closeButton = new()
                    {
                        Style = (Style)Application.Current.Resources["ShellChromeButtonStyle"],
                        Tag = tab.Id,
                        Width = compact ? 20 : 22,
                        Height = compact ? 20 : 22,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Content = new FontIcon
                        {
                            FontSize = 9,
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
        }

        private void UpdateBrowserTabStripTitles()
        {
            if (_browserTabTitlesById.Count != _browserTabs.Count)
            {
                RefreshBrowserTabStrip();
                return;
            }

            foreach (BrowserTabSession tab in _browserTabs)
            {
                if (_browserTabTitlesById.TryGetValue(tab.Id, out TextBlock title))
                {
                    title.Text = string.IsNullOrWhiteSpace(tab.Title) ? "New tab" : tab.Title;
                }
            }
        }

        private void UpdateBrowserTabSelectionVisuals()
        {
            if (_browserTabButtonsById.Count != _browserTabs.Count || _browserTabTitlesById.Count != _browserTabs.Count)
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
            BrowserChromeBorder.Padding = compact ? new Thickness(6, 4, 6, 4) : new Thickness(8, 6, 8, 5);
            BrowserChromeGrid.RowSpacing = compact ? 4 : 6;
            BrowserTabStripPanel.Spacing = compact ? 2 : 4;
            BrowserTabStripScroller.VerticalAlignment = VerticalAlignment.Center;

            double chromeButtonSize = compact ? 28 : 30;
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

        private Brush ResolveShellBrush(string key)
        {
            ElementTheme effectiveTheme = _themePreference == ElementTheme.Default
                ? (ActualTheme == ElementTheme.Light ? ElementTheme.Light : ElementTheme.Dark)
                : _themePreference;

            Windows.UI.Color color = (effectiveTheme, key) switch
            {
                (ElementTheme.Light, "ShellMutedSurfaceBrush") => Windows.UI.Color.FromArgb(0xFF, 0xF4, 0xF4, 0xF5),
                (ElementTheme.Light, "ShellNavActiveBrush") => Windows.UI.Color.FromArgb(0xFF, 0xE7, 0xE7, 0xEB),
                (ElementTheme.Light, "ShellBorderBrush") => Windows.UI.Color.FromArgb(0xFF, 0xE4, 0xE4, 0xE7),
                (ElementTheme.Light, "ShellPaneActiveBorderBrush") => Windows.UI.Color.FromArgb(0xFF, 0x25, 0x63, 0xEB),
                (ElementTheme.Light, "ShellTextPrimaryBrush") => Windows.UI.Color.FromArgb(0xFF, 0x18, 0x18, 0x1B),
                (ElementTheme.Light, "ShellTextSecondaryBrush") => Windows.UI.Color.FromArgb(0xFF, 0x52, 0x52, 0x5B),
                (ElementTheme.Dark, "ShellMutedSurfaceBrush") => Windows.UI.Color.FromArgb(0xFF, 0x17, 0x18, 0x1C),
                (ElementTheme.Dark, "ShellNavActiveBrush") => Windows.UI.Color.FromArgb(0xFF, 0x1F, 0x22, 0x28),
                (ElementTheme.Dark, "ShellBorderBrush") => Windows.UI.Color.FromArgb(0xFF, 0x23, 0x25, 0x2B),
                (ElementTheme.Dark, "ShellPaneActiveBorderBrush") => Windows.UI.Color.FromArgb(0xFF, 0x60, 0xA5, 0xFA),
                (ElementTheme.Dark, "ShellTextPrimaryBrush") => Windows.UI.Color.FromArgb(0xFF, 0xFA, 0xFA, 0xFA),
                (ElementTheme.Dark, "ShellTextSecondaryBrush") => Windows.UI.Color.FromArgb(0xFF, 0xA1, 0xA1, 0xAA),
                _ => default,
            };

            if (color != default)
            {
                return new SolidColorBrush(color);
            }

            return (Brush)Application.Current.Resources[key];
        }

        private void ApplyBrowserTabButtonState(Button button, bool active)
        {
            if (button is null)
            {
                return;
            }

            button.Background = null;
            button.BorderBrush = null;
            button.Foreground = ResolveShellBrush(active ? "ShellTextPrimaryBrush" : "ShellTextSecondaryBrush");
            button.BorderThickness = new Thickness(0);
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
            await EnsureInitializedAsync();
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

            RefreshBrowserTabStrip();
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
                BrowserView.CoreWebView2.NavigateToString(BuildStartPageHtml());
                LogBrowserEvent("start-page.shown", "Browser start page rendered");
            }

            UpdateNavigationButtons();
            StateChanged?.Invoke(this, EventArgs.Empty);
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
                    Text = "No preferred shared-profile extensions are available in this pane.",
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
                sender.NavigateToString(BuildErrorPageHtml(args.WebErrorStatus.ToString(), _currentUri));
                LogBrowserEvent("navigate.failed", $"Browser navigation failed for {_currentUri}", new Dictionary<string, string>
                {
                    ["error"] = args.WebErrorStatus.ToString(),
                    ["uri"] = _currentUri ?? string.Empty,
                });
                StateChanged?.Invoke(this, EventArgs.Empty);
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
            StateChanged?.Invoke(this, EventArgs.Empty);
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

            _currentTitle = title.Trim();
            SyncSelectedBrowserTab(title: _currentTitle);
            TitleChanged?.Invoke(this, _currentTitle);
            StateChanged?.Invoke(this, EventArgs.Empty);
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

        private async void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            try
            {
                BrowserCredentialCaptureMessage message = JsonSerializer.Deserialize<BrowserCredentialCaptureMessage>(args.WebMessageAsJson);
                if (message is null || !string.Equals(message.Type, "credentialCapture", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                BrowserCredentialImportResult result = BrowserCredentialStore.SaveCredential(
                    string.IsNullOrWhiteSpace(message.Url) ? _currentUri : message.Url,
                    message.Username,
                    message.Password,
                    name: ProjectName,
                    note: string.IsNullOrWhiteSpace(message.CaptureKind)
                        ? "Captured from WinMux browser"
                        : $"Captured from WinMux browser ({message.CaptureKind})");

                if (!result.Ok)
                {
                    return;
                }

                UpdateCredentialAutofillStatus();
                LogBrowserEvent("credentials.saved", result.Message, new Dictionary<string, string>
                {
                    ["url"] = message.Url ?? _currentUri ?? string.Empty,
                    ["usernamePresent"] = (!string.IsNullOrWhiteSpace(message.Username)).ToString(),
                });
            }
            catch (Exception ex)
            {
                LogBrowserEvent("credentials.save_failed", $"Failed to save credential candidate: {ex.Message}", new Dictionary<string, string>
                {
                    ["error"] = ex.Message,
                });
            }
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

        private async Task EnsureCredentialCaptureScriptAsync(CoreWebView2 core)
        {
            if (_credentialCaptureScriptInjected)
            {
                return;
            }

            string script = """
(() => {
  if (!window.chrome?.webview || window.__winmuxCredentialCaptureInstalled) {
    return;
  }

  window.__winmuxCredentialCaptureInstalled = true;

  const visible = (element) => {
    if (!element) return false;
    const style = window.getComputedStyle(element);
    const rect = element.getBoundingClientRect();
    return style.display !== 'none' && style.visibility !== 'hidden' && rect.width > 0 && rect.height > 0;
  };

  const gather = (root) => {
    const scope = root instanceof Element ? root : document;
    const passwordFields = Array.from(scope.querySelectorAll('input[type="password"]'))
      .filter(candidate => visible(candidate) && !candidate.disabled && !candidate.readOnly);
    if (passwordFields.length === 0) return null;

    const newPasswordFields = passwordFields.filter(candidate => (candidate.autocomplete || '').toLowerCase() === 'new-password');
    const currentPasswordField = passwordFields.find(candidate => (candidate.autocomplete || '').toLowerCase() === 'current-password') || null;
    if (newPasswordFields.length > 0 || (passwordFields.length > 1 && !currentPasswordField)) {
      return null;
    }

    const passwordField = currentPasswordField || passwordFields[0];
    if (!passwordField || !passwordField.value) return null;

    const usernameField = scope.querySelector('input[type="email"], input[autocomplete="username"], input[name*="user" i], input[name*="email" i], input[id*="user" i], input[id*="email" i], input[type="text"]');
    return {
      type: 'credentialCapture',
      url: location.href,
      username: usernameField && visible(usernameField) ? usernameField.value : '',
      password: passwordField.value,
      captureKind: currentPasswordField ? 'current-password' : 'single-password'
    };
  };

  const postCandidate = (root) => {
    const payload = gather(root);
    if (payload) {
      window.chrome.webview.postMessage(payload);
    }
  };

  document.addEventListener('submit', (event) => {
    postCandidate(event.target);
  }, true);

  document.addEventListener('click', (event) => {
    const target = event.target instanceof Element ? event.target : null;
    const trigger = target ? target.closest('button, input[type="submit"], [role="button"]') : null;
    if (!trigger) {
      return;
    }

    window.setTimeout(() => postCandidate(trigger.form ?? document), 0);
  }, true);
})();
""";

            await core.AddScriptToExecuteOnDocumentCreatedAsync(script).AsTask().ConfigureAwait(true);
            _credentialCaptureScriptInjected = true;
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
            string key = GetSharedBrowserProfileRootPath();
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

        private static string GetSharedBrowserProfileRootPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinMux",
                SharedBrowserProfileFolderName);
        }

        private async Task<CoreWebView2Environment> CreateEnvironmentForProfileRootAsync(string userDataFolder)
        {
            string resolvedRoot = await Task.Run(() =>
            {
                Directory.CreateDirectory(userDataFolder);
                TrySeedProfileRootFromChromium(userDataFolder);
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
            string root = GetSharedBrowserProfileRootPath();
            Directory.CreateDirectory(root);
            return root;
        }

        private static string BuildStableProfileKey(string value)
        {
            byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim().ToLowerInvariant()));
            return Convert.ToHexString(bytes[..16]).ToLowerInvariant();
        }

        private void TrySeedProfileRootFromChromium(string targetRoot)
        {
            ProfileSeedMetadata existingMetadata = TryLoadProfileSeedMetadata(targetRoot);
            bool hasExistingEntries = Directory.EnumerateFileSystemEntries(targetRoot).Any();

            if (TryMigrateLegacyWinMuxProfile(targetRoot))
            {
                return;
            }

            ChromiumProfileSeedSource source = ResolveChromiumProfileSeedSource();
            if (source is null)
            {
                _profileSeedStatus = existingMetadata is null
                    ? "No Chromium profile detected on this machine"
                    : $"Reusing shared browser profile from {existingMetadata.BrowserName} · {existingMetadata.ProfileDisplayName}";
                return;
            }

            bool requiresSeedRepair = RequiresSharedProfileSeed(hasExistingEntries, existingMetadata);
            if (!requiresSeedRepair)
            {
                _profileSeedSource = source;
                _profileSeedStatus = $"Reusing shared browser profile from {existingMetadata.BrowserName} · {existingMetadata.ProfileDisplayName}";
                return;
            }

            try
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                LogBrowserEvent("profile.seed_started", $"Seeding shared browser profile from {source.UserDataRoot}", new Dictionary<string, string>
                {
                    ["browserName"] = source.BrowserName,
                    ["profileDirectoryName"] = source.ProfileDirectoryName,
                    ["sourceRoot"] = source.UserDataRoot,
                    ["sourceProfile"] = source.ProfilePath,
                    ["targetRoot"] = targetRoot,
                });

                bool overwriteExisting = ShouldOverwriteExistingSeed(hasExistingEntries, existingMetadata);
                SeedCopySummary summary = CopyChromiumProfileSeed(source, targetRoot, overwriteExisting: overwriteExisting);
                string sourceLabel = FormatProfileSeedLabel(source);
                string eventName;
                bool repairComplete = summary.SkippedFiles == 0;

                stopwatch.Stop();
                if (!hasExistingEntries)
                {
                    eventName = summary.SkippedFiles > 0 ? "profile.seeded_partial" : "profile.seeded";
                    _profileSeedStatus = summary.SkippedFiles > 0
                        ? $"Partially seeded shared browser profile from {sourceLabel} ({summary.CopiedFiles} copied, {summary.SkippedFiles} skipped)"
                        : $"Seeded shared browser profile from {sourceLabel} ({summary.CopiedFiles} files copied)";
                }
                else if (summary.CopiedFiles > 0)
                {
                    eventName = "profile.seed_repaired";
                    _profileSeedStatus = $"Completed shared browser profile repair from {sourceLabel} ({summary.CopiedFiles} copied)";
                }
                else
                {
                    SaveProfileSeedMetadata(targetRoot, source, repairComplete);
                    _profileSeedSource = source;
                    _profileSeedStatus = existingMetadata is null
                        ? $"Reusing shared browser profile from {sourceLabel}"
                        : $"Reusing shared browser profile from {existingMetadata.BrowserName} · {existingMetadata.ProfileDisplayName}";
                    return;
                }

                SaveProfileSeedMetadata(targetRoot, source, repairComplete);
                _profileSeedSource = source;
                LogBrowserEvent(eventName, $"Seeded browser profile from {source.UserDataRoot}", new Dictionary<string, string>
                {
                    ["browserName"] = source.BrowserName,
                    ["profileDirectoryName"] = source.ProfileDirectoryName,
                    ["sourceRoot"] = source.UserDataRoot,
                    ["sourceProfile"] = source.ProfilePath,
                    ["targetRoot"] = targetRoot,
                    ["copiedFiles"] = summary.CopiedFiles.ToString(),
                    ["skippedFiles"] = summary.SkippedFiles.ToString(),
                    ["durationMs"] = stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture),
                });
            }
            catch (Exception ex)
            {
                _profileSeedStatus = "Chrome profile seed failed";
                LogBrowserEvent("profile.seed_failed", $"Failed to seed browser profile from {source.UserDataRoot}: {ex.Message}", new Dictionary<string, string>
                {
                    ["browserName"] = source.BrowserName,
                    ["profileDirectoryName"] = source.ProfileDirectoryName,
                    ["sourceRoot"] = source.UserDataRoot,
                    ["sourceProfile"] = source.ProfilePath,
                    ["targetRoot"] = targetRoot,
                    ["error"] = ex.Message,
                });
            }
        }

        private bool TryMigrateLegacyWinMuxProfile(string targetRoot)
        {
            using IEnumerator<string> targetEntries = Directory.EnumerateFileSystemEntries(targetRoot).GetEnumerator();
            if (targetEntries.MoveNext())
            {
                return false;
            }

            string legacyRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinMux",
                LegacyBrowserProfilesFolderName);

            if (!Directory.Exists(legacyRoot))
            {
                return false;
            }

            string sourceProfile = Directory.GetDirectories(legacyRoot)
                .Where(candidate => !string.Equals(Path.GetFileName(candidate), SharedBrowserProfileFolderName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(GetLastWriteTimeSafeUtc)
                .FirstOrDefault(candidate => Directory.EnumerateFileSystemEntries(candidate).Any());

            if (string.IsNullOrWhiteSpace(sourceProfile))
            {
                return false;
            }

            try
            {
                SeedCopySummary summary = default;
                CopyDirectory(sourceProfile, targetRoot, ref summary, overwriteExisting: false);
                _profileSeedStatus = summary.CopiedFiles > 0
                    ? $"Migrated shared browser profile from legacy WinMux data ({summary.CopiedFiles} copied)"
                    : "Reusing shared browser profile";

                LogBrowserEvent("profile.migrated", $"Migrated shared browser profile from {sourceProfile}", new Dictionary<string, string>
                {
                    ["sourceRoot"] = sourceProfile,
                    ["targetRoot"] = targetRoot,
                    ["copiedFiles"] = summary.CopiedFiles.ToString(),
                    ["skippedFiles"] = summary.SkippedFiles.ToString(),
                });
                return summary.CopiedFiles > 0;
            }
            catch (Exception ex)
            {
                LogBrowserEvent("profile.migration_failed", $"Failed to migrate shared browser profile from {sourceProfile}: {ex.Message}", new Dictionary<string, string>
                {
                    ["sourceRoot"] = sourceProfile,
                    ["targetRoot"] = targetRoot,
                    ["error"] = ex.Message,
                });
                return false;
            }
        }

        private static string FormatProfileSeedLabel(ChromiumProfileSeedSource source)
        {
            if (source is null)
            {
                return "Chromium";
            }

            return string.IsNullOrWhiteSpace(source.ProfileDisplayName)
                ? source.BrowserName ?? "Chromium"
                : $"{source.BrowserName} · {source.ProfileDisplayName}";
        }

        private static bool RequiresSharedProfileSeed(bool hasExistingEntries, ProfileSeedMetadata existingMetadata)
        {
            if (!hasExistingEntries)
            {
                return true;
            }

            if (existingMetadata is null)
            {
                return true;
            }

            return existingMetadata.SeedFormatVersion < CurrentProfileSeedFormatVersion ||
                !existingMetadata.RepairComplete;
        }

        private static bool ShouldOverwriteExistingSeed(
            bool hasExistingEntries,
            ProfileSeedMetadata existingMetadata)
        {
            if (!hasExistingEntries)
            {
                return true;
            }

            return existingMetadata is null ||
                existingMetadata.SeedFormatVersion < CurrentProfileSeedFormatVersion;
        }

        private static ProfileSeedMetadata TryLoadProfileSeedMetadata(string targetRoot)
        {
            try
            {
                string metadataPath = Path.Combine(targetRoot, ProfileSeedMetadataFileName);
                if (!File.Exists(metadataPath))
                {
                    return null;
                }

                string json = File.ReadAllText(metadataPath);
                return JsonSerializer.Deserialize<ProfileSeedMetadata>(json);
            }
            catch
            {
                return null;
            }
        }

        private static void SaveProfileSeedMetadata(string targetRoot, ChromiumProfileSeedSource source, bool repairComplete)
        {
            if (string.IsNullOrWhiteSpace(targetRoot) || source is null)
            {
                return;
            }

            ProfileSeedMetadata metadata = new()
            {
                BrowserName = source.BrowserName,
                UserDataRoot = source.UserDataRoot,
                ProfileDirectoryName = source.ProfileDirectoryName,
                ProfileDisplayName = source.ProfileDisplayName,
                ImportedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                SeedFormatVersion = CurrentProfileSeedFormatVersion,
                RepairComplete = repairComplete,
            };

            string metadataPath = Path.Combine(targetRoot, ProfileSeedMetadataFileName);
            File.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata, new JsonSerializerOptions
            {
                WriteIndented = true,
            }));
        }

        private static DateTime GetLastWriteTimeSafeUtc(string path)
        {
            try
            {
                return Directory.GetLastWriteTimeUtc(path);
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        private static ChromiumProfileSeedSource ResolveChromiumProfileSeedSource()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            (string BrowserName, string UserDataRoot)[] candidates =
            {
                ("Chrome", Path.Combine(localAppData, "Google", "Chrome", "User Data")),
                ("Chromium", Path.Combine(localAppData, "Chromium", "User Data")),
                ("Brave", Path.Combine(localAppData, "BraveSoftware", "Brave-Browser", "User Data")),
            };

            List<ChromiumProfileSeedSource> sources = new();
            foreach ((string browserName, string candidateRoot) in candidates)
            {
                if (!Directory.Exists(candidateRoot))
                {
                    continue;
                }

                Dictionary<string, string> displayNames = TryReadProfileDisplayNames(candidateRoot);
                string preferredProfile = TryReadLastUsedProfile(candidateRoot);
                IEnumerable<string> profileDirectories = Directory.GetDirectories(candidateRoot)
                    .Where(candidate =>
                    {
                        string name = Path.GetFileName(candidate);
                        return string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase) ||
                               name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase);
                    })
                    .Where(candidate => Directory.EnumerateFileSystemEntries(candidate).Any());

                List<string> orderedProfiles = profileDirectories
                    .OrderByDescending(candidate => string.Equals(Path.GetFileName(candidate), preferredProfile, StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(GetLastWriteTimeSafeUtc)
                    .ToList();

                if (orderedProfiles.Count == 0)
                {
                    continue;
                }

                string profilePath = orderedProfiles[0];
                string profileDirectoryName = Path.GetFileName(profilePath);
                sources.Add(new ChromiumProfileSeedSource
                {
                    BrowserName = browserName,
                    UserDataRoot = candidateRoot,
                    ProfileDirectoryName = profileDirectoryName,
                    ProfilePath = profilePath,
                    ProfileDisplayName = displayNames.TryGetValue(profileDirectoryName, out string displayName)
                        ? displayName
                        : profileDirectoryName,
                });
            }

            return sources
                .OrderByDescending(candidate => GetLastWriteTimeSafeUtc(candidate.ProfilePath))
                .ThenBy(candidate => candidate.BrowserName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        private static string TryReadLastUsedProfile(string userDataRoot)
        {
            try
            {
                string localStatePath = Path.Combine(userDataRoot, "Local State");
                if (!File.Exists(localStatePath))
                {
                    return null;
                }

                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(localStatePath));
                if (!document.RootElement.TryGetProperty("profile", out JsonElement profileElement) ||
                    !profileElement.TryGetProperty("last_used", out JsonElement lastUsedElement))
                {
                    return null;
                }

                return lastUsedElement.GetString();
            }
            catch
            {
                return null;
            }
        }

        private static Dictionary<string, string> TryReadProfileDisplayNames(string userDataRoot)
        {
            Dictionary<string, string> results = new(StringComparer.OrdinalIgnoreCase);
            try
            {
                string localStatePath = Path.Combine(userDataRoot, "Local State");
                if (!File.Exists(localStatePath))
                {
                    return results;
                }

                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(localStatePath));
                if (!document.RootElement.TryGetProperty("profile", out JsonElement profileElement) ||
                    !profileElement.TryGetProperty("info_cache", out JsonElement infoCacheElement) ||
                    infoCacheElement.ValueKind != JsonValueKind.Object)
                {
                    return results;
                }

                foreach (JsonProperty property in infoCacheElement.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.Object &&
                        property.Value.TryGetProperty("name", out JsonElement nameElement) &&
                        !string.IsNullOrWhiteSpace(nameElement.GetString()))
                    {
                        results[property.Name] = nameElement.GetString();
                    }
                }
            }
            catch
            {
            }

            return results;
        }

        private static SeedCopySummary CopyChromiumProfileSeed(ChromiumProfileSeedSource source, string targetRoot, bool overwriteExisting)
        {
            string sourceProfile = source?.ProfilePath;
            if (string.IsNullOrWhiteSpace(sourceProfile) || !Directory.Exists(sourceProfile))
            {
                throw new DirectoryNotFoundException($"Chromium profile path '{sourceProfile}' was not found.");
            }

            SeedCopySummary summary = default;
            CopyFileIfExists(Path.Combine(source.UserDataRoot, "Local State"), Path.Combine(targetRoot, "Local State"), ref summary, overwriteExisting);
            CopyFileIfExists(Path.Combine(source.UserDataRoot, "Last Version"), Path.Combine(targetRoot, "Last Version"), ref summary, overwriteExisting);

            string targetDefault = Path.Combine(targetRoot, "Default");
            Directory.CreateDirectory(targetDefault);

            foreach (string relativeFile in ChromiumProfileFilesToSeed)
            {
                CopyFileIfExists(Path.Combine(sourceProfile, relativeFile), Path.Combine(targetDefault, relativeFile), ref summary, overwriteExisting);
            }

            foreach (string relativeDirectory in ChromiumProfileDirectoriesToSeed)
            {
                CopyDirectoryIfExists(Path.Combine(sourceProfile, relativeDirectory), Path.Combine(targetDefault, relativeDirectory), ref summary, overwriteExisting);
            }

            CopyFileIfExists(Path.Combine(sourceProfile, "Network", "Cookies"), Path.Combine(targetDefault, "Network", "Cookies"), ref summary, overwriteExisting);
            CopyFileIfExists(Path.Combine(sourceProfile, "Network", "Cookies-journal"), Path.Combine(targetDefault, "Network", "Cookies-journal"), ref summary, overwriteExisting);
            return summary;
        }

        private static void CopyDirectory(string sourceDirectory, string targetDirectory, ref SeedCopySummary summary, bool overwriteExisting)
        {
            Directory.CreateDirectory(targetDirectory);

            foreach (string sourceFile in Directory.GetFiles(sourceDirectory))
            {
                string targetFile = Path.Combine(targetDirectory, Path.GetFileName(sourceFile));
                CopyFileIfExists(sourceFile, targetFile, ref summary, overwriteExisting);
            }

            foreach (string sourceSubdirectory in Directory.GetDirectories(sourceDirectory))
            {
                string targetSubdirectory = Path.Combine(targetDirectory, Path.GetFileName(sourceSubdirectory));
                CopyDirectory(sourceSubdirectory, targetSubdirectory, ref summary, overwriteExisting);
            }
        }

        private static void CopyDirectoryIfExists(string sourceDirectory, string targetDirectory, ref SeedCopySummary summary, bool overwriteExisting)
        {
            if (!Directory.Exists(sourceDirectory))
            {
                return;
            }

            CopyDirectory(sourceDirectory, targetDirectory, ref summary, overwriteExisting);
        }

        private static void CopyFileIfExists(string sourceFile, string targetFile, ref SeedCopySummary summary, bool overwriteExisting)
        {
            if (!File.Exists(sourceFile))
            {
                return;
            }

            if (!overwriteExisting && (File.Exists(targetFile) || Directory.Exists(targetFile)))
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
                using FileStream sourceStream = new(sourceFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using FileStream targetStream = new(targetFile, FileMode.Create, FileAccess.Write, FileShare.None);
                sourceStream.CopyTo(targetStream);
                summary = new SeedCopySummary
                {
                    CopiedFiles = summary.CopiedFiles + 1,
                    SkippedFiles = summary.SkippedFiles,
                };
            }
            catch (IOException)
            {
                summary = new SeedCopySummary
                {
                    CopiedFiles = summary.CopiedFiles,
                    SkippedFiles = summary.SkippedFiles + 1,
                };
            }
            catch (UnauthorizedAccessException)
            {
                summary = new SeedCopySummary
                {
                    CopiedFiles = summary.CopiedFiles,
                    SkippedFiles = summary.SkippedFiles + 1,
                };
            }
        }

        private async Task ImportSeededExtensionsAsync(CoreWebView2 core)
        {
            string extensionsRoot = Path.Combine(ResolveProfileRoot(), "Default", "Extensions");
            if (!Directory.Exists(extensionsRoot))
            {
                ChromiumProfileSeedSource source = _profileSeedSource ?? ResolveChromiumProfileSeedSource();
                string fallbackExtensionsRoot = source?.ExtensionsPath;

                if (string.IsNullOrWhiteSpace(fallbackExtensionsRoot) || !Directory.Exists(fallbackExtensionsRoot))
                {
                    _extensionImportStatus = "No Chromium extension directory was found to import from.";
                    return;
                }

                extensionsRoot = fallbackExtensionsRoot;
            }

            IReadOnlyList<CoreWebView2BrowserExtension> installedExtensions = await core.Profile.GetBrowserExtensionsAsync();
            HashSet<string> installedIds = installedExtensions
                .Select(extension => extension.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            List<(string ExtensionId, string VersionDirectory)> availableExtensions = Directory.GetDirectories(extensionsRoot)
                .Select(extensionDirectory =>
                {
                    string extensionId = Path.GetFileName(extensionDirectory);
                    string versionDirectory = Directory.GetDirectories(extensionDirectory)
                        .OrderByDescending(Directory.GetLastWriteTimeUtc)
                        .FirstOrDefault(candidate => File.Exists(Path.Combine(candidate, "manifest.json")));
                    return (ExtensionId: extensionId, VersionDirectory: versionDirectory);
                })
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate.ExtensionId) && !string.IsNullOrWhiteSpace(candidate.VersionDirectory))
                .ToList();

            List<(string ExtensionId, string VersionDirectory)> preferredExtensions = availableExtensions
                .Where(candidate => PreferredChromiumExtensionIds.Contains(candidate.ExtensionId, StringComparer.OrdinalIgnoreCase))
                .OrderBy(candidate => Array.IndexOf(PreferredChromiumExtensionIds, candidate.ExtensionId))
                .ToList();

            if (preferredExtensions.Count == 0)
            {
                _extensionImportStatus = "Preferred Chrome extensions were not found in the local profile.";
                LogBrowserEvent("extension.preferred_missing", "No preferred Chromium extensions were available to import.", new Dictionary<string, string>
                {
                    ["extensionsRoot"] = extensionsRoot,
                });
                return;
            }

            int importedCount = 0;
            foreach ((string extensionId, string versionDirectory) in preferredExtensions)
            {
                if (string.IsNullOrWhiteSpace(extensionId) || installedIds.Contains(extensionId))
                {
                    continue;
                }

                try
                {
                    CoreWebView2BrowserExtension installed = await core.Profile.AddBrowserExtensionAsync(versionDirectory);
                    installedIds.Add(installed.Id);
                    importedCount++;
                    LogBrowserEvent("extension.installed", $"Installed browser extension {installed.Name}", new Dictionary<string, string>
                    {
                        ["extensionId"] = installed.Id ?? extensionId,
                        ["name"] = installed.Name ?? extensionId,
                        ["sourcePath"] = versionDirectory,
                    });
                }
                catch (Exception ex)
                {
                    LogBrowserEvent("extension.install_failed", $"Failed to install browser extension {extensionId}: {ex.Message}", new Dictionary<string, string>
                    {
                        ["extensionId"] = extensionId,
                        ["sourcePath"] = versionDirectory,
                        ["error"] = ex.Message,
                    });
                }
            }

            _extensionImportStatus = importedCount > 0
                ? $"Imported {importedCount} preferred Chrome extension{(importedCount == 1 ? string.Empty : "s")}."
                : "Preferred extensions were already present in this browser profile.";
        }

        private async Task RefreshInstalledExtensionsAsync(CoreWebView2 core)
        {
            _installedExtensions.Clear();

            IReadOnlyList<CoreWebView2BrowserExtension> installedExtensions = await core.Profile.GetBrowserExtensionsAsync();
            foreach (CoreWebView2BrowserExtension extension in installedExtensions)
            {
                if (PreferredChromiumExtensionIds.Contains(extension.Id, StringComparer.OrdinalIgnoreCase))
                {
                    _installedExtensions.Add(new BrowserExtensionSnapshot
                    {
                        Id = extension.Id,
                        Name = extension.Name,
                    });
                }
            }
        }

        private string BuildStartPageHtml()
        {
            bool darkTheme = (_themePreference == ElementTheme.Default ? ActualTheme : _themePreference) != ElementTheme.Light;
            string background = darkTheme ? "#111214" : "#ffffff";
            string surface = darkTheme ? "#17181c" : "#f4f4f5";
            string border = darkTheme ? "#23252b" : "#e4e4e7";
            string text = darkTheme ? "#fafafa" : "#18181b";
            string subtext = darkTheme ? "#a1a1aa" : "#52525b";
            string accent = darkTheme ? "#4ea8de" : "#2563eb";
            string projectName = WebUtility.HtmlEncode(ProjectName ?? "Project preview");
            string projectPath = WebUtility.HtmlEncode(ProjectPath ?? string.Empty);

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
                links.Append($"<a class=\"chip\" href=\"{previewUrl}\">{previewUrl}</a>");
            }

            return $$"""
<!doctype html>
<html>
<head>
  <meta charset="utf-8" />
  <title>{{projectName}}</title>
  <style>
    body {
      margin: 0;
      background: {{background}};
      color: {{text}};
      font-family: "Segoe UI", sans-serif;
    }
    .wrap {
      padding: 28px;
      display: grid;
      gap: 18px;
    }
    .hero {
      padding: 18px 20px;
      border: 1px solid {{border}};
      background: {{surface}};
      border-radius: 10px;
    }
    h1 {
      margin: 0 0 8px;
      font-size: 24px;
      font-weight: 700;
    }
    p {
      margin: 0;
      color: {{subtext}};
      line-height: 1.5;
    }
    .chips {
      display: flex;
      flex-wrap: wrap;
      gap: 10px;
    }
    .chip {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      min-height: 34px;
      padding: 0 12px;
      border: 1px solid {{border}};
      border-radius: 999px;
      background: {{surface}};
      color: {{text}};
      text-decoration: none;
      font-size: 13px;
    }
    .chip:hover {
      border-color: {{accent}};
      color: {{accent}};
    }
    code {
      font-family: Consolas, monospace;
      color: {{text}};
    }
  </style>
</head>
<body>
  <div class="wrap">
    <div class="hero">
      <h1>{{projectName}}</h1>
      <p>Shared-browser preview pane. Type any URL above, or open a common local dev server below.</p>
    </div>
    <div>
      <p>Project path</p>
      <p><code>{{projectPath}}</code></p>
    </div>
    <div>
      <p>Suggested previews</p>
      <div class="chips">{{links}}</div>
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
            return $$"""
<!doctype html>
<html>
<head>
  <meta charset="utf-8" />
  <title>Preview unavailable</title>
  <style>
    body {
      margin: 0;
      background: #111214;
      color: #fafafa;
      font-family: "Segoe UI", sans-serif;
    }
    .wrap {
      padding: 24px;
      display: grid;
      gap: 12px;
    }
    .card {
      border: 1px solid #23252b;
      background: #17181c;
      border-radius: 10px;
      padding: 16px;
    }
    h1 {
      margin: 0 0 8px;
      font-size: 20px;
    }
    p {
      margin: 0;
      color: #a1a1aa;
      line-height: 1.5;
    }
    code {
      color: #fafafa;
      font-family: Consolas, monospace;
    }
  </style>
</head>
<body>
  <div class="wrap">
    <div class="card">
      <h1>Preview unavailable</h1>
      <p>The browser pane could not load <code>{{safeUri}}</code>.</p>
    </div>
    <div class="card">
      <p>Error</p>
      <p><code>{{safeError}}</code></p>
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
