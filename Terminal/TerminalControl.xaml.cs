using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.IO;
using System.Text.Json;

namespace SelfContainedDeployment.Terminal
{
    public sealed partial class TerminalControl : UserControl
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        private ConPtyConnection _connection;
        private bool _rendererReady;
        private bool _webViewInitialized;
        private bool _started;
        private bool _disposed;
        private int _cols = 120;
        private int _rows = 32;
        private string _sessionTitle = "Terminal";
        private ElementTheme _themePreference = ElementTheme.Default;

        public event EventHandler<string> SessionTitleChanged;
        public event EventHandler SessionExited;

        public TerminalControl()
        {
            InitializeComponent();
            InitialWorkingDirectory = Environment.CurrentDirectory;
            ActualThemeChanged += OnActualThemeChanged;
            SizeChanged += OnTerminalSizeChanged;
        }

        public string ShellCommand { get; set; }

        public string InitialWorkingDirectory { get; set; }

        public string DisplayWorkingDirectory { get; set; }

        public string ProcessWorkingDirectory { get; set; }

        public string SessionTitle => _sessionTitle;

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_webViewInitialized || _disposed)
            {
                return;
            }

            try
            {
                await TerminalView.EnsureCoreWebView2Async();
                TerminalView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                TerminalView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                TerminalView.CoreWebView2.Settings.AreDevToolsEnabled = true;
                TerminalView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = true;
                TerminalView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

                string rendererPath = ResolveRendererPath();
                TerminalView.Source = new Uri(rendererPath);
                _webViewInitialized = true;
            }
            catch (Exception ex)
            {
                ShowStatus($"WebView2 failed: {ex.Message}", keepVisible: true);
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
        }

        private void OnWebMessageReceived(Microsoft.Web.WebView2.Core.CoreWebView2 sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs args)
        {
            RendererMessage message;

            try
            {
                message = JsonSerializer.Deserialize<RendererMessage>(args.WebMessageAsJson, JsonOptions);
            }
            catch
            {
                return;
            }

            if (message?.Type is null)
            {
                return;
            }

            switch (message.Type)
            {
                case "ready":
                    _rendererReady = true;
                    PostCurrentTheme();
                    EnsureStarted();
                    break;
                case "resize":
                    _cols = Math.Max(1, message.Cols);
                    _rows = Math.Max(1, message.Rows);

                    if (_started)
                    {
                        try
                        {
                            _connection?.Resize(_cols, _rows);
                        }
                        catch (Exception ex)
                        {
                            ShowStatus(ex.Message, keepVisible: false);
                        }
                    }
                    else
                    {
                        EnsureStarted();
                    }

                    break;
                case "input":
                    _connection?.WriteInput(message.Data);
                    break;
                case "title":
                    UpdateSessionTitle(string.IsNullOrWhiteSpace(message.Title) ? _sessionTitle : message.Title.Trim());
                    break;
                case "focus":
                    FocusTerminal();
                    break;
            }
        }

        private void EnsureStarted()
        {
            if (_started || !_rendererReady || _disposed)
            {
                return;
            }

            _started = true;

            try
            {
                _connection = new ConPtyConnection();
                _connection.OutputReceived += OnOutputReceived;
                _connection.ProcessExited += OnProcessExited;
                _connection.Start(_cols, _rows, ShellCommand, ResolveProcessWorkingDirectory());

                string initialTitle = GetInitialTitle();
                UpdateSessionTitle(initialTitle);
                PostMessage(new HostMessage { Type = "setTitle", Title = initialTitle });
                PostMessage(new HostMessage { Type = "focus" });
            }
            catch (Exception ex)
            {
                _started = false;
                PostMessage(new HostMessage { Type = "system", Text = $"Failed to start terminal: {ex.Message}" });
                ShowStatus(ex.Message, keepVisible: true);
            }
        }

        private void OnOutputReceived(string text)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_disposed)
                {
                    return;
                }

                PostMessage(new HostMessage { Type = "output", Data = text });
                HideStatus();
            });
        }

        private void OnProcessExited()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_disposed)
                {
                    return;
                }

                PostMessage(new HostMessage { Type = "exit", Text = "Shell exited. Close the tab or open a new one." });
                ShowStatus("Shell exited", keepVisible: true);
                SessionExited?.Invoke(this, EventArgs.Empty);
            });
        }

        public void FocusTerminal()
        {
            if (_disposed)
            {
                return;
            }

            TerminalView.Focus(FocusState.Programmatic);
            PostMessage(new HostMessage { Type = "focus" });
        }

        public void ApplyTheme(ElementTheme theme)
        {
            _themePreference = theme;
            PostCurrentTheme();
        }

        public void SendInput(string text)
        {
            if (_disposed || string.IsNullOrEmpty(text))
            {
                return;
            }

            EnsureStarted();
            _connection?.WriteInput(text);
        }

        public void RequestFit()
        {
            PostMessage(new HostMessage { Type = "fit" });
        }

        public void DisposeTerminal()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                if (TerminalView.CoreWebView2 is not null)
                {
                    TerminalView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                }
            }
            catch
            {
            }

            try
            {
                if (_connection is not null)
                {
                    _connection.OutputReceived -= OnOutputReceived;
                    _connection.ProcessExited -= OnProcessExited;
                    _connection.Dispose();
                }
            }
            catch
            {
            }
            finally
            {
                _connection = null;
            }
        }

        private void UpdateSessionTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return;
            }

            _sessionTitle = title;
            SessionTitleChanged?.Invoke(this, title);
        }

        private string GetInitialTitle()
        {
            string titleSource = string.IsNullOrWhiteSpace(DisplayWorkingDirectory)
                ? InitialWorkingDirectory
                : DisplayWorkingDirectory;

            if (string.IsNullOrWhiteSpace(titleSource))
            {
                return "Command Prompt";
            }

            string trimmed = titleSource.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string leaf = Path.GetFileName(trimmed);

            return string.IsNullOrWhiteSpace(leaf) ? trimmed : leaf;
        }

        private void OnTerminalSizeChanged(object sender, SizeChangedEventArgs e)
        {
            RequestFit();
        }

        private string ResolveProcessWorkingDirectory()
        {
            if (!string.IsNullOrWhiteSpace(ProcessWorkingDirectory))
            {
                return ProcessWorkingDirectory;
            }

            if (!string.IsNullOrWhiteSpace(InitialWorkingDirectory) &&
                !InitialWorkingDirectory.StartsWith("/", StringComparison.Ordinal))
            {
                return InitialWorkingDirectory;
            }

            return Environment.CurrentDirectory;
        }

        private void OnActualThemeChanged(FrameworkElement sender, object args)
        {
            if (_themePreference == ElementTheme.Default)
            {
                PostCurrentTheme();
            }
        }

        private void PostCurrentTheme()
        {
            PostMessage(new HostMessage
            {
                Type = "setTheme",
                Theme = ResolveThemeName(),
            });
        }

        private string ResolveThemeName()
        {
            ElementTheme resolved = _themePreference switch
            {
                ElementTheme.Light => ElementTheme.Light,
                ElementTheme.Dark => ElementTheme.Dark,
                _ => ActualTheme == ElementTheme.Light ? ElementTheme.Light : ElementTheme.Dark,
            };

            return resolved == ElementTheme.Light ? "light" : "dark";
        }

        private static string ResolveRendererPath()
        {
            string overrideRoot = Environment.GetEnvironmentVariable("NATIVE_TERMINAL_WEB_ROOT");
            if (!string.IsNullOrWhiteSpace(overrideRoot))
            {
                string candidate = Path.Combine(overrideRoot, "terminal-host.html");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return Path.Combine(AppContext.BaseDirectory, "Web", "terminal-host.html");
        }

        private void PostMessage(HostMessage message)
        {
            if (!_webViewInitialized || TerminalView.CoreWebView2 is null)
            {
                return;
            }

            TerminalView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, JsonOptions));
        }

        private void ShowStatus(string text, bool keepVisible)
        {
            StatusText.Text = text;
            StatusBadge.Visibility = Visibility.Visible;

            if (!keepVisible)
            {
                HideStatus();
            }
        }

        private void HideStatus()
        {
            StatusBadge.Visibility = Visibility.Collapsed;
        }

        private sealed class RendererMessage
        {
            public string Type { get; set; }
            public string Data { get; set; }
            public int Cols { get; set; }
            public int Rows { get; set; }
            public string Title { get; set; }
        }

        private sealed class HostMessage
        {
            public string Type { get; set; }
            public string Data { get; set; }
            public string Text { get; set; }
            public string Title { get; set; }
            public string Theme { get; set; }
        }
    }
}
