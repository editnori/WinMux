using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using SelfContainedDeployment.Automation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Windows.System;

namespace SelfContainedDeployment.Panes
{
    public sealed partial class BrowserPaneControl : UserControl
    {
        private static readonly Dictionary<string, Task<CoreWebView2Environment>> EnvironmentTasks = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object EnvironmentSync = new();
        private bool _initialized;
        private bool _disposed;
        private string _currentUri;
        private string _currentTitle = "Preview";
        private ElementTheme _themePreference = ElementTheme.Default;

        public BrowserPaneControl()
        {
            InitializeComponent();
            ActualThemeChanged += OnActualThemeChanged;
        }

        public event EventHandler<string> TitleChanged;

        public string ProjectId { get; set; }

        public string ProjectName { get; set; }

        public string ProjectPath { get; set; }

        public string InitialUri { get; set; }

        public string CurrentUri => _currentUri;

        public string CurrentTitle => _currentTitle;

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

            try
            {
                CoreWebView2Environment environment = await GetEnvironmentAsync();
                await BrowserView.EnsureCoreWebView2Async(environment);
                LogBrowserEvent("webview.ready", "Browser WebView2 initialized with project-scoped profile");
            }
            catch (Exception ex)
            {
                LogBrowserEvent("webview.profile_failed", $"Project-scoped browser profile failed: {ex.Message}", new Dictionary<string, string>
                {
                    ["error"] = ex.Message,
                });

                await BrowserView.EnsureCoreWebView2Async();
                LogBrowserEvent("webview.ready", "Browser WebView2 initialized with default profile");
            }

            BrowserView.CoreWebView2.Settings.AreDevToolsEnabled = true;
            BrowserView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = true;
            BrowserView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            BrowserView.CoreWebView2.DocumentTitleChanged += OnDocumentTitleChanged;
            BrowserView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            BrowserView.CoreWebView2.SourceChanged += OnSourceChanged;
            _initialized = true;

            ApplyTheme(_themePreference == ElementTheme.Default ? ActualTheme : _themePreference);

            if (string.IsNullOrWhiteSpace(InitialUri))
            {
                await NavigateToStartPageAsync();
            }
            else
            {
                string initialUri = NormalizeUri(InitialUri);
                AddressBox.Text = initialUri;
                BrowserView.CoreWebView2.Navigate(initialUri);
                LogBrowserEvent("navigate.requested", $"Navigating browser pane to {initialUri}");
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
        }

        private async Task NavigateToStartPageAsync()
        {
            if (!_initialized || BrowserView.CoreWebView2 is null)
            {
                return;
            }

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

            Navigate(AddressBox.Text);
            e.Handled = true;
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
            MethodInfo createAsync = typeof(CoreWebView2Environment).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(method => string.Equals(method.Name, "CreateAsync", StringComparison.Ordinal));

            ParameterInfo[] parameters = createAsync.GetParameters();
            object[] arguments = parameters.Length switch
            {
                3 => new object[] { null, userDataFolder, null },
                2 => new object[] { null, userDataFolder },
                1 => new object[] { userDataFolder },
                _ => Array.Empty<object>(),
            };

            object taskObject = createAsync.Invoke(null, arguments);
            Task task = (Task)taskObject;
            await task.ConfigureAwait(true);

            PropertyInfo resultProperty = task.GetType().GetProperty("Result");
            return (CoreWebView2Environment)resultProperty.GetValue(task);
        }

        private string ResolveProfileRoot()
        {
            string profileSegment = string.IsNullOrWhiteSpace(ProjectId) ? "default" : ProjectId;
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinMux",
                "browser-profiles",
                profileSegment);

            Directory.CreateDirectory(root);
            return root;
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
      <p>Project-scoped preview pane. Type any URL above, or open a common local dev server below.</p>
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
