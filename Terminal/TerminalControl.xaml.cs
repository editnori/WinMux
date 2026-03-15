using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SelfContainedDeployment.Automation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

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
        private bool _startupInputSent;
        private int _cols = 120;
        private int _rows = 32;
        private int _lastLoggedResizeCols;
        private int _lastLoggedResizeRows;
        private string _sessionTitle = "Terminal";
        private ElementTheme _themePreference = ElementTheme.Default;
        private readonly StringBuilder _outputBuffer = new();
        private readonly Dictionary<string, TaskCompletionSource<NativeAutomationTerminalSnapshot>> _inspectionRequests = new();

        public event EventHandler<string> SessionTitleChanged;
        public event EventHandler RendererReady;
        public event EventHandler SessionExited;

        public TerminalControl()
        {
            InitializeComponent();
            InitialWorkingDirectory = Environment.CurrentDirectory;
            ActualThemeChanged += OnActualThemeChanged;
            SizeChanged += OnTerminalSizeChanged;
            ApplyBackgroundColor();
        }

        public string ShellCommand { get; set; }

        public string InitialWorkingDirectory { get; set; }

        public string DisplayWorkingDirectory { get; set; }

        public string ProcessWorkingDirectory { get; set; }

        public string StartupInput { get; set; }

        public string SessionTitle => _sessionTitle;

        public string InitialTitleHint => GetInitialTitle();

        private void LogTerminalEvent(string name, string message = null, IReadOnlyDictionary<string, string> data = null)
        {
            NativeAutomationEventLog.Record("terminal", name, message, data ?? new Dictionary<string, string>
            {
                ["title"] = _sessionTitle ?? string.Empty,
                ["displayWorkingDirectory"] = DisplayWorkingDirectory ?? string.Empty,
                ["shellCommand"] = ShellCommand ?? string.Empty,
            });
        }

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
                    LogTerminalEvent("renderer.ready", "Terminal renderer reported ready");
                    ApplyBackgroundColor();
                    PostCurrentTheme();
                    RendererReady?.Invoke(this, EventArgs.Empty);
                    EnsureStarted();
                    HideStartupMask();
                    break;
                case "resize":
                    _cols = Math.Max(1, message.Cols);
                    _rows = Math.Max(1, message.Rows);
                    if (_cols != _lastLoggedResizeCols || _rows != _lastLoggedResizeRows)
                    {
                        _lastLoggedResizeCols = _cols;
                        _lastLoggedResizeRows = _rows;
                        LogTerminalEvent("renderer.resize", $"Renderer resized to {_cols}x{_rows}", new Dictionary<string, string>
                        {
                            ["cols"] = _cols.ToString(),
                            ["rows"] = _rows.ToString(),
                            ["title"] = _sessionTitle ?? string.Empty,
                        });
                    }

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
                case "copy":
                    CopySelectionToClipboard(message.Data);
                    break;
                case "paste":
                    PasteClipboardToTerminalAsync();
                    break;
                case "title":
                    UpdateSessionTitle(string.IsNullOrWhiteSpace(message.Title) ? _sessionTitle : message.Title.Trim());
                    break;
                case "state":
                    CompleteInspection(message);
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
                LogTerminalEvent("session.started", "Started ConPTY session", new Dictionary<string, string>
                {
                    ["cols"] = _cols.ToString(),
                    ["rows"] = _rows.ToString(),
                    ["shellCommand"] = ShellCommand ?? string.Empty,
                    ["processWorkingDirectory"] = ResolveProcessWorkingDirectory() ?? string.Empty,
                });

                string initialTitle = GetInitialTitle();
                UpdateSessionTitle(initialTitle);
                PostMessage(new HostMessage { Type = "setTitle", Title = initialTitle });
                PostMessage(new HostMessage { Type = "focus" });
                TrySendStartupInput();
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

                AppendOutput(text);
                HideStartupMask();
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
                LogTerminalEvent("session.exited", "ConPTY session exited");
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
            LogTerminalEvent("focus.requested", "Terminal focus requested");
        }

        public void ApplyTheme(ElementTheme theme)
        {
            _themePreference = theme;
            ApplyBackgroundColor();
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
            LogTerminalEvent("input.sent", "Input forwarded to terminal", new Dictionary<string, string>
            {
                ["length"] = text.Length.ToString(),
            });
        }

        private void TrySendStartupInput()
        {
            if (_startupInputSent || string.IsNullOrWhiteSpace(StartupInput))
            {
                return;
            }

            _connection?.WriteInput(StartupInput);
            _startupInputSent = true;
            LogTerminalEvent("startup-input.sent", "Startup input sent to terminal", new Dictionary<string, string>
            {
                ["length"] = StartupInput.Length.ToString(),
            });
        }

        private static void CopySelectionToClipboard(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            DataPackage package = new();
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
        }

        private async void PasteClipboardToTerminalAsync()
        {
            try
            {
                DataPackageView package = Clipboard.GetContent();
                if (package is null || !package.Contains(StandardDataFormats.Text))
                {
                    return;
                }

                string text = await package.GetTextAsync();
                if (!string.IsNullOrEmpty(text))
                {
                    SendInput(text);
                }
            }
            catch
            {
            }
        }

        public void RequestFit()
        {
            PostMessage(new HostMessage { Type = "fit" });
            LogTerminalEvent("fit.requested", "Renderer fit requested");
        }

        public async Task<NativeAutomationTerminalSnapshot> GetTerminalSnapshotAsync()
        {
            NativeAutomationTerminalSnapshot fallback = BuildFallbackTerminalSnapshot();
            if (_disposed || !_rendererReady || !_webViewInitialized || TerminalView.CoreWebView2 is null)
            {
                return fallback;
            }

            string requestId = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<NativeAutomationTerminalSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            _inspectionRequests[requestId] = tcs;

            PostMessage(new HostMessage
            {
                Type = "inspect",
                RequestId = requestId,
            });

            try
            {
                NativeAutomationTerminalSnapshot snapshot = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(true);
                snapshot.Title ??= _sessionTitle;
                snapshot.DisplayWorkingDirectory ??= DisplayWorkingDirectory;
                snapshot.ShellCommand ??= ShellCommand;
                snapshot.RendererReady = _rendererReady;
                snapshot.Started = _started;
                snapshot.BufferTail ??= BuildFallbackTerminalSnapshot().BufferTail;
                return snapshot;
            }
            catch
            {
                _inspectionRequests.Remove(requestId);
                return fallback;
            }
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

            string normalizedTitle = NormalizeSessionTitle(title);
            if (string.IsNullOrWhiteSpace(normalizedTitle))
            {
                return;
            }

            if (string.Equals(_sessionTitle, normalizedTitle, StringComparison.Ordinal))
            {
                return;
            }

            _sessionTitle = normalizedTitle;
            PostMessage(new HostMessage { Type = "setTitle", Title = normalizedTitle });
            LogTerminalEvent("title.updated", $"Session title set to {normalizedTitle}", new Dictionary<string, string>
            {
                ["title"] = normalizedTitle,
            });
            SessionTitleChanged?.Invoke(this, normalizedTitle);
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

        private string NormalizeSessionTitle(string title)
        {
            string trimmed = title?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return null;
            }

            string launcherName = ExtractCommandExecutableName(ShellCommand);
            string titleLeaf = ExtractPathLeaf(trimmed);
            if (!string.IsNullOrWhiteSpace(launcherName) &&
                string.Equals(titleLeaf, launcherName, StringComparison.OrdinalIgnoreCase))
            {
                return GetInitialTitle();
            }

            return trimmed;
        }

        private static string ExtractCommandExecutableName(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                return null;
            }

            string trimmed = command.Trim();
            string token;
            if (trimmed[0] == '"')
            {
                int closingQuote = trimmed.IndexOf('"', 1);
                token = closingQuote > 1 ? trimmed[1..closingQuote] : trimmed.Trim('"');
            }
            else
            {
                int spaceIndex = trimmed.IndexOf(' ');
                token = spaceIndex > 0 ? trimmed[..spaceIndex] : trimmed;
            }

            return ExtractPathLeaf(token);
        }

        private static string ExtractPathLeaf(string pathLike)
        {
            if (string.IsNullOrWhiteSpace(pathLike))
            {
                return null;
            }

            string trimmed = pathLike.Trim().TrimEnd('\\', '/');
            if (trimmed.Length == 0)
            {
                return null;
            }

            int slashIndex = Math.Max(trimmed.LastIndexOf('\\'), trimmed.LastIndexOf('/'));
            return slashIndex >= 0 && slashIndex < trimmed.Length - 1
                ? trimmed[(slashIndex + 1)..]
                : trimmed;
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

        private void AppendOutput(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            _outputBuffer.Append(text);
            const int maxChars = 120000;
            if (_outputBuffer.Length > maxChars)
            {
                _outputBuffer.Remove(0, _outputBuffer.Length - maxChars);
            }
        }

        private NativeAutomationTerminalSnapshot BuildFallbackTerminalSnapshot()
        {
            string buffer = _outputBuffer.ToString();
            if (buffer.Length > 4000)
            {
                buffer = buffer[^4000..];
            }

            return new NativeAutomationTerminalSnapshot
            {
                Title = _sessionTitle,
                DisplayWorkingDirectory = DisplayWorkingDirectory,
                ShellCommand = ShellCommand,
                RendererReady = _rendererReady,
                Started = _started,
                Cols = _cols,
                Rows = _rows,
                BufferTail = buffer,
            };
        }

        private void CompleteInspection(RendererMessage message)
        {
            if (message is null || string.IsNullOrWhiteSpace(message.RequestId))
            {
                return;
            }

            if (!_inspectionRequests.Remove(message.RequestId, out TaskCompletionSource<NativeAutomationTerminalSnapshot> tcs))
            {
                return;
            }

            tcs.TrySetResult(new NativeAutomationTerminalSnapshot
            {
                Title = string.IsNullOrWhiteSpace(message.Title)
                    ? _sessionTitle
                    : NormalizeSessionTitle(message.Title) ?? _sessionTitle,
                DisplayWorkingDirectory = DisplayWorkingDirectory,
                ShellCommand = ShellCommand,
                RendererReady = _rendererReady,
                Started = _started,
                Cols = Math.Max(1, message.Cols),
                Rows = Math.Max(1, message.Rows),
                CursorX = message.CursorX,
                CursorY = message.CursorY,
                Selection = message.Selection,
                VisibleText = message.VisibleText,
                BufferTail = string.IsNullOrWhiteSpace(message.BufferTail) ? BuildFallbackTerminalSnapshot().BufferTail : message.BufferTail,
            });
        }

        private void OnActualThemeChanged(FrameworkElement sender, object args)
        {
            ApplyBackgroundColor();
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

        private void ApplyBackgroundColor()
        {
            Color backgroundColor = ResolveThemeName() == "light"
                ? new Color { A = 255, R = 255, G = 255, B = 255 }
                : new Color { A = 255, R = 9, G = 9, B = 11 };

            if (TerminalRoot is not null)
            {
                TerminalRoot.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(backgroundColor);
            }

            if (StartupMask is not null)
            {
                StartupMask.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(backgroundColor);
            }

            if (TerminalView is null)
            {
                return;
            }

            TerminalView.DefaultBackgroundColor = backgroundColor;
        }

        private void HideStartupMask()
        {
            if (StartupMask is not null)
            {
                StartupMask.Visibility = Visibility.Collapsed;
            }
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
            LogTerminalEvent("status.shown", text);

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
            public string RequestId { get; set; }
            public int CursorX { get; set; }
            public int CursorY { get; set; }
            public string Selection { get; set; }
            public string VisibleText { get; set; }
            public string BufferTail { get; set; }
        }

        private sealed class HostMessage
        {
            public string Type { get; set; }
            public string Data { get; set; }
            public string Text { get; set; }
            public string Title { get; set; }
            public string Theme { get; set; }
            public string RequestId { get; set; }
        }
    }
}
