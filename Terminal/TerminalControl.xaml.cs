using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Dispatching;
using Microsoft.Web.WebView2.Core;
using SelfContainedDeployment.Automation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
        private static readonly object EnvironmentSync = new();
        private static Task<CoreWebView2Environment> SharedEnvironmentTask;
        private static readonly Regex CodexResumeRegex = new(@"codex\s+resume\s+([0-9a-f-]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ClaudeResumeRegex = new(@"claude\s+--resume\s+([0-9a-f-]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex CodexResumeInlineRegex = new(@"^codex\s+resume\s+([0-9a-f-]+)\s*(.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ClaudeResumeInlineRegex = new(@"^claude\s+--resume\s+([0-9a-f-]+)\s*(.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex AnsiEscapeRegex = new(@"\u001B(?:\[[0-9;?]*[ -/]*[@-~]|\][^\u0007]*\u0007)", RegexOptions.Compiled);

        private ConPtyConnection _connection;
        private Task _initializationTask;
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
        private readonly StringBuilder _inputLineBuffer = new();
        private readonly Dictionary<string, TaskCompletionSource<NativeAutomationTerminalSnapshot>> _inspectionRequests = new();
        private readonly DispatcherQueueTimer _fitTimer;
        private string _replayTool;
        private string _replaySessionId;
        private string _replayCommand;
        private string _toolLaunchArguments;

        public event EventHandler<string> SessionTitleChanged;
        public event EventHandler RendererReady;
        public event EventHandler SessionExited;
        public event EventHandler InteractionRequested;
        public event EventHandler ReplayStateChanged;

        public TerminalControl()
        {
            InitializeComponent();
            InitialWorkingDirectory = Environment.CurrentDirectory;
            ActualThemeChanged += OnActualThemeChanged;
            SizeChanged += OnTerminalSizeChanged;
            PointerPressed += (_, _) => RaiseInteractionRequested();
            _fitTimer = DispatcherQueue.CreateTimer();
            _fitTimer.IsRepeating = false;
            _fitTimer.Interval = TimeSpan.FromMilliseconds(45);
            _fitTimer.Tick += OnFitTimerTick;
            ApplyBackgroundColor();
            UpdateStartupMask();
        }

        public string ShellCommand { get; set; }

        public string InitialWorkingDirectory { get; set; }

        public string DisplayWorkingDirectory { get; set; }

        public string ProcessWorkingDirectory { get; set; }

        public IReadOnlyDictionary<string, string> LaunchEnvironment { get; set; }

        public string StartupInput { get; set; }

        public string SessionTitle => _sessionTitle;

        public string InitialTitleHint => GetInitialTitle();

        public string ReplayTool => _replayTool;

        public string ReplaySessionId => _replaySessionId;

        public string ReplayCommand => _replayCommand;

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
            UpdateStartupMask();
            await EnsureInitializedAsync();
        }

        private async Task EnsureInitializedAsync()
        {
            if (_webViewInitialized || _disposed)
            {
                return;
            }

            Task initializationTask = _initializationTask;
            if (initializationTask is null)
            {
                initializationTask = InitializeTerminalWebViewAsync();
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

        private async Task InitializeTerminalWebViewAsync()
        {
            try
            {
                try
                {
                    CoreWebView2Environment environment = await GetEnvironmentAsync().ConfigureAwait(true);
                    if (_disposed)
                    {
                        return;
                    }

                    await TerminalView.EnsureCoreWebView2Async(environment).AsTask().ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    LogTerminalEvent("webview.profile_failed", "Terminal shared WebView2 environment failed; retrying with default profile.", new Dictionary<string, string>
                    {
                        ["exceptionType"] = ex.GetType().FullName ?? string.Empty,
                        ["message"] = ex.Message ?? string.Empty,
                    });
                    await TerminalView.EnsureCoreWebView2Async().AsTask().ConfigureAwait(true);
                }

                if (_disposed)
                {
                    return;
                }

                string rendererPath = ResolveRendererPath();
                TerminalView.Source = new Uri(rendererPath);

                CoreWebView2 core = await WaitForCoreWebView2Async().ConfigureAwait(true);
                CoreWebView2Settings settings = core.Settings;
                if (settings is not null)
                {
                    settings.AreDefaultContextMenusEnabled = false;
                    settings.IsStatusBarEnabled = false;
                    settings.AreDevToolsEnabled = true;
                    settings.AreBrowserAcceleratorKeysEnabled = true;
                }

                core.WebMessageReceived -= OnWebMessageReceived;
                core.WebMessageReceived += OnWebMessageReceived;

                _webViewInitialized = true;
                LogTerminalEvent("webview.initialized", "Terminal WebView2 initialized", new Dictionary<string, string>
                {
                    ["rendererPath"] = rendererPath,
                });
            }
            catch (Exception ex)
            {
                lock (EnvironmentSync)
                {
                    SharedEnvironmentTask = null;
                }

                LogTerminalEvent("webview.initialization_failed", "Terminal WebView2 initialization failed", new Dictionary<string, string>
                {
                    ["exceptionType"] = ex.GetType().FullName ?? string.Empty,
                    ["message"] = ex.Message ?? string.Empty,
                    ["details"] = ex.ToString(),
                });
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
                    TrackInput(message.Data);
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
                    RaiseInteractionRequested();
                    FocusTerminal();
                    break;
            }
        }

        private void OnInteractionRequested(object sender, RoutedEventArgs e)
        {
            RaiseInteractionRequested();
        }

        private void RaiseInteractionRequested()
        {
            InteractionRequested?.Invoke(this, EventArgs.Empty);
        }

        private void EnsureStarted()
        {
            if (_started || !_rendererReady || _disposed)
            {
                return;
            }

            _started = true;
            UpdateStartupMask();

            try
            {
                _connection = new ConPtyConnection();
                _connection.OutputReceived += OnOutputReceived;
                _connection.ProcessExited += OnProcessExited;
                _connection.Start(_cols, _rows, ShellCommand, ResolveProcessWorkingDirectory(), LaunchEnvironment);
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
                UpdateReplayCommandFromOutput();
                if (ContainsDisplayText(text))
                {
                    HideStartupMask();
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
            TrackInput(text);
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
            TrackInput(StartupInput);
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
            if (_disposed)
            {
                return;
            }

            _fitTimer.Stop();
            _fitTimer.Start();
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
            if (e.NewSize.Width <= 1 || e.NewSize.Height <= 1)
            {
                return;
            }

            RequestFit();
        }

        private void OnFitTimerTick(DispatcherQueueTimer sender, object args)
        {
            _fitTimer.Stop();
            if (_disposed || !_webViewInitialized || TerminalView.CoreWebView2 is null)
            {
                return;
            }

            PostMessage(new HostMessage { Type = "fit" });
            LogTerminalEvent("fit.requested", "Renderer fit requested");
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

        private void TrackInput(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            foreach (char character in text)
            {
                switch (character)
                {
                    case '\r':
                    case '\n':
                        CommitTrackedInputLine();
                        break;
                    case '\b':
                    case (char)127:
                        if (_inputLineBuffer.Length > 0)
                        {
                            _inputLineBuffer.Length--;
                        }
                        break;
                    default:
                        if (!char.IsControl(character))
                        {
                            _inputLineBuffer.Append(character);
                        }
                        break;
                }
            }
        }

        private void CommitTrackedInputLine()
        {
            if (_inputLineBuffer.Length == 0)
            {
                return;
            }

            string line = _inputLineBuffer.ToString().Trim();
            _inputLineBuffer.Clear();
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            if (TryUpdateReplayTemplateFromCommand(line))
            {
                ReplayStateChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private bool TryUpdateReplayTemplateFromCommand(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            string trimmed = line.Trim();
            if (trimmed.StartsWith("codex", StringComparison.OrdinalIgnoreCase))
            {
                _replayTool = "codex";
                Match resumeMatch = CodexResumeInlineRegex.Match(trimmed);
                if (resumeMatch.Success)
                {
                    _toolLaunchArguments = resumeMatch.Groups[2].Value.Trim();
                    return SetReplayCommand("codex", resumeMatch.Groups[1].Value, BuildReplayCommand("codex", resumeMatch.Groups[1].Value, _toolLaunchArguments));
                }

                _toolLaunchArguments = Regex.Match(trimmed, @"^codex\b\s*(.*)$", RegexOptions.IgnoreCase).Groups[1].Value.Trim();
                return false;
            }

            if (trimmed.StartsWith("claude", StringComparison.OrdinalIgnoreCase))
            {
                _replayTool = "claude";
                Match resumeMatch = ClaudeResumeInlineRegex.Match(trimmed);
                if (resumeMatch.Success)
                {
                    _toolLaunchArguments = resumeMatch.Groups[2].Value.Trim();
                    return SetReplayCommand("claude", resumeMatch.Groups[1].Value, BuildReplayCommand("claude", resumeMatch.Groups[1].Value, _toolLaunchArguments));
                }

                _toolLaunchArguments = Regex.Match(trimmed, @"^claude\b\s*(.*)$", RegexOptions.IgnoreCase).Groups[1].Value.Trim();
                return false;
            }

            return false;
        }

        private void UpdateReplayCommandFromOutput()
        {
            string buffer = _outputBuffer.ToString();
            if (buffer.Length > 8000)
            {
                buffer = buffer[^8000..];
            }

            Match codexMatch = CodexResumeRegex.Match(buffer);
            if (codexMatch.Success)
            {
                if (SetReplayCommand("codex", codexMatch.Groups[1].Value, BuildReplayCommand("codex", codexMatch.Groups[1].Value, _replayTool == "codex" ? _toolLaunchArguments : null)))
                {
                    ReplayStateChanged?.Invoke(this, EventArgs.Empty);
                }

                return;
            }

            Match claudeMatch = ClaudeResumeRegex.Match(buffer);
            if (claudeMatch.Success && SetReplayCommand("claude", claudeMatch.Groups[1].Value, BuildReplayCommand("claude", claudeMatch.Groups[1].Value, _replayTool == "claude" ? _toolLaunchArguments : null)))
            {
                ReplayStateChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private bool SetReplayCommand(string tool, string sessionId, string command)
        {
            if (string.Equals(_replayTool, tool, StringComparison.Ordinal) &&
                string.Equals(_replaySessionId, sessionId, StringComparison.Ordinal) &&
                string.Equals(_replayCommand, command, StringComparison.Ordinal))
            {
                return false;
            }

            _replayTool = tool;
            _replaySessionId = sessionId;
            _replayCommand = command;
            return true;
        }

        private static string BuildReplayCommand(string tool, string sessionId, string arguments)
        {
            string suffix = string.IsNullOrWhiteSpace(arguments) ? string.Empty : " " + arguments.Trim();
            return string.Equals(tool, "codex", StringComparison.OrdinalIgnoreCase)
                ? $"codex resume {sessionId}{suffix}"
                : $"claude --resume {sessionId}{suffix}";
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
                ViewportY = 0,
                BufferLength = 0,
                ReplayTool = _replayTool,
                ReplaySessionId = _replaySessionId,
                ReplayCommand = _replayCommand,
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
                ViewportY = Math.Max(0, message.ViewportY),
                BufferLength = Math.Max(0, message.BufferLength),
                ReplayTool = _replayTool,
                ReplaySessionId = _replaySessionId,
                ReplayCommand = _replayCommand,
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

        private void UpdateStartupMask()
        {
            if (StartupTitleText is not null)
            {
                StartupTitleText.Text = ResolveStartupTitle();
            }

            if (StartupDetailText is not null)
            {
                string detail = DisplayWorkingDirectory;
                if (string.IsNullOrWhiteSpace(detail))
                {
                    detail = InitialWorkingDirectory;
                }

                if (string.IsNullOrWhiteSpace(detail))
                {
                    detail = "Preparing terminal host…";
                }

                StartupDetailText.Text = detail;
            }
        }

        private string ResolveStartupTitle()
        {
            string command = ShellCommand ?? string.Empty;
            if (command.Contains("wsl.exe", StringComparison.OrdinalIgnoreCase))
            {
                return "Starting WSL shell";
            }

            if (command.Contains("powershell", StringComparison.OrdinalIgnoreCase))
            {
                return "Starting PowerShell";
            }

            if (command.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase))
            {
                return "Starting Command Prompt";
            }

            return "Starting terminal";
        }

        private static bool ContainsDisplayText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            string sanitized = AnsiEscapeRegex.Replace(text, string.Empty);
            sanitized = new string(sanitized.Where(character =>
                !char.IsControl(character) ||
                character == '\r' ||
                character == '\n' ||
                character == '\t').ToArray());

            return sanitized.Any(character => !char.IsWhiteSpace(character));
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

        internal static Task<CoreWebView2Environment> GetEnvironmentAsync()
        {
            lock (EnvironmentSync)
            {
                if (SharedEnvironmentTask is null || SharedEnvironmentTask.IsFaulted || SharedEnvironmentTask.IsCanceled)
                {
                    SharedEnvironmentTask = CreateEnvironmentAsync();
                }

                return SharedEnvironmentTask;
            }
        }

        private static async Task<CoreWebView2Environment> CreateEnvironmentAsync()
        {
            string userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinMux",
                "terminal-webview");
            Directory.CreateDirectory(userDataFolder);

            CoreWebView2EnvironmentOptions options = new();
            string additionalArguments = Environment.GetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS");
            if (!string.IsNullOrWhiteSpace(additionalArguments))
            {
                options.AdditionalBrowserArguments = additionalArguments;
            }

            return await CoreWebView2Environment.CreateWithOptionsAsync(null, userDataFolder, options);
        }

        private async Task<CoreWebView2> WaitForCoreWebView2Async()
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                if (TerminalView.CoreWebView2 is not null)
                {
                    return TerminalView.CoreWebView2;
                }

                await Task.Delay(50).ConfigureAwait(true);
            }

            throw new InvalidOperationException("Terminal WebView2 core was not created.");
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
            public int ViewportY { get; set; }
            public int BufferLength { get; set; }
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
