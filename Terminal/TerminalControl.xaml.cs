using EasyWindowsTerminalControl;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Terminal.Wpf;
using SelfContainedDeployment.Automation;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using WinUIEx.Messaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;
using NativeTerminalViewControl = SelfContainedDeployment.Terminal.SafeEasyTerminalControl;
using DrawingColor = System.Drawing.Color;
using DrawingSize = System.Drawing.Size;
using UIColor = Windows.UI.Color;

namespace SelfContainedDeployment.Terminal
{
    public sealed partial class TerminalControl : UserControl
    {
        private const short NativeTerminalFontSize = 14;
        private static readonly object EnvironmentSync = new();
        private static Task<CoreWebView2Environment> SharedEnvironmentTask;
        private static readonly Regex CodexResumeRegex = new(@"codex\s+resume\s+([0-9a-f-]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ClaudeResumeRegex = new(@"claude\s+--resume\s+([0-9a-f-]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex CodexResumeInlineRegex = new(@"^codex\s+resume\s+([0-9a-f-]+)\s*(.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ClaudeResumeInlineRegex = new(@"^claude\s+--resume\s+([0-9a-f-]+)\s*(.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ReplaySessionIdRegex = new(@"^[0-9a-f-]{8,128}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SafeReplayArgumentsRegex = new(@"^[^\x00-\x1F\x7F;&|<>`]{0,512}$", RegexOptions.Compiled);
        private static readonly Regex AnsiEscapeRegex = new(@"\u001B(?:\[[0-9;?]*[ -/]*[@-~]|\][^\u0007]*(?:\u0007|\u001B\\))", RegexOptions.Compiled);
        private static readonly Regex ShellPromptRegex = new(@"(?:^|\n)\s*(?:[>$#]|❯|\?)\s*$", RegexOptions.Compiled);
        private static readonly Regex TitleSequenceRegex = new(@"\u001B\]0;(?<title>[^\u0007\x1B]*)(?:\u0007|\u001B\\)", RegexOptions.Compiled);
        private static readonly object DiagnosticLogSync = new();
        private static readonly string DiagnosticLogPath = Path.Combine(Path.GetTempPath(), "winmux-native-terminal.log");
        private const BindingFlags InstancePropertyFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private readonly Stopwatch _startupStopwatch = Stopwatch.StartNew();
        private readonly StringBuilder _outputBuffer = new();
        private readonly StringBuilder _inputLineBuffer = new();
        private readonly DispatcherQueueTimer _replayRestoreTimer;
        private readonly DispatcherQueueTimer _postStartInputTimer;

        private TermPTY _connection;
        private WinMuxTerminalProcessFactory _processFactory;
        private Process _process;
        private bool _rendererReady;
        private bool _nativeTerminalLoaded;
        private bool _started;
        private bool _sessionExited;
        private bool _hasDisplayOutput;
        private bool _disposed;
        private bool _startupInputSent;
        private bool _replayRestoreInputSent;
        private bool _replayRestorePending;
        private bool _replayRestoreFailed;
        private bool _pendingProgrammaticFocus;
        private bool _paneSelected;
        private bool _hostedSurfaceSuppressed;
        private int _cols = 120;
        private int _rows = 32;
        private string _sessionTitle = "Terminal";
        private ElementTheme _themePreference = ElementTheme.Default;
        private string _activeToolSession;
        private string _replayTool;
        private string _replaySessionId;
        private string _replayCommand;
        private string _restoreReplayCommand;
        private string _toolLaunchArguments;
        private string _headerTitleOverride;
        private bool _liveResizeMode;
        private double? _rendererReadyMs;
        private double? _sessionStartedMs;
        private double? _firstDisplayOutputMs;
        private NativeTerminalViewControl _nativeTerminalView;
        private UIElement _nativeTerminalFocusProxy;
        private Delegate _nativeTerminalMessageHookHandler;
        private Delegate _nativeTerminalWindowMessageHandler;
        private string _hostedSurfaceSuppressionTitle;
        private string _hostedSurfaceSuppressionMessage;

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

            _replayRestoreTimer = DispatcherQueue.CreateTimer();
            _replayRestoreTimer.IsRepeating = false;
            _replayRestoreTimer.Interval = TimeSpan.FromSeconds(4);
            _replayRestoreTimer.Tick += OnReplayRestoreTimerTick;

            _postStartInputTimer = DispatcherQueue.CreateTimer();
            _postStartInputTimer.IsRepeating = false;
            _postStartInputTimer.Interval = TimeSpan.FromMilliseconds(120);
            _postStartInputTimer.Tick += OnPostStartInputTimerTick;

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

        public string ReplayArguments => _toolLaunchArguments;

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
            EnsureNativeTerminalViewCreated();
            UpdateStartupMask();
            UpdateTerminalHeader();
            if (AutoStartSession)
            {
                EnsureStarted();
            }
            else
            {
                ShowSuspendedState();
            }
            await WarmupAsync().ConfigureAwait(true);
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
        }

        private void OnNativeTerminalLoaded(object sender, RoutedEventArgs e)
        {
            AttachNativeTerminalInteractionProxy();
            MarkNativeTerminalReady();
        }

        private void MarkNativeTerminalReady()
        {
            if (_disposed || _nativeTerminalLoaded)
            {
                return;
            }

            _nativeTerminalLoaded = true;
            _rendererReady = true;
            MarkStartupMilestone(ref _rendererReadyMs, "startup.renderer_ready", "Native terminal renderer loaded");
            ApplyTerminalAutomationIsolation();
            ApplyBackgroundColor();
            UpdateNativeTerminalTheme();
            UpdateTerminalHeader();
            RequestFit();
            TryFulfillPendingProgrammaticFocus();
            RendererReady?.Invoke(this, EventArgs.Empty);

            if (AutoStartSession)
            {
                EnsureStarted();
            }
            else
            {
                ShowSuspendedState();
            }
        }

        private void EnsureStarted()
        {
            if (_started || _disposed || !AutoStartSession || _nativeTerminalView is null)
            {
                return;
            }

            _started = true;
            _sessionExited = false;
            _hasDisplayOutput = false;
            _startupInputSent = false;
            _replayRestoreInputSent = false;
            _replayRestorePending = !string.IsNullOrWhiteSpace(_restoreReplayCommand);
            _replayRestoreFailed = false;
            ResetActiveToolSession(raiseCompletedEvent: false);
            UpdateStartupMask();
            UpdateTerminalHeader();

            _connection = new TermPTY();
            _connection.TermReady += OnTermReady;
            _connection.TerminalOutput += OnTerminalOutput;
            _connection.InterceptInputToTermApp += InterceptInputToTermApp;
            _nativeTerminalView.ConPTYTerm = _connection;
            _nativeTerminalView.LogConPTYOutput = true;
            _nativeTerminalView.Win32InputMode = true;
            _nativeTerminalView.StartupCommandLine = ShellCommand;

            int startCols = _nativeTerminalView.Terminal is not null && _nativeTerminalView.Terminal.Columns > 0
                ? _nativeTerminalView.Terminal.Columns
                : _cols;
            int startRows = _nativeTerminalView.Terminal is not null && _nativeTerminalView.Terminal.Rows > 0
                ? _nativeTerminalView.Terminal.Rows
                : _rows;
            UpdateTerminalMetrics(startCols, startRows);

            MarkStartupMilestone(
                ref _sessionStartedMs,
                "startup.session_started",
                "Started native terminal session",
                new Dictionary<string, string>
                {
                    ["cols"] = _cols.ToString(),
                    ["rows"] = _rows.ToString(),
                    ["shellCommand"] = ShellCommand ?? string.Empty,
                    ["processWorkingDirectory"] = ResolveProcessWorkingDirectory() ?? string.Empty,
                });

            UpdateSessionTitle(GetInitialTitle());
            ShowStatus("Starting terminal…", keepVisible: true);
            LogTerminalEvent("session.started", "Started native terminal session", new Dictionary<string, string>
            {
                ["cols"] = _cols.ToString(),
                ["rows"] = _rows.ToString(),
                ["shellCommand"] = ShellCommand ?? string.Empty,
                ["processWorkingDirectory"] = ResolveProcessWorkingDirectory() ?? string.Empty,
            });
            TraceNativeTerminal($"EnsureStarted shell='{ShellCommand}' cwd='{ResolveProcessWorkingDirectory()}' cols={_cols} rows={_rows}");
            _ = StartNativeTerminalAsync();
        }

        private async Task StartNativeTerminalAsync()
        {
            string workingDirectory = ResolveProcessWorkingDirectory();
            ConfigureProcessFactory(workingDirectory);
            IReadOnlyDictionary<string, string> launchEnvironment = ConPtyConnection.BuildLaunchEnvironment(workingDirectory, LaunchEnvironment);
            Dictionary<string, string> previousEnvironmentValues = new(StringComparer.OrdinalIgnoreCase);
            string previousCurrentDirectory = Environment.CurrentDirectory;

            try
            {
                foreach ((string key, string value) in launchEnvironment)
                {
                    previousEnvironmentValues[key] = Environment.GetEnvironmentVariable(key);
                    Environment.SetEnvironmentVariable(key, value);
                }

                if (Directory.Exists(workingDirectory))
                {
                    Environment.CurrentDirectory = workingDirectory;
                }

                await _nativeTerminalView.RestartTerm(_connection, false).ConfigureAwait(true);
                AdoptLiveConnection(_nativeTerminalView.ConPTYTerm);
                TryAdoptProcessFromConnection(_nativeTerminalView.ConPTYTerm);
            }
            catch (Exception ex)
            {
                TraceNativeTerminal($"Start failed: {ex}");
                if (_disposed)
                {
                    return;
                }

                _started = false;
                _sessionExited = true;
                ShowStatus($"Failed to start terminal: {ex.Message}", keepVisible: true);
                UpdateTerminalHeader();
                LogTerminalEvent("session.start_failed", "Failed to start native terminal session", new Dictionary<string, string>
                {
                    ["message"] = ex.Message,
                    ["exceptionType"] = ex.GetType().FullName ?? string.Empty,
                });
                ResetActiveToolSession(raiseCompletedEvent: false);
            }
            finally
            {
                foreach ((string key, string previousValue) in previousEnvironmentValues)
                {
                    Environment.SetEnvironmentVariable(key, previousValue);
                }

                Environment.CurrentDirectory = previousCurrentDirectory;
            }
        }

        private void OnProcessStarted(Process process)
        {
            if (_disposed || process is null)
            {
                return;
            }

            if (_process is not null && !ReferenceEquals(_process, process))
            {
                _process.Exited -= OnProcessExited;
                _process.Dispose();
            }

            _process = process;
            _process.Exited -= OnProcessExited;
            _process.Exited += OnProcessExited;
            TraceNativeTerminal($"ProcessStarted pid={process.Id}");
        }

        private void OnTermReady(object sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_disposed)
                {
                    return;
                }

                AdoptLiveConnection(_nativeTerminalView.ConPTYTerm);
                TryAdoptProcessFromConnection(_nativeTerminalView.ConPTYTerm);
                if (_connection is null)
                {
                    return;
                }

                if (_nativeTerminalView.Terminal is not null)
                {
                    _nativeTerminalView.Terminal.AutoResize = true;
                }

                _connection.Win32DirectInputMode(enable: true);
                UpdateNativeTerminalTheme();
                TraceNativeTerminal("TermReady");
                RequestFit();
                HideStatus();
                _postStartInputTimer.Stop();
                _postStartInputTimer.Start();
            });
        }

        private void OnPostStartInputTimerTick(DispatcherQueueTimer sender, object args)
        {
            sender.Stop();
            TrySendStartupInput();
            TrySendReplayRestoreCommand();
        }

        private void OnTerminalOutput(object sender, TerminalOutputEventArgs e)
        {
            if (e is null || string.IsNullOrEmpty(e.Data))
            {
                return;
            }

            string text = e.Data;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_disposed)
                {
                    return;
                }

                AppendOutput(text);
                if (!_hasDisplayOutput && ContainsDisplayText(text))
                {
                    TraceNativeTerminal("FirstDisplayOutput");
                }
                TryUpdateTitleFromOutput(text);

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
                    TryFulfillPendingProgrammaticFocus();
                }

                if (_replayRestorePending && _process?.HasExited == false)
                {
                    _replayRestoreTimer.Stop();
                    _replayRestoreTimer.Start();
                }

                HideStatus();
                UpdateTerminalHeader();
                OutputActivity?.Invoke(this, EventArgs.Empty);
            });
        }

        private void AdoptLiveConnection(TermPTY connection)
        {
            if (connection is null || ReferenceEquals(_connection, connection))
            {
                return;
            }

            if (_connection is not null)
            {
                _connection.TermReady -= OnTermReady;
                _connection.TerminalOutput -= OnTerminalOutput;
                _connection.InterceptInputToTermApp -= InterceptInputToTermApp;
            }

            _connection = connection;
            _connection.TermReady += OnTermReady;
            _connection.TerminalOutput += OnTerminalOutput;
            _connection.InterceptInputToTermApp += InterceptInputToTermApp;
        }

        private void InterceptInputToTermApp(ref Span<char> text)
        {
            if (text.IsEmpty)
            {
                return;
            }

            string input = text.ToString();
            DispatcherQueue.TryEnqueue(() => TrackInput(input));
        }

        private void OnProcessExited(object sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_disposed)
                {
                    return;
                }

                _sessionExited = true;
                _started = false;
                _replayRestoreTimer.Stop();
                _postStartInputTimer.Stop();
                _pendingProgrammaticFocus = false;
                ResetActiveToolSession(raiseCompletedEvent: false);
                HideStatus();
                UpdateTerminalHeader();
                SessionExited?.Invoke(this, EventArgs.Empty);
                if (_replayRestorePending)
                {
                    _replayRestorePending = false;
                    _replayRestoreFailed = true;
                    ReplayRestoreStateChanged?.Invoke(this, EventArgs.Empty);
                }

                LogTerminalEvent("session.exited", "Native terminal session exited");
            });
        }

        public void FocusTerminal()
        {
            EnsureNativeTerminalViewCreated();
            if (!CanReceiveProgrammaticFocus())
            {
                _pendingProgrammaticFocus = true;
                TraceNativeTerminal("Deferred programmatic focus until terminal is interactive");
                return;
            }

            TryApplyProgrammaticFocus("requested");
        }

        public void CancelPendingProgrammaticFocus()
        {
            _pendingProgrammaticFocus = false;
        }

        public Task WarmupAsync()
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            if (AutoStartSession)
            {
                EnsureStarted();
            }

            return Task.CompletedTask;
        }

        public void ApplyTheme(ElementTheme theme)
        {
            _themePreference = theme;
            ApplyBackgroundColor();
            UpdateNativeTerminalTheme();
            UpdateTerminalHeader();
        }

        public void SetPaneSelected(bool selected)
        {
            if (_paneSelected == selected)
            {
                return;
            }

            _paneSelected = selected;
            ApplyPaneSelectionChrome();
        }

        public void SetHeaderTitleOverride(string title)
        {
            _headerTitleOverride = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
            UpdateTerminalHeader();
        }

        public void SendInput(string text)
        {
            if (string.IsNullOrEmpty(text) || _connection is null)
            {
                return;
            }

            TrackInput(text);
            _connection.WriteToTerm(text);
        }

        public void RequestFit()
        {
            if (_disposed || !_nativeTerminalLoaded)
            {
                return;
            }

            if (_nativeTerminalView is null)
            {
                return;
            }

            DrawingSize size = new(
                Math.Max(0, (int)Math.Round(NativeTerminalHost.ActualWidth)),
                Math.Max(0, (int)Math.Round(NativeTerminalHost.ActualHeight)));
            if (size.Width <= 0 || size.Height <= 0)
            {
                return;
            }

            if (_nativeTerminalView.Terminal is null)
            {
                return;
            }

            UpdateTerminalMetrics(_nativeTerminalView.Terminal.Columns, _nativeTerminalView.Terminal.Rows);
        }

        public void SetLiveResizeMode(bool enabled)
        {
            if (_liveResizeMode == enabled)
            {
                return;
            }

            _liveResizeMode = enabled;
            if (enabled)
            {
                ApplyHostedSurfaceVisibility();
                HideStatus();
                return;
            }

            ApplyHostedSurfaceVisibility();
            UpdateStartupMask();
            RequestFit();
        }

        public void SetHostedSurfaceSuppressed(bool suppressed, string title = null, string message = null)
        {
            if (_hostedSurfaceSuppressed == suppressed &&
                string.Equals(_hostedSurfaceSuppressionTitle, title, StringComparison.Ordinal) &&
                string.Equals(_hostedSurfaceSuppressionMessage, message, StringComparison.Ordinal))
            {
                return;
            }

            _hostedSurfaceSuppressed = suppressed;
            _hostedSurfaceSuppressionTitle = suppressed ? title?.Trim() : null;
            _hostedSurfaceSuppressionMessage = suppressed ? message?.Trim() : null;
            ApplyHostedSurfaceVisibility();
            UpdateStartupMask();
            if (!suppressed && !_liveResizeMode)
            {
                RequestFit();
            }
        }

        public Task<NativeAutomationTerminalSnapshot> GetTerminalSnapshotAsync()
        {
            return Task.FromResult(BuildTerminalSnapshot());
        }

        public void DisposeTerminal()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _replayRestoreTimer.Stop();
            _postStartInputTimer.Stop();
            _pendingProgrammaticFocus = false;
            ResetActiveToolSession(raiseCompletedEvent: false);

            if (_process is not null)
            {
                _process.Exited -= OnProcessExited;
                _process.Dispose();
                _process = null;
            }

            DetachProcessFactory();

            if (_connection is not null)
            {
                _connection.TermReady -= OnTermReady;
                _connection.TerminalOutput -= OnTerminalOutput;
                _connection.InterceptInputToTermApp -= InterceptInputToTermApp;
                try
                {
                    _connection.CloseStdinToApp();
                }
                catch
                {
                }

                try
                {
                    _connection.StopExternalTermOnly();
                }
                catch
                {
                }

                _connection = null;
            }

            if (_nativeTerminalView is not null)
            {
                _nativeTerminalView.ConPTYTerm = null;
            }
        }

        private void TrySendStartupInput()
        {
            if (_startupInputSent || _connection is null || string.IsNullOrWhiteSpace(StartupInput))
            {
                return;
            }

            _startupInputSent = true;
            TrackInput(StartupInput);
            _connection.WriteToTerm(StartupInput);
        }

        private void TrySendReplayRestoreCommand()
        {
            if (_replayRestoreInputSent || _connection is null || string.IsNullOrWhiteSpace(_restoreReplayCommand))
            {
                return;
            }

            _replayRestoreInputSent = true;
            _replayRestorePending = true;
            _replayRestoreFailed = false;
            ReplayRestoreStateChanged?.Invoke(this, EventArgs.Empty);
            LogTerminalEvent("replay.restore_started", "Issued replay restore command", new Dictionary<string, string>
            {
                ["command"] = _restoreReplayCommand,
            });

            string input = _restoreReplayCommand.EndsWith('\n') || _restoreReplayCommand.EndsWith('\r')
                ? _restoreReplayCommand
                : _restoreReplayCommand + Environment.NewLine;
            TrackInput(input);
            _connection.WriteToTerm(input);
            _replayRestoreTimer.Stop();
            _replayRestoreTimer.Start();
        }

        private void OnReplayRestoreTimerTick(DispatcherQueueTimer sender, object args)
        {
            sender.Stop();
            if (!_replayRestorePending)
            {
                return;
            }

            _replayRestorePending = false;
            _replayRestoreFailed = true;
            ReplayRestoreStateChanged?.Invoke(this, EventArgs.Empty);
            LogTerminalEvent("replay.restore_timeout", "Replay restore timed out");
            UpdateTerminalHeader();
        }

        private void ShowSuspendedState()
        {
            ApplyHostedSurfaceVisibility();
            ResetActiveToolSession(raiseCompletedEvent: false);
            if (StartupMask is not null)
            {
                StartupMask.Visibility = Visibility.Visible;
            }

            HideStatus();
            UpdateStartupMask();
            UpdateTerminalHeader();
        }

        private void ApplyHostedSurfaceVisibility()
        {
            if (NativeTerminalHost is null)
            {
                return;
            }

            bool showNativeHost = !_hostedSurfaceSuppressed
                && !_liveResizeMode
                && _nativeTerminalLoaded
                && AutoStartSession
                && !_sessionExited;
            NativeTerminalHost.Visibility = showNativeHost ? Visibility.Visible : Visibility.Collapsed;
        }

        private static void CopySelectionToClipboard(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            DataPackage package = new();
            package.SetText(text);
            Clipboard.SetContent(package);
        }

        private void UpdateSessionTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return;
            }

            string normalizedTitle = NormalizeSessionTitle(title);
            if (string.IsNullOrWhiteSpace(normalizedTitle) ||
                string.Equals(_sessionTitle, normalizedTitle, StringComparison.Ordinal))
            {
                return;
            }

            _sessionTitle = normalizedTitle;
            LogTerminalEvent("title.updated", $"Session title set to {normalizedTitle}", new Dictionary<string, string>
            {
                ["title"] = normalizedTitle,
            });
            UpdateTerminalHeader();
            SessionTitleChanged?.Invoke(this, normalizedTitle);
        }

        private void TryUpdateTitleFromOutput(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            MatchCollection matches = TitleSequenceRegex.Matches(text);
            if (matches.Count == 0)
            {
                return;
            }

            string title = matches[^1].Groups["title"].Value;
            if (!string.IsNullOrWhiteSpace(title))
            {
                UpdateSessionTitle(title);
            }
        }

        private string GetInitialTitle()
        {
            return "Terminal";
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

            if (LooksLikePathTitle(trimmed) && !string.IsNullOrWhiteSpace(titleLeaf))
            {
                return GetInitialTitle();
            }

            return trimmed;
        }

        private static bool LooksLikePathTitle(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string trimmed = value.Trim();
            if (trimmed.StartsWith("~/", StringComparison.Ordinal) ||
                trimmed.StartsWith("/", StringComparison.Ordinal) ||
                trimmed.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return true;
            }

            return trimmed.Length > 2 &&
                char.IsLetter(trimmed[0]) &&
                trimmed[1] == ':' &&
                (trimmed[2] == '\\' || trimmed[2] == '/');
        }

        private static bool LooksLikeExecutablePath(string value)
        {
            if (!LooksLikePathTitle(value))
            {
                return false;
            }

            return value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                value.EndsWith(".com", StringComparison.OrdinalIgnoreCase);
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
            if (_disposed || !_nativeTerminalLoaded)
            {
                return;
            }

            if (e.NewSize.Width <= 1 || e.NewSize.Height <= 1)
            {
                return;
            }

            RequestFit();
        }

        private string ResolveProcessWorkingDirectory()
        {
            if (!string.IsNullOrWhiteSpace(ProcessWorkingDirectory) && Directory.Exists(ProcessWorkingDirectory))
            {
                return ProcessWorkingDirectory;
            }

            if (!string.IsNullOrWhiteSpace(InitialWorkingDirectory) && Directory.Exists(InitialWorkingDirectory))
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
            if (_outputBuffer.Length > 200000)
            {
                _outputBuffer.Remove(0, _outputBuffer.Length - 200000);
            }
        }

        private void MarkStartupMilestone(
            ref double? milestone,
            string eventName,
            string message,
            IReadOnlyDictionary<string, string> payload = null)
        {
            if (milestone is not null)
            {
                return;
            }

            milestone = _startupStopwatch.Elapsed.TotalMilliseconds;
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
                ResetActiveToolSession(raiseCompletedEvent: true);
            }

            if (_replayRestorePending)
            {
                _replayRestorePending = false;
                _replayRestoreFailed = false;
                ReplayRestoreStateChanged?.Invoke(this, EventArgs.Empty);
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
            ToolSessionStateChanged?.Invoke(this, EventArgs.Empty);
            if (!string.IsNullOrWhiteSpace(previous) && string.IsNullOrWhiteSpace(normalized))
            {
                ToolInteractionCompleted?.Invoke(this, EventArgs.Empty);
            }

            UpdateToolSessionRail();
            UpdateTerminalHeader();
        }

        private void ResetActiveToolSession(bool raiseCompletedEvent)
        {
            string previous = _activeToolSession;
            if (string.IsNullOrWhiteSpace(previous))
            {
                UpdateToolSessionRail();
                UpdateTerminalHeader();
                return;
            }

            _activeToolSession = null;
            ToolSessionStateChanged?.Invoke(this, EventArgs.Empty);
            if (raiseCompletedEvent)
            {
                ToolInteractionCompleted?.Invoke(this, EventArgs.Empty);
            }

            UpdateToolSessionRail();
            UpdateTerminalHeader();
        }

        private static string BuildReplayCommand(string tool, string sessionId, string arguments)
        {
            string suffix = string.IsNullOrWhiteSpace(arguments) ? string.Empty : " " + arguments.Trim();
            return string.Equals(tool, "codex", StringComparison.OrdinalIgnoreCase)
                ? $"codex resume {sessionId}{suffix}"
                : $"claude --resume {sessionId}{suffix}";
        }

        public static bool TryBuildReplayRestoreCommand(string tool, string sessionId, string arguments, out string command)
        {
            command = null;
            if (!IsSupportedReplayTool(tool) || !IsValidReplaySessionId(sessionId))
            {
                return false;
            }

            string normalizedArguments = NormalizeReplayArguments(arguments);
            if (!string.IsNullOrWhiteSpace(arguments) && normalizedArguments is null)
            {
                return false;
            }

            command = BuildReplayCommand(tool, sessionId, normalizedArguments);
            return true;
        }

        public static bool TryExtractReplayCommandMetadata(string command, out string tool, out string sessionId, out string arguments)
        {
            tool = null;
            sessionId = null;
            arguments = null;

            if (string.IsNullOrWhiteSpace(command))
            {
                return false;
            }

            string trimmed = command.Trim();
            Match codexMatch = CodexResumeInlineRegex.Match(trimmed);
            if (codexMatch.Success)
            {
                string normalizedArguments = NormalizeReplayArguments(codexMatch.Groups[2].Value);
                if (!string.IsNullOrWhiteSpace(codexMatch.Groups[2].Value) && normalizedArguments is null)
                {
                    return false;
                }

                tool = "codex";
                sessionId = codexMatch.Groups[1].Value;
                arguments = normalizedArguments;
                return IsValidReplaySessionId(sessionId);
            }

            Match claudeMatch = ClaudeResumeInlineRegex.Match(trimmed);
            if (!claudeMatch.Success)
            {
                return false;
            }

            string normalizedClaudeArguments = NormalizeReplayArguments(claudeMatch.Groups[2].Value);
            if (!string.IsNullOrWhiteSpace(claudeMatch.Groups[2].Value) && normalizedClaudeArguments is null)
            {
                return false;
            }

            tool = "claude";
            sessionId = claudeMatch.Groups[1].Value;
            arguments = normalizedClaudeArguments;
            return IsValidReplaySessionId(sessionId);
        }

        private static bool IsSupportedReplayTool(string tool)
        {
            return string.Equals(tool, "codex", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tool, "claude", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsValidReplaySessionId(string sessionId)
        {
            return !string.IsNullOrWhiteSpace(sessionId) && ReplaySessionIdRegex.IsMatch(sessionId);
        }

        private static string NormalizeReplayArguments(string arguments)
        {
            if (string.IsNullOrWhiteSpace(arguments))
            {
                return null;
            }

            string trimmed = arguments.Trim();
            return SafeReplayArgumentsRegex.IsMatch(trimmed) ? trimmed : null;
        }

        private NativeAutomationTerminalSnapshot BuildTerminalSnapshot()
        {
            string consoleText = TermPTY.StripColors(_outputBuffer.ToString());
            if (string.IsNullOrWhiteSpace(consoleText))
            {
                consoleText = _connection?.GetConsoleText() ?? string.Empty;
            }

            if (!_hasDisplayOutput && LooksLikeExecutablePath(consoleText.Trim()))
            {
                consoleText = string.Empty;
            }

            string[] lines = SplitLines(consoleText);
            string selection = _nativeTerminalLoaded && _nativeTerminalView?.Terminal is not null
                ? _nativeTerminalView.Terminal.GetSelectedText()
                : string.Empty;

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
                RendererReadyMs = _rendererReadyMs,
                SessionStartedMs = _sessionStartedMs,
                FirstDisplayOutputMs = _firstDisplayOutputMs,
                Cols = _cols,
                Rows = _rows,
                CursorX = 0,
                CursorY = 0,
                ViewportY = Math.Max(0, lines.Length - _rows),
                BufferLength = lines.Length,
                ReplayTool = _replayTool,
                ReplaySessionId = _replaySessionId,
                ReplayCommand = _replayCommand,
                ReplayArguments = _toolLaunchArguments,
                ActiveToolSession = _activeToolSession,
                ToolSurfaceVisible = HasLiveToolSession,
                Selection = selection,
                VisibleText = BuildVisibleText(lines, _rows),
                BufferTail = BuildBufferTail(lines, 200),
            };
        }

        private void OnActualThemeChanged(FrameworkElement sender, object args)
        {
            ApplyBackgroundColor();
            UpdateNativeTerminalTheme();
            UpdateToolSessionRail();
            UpdateTerminalHeader();
        }

        private bool IsSessionExited()
        {
            return _sessionExited || _process?.HasExited == true;
        }

        private void RaiseInteractionRequested()
        {
            InteractionRequested?.Invoke(this, EventArgs.Empty);
        }

        private void OnNativeTerminalInteractionPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            RaiseInteractionRequested();
        }

        private void OnNativeTerminalInteractionGotFocus(object sender, RoutedEventArgs e)
        {
            RaiseInteractionRequested();
        }

        private void OnNativeTerminalInteractionGettingFocus(UIElement sender, GettingFocusEventArgs args)
        {
            RaiseInteractionRequested();
        }

        private void OnNativeTerminalInteractionMessageHook(object sender, WindowMessageEventArgs e)
        {
            switch ((WindowsMessages)e.Message.MessageId)
            {
                case WindowsMessages.MOUSEACTIVATE:
                case WindowsMessages.SETFOCUS:
                case WindowsMessages.LBUTTONDOWN:
                case WindowsMessages.LBUTTONDBLCLK:
                case WindowsMessages.RBUTTONDOWN:
                case WindowsMessages.MBUTTONDOWN:
                    RaiseInteractionRequested();
                    break;
            }
        }

        private ElementTheme ResolveEffectiveTheme()
        {
            return _themePreference switch
            {
                ElementTheme.Light => ElementTheme.Light,
                ElementTheme.Dark => ElementTheme.Dark,
                _ => ActualTheme == ElementTheme.Light ? ElementTheme.Light : ElementTheme.Dark,
            };
        }

        private string ResolveThemeName()
        {
            return ResolveEffectiveTheme() == ElementTheme.Light ? "light" : "dark";
        }

        private UIColor ResolveShellColor(string key, UIColor fallbackColor)
        {
            return ShellTheme.ResolveColorForTheme(ResolveEffectiveTheme(), key, fallbackColor);
        }

        private void ApplyBackgroundColor()
        {
            UIColor backgroundColor = ResolveShellColor(
                "ShellSurfaceBackgroundBrush",
                ResolveEffectiveTheme() == ElementTheme.Light
                    ? new UIColor { A = 255, R = 249, G = 251, B = 253 }
                    : new UIColor { A = 255, R = 16, G = 18, B = 22 });

            SolidColorBrush brush = new(backgroundColor);
            if (TerminalRoot is not null)
            {
                TerminalRoot.Background = brush;
            }

            if (TerminalContentHost is not null)
            {
                TerminalContentHost.Background = brush;
            }

            if (StartupMask is not null)
            {
                StartupMask.Background = brush;
            }

            ApplyPaneSelectionChrome();
        }

        private void ApplyPaneSelectionChrome()
        {
            bool lightTheme = ResolveEffectiveTheme() == ElementTheme.Light;
            UIColor surfaceColor = ResolveShellColor(
                "ShellSurfaceBackgroundBrush",
                lightTheme
                    ? new UIColor { A = 255, R = 249, G = 251, B = 253 }
                    : new UIColor { A = 255, R = 16, G = 18, B = 22 });
            UIColor borderColor = ResolveShellColor(
                "ShellBorderBrush",
                lightTheme
                    ? new UIColor { A = 255, R = 184, G = 198, B = 212 }
                    : new UIColor { A = 255, R = 30, G = 33, B = 39 });
            UIColor accentColor = ResolveShellColor(
                "ShellTerminalBrush",
                lightTheme
                    ? new UIColor { A = 255, R = 15, G = 118, B = 110 }
                    : new UIColor { A = 255, R = 45, G = 212, B = 191 });

            if (TerminalHeader is not null)
            {
                TerminalHeader.Background = new SolidColorBrush(surfaceColor);
                TerminalHeader.BorderBrush = new SolidColorBrush(borderColor);
            }

            if (NativeTerminalViewport is not null)
            {
                NativeTerminalViewport.Background = new SolidColorBrush(surfaceColor);
                NativeTerminalViewport.BorderBrush = new SolidColorBrush(new UIColor { A = 0, R = 0, G = 0, B = 0 });
                NativeTerminalViewport.BorderThickness = new Thickness(0);
            }
        }

        private static UIColor MixColors(UIColor baseColor, UIColor overlayColor, double overlayWeight)
        {
            double clampedWeight = Math.Clamp(overlayWeight, 0, 1);
            double baseWeight = 1 - clampedWeight;
            return new UIColor
            {
                A = 255,
                R = (byte)Math.Round((baseColor.R * baseWeight) + (overlayColor.R * clampedWeight)),
                G = (byte)Math.Round((baseColor.G * baseWeight) + (overlayColor.G * clampedWeight)),
                B = (byte)Math.Round((baseColor.B * baseWeight) + (overlayColor.B * clampedWeight)),
            };
        }

        private static UIColor LiftColor(UIColor color, bool lightTheme)
        {
            return MixColors(
                color,
                new UIColor { A = 255, R = 255, G = 255, B = 255 },
                lightTheme ? 0.16 : 0.24);
        }

        private static DrawingColor ToDrawingColor(UIColor color)
        {
            return DrawingColor.FromArgb(color.A, color.R, color.G, color.B);
        }

        private static uint ToTerminalColor(UIColor color)
        {
            return EasyTerminalControl.ColorToVal(ToDrawingColor(color));
        }

        private void UpdateNativeTerminalTheme()
        {
            if (!_nativeTerminalLoaded)
            {
                return;
            }

            if (_nativeTerminalView is null)
            {
                return;
            }

            TerminalTheme theme = CreateNativeTerminalTheme();
            _nativeTerminalView.FontFamilyWhenSettingTheme = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono");
            _nativeTerminalView.FontSizeWhenSettingTheme = NativeTerminalFontSize;
            _nativeTerminalView.Theme = theme;
            _nativeTerminalView.Terminal?.SetTheme(
                theme,
                "Cascadia Mono",
                NativeTerminalFontSize,
                ResolveExternalBackgroundColor());
        }

        private TerminalTheme CreateNativeTerminalTheme()
        {
            bool lightTheme = ResolveEffectiveTheme() == ElementTheme.Light;
            UIColor background = ResolveShellColor(
                "ShellSurfaceBackgroundBrush",
                lightTheme
                    ? new UIColor { A = 255, R = 249, G = 251, B = 253 }
                    : new UIColor { A = 255, R = 16, G = 18, B = 22 });
            UIColor pageBackground = ResolveShellColor(
                "ShellPageBackgroundBrush",
                lightTheme
                    ? new UIColor { A = 255, R = 242, G = 245, B = 248 }
                    : new UIColor { A = 255, R = 12, G = 13, B = 16 });
            UIColor foreground = ResolveShellColor(
                "ShellTextPrimaryBrush",
                lightTheme
                    ? new UIColor { A = 255, R = 26, G = 30, B = 35 }
                    : new UIColor { A = 255, R = 243, G = 244, B = 246 });
            UIColor secondary = ResolveShellColor(
                "ShellTextSecondaryBrush",
                lightTheme
                    ? new UIColor { A = 255, R = 57, G = 66, B = 77 }
                    : new UIColor { A = 255, R = 167, G = 173, B = 183 });
            UIColor tertiary = ResolveShellColor(
                "ShellTextTertiaryBrush",
                lightTheme
                    ? new UIColor { A = 255, R = 85, G = 96, B = 109 }
                    : new UIColor { A = 255, R = 122, G = 128, B = 139 });
            UIColor success = ResolveShellColor("ShellSuccessBrush", lightTheme
                ? new UIColor { A = 255, R = 22, G = 163, B = 74 }
                : new UIColor { A = 255, R = 74, G = 222, B = 128 });
            UIColor warning = ResolveShellColor("ShellWarningBrush", lightTheme
                ? new UIColor { A = 255, R = 202, G = 138, B = 4 }
                : new UIColor { A = 255, R = 251, G = 191, B = 36 });
            UIColor danger = ResolveShellColor("ShellDangerBrush", lightTheme
                ? new UIColor { A = 255, R = 220, G = 38, B = 38 }
                : new UIColor { A = 255, R = 248, G = 113, B = 113 });
            UIColor info = ResolveShellColor("ShellInfoBrush", lightTheme
                ? new UIColor { A = 255, R = 37, G = 99, B = 235 }
                : new UIColor { A = 255, R = 96, G = 165, B = 250 });
            UIColor accent = ResolveShellColor("ShellTerminalBrush", lightTheme
                ? new UIColor { A = 255, R = 15, G = 118, B = 110 }
                : new UIColor { A = 255, R = 45, G = 212, B = 191 });
            UIColor magenta = MixColors(info, danger, lightTheme ? 0.46 : 0.52);
            UIColor lowWhite = lightTheme
                ? ResolveShellColor("ShellMutedSurfaceBrush", new UIColor { A = 255, R = 238, G = 241, B = 244 })
                : MixColors(background, foreground, 0.76);
            UIColor brightWhite = lightTheme
                ? LiftColor(background, true)
                : LiftColor(foreground, false);

            return new TerminalTheme
            {
                DefaultBackground = ToTerminalColor(background),
                DefaultForeground = ToTerminalColor(foreground),
                DefaultSelectionBackground = ToTerminalColor(MixColors(background, accent, lightTheme ? 0.18 : 0.24)),
                CursorStyle = ResolveCursorStyle(),
                ColorTable = new[]
                {
                    ToTerminalColor(lightTheme ? foreground : pageBackground),
                    ToTerminalColor(danger),
                    ToTerminalColor(success),
                    ToTerminalColor(warning),
                    ToTerminalColor(info),
                    ToTerminalColor(magenta),
                    ToTerminalColor(accent),
                    ToTerminalColor(lowWhite),
                    ToTerminalColor(tertiary),
                    ToTerminalColor(LiftColor(danger, lightTheme)),
                    ToTerminalColor(LiftColor(success, lightTheme)),
                    ToTerminalColor(LiftColor(warning, lightTheme)),
                    ToTerminalColor(LiftColor(info, lightTheme)),
                    ToTerminalColor(LiftColor(magenta, lightTheme)),
                    ToTerminalColor(LiftColor(accent, lightTheme)),
                    ToTerminalColor(brightWhite),
                },
            };
        }

        private CursorStyle ResolveCursorStyle()
        {
            return ResolveTerminalShellKind() switch
            {
                "powershell" => CursorStyle.BlinkingBar,
                "wsl" => CursorStyle.BlinkingBar,
                "cmd" => CursorStyle.BlinkingUnderline,
                _ => CursorStyle.BlinkingBar,
            };
        }

        private DrawingColor ResolveExternalBackgroundColor()
        {
            return ToDrawingColor(ResolveShellColor(
                "ShellPageBackgroundBrush",
                ResolveEffectiveTheme() == ElementTheme.Light
                    ? new UIColor { A = 255, R = 242, G = 245, B = 248 }
                    : new UIColor { A = 255, R = 12, G = 13, B = 16 }));
        }

        private void UpdateStartupMask()
        {
            if (StartupMask is not null)
            {
                // The native terminal uses its own HWND, so XAML overlays cannot stay above
                // a live terminal surface. Keep the overlay only for suspended or not-yet-created panes.
                bool allowOverlay = _hostedSurfaceSuppressed
                    || !AutoStartSession
                    || _nativeTerminalView is null
                    || NativeTerminalHost?.Visibility != Visibility.Visible;
                StartupMask.Visibility = allowOverlay ? Visibility.Visible : Visibility.Collapsed;
            }

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
        }

        private string ResolveStartupKicker()
        {
            if (_hostedSurfaceSuppressed)
            {
                return "Dialog open";
            }

            if (!AutoStartSession)
            {
                return "Suspended pane";
            }

            return ResolveTerminalShellKind() switch
            {
                "wsl" => "WSL shell",
                "powershell" => "PowerShell",
                "cmd" => "Command Prompt",
                _ => "Terminal workspace",
            };
        }

        private string ResolveStartupTitle()
        {
            if (_hostedSurfaceSuppressed)
            {
                return string.IsNullOrWhiteSpace(_hostedSurfaceSuppressionTitle)
                    ? "Hosted pane paused"
                    : _hostedSurfaceSuppressionTitle;
            }

            if (!AutoStartSession)
            {
                return "Session suspended";
            }

            return ResolveTerminalShellKind() switch
            {
                "wsl" => "Starting WSL shell",
                "powershell" => "Starting PowerShell",
                "cmd" => "Starting Command Prompt",
                _ => "Starting terminal",
            };
        }

        private string ResolveStartupMeta()
        {
            if (_hostedSurfaceSuppressed)
            {
                return string.IsNullOrWhiteSpace(_hostedSurfaceSuppressionMessage)
                    ? "Finish the dialog to return to the live terminal."
                    : _hostedSurfaceSuppressionMessage;
            }

            if (!AutoStartSession)
            {
                return "This pane is suspended until you explicitly resume it.";
            }

            return ResolveTerminalShellKind() switch
            {
                "wsl" => "Attaching the native renderer and booting the distro.",
                "powershell" => "Preparing PowerShell and the native terminal renderer.",
                "cmd" => "Preparing Command Prompt and the native terminal renderer.",
                _ => "Preparing the terminal host and native rendering path.",
            };
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
            ApplyHostedSurfaceVisibility();
            RequestFit();
            UpdateStartupMask();
        }

        private bool CanReceiveProgrammaticFocus()
        {
            return !_disposed
                && !_sessionExited
                && _nativeTerminalLoaded
                && _rendererReady
                && _hasDisplayOutput
                && _nativeTerminalView?.XamlRoot is not null;
        }

        private void TryFulfillPendingProgrammaticFocus()
        {
            if (!_pendingProgrammaticFocus || !CanReceiveProgrammaticFocus())
            {
                return;
            }

            _pendingProgrammaticFocus = false;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!CanReceiveProgrammaticFocus())
                {
                    _pendingProgrammaticFocus = true;
                    return;
                }

                TryApplyProgrammaticFocus("deferred");
            });
        }

        private void TryApplyProgrammaticFocus(string reason)
        {
            try
            {
                bool focused = Focus(FocusState.Programmatic);
                TraceNativeTerminal($"Programmatic focus {reason} result={focused}");
            }
            catch (Exception ex)
            {
                _pendingProgrammaticFocus = true;
                TraceNativeTerminal($"Programmatic focus {reason} failed: {ex.Message}");
            }
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

        internal static void ClearSharedWebViewEnvironmentCache()
        {
            lock (EnvironmentSync)
            {
                SharedEnvironmentTask = null;
            }
        }

        private void ShowStatus(string text, bool keepVisible)
        {
            if (StatusBadge is null || StatusText is null)
            {
                return;
            }

            StatusText.Text = text ?? string.Empty;
            StatusBadge.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
            UpdateTerminalHeader();
        }

        private void HideStatus()
        {
            if (StatusBadge is not null)
            {
                StatusBadge.Visibility = Visibility.Collapsed;
            }

            UpdateTerminalHeader();
        }

        private void UpdateToolSessionRail()
        {
            if (ToolSessionRail is null)
            {
                return;
            }

            string activeTool = !IsSessionExited() && !string.IsNullOrWhiteSpace(_activeToolSession)
                ? _activeToolSession
                : null;

            if (string.IsNullOrWhiteSpace(activeTool))
            {
                ToolSessionRail.Visibility = Visibility.Collapsed;
                ToolSessionRail.Opacity = 0;
                return;
            }

            ToolSessionRail.Background = new SolidColorBrush(ResolveToolSessionRailColor(activeTool));
            ToolSessionRail.Visibility = Visibility.Visible;
            ToolSessionRail.Opacity = 1;
        }

        private static UIColor ResolveToolSessionRailColor(string tool)
        {
            return string.Equals(tool, "claude", StringComparison.OrdinalIgnoreCase)
                ? new UIColor { A = 255, R = 97, G = 165, B = 250 }
                : new UIColor { A = 255, R = 224, G = 90, B = 90 };
        }

        private void UpdateTerminalHeader()
        {
            if (TerminalHeaderTitleText is null || TerminalHeaderMetaText is null)
            {
                return;
            }

            TerminalHeaderTitleText.Text = string.IsNullOrWhiteSpace(_headerTitleOverride)
                ? ResolveTerminalHeaderTitle()
                : _headerTitleOverride;
            TerminalHeaderMetaText.Text = ResolveTerminalHeaderMeta();
            string headerPath = ResolveHeaderPath();
            ToolTipService.SetToolTip(TerminalHeaderMetaText, string.IsNullOrWhiteSpace(headerPath) ? TerminalHeaderMetaText.Text : headerPath);
        }

        private string ResolveTerminalHeaderTitle()
        {
            if (!string.IsNullOrWhiteSpace(_sessionTitle) &&
                !string.Equals(_sessionTitle, GetInitialTitle(), StringComparison.Ordinal))
            {
                return _sessionTitle;
            }

            return ResolveShellDisplayName(includeTerminalSuffix: true) ?? GetInitialTitle();
        }

        private string ResolveTerminalHeaderMeta()
        {
            if (StatusBadge?.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(StatusText?.Text))
            {
                return StatusText.Text;
            }

            if (!AutoStartSession && !string.IsNullOrWhiteSpace(SuspendedStatusText))
            {
                return SuspendedStatusText;
            }

            if (IsSessionExited())
            {
                return _liveResizeMode
                    ? JoinMetaSegments("Session ended", "resizing")
                    : "Session ended";
            }

            if (!string.IsNullOrWhiteSpace(_activeToolSession))
            {
                return _liveResizeMode
                    ? JoinMetaSegments(ResolveToolSessionLabel(), "resizing")
                    : ResolveToolSessionLabel();
            }

            if (_replayRestorePending)
            {
                return _liveResizeMode
                    ? JoinMetaSegments("Restoring session", "resizing")
                    : "Restoring session";
            }

            string shellLabel = ResolveShellDisplayName(includeTerminalSuffix: false);
            return _liveResizeMode
                ? JoinMetaSegments(shellLabel, "resizing")
                : shellLabel ?? "Interactive shell";
        }

        private string ResolveHeaderPath()
        {
            if (!string.IsNullOrWhiteSpace(DisplayWorkingDirectory))
            {
                return DisplayWorkingDirectory;
            }

            return !string.IsNullOrWhiteSpace(InitialWorkingDirectory)
                ? InitialWorkingDirectory
                : null;
        }

        private string ResolveShellDisplayName(bool includeTerminalSuffix)
        {
            string shellName = ResolveTerminalShellKind() switch
            {
                "wsl" => "WSL",
                "powershell" => "PowerShell",
                "cmd" => "Command Prompt",
                _ => "Interactive shell",
            };

            if (!includeTerminalSuffix || string.IsNullOrWhiteSpace(shellName) || shellName == "Interactive shell")
            {
                return shellName;
            }

            return $"{shellName} terminal";
        }

        private string ResolveToolSessionLabel()
        {
            return _activeToolSession switch
            {
                "codex" => "Codex session",
                "claude" => "Claude session",
                _ => "Agent session",
            };
        }

        private static string JoinMetaSegments(params string[] values)
        {
            return string.Join(
                " · ",
                values.Where(value => !string.IsNullOrWhiteSpace(value)));
        }

        private void ConfigureProcessFactory(string workingDirectory)
        {
            DetachProcessFactory();
            _processFactory = new WinMuxTerminalProcessFactory(workingDirectory, LaunchEnvironment);
            _processFactory.ProcessStarted += OnProcessStarted;
            TrySetInternalProcessFactory(_processFactory);
        }

        private void DetachProcessFactory()
        {
            if (_processFactory is null)
            {
                TrySetInternalProcessFactory(null);
                return;
            }

            TrySetInternalProcessFactory(null);
            _processFactory.ProcessStarted -= OnProcessStarted;
            _processFactory = null;
        }

        private bool TrySetInternalProcessFactory(object value)
        {
            if (_nativeTerminalView is null)
            {
                return false;
            }

            PropertyInfo property = _nativeTerminalView.GetType().GetProperty("InternalProcessFactory", InstancePropertyFlags);
            if (property is null || !property.CanWrite)
            {
                TraceNativeTerminal("InternalProcessFactory property unavailable on native terminal control");
                return false;
            }

            if (value is not null && !property.PropertyType.IsInstanceOfType(value))
            {
                TraceNativeTerminal($"InternalProcessFactory rejected {value.GetType().FullName}");
                return false;
            }

            property.SetValue(_nativeTerminalView, value);
            TraceNativeTerminal(value is null
                ? "InternalProcessFactory cleared"
                : $"InternalProcessFactory set to {value.GetType().FullName}");
            return true;
        }

        private bool TryAdoptProcessFromConnection(TermPTY connection)
        {
            if (connection is null)
            {
                return false;
            }

            try
            {
                PropertyInfo processInfoProperty = connection.GetType().GetProperty("ProcessInfo", InstancePropertyFlags);
                object processInfo = processInfoProperty?.GetValue(connection);
                Process process = processInfo?.GetType().GetProperty("Process", InstancePropertyFlags)?.GetValue(processInfo) as Process;
                process ??= connection.GetType().GetProperty("Process", InstancePropertyFlags)?.GetValue(connection) as Process;
                if (process is null)
                {
                    return false;
                }

                OnProcessStarted(process);
                return true;
            }
            catch (Exception ex)
            {
                TraceNativeTerminal($"Failed to adopt process from connection: {ex.Message}");
                return false;
            }
        }

        private string ResolveTerminalShellKind()
        {
            if (string.IsNullOrWhiteSpace(ShellCommand))
            {
                return "terminal";
            }

            string command = ShellCommand;
            if (command.Contains("wsl.exe", StringComparison.OrdinalIgnoreCase))
            {
                return "wsl";
            }

            if (command.Contains("powershell", StringComparison.OrdinalIgnoreCase))
            {
                return "powershell";
            }

            if (command.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase))
            {
                return "cmd";
            }

            return "terminal";
        }

        private void UpdateTerminalMetrics(int cols, int rows)
        {
            if (cols > 0)
            {
                _cols = cols;
            }

            if (rows > 0)
            {
                _rows = rows;
            }
        }

        private void EnsureNativeTerminalViewCreated()
        {
            if (_nativeTerminalView is not null)
            {
                return;
            }

            _nativeTerminalView = new NativeTerminalViewControl
            {
            };
            ApplyTerminalAutomationIsolation();
            _nativeTerminalView.Loaded += OnNativeTerminalLoaded;
            _nativeTerminalView.GotFocus += OnNativeTerminalInteractionGotFocus;
            _nativeTerminalView.PointerPressed += OnNativeTerminalInteractionPointerPressed;
            _nativeTerminalView.Terminal.InteractionOccurred += OnNativeTerminalInteractionOccurred;
            AttachNativeTerminalInteractionProxy();
            NativeTerminalHost.Children.Add(_nativeTerminalView);
            ApplyHostedSurfaceVisibility();
            TraceNativeTerminal("NativeTerminalView created");
            DispatcherQueue.TryEnqueue(() =>
            {
                AttachNativeTerminalInteractionProxy();
                if (_nativeTerminalView?.XamlRoot is not null)
                {
                    TraceNativeTerminal("NativeTerminalView fallback-ready via XamlRoot");
                    MarkNativeTerminalReady();
                }
            });
        }

        private void AttachNativeTerminalInteractionProxy()
        {
            if (_nativeTerminalFocusProxy is not null || _nativeTerminalView is null)
            {
                return;
            }

            if (_nativeTerminalView.FindName("termContainer") is not UIElement termContainer)
            {
                return;
            }

            _nativeTerminalFocusProxy = termContainer;
            _nativeTerminalFocusProxy.GettingFocus += OnNativeTerminalInteractionGettingFocus;
            _nativeTerminalFocusProxy.GotFocus += OnNativeTerminalInteractionGotFocus;
            _nativeTerminalFocusProxy.AddHandler(
                UIElement.PointerPressedEvent,
                new PointerEventHandler(OnNativeTerminalInteractionPointerPressed),
                handledEventsToo: true);

            AttachNativeTerminalWindowMessageRelay("WindowMessageReceived", ref _nativeTerminalWindowMessageHandler);
            AttachNativeTerminalWindowMessageRelay("MessageHook", ref _nativeTerminalMessageHookHandler);
        }

        private void OnNativeTerminalInteractionOccurred(object sender, EventArgs e)
        {
            RaiseInteractionRequested();
        }

        private void AttachNativeTerminalWindowMessageRelay(string eventName, ref Delegate eventHandler)
        {
            if (_nativeTerminalFocusProxy is null || eventHandler is not null)
            {
                return;
            }

            EventInfo messageEvent = _nativeTerminalFocusProxy.GetType().GetEvent(
                eventName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (messageEvent is null)
            {
                return;
            }

            try
            {
                eventHandler = Delegate.CreateDelegate(
                    messageEvent.EventHandlerType,
                    this,
                    nameof(OnNativeTerminalInteractionMessageHook));
                messageEvent.AddEventHandler(_nativeTerminalFocusProxy, eventHandler);
            }
            catch
            {
                eventHandler = null;
            }
        }

        private void ApplyTerminalAutomationIsolation()
        {
            if (NativeTerminalHost is not null)
            {
                AutomationProperties.SetAccessibilityView(NativeTerminalHost, AccessibilityView.Raw);
            }

            if (_nativeTerminalView is not null)
            {
                AutomationProperties.SetAccessibilityView(_nativeTerminalView, AccessibilityView.Raw);
            }

            if (_nativeTerminalView?.Terminal is UIElement terminalElement)
            {
                AutomationProperties.SetAccessibilityView(terminalElement, AccessibilityView.Raw);
            }
        }

        private static void TraceNativeTerminal(string message)
        {
            try
            {
                lock (DiagnosticLogSync)
                {
                    File.AppendAllText(
                        DiagnosticLogPath,
                        $"{DateTime.Now:O} {message}{Environment.NewLine}");
                }
            }
            catch
            {
            }
        }

        private static string[] SplitLines(string text)
        {
            string normalized = (text ?? string.Empty)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');
            return normalized.Split('\n', StringSplitOptions.None);
        }

        private static string BuildVisibleText(string[] lines, int rows)
        {
            if (lines.Length == 0)
            {
                return string.Empty;
            }

            int safeRows = Math.Max(1, rows);
            int start = Math.Max(0, lines.Length - safeRows);
            return string.Join("\n", lines[start..]);
        }

        private static string BuildBufferTail(string[] lines, int maxLines)
        {
            if (lines.Length == 0)
            {
                return string.Empty;
            }

            int start = Math.Max(0, lines.Length - Math.Max(1, maxLines));
            return string.Join("\n", lines[start..]);
        }
    }
}
