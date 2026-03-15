using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using SelfContainedDeployment.Automation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text;
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

        private static readonly string[] ChromiumProfileFilesToSeed =
        {
            "Bookmarks",
            "Bookmarks.bak",
            "Account Web Data",
            "Account Web Data-journal",
            "Affiliation Database",
            "Affiliation Database-journal",
            "Favicons",
            "Favicons-journal",
            "History",
            "History-journal",
            "Last Tabs",
            "Last Session",
            "Login Data",
            "Login Data For Account",
            "Login Data-journal",
            "Preferences",
            "Secure Preferences",
            "Shortcuts",
            "Shortcuts-journal",
            "Top Sites",
            "Top Sites-journal",
            "Visited Links",
            "Web Data",
            "Web Data-journal",
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
            "Sessions",
            "Shared Dictionary",
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
        private readonly List<BrowserExtensionSnapshot> _installedExtensions = new();

        public BrowserPaneControl()
        {
            InitializeComponent();
            ActualThemeChanged += OnActualThemeChanged;
            PointerPressed += (_, _) => RaiseInteractionRequested();
            GotFocus += OnInteractionRequested;
        }

        public event EventHandler<string> TitleChanged;
        public event EventHandler InteractionRequested;
        public event EventHandler<string> OpenPaneRequested;

        public string ProjectId { get; set; }

        public string ProjectName { get; set; }

        public string ProjectPath { get; set; }

        public string ProjectRootPath { get; set; }

        public string InitialUri { get; set; }

        public string CurrentUri => _currentUri;

        public string CurrentTitle => _currentTitle;

        public string AddressText => AddressBox.Text ?? string.Empty;

        public bool IsInitialized => _initialized;

        public string ProfileSeedStatus => _profileSeedStatus;

        public string ExtensionImportStatus => _extensionImportStatus;

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
            RaiseInteractionRequested();
            if (!_initialized || BrowserView.CoreWebView2 is null)
            {
                AddressBox.Focus(FocusState.Programmatic);
                return;
            }

            BrowserView.Focus(FocusState.Programmatic);
        }

        public void RequestLayout()
        {
        }

        public void DisposePane()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (BrowserView.CoreWebView2 is not null)
            {
                BrowserView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
                BrowserView.CoreWebView2.DocumentTitleChanged -= OnDocumentTitleChanged;
                BrowserView.CoreWebView2.SourceChanged -= OnSourceChanged;
            }
        }

        public void Navigate(string target)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                _ = NavigateToStartPageAsync();
                return;
            }

            string normalized = NormalizeUri(target);
            AddressBox.Text = normalized;
            RaiseInteractionRequested();
            if (_initialized && BrowserView.CoreWebView2 is not null)
            {
                BrowserView.CoreWebView2.Navigate(normalized);
                LogBrowserEvent("navigate.requested", $"Navigating browser pane to {normalized}");
            }
            else
            {
                InitialUri = normalized;
            }
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
                core.Settings.IsPasswordAutosaveEnabled = true;
                LogBrowserEvent("webview.configure", "Enabled browser password autosave", new Dictionary<string, string>
                {
                    ["step"] = "settings.passwordAutosave",
                });
                core.Settings.IsGeneralAutofillEnabled = true;
                LogBrowserEvent("webview.configure", "Enabled browser general autofill", new Dictionary<string, string>
                {
                    ["step"] = "settings.generalAutofill",
                });
                core.Profile.IsPasswordAutosaveEnabled = true;
                core.Profile.IsGeneralAutofillEnabled = true;
                core.DocumentTitleChanged += OnDocumentTitleChanged;
                core.NavigationCompleted += OnNavigationCompleted;
                core.SourceChanged += OnSourceChanged;
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

                if (string.IsNullOrWhiteSpace(InitialUri))
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
                    LogBrowserEvent("webview.configure", $"Navigating browser pane to {initialUri}", new Dictionary<string, string>
                    {
                        ["step"] = "navigate.initialUri",
                        ["uri"] = initialUri,
                    });
                    core.Navigate(initialUri);
                    LogBrowserEvent("navigate.requested", $"Navigating browser pane to {initialUri}");
                }
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
        }

        private async Task NavigateToStartPageAsync()
        {
            if (!_initialized || BrowserView.CoreWebView2 is null)
            {
                return;
            }

            RaiseInteractionRequested();
            _currentUri = "winmux://start";
            AddressBox.Text = string.Empty;
            BrowserView.CoreWebView2.NavigateToString(BuildStartPageHtml());
            LogBrowserEvent("start-page.shown", "Browser start page rendered");
            await Task.CompletedTask;
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

            MenuFlyoutItem blankPaneItem = new()
            {
                Text = "Open a blank browser pane",
            };
            blankPaneItem.Click += (_, _) => OpenPaneRequested?.Invoke(this, null);
            flyout.Items.Add(blankPaneItem);

            flyout.Items.Add(new MenuFlyoutItem
            {
                Text = "Each browser pane keeps one live page. Use another pane when you need a second page.",
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
                sender.NavigateToString(BuildErrorPageHtml(args.WebErrorStatus.ToString(), _currentUri));
                LogBrowserEvent("navigate.failed", $"Browser navigation failed for {_currentUri}", new Dictionary<string, string>
                {
                    ["error"] = args.WebErrorStatus.ToString(),
                    ["uri"] = _currentUri ?? string.Empty,
                });
                return;
            }

            if (!string.Equals(_currentUri, "winmux://start", StringComparison.OrdinalIgnoreCase))
            {
                AddressBox.Text = BrowserView.Source?.ToString() ?? _currentUri ?? string.Empty;
            }

            LogBrowserEvent("navigate.completed", $"Browser navigation completed for {_currentUri}", new Dictionary<string, string>
            {
                ["uri"] = _currentUri ?? string.Empty,
                ["success"] = args.IsSuccess.ToString(),
            });
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
            TitleChanged?.Invoke(this, _currentTitle);
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

            LogBrowserEvent("source.changed", $"Browser source changed to {_currentUri}");
        }

        private async Task<CoreWebView2Environment> GetEnvironmentAsync()
        {
            string key = ResolveProfileRoot();
            Task<CoreWebView2Environment> environmentTask;

            lock (EnvironmentSync)
            {
                if (!EnvironmentTasks.TryGetValue(key, out environmentTask))
                {
                    environmentTask = CreateEnvironmentAsync(key);
                    EnvironmentTasks[key] = environmentTask;
                }
            }

            return await environmentTask.ConfigureAwait(true);
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
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinMux",
                SharedBrowserProfileFolderName);

            Directory.CreateDirectory(root);
            TrySeedProfileRootFromChromium(root);
            return root;
        }

        private static string BuildStableProfileKey(string value)
        {
            byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim().ToLowerInvariant()));
            return Convert.ToHexString(bytes[..16]).ToLowerInvariant();
        }

        private void TrySeedProfileRootFromChromium(string targetRoot)
        {
            if (TryMigrateLegacyWinMuxProfile(targetRoot))
            {
                return;
            }

            string sourceRoot = ResolveChromiumUserDataRoot();
            if (string.IsNullOrWhiteSpace(sourceRoot))
            {
                _profileSeedStatus = "No Chromium profile detected on this machine";
                return;
            }

            try
            {
                using IEnumerator<string> entries = Directory.EnumerateFileSystemEntries(targetRoot).GetEnumerator();
                bool hasExistingEntries = entries.MoveNext();
                SeedCopySummary summary = CopyChromiumProfileSeed(sourceRoot, targetRoot, overwriteExisting: !hasExistingEntries);
                string eventName;

                if (!hasExistingEntries)
                {
                    eventName = summary.SkippedFiles > 0 ? "profile.seeded_partial" : "profile.seeded";
                    _profileSeedStatus = summary.SkippedFiles > 0
                        ? $"Partially seeded shared browser profile from Chrome ({summary.CopiedFiles} copied, {summary.SkippedFiles} skipped)"
                        : $"Seeded shared browser profile from Chrome ({summary.CopiedFiles} files copied)";
                }
                else if (summary.CopiedFiles > 0)
                {
                    eventName = "profile.seed_repaired";
                    _profileSeedStatus = $"Completed Chrome seed for shared browser profile ({summary.CopiedFiles} copied)";
                }
                else
                {
                    _profileSeedStatus = "Reusing shared browser profile";
                    return;
                }

                LogBrowserEvent(eventName, $"Seeded browser profile from {sourceRoot}", new Dictionary<string, string>
                {
                    ["sourceRoot"] = sourceRoot,
                    ["targetRoot"] = targetRoot,
                    ["copiedFiles"] = summary.CopiedFiles.ToString(),
                    ["skippedFiles"] = summary.SkippedFiles.ToString(),
                });
            }
            catch (Exception ex)
            {
                _profileSeedStatus = "Chrome profile seed failed";
                LogBrowserEvent("profile.seed_failed", $"Failed to seed browser profile from {sourceRoot}: {ex.Message}", new Dictionary<string, string>
                {
                    ["sourceRoot"] = sourceRoot,
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

        private static string ResolveChromiumUserDataRoot()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string[] candidates =
            {
                Path.Combine(localAppData, "Google", "Chrome", "User Data"),
                Path.Combine(localAppData, "Chromium", "User Data"),
                Path.Combine(localAppData, "BraveSoftware", "Brave-Browser", "User Data"),
            };

            foreach (string candidate in candidates)
            {
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static SeedCopySummary CopyChromiumProfileSeed(string sourceRoot, string targetRoot, bool overwriteExisting)
        {
            string sourceDefault = Path.Combine(sourceRoot, "Default");
            if (!Directory.Exists(sourceDefault))
            {
                throw new DirectoryNotFoundException($"Chromium profile root '{sourceRoot}' does not contain a Default profile.");
            }

            SeedCopySummary summary = default;
            CopyFileIfExists(Path.Combine(sourceRoot, "Local State"), Path.Combine(targetRoot, "Local State"), ref summary, overwriteExisting);
            CopyFileIfExists(Path.Combine(sourceRoot, "Last Version"), Path.Combine(targetRoot, "Last Version"), ref summary, overwriteExisting);

            string targetDefault = Path.Combine(targetRoot, "Default");
            Directory.CreateDirectory(targetDefault);

            foreach (string relativeFile in ChromiumProfileFilesToSeed)
            {
                CopyFileIfExists(Path.Combine(sourceDefault, relativeFile), Path.Combine(targetDefault, relativeFile), ref summary, overwriteExisting);
            }

            foreach (string relativeDirectory in ChromiumProfileDirectoriesToSeed)
            {
                CopyDirectoryIfExists(Path.Combine(sourceDefault, relativeDirectory), Path.Combine(targetDefault, relativeDirectory), ref summary, overwriteExisting);
            }

            CopyFileIfExists(Path.Combine(sourceDefault, "Network", "Cookies"), Path.Combine(targetDefault, "Network", "Cookies"), ref summary, overwriteExisting);
            CopyFileIfExists(Path.Combine(sourceDefault, "Network", "Cookies-journal"), Path.Combine(targetDefault, "Network", "Cookies-journal"), ref summary, overwriteExisting);
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
                string sourceRoot = ResolveChromiumUserDataRoot();
                string fallbackExtensionsRoot = string.IsNullOrWhiteSpace(sourceRoot)
                    ? null
                    : Path.Combine(sourceRoot, "Default", "Extensions");

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
