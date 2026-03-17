using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Dispatching;
using Microsoft.Web.WebView2.Core;
using SelfContainedDeployment.Automation;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        private static readonly Regex ShellPromptRegex = new(@"(?:^|\n)\s*(?:[>$#]|❯|\?)\s*$", RegexOptions.Compiled);

        private ConPtyConnection _connection;
        private Task _initializationTask;
        private bool _rendererReady;
        private bool _rendererReadyProbePending;
        private bool _webViewInitialized;
        private bool _started;
        private bool _sessionExited;
        private bool _hasDisplayOutput;
        private bool _disposed;
        private bool _startupInputSent;
        private bool _replayRestoreInputSent;
        private bool _replayRestorePending;
        private bool _replayRestoreFailed;
        private int _cols = 120;
        private int _rows = 32;
        private int _lastLoggedResizeCols;
        private int _lastLoggedResizeRows;
        private string _sessionTitle = "Terminal";
        private ElementTheme _themePreference = ElementTheme.Default;
        private readonly Stopwatch _startupStopwatch = Stopwatch.StartNew();
        private readonly StringBuilder _outputBuffer = new();
        private readonly StringBuilder _pendingRendererOutput = new();
        private readonly StringBuilder _inputLineBuffer = new();
        private readonly Dictionary<string, TaskCompletionSource<NativeAutomationTerminalSnapshot>> _inspectionRequests = new();
        private readonly DispatcherQueueTimer _fitTimer;
        private readonly DispatcherQueueTimer _replayRestoreTimer;
        private double? _webViewInitializedMs;
        private double? _navigationCompletedMs;
        private double? _rendererReadyMs;
        private double? _sessionStartedMs;
        private double? _firstDisplayOutputMs;
        private string _activeToolSession;
        private string _replayTool;
        private string _replaySessionId;
        private string _replayCommand;
        private string _restoreReplayCommand;
        private string _toolLaunchArguments;
        private string _headerTitleOverride;

        public event EventHandler<string> SessionTitleChanged;
        public event EventHandler RendererReady;
        public event EventHandler SessionExited;
        public event EventHandler InteractionRequested;
        public event EventHandler OutputActivity;
        public event EventHandler ToolInteractionCompleted;
        public event EventHandler ToolSessionStateChanged;
        public event EventHandler ReplayStateChanged;
        public event EventHandler ReplayRestoreStateChanged;

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
            _replayRestoreTimer = DispatcherQueue.CreateTimer();
            _replayRestoreTimer.IsRepeating = false;
            _replayRestoreTimer.Interval = TimeSpan.FromSeconds(4);
            _replayRestoreTimer.Tick += OnReplayRestoreTimerTick;
            ApplyBackgroundColor();
            UpdateStartupMask();
            UpdateTerminalHeader();
        }

        public string ShellCommand { get; set; }

        public string InitialWorkingDirectory { get; set; }

        public string DisplayWorkingDirectory { get; set; }

        public string ProcessWorkingDirectory { get; set; }

        public IReadOnlyDictionary<string, string> LaunchEnvironment { get; set; }

        public string StartupInput { get; set; }

        public string RestoreReplayCommand
        {
            get => _restoreReplayCommand;
            set => _restoreReplayCommand = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        public bool AutoStartSession { get; set; } = true;

        public string SuspendedStatusText { get; set; }

        public string SessionTitle => _sessionTitle;

        public string HeaderTitleOverride => _headerTitleOverride;

        public string InitialTitleHint => GetInitialTitle();

        public string ReplayTool => _replayTool;

        public string ReplaySessionId => _replaySessionId;

        public string ReplayCommand => _replayCommand;

        public bool ReplayRestorePending => _replayRestorePending;

        public bool ReplayRestoreFailed => _replayRestoreFailed;

        public string ActiveToolSession => _activeToolSession;

        public bool HasLiveToolSession => !_sessionExited && !string.IsNullOrWhiteSpace(_activeToolSession);

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
            UpdateTerminalHeader();
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
            using var perfScope = NativeAutomationDiagnostics.TrackOperation("terminal.webview.init");
            NativeAutomationDiagnostics.IncrementCounter("terminalWebViewInit.count");
            try
            {
                CoreWebView2 core;
                try
                {
                    CoreWebView2Environment environment = await GetEnvironmentAsync().ConfigureAwait(true);
                    if (_disposed)
                    {
                        return;
                    }

                    await TerminalView.EnsureCoreWebView2Async(environment).AsTask().ConfigureAwait(true);
                    core = await WaitForCoreWebView2Async().ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    LogTerminalEvent("webview.profile_failed", "Terminal shared WebView2 environment failed; retrying with default profile.", new Dictionary<string, string>
                    {
                        ["exceptionType"] = ex.GetType().FullName ?? string.Empty,
                        ["message"] = ex.Message ?? string.Empty,
                    });
                    await TerminalView.EnsureCoreWebView2Async().AsTask().ConfigureAwait(true);
                    core = await WaitForCoreWebView2Async().ConfigureAwait(true);
                }

                if (_disposed)
                {
                    return;
                }

                ConfigureInitializedTerminal(core);
                string rendererPath = ResolveRendererPath();
                _rendererReady = false;
                _rendererReadyProbePending = false;
                TerminalView.Source = new Uri(rendererPath);

                _webViewInitialized = true;
                MarkStartupMilestone(
                    ref _webViewInitializedMs,
                    "startup.webview_initialized",
                    "Terminal WebView2 initialized",
                    new Dictionary<string, string>
                    {
                        ["rendererPath"] = rendererPath,
                    });
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
                    _rendererReadyProbePending = false;
                    MarkStartupMilestone(ref _rendererReadyMs, "startup.renderer_ready", "Terminal renderer reported ready");
                    LogTerminalEvent("renderer.ready", "Terminal renderer reported ready");
                    ApplyBackgroundColor();
                    PostCurrentTheme();
                    PostToolSessionState();
                    PostMessage(new HostMessage { Type = "setTitle", Title = _sessionTitle });
                    FlushPendingRendererOutput();
                    RequestFit();
                    HideStatus();
                    RendererReady?.Invoke(this, EventArgs.Empty);
                    if (AutoStartSession)
                    {
                        EnsureStarted();
                    }
                    else
                    {
                        ShowSuspendedState();
                    }
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
                        if (AutoStartSession)
                        {
                            EnsureStarted();
                        }
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

        private async void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (_disposed)
            {
                return;
            }

            if (!args.IsSuccess)
            {
                LogTerminalEvent("renderer.navigation_failed", "Terminal renderer navigation failed", new Dictionary<string, string>
                {
                    ["status"] = args.WebErrorStatus.ToString(),
                });
                ShowStatus($"Terminal renderer failed to load: {args.WebErrorStatus}", keepVisible: true);
                return;
            }

            MarkStartupMilestone(ref _navigationCompletedMs, "startup.navigation_completed", "Terminal renderer navigation completed");
            LogTerminalEvent("renderer.navigation_completed", "Terminal renderer navigation completed");
            if (AutoStartSession)
            {
                EnsureStarted();
            }
            await EnsureRendererReadyAsync().ConfigureAwait(true);
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
            if (_started || !_webViewInitialized || _disposed || !AutoStartSession)
            {
                return;
            }

            _started = true;
            _sessionExited = false;
            _hasDisplayOutput = false;
            UpdateStartupMask();

            try
            {
                _connection = new ConPtyConnection();
                _connection.OutputReceived += OnOutputReceived;
                _connection.ProcessExited += OnProcessExited;
                _connection.Start(_cols, _rows, ShellCommand, ResolveProcessWorkingDirectory(), LaunchEnvironment);
                MarkStartupMilestone(
                    ref _sessionStartedMs,
                    "startup.session_started",
                    "Started ConPTY session",
                    new Dictionary<string, string>
                    {
                        ["cols"] = _cols.ToString(),
                        ["rows"] = _rows.ToString(),
                        ["shellCommand"] = ShellCommand ?? string.Empty,
                        ["processWorkingDirectory"] = ResolveProcessWorkingDirectory() ?? string.Empty,
                    });
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
                TrySendReplayRestoreCommand();
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
                if (ShouldScanForReplayCommand(text))
                {
                    UpdateReplayCommandFromOutput();
                }

                if (ShouldScanForPrompt(text))
                {
                    UpdateLiveToolSessionFromOutput();
                }
                if (ContainsDisplayText(text))
                {
                    MarkStartupMilestone(ref _firstDisplayOutputMs, "startup.first_display_output", "Terminal produced visible output");
                    _hasDisplayOutput = true;
                    HideStartupMask();
                }

                 if (_replayRestorePending && _connection?.IsRunning == true)
                 {
                     _replayRestoreTimer.Stop();
                     _replayRestoreTimer.Start();
                 }

                if (_rendererReady)
                {
                    PostMessage(new HostMessage { Type = "output", Data = text });
                }
                else
                {
                    BufferPendingRendererOutput(text);
                }

                HideStatus();
                OutputActivity?.Invoke(this, EventArgs.Empty);
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

                _started = false;
                _sessionExited = true;
                SetActiveToolSession(null);
                HideStartupMask();
                PostMessage(new HostMessage { Type = "exit", Text = "Shell exited. Close the tab or open a new one." });
                if (_replayRestorePending)
                {
                    _replayRestorePending = false;
                    _replayRestoreFailed = true;
                    _replayRestoreTimer.Stop();
                    ReplayRestoreStateChanged?.Invoke(this, EventArgs.Empty);
                    ShowStatus("Replay restore failed. Close the tab or relaunch the saved session.", keepVisible: true);
                    LogTerminalEvent("replay.restore_failed", "Saved replay command exited before the restore could settle", new Dictionary<string, string>
                    {
                        ["replayCommand"] = _restoreReplayCommand ?? string.Empty,
                    });
                }
                else
                {
                    ShowStatus("Shell exited", keepVisible: true);
                }

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

        public Task WarmupAsync()
        {
            return EnsureInitializedAsync();
        }

        public void ApplyTheme(ElementTheme theme)
        {
            _themePreference = theme;
            RequestedTheme = theme;
            ApplyBackgroundColor();
            UpdateTerminalHeader();
            PostCurrentTheme();
        }

        public void SetHeaderTitleOverride(string title)
        {
            string normalized = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
            if (string.Equals(_headerTitleOverride, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _headerTitleOverride = normalized;
            UpdateTerminalHeader();
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

        private void TrySendReplayRestoreCommand()
        {
            if (_replayRestoreInputSent || string.IsNullOrWhiteSpace(_restoreReplayCommand))
            {
                return;
            }

            string command = _restoreReplayCommand.EndsWith("\r", StringComparison.Ordinal)
                ? _restoreReplayCommand
                : _restoreReplayCommand + "\r";
            _connection?.WriteInput(command);
            TrackInput(command);
            _replayRestoreInputSent = true;
            _replayRestorePending = true;
            _replayRestoreFailed = false;
            _replayRestoreTimer.Stop();
            _replayRestoreTimer.Start();
            ReplayRestoreStateChanged?.Invoke(this, EventArgs.Empty);
            LogTerminalEvent("replay.restore_started", "Sent saved replay command to restored terminal", new Dictionary<string, string>
            {
                ["replayCommand"] = _restoreReplayCommand,
            });
        }

        private void OnReplayRestoreTimerTick(DispatcherQueueTimer sender, object args)
        {
            _replayRestoreTimer.Stop();
            if (!_replayRestorePending)
            {
                return;
            }

            _replayRestorePending = false;
            _replayRestoreFailed = false;
            ReplayRestoreStateChanged?.Invoke(this, EventArgs.Empty);
            LogTerminalEvent("replay.restore_confirmed", "Saved replay command survived the restore grace window", new Dictionary<string, string>
            {
                ["replayCommand"] = _restoreReplayCommand ?? string.Empty,
            });
        }

        private void ShowSuspendedState()
        {
            _started = false;
            _sessionExited = true;
            UpdateStartupMask();
            ShowStatus(string.IsNullOrWhiteSpace(SuspendedStatusText) ? "Session ended" : SuspendedStatusText, keepVisible: true);
            if (StartupMask is not null)
            {
                StartupMask.Visibility = Visibility.Visible;
            }
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
                snapshot.Exited = IsSessionExited();
                snapshot.AutoStartSession = AutoStartSession;
                snapshot.ReplayRestorePending = _replayRestorePending;
                snapshot.ReplayRestoreFailed = _replayRestoreFailed;
                snapshot.StartupVisible = StartupMask?.Visibility == Visibility.Visible;
                snapshot.StatusVisible = StatusBadge?.Visibility == Visibility.Visible;
                snapshot.StatusText = StatusText?.Text;
                snapshot.HasDisplayOutput = _hasDisplayOutput;
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
            _replayRestoreTimer.Stop();
            _fitTimer.Stop();
            ActualThemeChanged -= OnActualThemeChanged;
            SizeChanged -= OnTerminalSizeChanged;

            TaskCompletionSource<NativeAutomationTerminalSnapshot>[] pendingInspectionRequests = _inspectionRequests.Values.ToArray();
            _inspectionRequests.Clear();
            foreach (TaskCompletionSource<NativeAutomationTerminalSnapshot> pendingInspection in pendingInspectionRequests)
            {
                pendingInspection.TrySetCanceled();
            }

            try
            {
                if (TerminalView.CoreWebView2 is not null)
                {
                    TerminalView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                    TerminalView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
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
            UpdateTerminalHeader();
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

            NativeAutomationDiagnostics.IncrementCounter("terminalFit.count");
            using var perfScope = NativeAutomationDiagnostics.TrackOperation("terminal.fit");
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

        private void BufferPendingRendererOutput(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            _pendingRendererOutput.Append(text);
            const int maxChars = 120000;
            if (_pendingRendererOutput.Length > maxChars)
            {
                _pendingRendererOutput.Remove(0, _pendingRendererOutput.Length - maxChars);
            }
        }

        private void FlushPendingRendererOutput()
        {
            if (!_rendererReady || _pendingRendererOutput.Length == 0)
            {
                return;
            }

            string bufferedOutput = _pendingRendererOutput.ToString();
            _pendingRendererOutput.Clear();
            PostMessage(new HostMessage { Type = "output", Data = bufferedOutput });
        }

        private void MarkStartupMilestone(
            ref double? milestoneMs,
            string eventName,
            string message,
            IReadOnlyDictionary<string, string> data = null)
        {
            if (milestoneMs.HasValue)
            {
                return;
            }

            milestoneMs = Math.Round(_startupStopwatch.Elapsed.TotalMilliseconds, 3);
            Dictionary<string, string> payload = data is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(data);
            payload["elapsedMs"] = milestoneMs.Value.ToString("0.###");
            LogTerminalEvent(eventName, message, payload);
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
                SetActiveToolSession("codex");
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
                SetActiveToolSession("claude");
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

        private bool ShouldScanForReplayCommand(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(_replayCommand))
            {
                return true;
            }

            return text.Contains("resume", StringComparison.OrdinalIgnoreCase);
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

        private void UpdateLiveToolSessionFromOutput()
        {
            if (string.IsNullOrWhiteSpace(_activeToolSession))
            {
                return;
            }

            string buffer = _outputBuffer.ToString();
            if (buffer.Length > 2000)
            {
                buffer = buffer[^2000..];
            }

            string sanitized = AnsiEscapeRegex.Replace(buffer, string.Empty)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');
            if (ShellPromptRegex.IsMatch(sanitized))
            {
                SetActiveToolSession(null);
            }
        }

        private bool ShouldScanForPrompt(string text)
        {
            if (string.IsNullOrWhiteSpace(_activeToolSession) || string.IsNullOrEmpty(text))
            {
                return false;
            }

            return text.Contains('\n') ||
                text.Contains('\r') ||
                text.Contains('>') ||
                text.Contains('$') ||
                text.Contains('#') ||
                text.Contains('❯');
        }

        private void SetActiveToolSession(string tool)
        {
            string previous = _activeToolSession;
            string normalized = string.IsNullOrWhiteSpace(tool)
                ? null
                : tool.Trim().ToLowerInvariant();
            if (string.Equals(previous, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _activeToolSession = normalized;
            PostToolSessionState();
            ToolSessionStateChanged?.Invoke(this, EventArgs.Empty);
            if (!string.IsNullOrWhiteSpace(previous) && string.IsNullOrWhiteSpace(normalized))
            {
                ToolInteractionCompleted?.Invoke(this, EventArgs.Empty);
            }
        }

        private bool ShouldShowToolSurface()
        {
            // The renderer no longer overlays tool chrome on top of terminal rows.
            return false;
        }

        private void PostToolSessionState()
        {
            PostMessage(new HostMessage
            {
                Type = "setToolSession",
                ToolSession = _activeToolSession,
                ToolSurfaceVisible = ShouldShowToolSurface(),
            });
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
                Exited = IsSessionExited(),
                AutoStartSession = AutoStartSession,
                ReplayRestorePending = _replayRestorePending,
                ReplayRestoreFailed = _replayRestoreFailed,
                StartupVisible = StartupMask?.Visibility == Visibility.Visible,
                StatusVisible = StatusBadge?.Visibility == Visibility.Visible,
                StatusText = StatusText?.Text,
                HasDisplayOutput = _hasDisplayOutput,
                WebViewInitializedMs = _webViewInitializedMs,
                NavigationCompletedMs = _navigationCompletedMs,
                RendererReadyMs = _rendererReadyMs,
                SessionStartedMs = _sessionStartedMs,
                FirstDisplayOutputMs = _firstDisplayOutputMs,
                Cols = _cols,
                Rows = _rows,
                ViewportY = 0,
                BufferLength = 0,
                ReplayTool = _replayTool,
                ReplaySessionId = _replaySessionId,
                ReplayCommand = _replayCommand,
                ActiveToolSession = _activeToolSession,
                ToolSurfaceVisible = ShouldShowToolSurface(),
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
                Exited = IsSessionExited(),
                AutoStartSession = AutoStartSession,
                ReplayRestorePending = _replayRestorePending,
                ReplayRestoreFailed = _replayRestoreFailed,
                StartupVisible = StartupMask?.Visibility == Visibility.Visible,
                StatusVisible = StatusBadge?.Visibility == Visibility.Visible,
                StatusText = StatusText?.Text,
                HasDisplayOutput = _hasDisplayOutput,
                WebViewInitializedMs = _webViewInitializedMs,
                NavigationCompletedMs = _navigationCompletedMs,
                RendererReadyMs = _rendererReadyMs,
                SessionStartedMs = _sessionStartedMs,
                FirstDisplayOutputMs = _firstDisplayOutputMs,
                Cols = Math.Max(1, message.Cols),
                Rows = Math.Max(1, message.Rows),
                CursorX = message.CursorX,
                CursorY = message.CursorY,
                ViewportY = Math.Max(0, message.ViewportY),
                BufferLength = Math.Max(0, message.BufferLength),
                ReplayTool = _replayTool,
                ReplaySessionId = _replaySessionId,
                ReplayCommand = _replayCommand,
                ActiveToolSession = string.IsNullOrWhiteSpace(message.ToolSession) ? _activeToolSession : message.ToolSession,
                ToolSurfaceVisible = message.ToolSurfaceVisible || ShouldShowToolSurface(),
                Selection = message.Selection,
                VisibleText = message.VisibleText,
                BufferTail = string.IsNullOrWhiteSpace(message.BufferTail) ? BuildFallbackTerminalSnapshot().BufferTail : message.BufferTail,
            });
        }

        private void OnActualThemeChanged(FrameworkElement sender, object args)
        {
            ApplyBackgroundColor();
            UpdateTerminalHeader();
            if (_themePreference == ElementTheme.Default)
            {
                PostCurrentTheme();
            }
        }

        private bool IsSessionExited()
        {
            return _sessionExited || !AutoStartSession;
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
            if (StartupKickerText is not null)
            {
                StartupKickerText.Text = ResolveStartupKicker();
            }

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

                if (!AutoStartSession && !string.IsNullOrWhiteSpace(SuspendedStatusText))
                {
                    detail = SuspendedStatusText;
                }

                StartupDetailText.Text = detail;
            }

            if (StartupMetaText is not null)
            {
                StartupMetaText.Text = ResolveStartupMeta();
            }

            UpdateTerminalHeader();
        }

        private string ResolveStartupKicker()
        {
            if (!AutoStartSession)
            {
                return "Suspended pane";
            }

            string command = ShellCommand ?? string.Empty;
            if (command.Contains("wsl.exe", StringComparison.OrdinalIgnoreCase))
            {
                return "WSL shell";
            }

            if (command.Contains("powershell", StringComparison.OrdinalIgnoreCase))
            {
                return "PowerShell";
            }

            if (command.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase))
            {
                return "Command Prompt";
            }

            return "Terminal workspace";
        }

        private string ResolveStartupTitle()
        {
            if (!AutoStartSession)
            {
                return "Session ended";
            }

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

        private string ResolveStartupMeta()
        {
            if (!AutoStartSession)
            {
                return "This pane is suspended until you explicitly resume it.";
            }

            string command = ShellCommand ?? string.Empty;
            if (command.Contains("wsl.exe", StringComparison.OrdinalIgnoreCase))
            {
                return "Booting the distro, reconnecting the terminal bridge, and waiting for the first prompt.";
            }

            if (command.Contains("powershell", StringComparison.OrdinalIgnoreCase))
            {
                return "Preparing PowerShell and attaching the shared renderer.";
            }

            if (command.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase))
            {
                return "Preparing Command Prompt and attaching the shared renderer.";
            }

            return "Preparing the terminal host, shell bridge, and renderer state.";
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

        private void ConfigureInitializedTerminal(CoreWebView2 core)
        {
            if (core is null)
            {
                throw new InvalidOperationException("Terminal CoreWebView2 was null after initialization.");
            }

            CoreWebView2Settings settings = core.Settings;
            if (settings is not null)
            {
                settings.AreDefaultContextMenusEnabled = false;
                settings.IsStatusBarEnabled = false;
                settings.AreDevToolsEnabled = true;
                settings.AreBrowserAcceleratorKeysEnabled = true;
            }

            core.WebMessageReceived -= OnWebMessageReceived;
            core.NavigationCompleted -= OnNavigationCompleted;
            core.WebMessageReceived += OnWebMessageReceived;
            core.NavigationCompleted += OnNavigationCompleted;
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

        private async Task EnsureRendererReadyAsync()
        {
            if (_disposed || _rendererReady || !_webViewInitialized || TerminalView.CoreWebView2 is null || _rendererReadyProbePending)
            {
                return;
            }

            _rendererReadyProbePending = true;

            try
            {
                await Task.Delay(50).ConfigureAwait(true);
                if (_disposed || _rendererReady || TerminalView.CoreWebView2 is null)
                {
                    return;
                }

                string script = """
                    (() => {
                        try {
                            if (window.__winmuxTerminalHost && typeof window.__winmuxTerminalHost.forceReady === "function") {
                                window.__winmuxTerminalHost.forceReady();
                                return JSON.stringify({ status: "forced", readyState: document.readyState });
                            }

                            return JSON.stringify({
                                status: "missing",
                                readyState: document.readyState,
                                hasBridge: !!(window.chrome && window.chrome.webview),
                            });
                        } catch (error) {
                            return JSON.stringify({
                                status: "error",
                                message: String(error),
                            });
                        }
                    })();
                    """;

                string result = await TerminalView.CoreWebView2.ExecuteScriptAsync(script).AsTask().ConfigureAwait(true);
                LogTerminalEvent("renderer.ready_probe", "Probed terminal renderer readiness after navigation", new Dictionary<string, string>
                {
                    ["result"] = result ?? string.Empty,
                });
            }
            catch (Exception ex)
            {
                LogTerminalEvent("renderer.ready_probe_failed", "Terminal renderer readiness probe failed", new Dictionary<string, string>
                {
                    ["exceptionType"] = ex.GetType().FullName ?? string.Empty,
                    ["message"] = ex.Message ?? string.Empty,
                });
            }
            finally
            {
                _rendererReadyProbePending = false;
            }
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
            UpdateTerminalHeader();
            LogTerminalEvent("status.shown", text);

            if (!keepVisible)
            {
                HideStatus();
            }
        }

        private void HideStatus()
        {
            StatusBadge.Visibility = Visibility.Collapsed;
            UpdateTerminalHeader();
        }

        private void UpdateTerminalHeader()
        {
            if (TerminalHeaderTitleText is null || TerminalHeaderMetaText is null)
            {
                return;
            }

            TerminalHeaderTitleText.Text = string.IsNullOrWhiteSpace(_sessionTitle) ? "Terminal" : _sessionTitle;
            if (!string.IsNullOrWhiteSpace(_headerTitleOverride))
            {
                TerminalHeaderTitleText.Text = _headerTitleOverride;
            }
            TerminalHeaderMetaText.Text = ResolveTerminalHeaderMeta();
        }

        private string ResolveTerminalHeaderMeta()
        {
            if (StatusBadge?.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(StatusText?.Text))
            {
                return StatusText.Text;
            }

            string detail = DisplayWorkingDirectory;
            if (string.IsNullOrWhiteSpace(detail))
            {
                detail = InitialWorkingDirectory;
            }

            if (!AutoStartSession && !string.IsNullOrWhiteSpace(SuspendedStatusText))
            {
                detail = SuspendedStatusText;
            }

            return string.IsNullOrWhiteSpace(detail) ? "Interactive shell" : detail;
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
            public string ToolSession { get; set; }
            public bool ToolSurfaceVisible { get; set; }
        }

        private sealed class HostMessage
        {
            public string Type { get; set; }
            public string Data { get; set; }
            public string Text { get; set; }
            public string Title { get; set; }
            public string Theme { get; set; }
            public string RequestId { get; set; }
            public string ToolSession { get; set; }
            public bool ToolSurfaceVisible { get; set; }
        }
    }
}
