// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Microsoft.UI.Dispatching;
using SelfContainedDeployment.Automation;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;
using static Windows.Win32.PInvoke;

namespace SelfContainedDeployment
{
    public partial class MainWindow : Window
    {
        private readonly NativeWindowRecorder _windowRecorder;
        private readonly AppWindow _appWindow;
        private readonly DispatcherQueueTimer _automationHeartbeatTimer;
        private readonly Timer _automationWatchdogTimer;
        private readonly object _automationWatchdogSync = new();
        private string _lastWatchdogCorrelationId;
        private DateTimeOffset _lastWatchdogCapturedAt = DateTimeOffset.MinValue;
        private NativeAutomationState _lastKnownAutomationState;
        private static readonly JsonSerializerOptions AutomationJsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };

        public MainWindow()
        {
            this.InitializeComponent();
            _windowRecorder = new NativeWindowRecorder(CaptureAutomationBitmapInternal);

            Title = SampleConfig.FeatureName;

            HWND hwnd = (HWND)WinRT.Interop.WindowNative.GetWindowHandle(this);
            _appWindow = ResolveAppWindow(hwnd);
            Activated += OnWindowActivated;
            LoadIcon(hwnd, "Assets/winmux.ico");
            ApplyChromeTheme(SampleConfig.CurrentTheme);
            SetWindowSize(hwnd, 1240, 860);
            PlacementCenterWindowInMonitorWin32(hwnd);
            _automationHeartbeatTimer = DispatcherQueue.CreateTimer();
            _automationHeartbeatTimer.IsRepeating = true;
            _automationHeartbeatTimer.Interval = TimeSpan.FromMilliseconds(250);
            _automationHeartbeatTimer.Tick += OnAutomationHeartbeatTick;
            _automationHeartbeatTimer.Start();
            OnAutomationHeartbeatTick(this, null);
            _automationWatchdogTimer = new Timer(OnAutomationWatchdogTick, null, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(500));
            Closed += OnWindowClosed;
        }

        internal void ApplyChromeTheme(ElementTheme theme)
        {
            if (!AppWindowTitleBar.IsCustomizationSupported() || _appWindow?.TitleBar is not AppWindowTitleBar titleBar)
            {
                return;
            }

            ElementTheme resolvedTheme = ResolveChromeTheme(theme);
            Windows.UI.Color background = resolvedTheme == ElementTheme.Light
                ? Windows.UI.Color.FromArgb(0xFF, 0xF5, 0xF6, 0xF8)
                : Windows.UI.Color.FromArgb(0xFF, 0x0C, 0x0D, 0x10);
            Windows.UI.Color hoverBackground = resolvedTheme == ElementTheme.Light
                ? Windows.UI.Color.FromArgb(0xFF, 0xF0, 0xF3, 0xF6)
                : Windows.UI.Color.FromArgb(0xFF, 0x13, 0x16, 0x1B);
            Windows.UI.Color pressedBackground = resolvedTheme == ElementTheme.Light
                ? Windows.UI.Color.FromArgb(0xFF, 0xE7, 0xEB, 0xF0)
                : Windows.UI.Color.FromArgb(0xFF, 0x17, 0x1A, 0x20);
            Windows.UI.Color foreground = resolvedTheme == ElementTheme.Light
                ? Windows.UI.Color.FromArgb(0xFF, 0x1B, 0x1F, 0x24)
                : Windows.UI.Color.FromArgb(0xFF, 0xF3, 0xF4, 0xF6);
            Windows.UI.Color inactiveForeground = resolvedTheme == ElementTheme.Light
                ? Windows.UI.Color.FromArgb(0xFF, 0x7E, 0x86, 0x91)
                : Windows.UI.Color.FromArgb(0xFF, 0x7A, 0x80, 0x8B);

            titleBar.BackgroundColor = background;
            titleBar.ForegroundColor = foreground;
            titleBar.InactiveBackgroundColor = background;
            titleBar.InactiveForegroundColor = inactiveForeground;
            titleBar.ButtonBackgroundColor = background;
            titleBar.ButtonForegroundColor = foreground;
            titleBar.ButtonHoverBackgroundColor = hoverBackground;
            titleBar.ButtonHoverForegroundColor = foreground;
            titleBar.ButtonPressedBackgroundColor = pressedBackground;
            titleBar.ButtonPressedForegroundColor = foreground;
            titleBar.ButtonInactiveBackgroundColor = background;
            titleBar.ButtonInactiveForegroundColor = inactiveForeground;
        }

        private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
        {
            ApplyChromeTheme(SampleConfig.CurrentTheme);
        }

        private ElementTheme ResolveChromeTheme(ElementTheme requestedTheme)
        {
            if (requestedTheme == ElementTheme.Light || requestedTheme == ElementTheme.Dark)
            {
                return requestedTheme;
            }

            if (Content is FrameworkElement root && root.ActualTheme == ElementTheme.Light)
            {
                return ElementTheme.Light;
            }

            return ElementTheme.Dark;
        }

        private static AppWindow ResolveAppWindow(HWND hwnd)
        {
            return AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));
        }

        internal MainPage MainPage => Content as MainPage;

        internal void PersistSessionState()
        {
            MainPage?.PersistSessionState();
        }

        public NativeAutomationState GetAutomationState()
        {
            return MainPage?.GetAutomationState() ?? new NativeAutomationState
            {
                WindowTitle = Title,
                ActiveView = "unavailable",
            };
        }

        public NativeAutomationActionResponse PerformAutomationAction(NativeAutomationActionRequest request)
        {
            return MainPage?.PerformAutomationAction(request) ?? new NativeAutomationActionResponse
            {
                Ok = false,
                Message = "Main page is not available.",
            };
        }

        public NativeAutomationUiTreeResponse GetAutomationUiTree()
        {
            return MainPage?.GetAutomationUiTree() ?? new NativeAutomationUiTreeResponse
            {
                WindowTitle = Title,
                ActiveView = "unavailable",
            };
        }

        public NativeAutomationUiActionResponse PerformAutomationUiAction(NativeAutomationUiActionRequest request)
        {
            return MainPage?.PerformAutomationUiAction(request) ?? new NativeAutomationUiActionResponse
            {
                Ok = false,
                Message = "Main page is not available.",
            };
        }

        public NativeAutomationPerfSnapshot GetAutomationPerfSnapshot()
        {
            return NativeAutomationDiagnostics.CaptureSnapshot();
        }

        public NativeAutomationDoctorResponse GetAutomationDoctorSnapshot(string automationLogPath, bool preferLiveState = true)
        {
            NativeAutomationPerfSnapshot perfSnapshot = GetAutomationPerfSnapshot();
            NativeAutomationState state = preferLiveState ? GetAutomationState() : GetCachedAutomationState();
            return new NativeAutomationDoctorResponse
            {
                Ok = true,
                Timestamp = DateTimeOffset.UtcNow.ToString("O"),
                ProcessId = Environment.ProcessId,
                WindowTitle = Title,
                AutomationLogPath = automationLogPath,
                AutomationLogTail = NativeAutomationDiagnostics.ReadTail(automationLogPath),
                StartupErrorLogPath = NativeAutomationDiagnostics.GetStartupErrorLogPath(),
                StartupErrorLogTail = NativeAutomationDiagnostics.GetStartupErrorLogTail(),
                LastUnhandledExceptionMessage = NativeAutomationDiagnostics.GetLastUnhandledExceptionMessage(),
                LastUnhandledExceptionDetails = NativeAutomationDiagnostics.GetLastUnhandledExceptionDetails(),
                UiResponsive = perfSnapshot.UiResponsive,
                State = state,
                Perf = perfSnapshot,
                Events = NativeAutomationEventLog.Snapshot(),
                RecordingStatus = GetRecordingStatus(),
                LastHangDump = NativeAutomationDiagnostics.GetLastHangDump(),
            };
        }

        public Task<NativeAutomationTerminalStateResponse> GetTerminalStateAsync(NativeAutomationTerminalStateRequest request)
        {
            return MainPage?.GetTerminalStateAsync(request) ?? Task.FromResult(new NativeAutomationTerminalStateResponse());
        }

        public NativeAutomationBrowserStateResponse GetBrowserState(NativeAutomationBrowserStateRequest request)
        {
            return MainPage?.GetBrowserState(request) ?? new NativeAutomationBrowserStateResponse();
        }

        public NativeAutomationDiffStateResponse GetDiffState(NativeAutomationDiffStateRequest request)
        {
            return MainPage?.GetDiffState(request) ?? new NativeAutomationDiffStateResponse();
        }

        public NativeAutomationEditorStateResponse GetEditorState(NativeAutomationEditorStateRequest request)
        {
            return MainPage?.GetEditorState(request) ?? new NativeAutomationEditorStateResponse();
        }

        public Task<NativeAutomationBrowserEvalResponse> EvaluateBrowserAsync(NativeAutomationBrowserEvalRequest request)
        {
            return MainPage?.EvaluateBrowserAsync(request) ?? Task.FromResult(new NativeAutomationBrowserEvalResponse
            {
                Ok = false,
                Message = "Main page is not available.",
            });
        }

        public Task<NativeAutomationBrowserScreenshotResponse> CaptureBrowserScreenshotAsync(NativeAutomationBrowserScreenshotRequest request)
        {
            return MainPage?.CaptureBrowserScreenshotAsync(request) ?? Task.FromResult(new NativeAutomationBrowserScreenshotResponse
            {
                Ok = false,
                Message = "Main page is not available.",
            });
        }

        public NativeAutomationDesktopWindowsResponse GetDesktopWindows()
        {
            return NativeDesktopAutomation.GetWindows();
        }

        public NativeAutomationDesktopActionResponse PerformDesktopAction(NativeAutomationDesktopActionRequest request)
        {
            return NativeDesktopAutomation.PerformAction(request);
        }

        public NativeAutomationRecordingStatusResponse StartRecording(NativeAutomationRecordingRequest request)
        {
            return _windowRecorder.Start(request);
        }

        public NativeAutomationRecordingStatusResponse GetRecordingStatus()
        {
            return _windowRecorder.GetStatus();
        }

        public Task<NativeAutomationRecordingStopResponse> StopRecordingAsync()
        {
            return _windowRecorder.StopAsync();
        }

        public async Task<NativeAutomationRenderTraceResponse> CaptureRenderTraceAsync(NativeAutomationRenderTraceRequest request)
        {
            request ??= new NativeAutomationRenderTraceRequest();
            int frameCount = Math.Clamp(request.Frames <= 0 ? 8 : request.Frames, 1, 30);
            List<NativeAutomationRenderFrame> frames = new();
            NativeAutomationState previousState = null;
            List<NativeAutomationUiNode> previousInteractiveNodes = null;

            try
            {
                if (request.Action is not null)
                {
                    PerformAutomationAction(request.Action);
                }

                if (request.UiAction is not null)
                {
                    PerformAutomationUiAction(request.UiAction);
                }

                string traceDirectory = request.CaptureScreenshots
                    ? Path.Combine(Path.GetTempPath(), $"native-render-trace-{Environment.ProcessId}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}")
                    : null;

                if (!string.IsNullOrWhiteSpace(traceDirectory))
                {
                    Directory.CreateDirectory(traceDirectory);
                }

                for (int index = 0; index < frameCount; index++)
                {
                    await WaitForNextRenderAsync();

                    string screenshotPath = null;
                    if (request.CaptureScreenshots)
                    {
                        bool annotated = request.Annotated;
                        if (annotated)
                        {
                            MainPage?.ShowAutomationOverlay();
                            await WaitForNextRenderAsync();
                        }

                        try
                        {
                            screenshotPath = Path.Combine(traceDirectory, $"frame-{index:00}.png");
                            CaptureAutomationScreenshotInternal(screenshotPath);
                        }
                        finally
                        {
                            if (annotated)
                            {
                                MainPage?.HideAutomationOverlay();
                                await WaitForNextRenderAsync();
                            }
                        }
                    }

                    NativeAutomationUiTreeResponse tree = GetAutomationUiTree();
                    NativeAutomationState currentState = GetAutomationState();
                    List<NativeAutomationUiNode> interactiveNodes = tree?.InteractiveNodes ?? new System.Collections.Generic.List<NativeAutomationUiNode>();
                    frames.Add(new NativeAutomationRenderFrame
                    {
                        Index = index,
                        Timestamp = DateTimeOffset.UtcNow.ToString("O"),
                        ScreenshotPath = screenshotPath,
                        State = currentState,
                        StateChanges = ComputeStateChanges(previousState, currentState),
                        InteractiveChanges = ComputeInteractiveChanges(previousInteractiveNodes, interactiveNodes),
                        InteractiveNodes = interactiveNodes,
                    });

                    previousState = currentState;
                    previousInteractiveNodes = interactiveNodes;
                }

                NativeAutomationEventLog.Record("render", "trace.captured", $"Captured {frameCount} render frame(s)", new System.Collections.Generic.Dictionary<string, string>
                {
                    ["frameCount"] = frameCount.ToString(),
                    ["screenshots"] = request.CaptureScreenshots.ToString(),
                    ["annotated"] = request.Annotated.ToString(),
                });

                return new NativeAutomationRenderTraceResponse
                {
                    Ok = true,
                    State = GetAutomationState(),
                    Frames = frames,
                };
            }
            catch (Exception ex)
            {
                return new NativeAutomationRenderTraceResponse
                {
                    Ok = false,
                    Message = ex.Message,
                    State = GetAutomationState(),
                    Frames = frames,
                };
            }
        }

        public async Task<NativeAutomationScreenshotResponse> CaptureAutomationScreenshotAsync(NativeAutomationScreenshotRequest request)
        {
            bool annotated = request?.Annotated == true;
            if (annotated)
            {
                MainPage?.ShowAutomationOverlay();
                await WaitForNextRenderAsync();
            }

            try
            {
                return CaptureAutomationScreenshotInternal(request?.Path);
            }
            finally
            {
                if (annotated)
                {
                    MainPage?.HideAutomationOverlay();
                    await WaitForNextRenderAsync();
                }
            }
        }

        private NativeAutomationScreenshotResponse CaptureAutomationScreenshotInternal(string outputPath)
        {
            string finalPath = string.IsNullOrWhiteSpace(outputPath)
                ? Path.Combine(Path.GetTempPath(), $"native-terminal-{Environment.ProcessId}.png")
                : outputPath;

            string targetDirectory = Path.GetDirectoryName(finalPath);
            if (!string.IsNullOrWhiteSpace(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            (Bitmap bitmap, int width, int height) = CaptureAutomationBitmapInternal();
            using var ownedBitmap = bitmap;
            bitmap.Save(finalPath, ImageFormat.Png);

            NativeAutomationEventLog.Record("automation", "screenshot.captured", "Captured native window screenshot", new System.Collections.Generic.Dictionary<string, string>
            {
                ["path"] = finalPath,
                ["width"] = width.ToString(),
                ["height"] = height.ToString(),
            });

            return new NativeAutomationScreenshotResponse
            {
                Ok = true,
                Path = finalPath,
                Width = width,
                Height = height,
            };
        }

        private (Bitmap Bitmap, int Width, int Height) CaptureAutomationBitmapInternal()
        {
            HWND hwnd = (HWND)WinRT.Interop.WindowNative.GetWindowHandle(this);
            GetWindowRect(hwnd, out RECT rect);

            int width = rect.right - rect.left;
            int height = rect.bottom - rect.top;

            Bitmap bitmap = new(width, height);
            using Graphics graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(rect.left, rect.top, 0, 0, bitmap.Size);
            return (bitmap, width, height);
        }

        private static Task WaitForNextRenderAsync()
        {
            var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

            object handler = null;
            handler = new EventHandler<object>((_, _) =>
            {
                CompositionTarget.Rendering -= (EventHandler<object>)handler;
                tcs.TrySetResult(null);
            });

            CompositionTarget.Rendering += (EventHandler<object>)handler;
            return tcs.Task;
        }

        private void OnWindowClosed(object sender, WindowEventArgs args)
        {
            Activated -= OnWindowActivated;
            Closed -= OnWindowClosed;
            _automationHeartbeatTimer.Stop();
            _automationHeartbeatTimer.Tick -= OnAutomationHeartbeatTick;
            if (_windowRecorder.GetStatus().Recording)
            {
                _ = _windowRecorder.StopAsync();
            }

            MainPage?.ReleaseLifetimeResources();
            _automationWatchdogTimer?.Dispose();
        }

        private void OnAutomationHeartbeatTick(object sender, object args)
        {
            NativeAutomationDiagnostics.MarkUiHeartbeat();
            _lastKnownAutomationState = MainPage?.GetAutomationState() ?? _lastKnownAutomationState;
        }

        private void OnAutomationWatchdogTick(object state)
        {
            NativeAutomationPerfSnapshot perfSnapshot = GetAutomationPerfSnapshot();
            if (perfSnapshot?.UiResponsive != false ||
                string.IsNullOrWhiteSpace(perfSnapshot.ActiveCorrelationId) ||
                string.IsNullOrWhiteSpace(perfSnapshot.ActiveAction))
            {
                return;
            }

            lock (_automationWatchdogSync)
            {
                bool duplicateCorrelation = string.Equals(
                    _lastWatchdogCorrelationId,
                    perfSnapshot.ActiveCorrelationId,
                    StringComparison.Ordinal);
                bool withinCooldown = DateTimeOffset.UtcNow - _lastWatchdogCapturedAt < TimeSpan.FromSeconds(10);
                if (duplicateCorrelation && withinCooldown)
                {
                    return;
                }

                string dumpDirectory = Path.Combine(
                    Path.GetTempPath(),
                    "winmux-hang-dumps",
                    $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{perfSnapshot.ActiveCorrelationId}");
                Directory.CreateDirectory(dumpDirectory);

                string screenshotPath = Path.Combine(dumpDirectory, "hang.png");
                string eventsPath = Path.Combine(dumpDirectory, "events.json");
                string message = $"UI watchdog captured a dump for '{perfSnapshot.ActiveAction}' after heartbeat stalled.";

                CaptureAutomationScreenshotInternal(screenshotPath);
                File.WriteAllText(eventsPath, JsonSerializer.Serialize(NativeAutomationEventLog.Snapshot(), AutomationJsonOptions));

                NativeAutomationDiagnostics.RecordHangDump(
                    perfSnapshot.ActiveCorrelationId,
                    perfSnapshot.ActiveAction,
                    screenshotPath,
                    eventsPath,
                    message);
                NativeAutomationEventLog.Record("watchdog", "hang.dump", message, new Dictionary<string, string>
                {
                    ["correlationId"] = perfSnapshot.ActiveCorrelationId,
                    ["action"] = perfSnapshot.ActiveAction,
                    ["screenshotPath"] = screenshotPath,
                    ["eventsPath"] = eventsPath,
                });

                _lastWatchdogCorrelationId = perfSnapshot.ActiveCorrelationId;
                _lastWatchdogCapturedAt = DateTimeOffset.UtcNow;
            }
        }

        private NativeAutomationState GetCachedAutomationState()
        {
            return _lastKnownAutomationState ?? new NativeAutomationState
            {
                WindowTitle = Title,
                ActiveView = "unknown",
            };
        }

        private static List<string> ComputeStateChanges(NativeAutomationState previousState, NativeAutomationState currentState)
        {
            if (previousState is null || currentState is null)
            {
                return new List<string>();
            }

            JsonElement previous = JsonSerializer.SerializeToElement(previousState, AutomationJsonOptions);
            JsonElement current = JsonSerializer.SerializeToElement(currentState, AutomationJsonOptions);
            List<string> changes = new();
            CollectJsonChanges("$", previous, current, changes, 48);
            return changes;
        }

        private static List<string> ComputeInteractiveChanges(
            IReadOnlyList<NativeAutomationUiNode> previousNodes,
            IReadOnlyList<NativeAutomationUiNode> currentNodes)
        {
            if (previousNodes is null || currentNodes is null)
            {
                return new List<string>();
            }

            Dictionary<string, string> previous = BuildInteractiveSignatureMap(previousNodes);
            Dictionary<string, string> current = BuildInteractiveSignatureMap(currentNodes);
            HashSet<string> keys = new(previous.Keys, StringComparer.Ordinal);
            keys.UnionWith(current.Keys);

            List<string> changes = new();
            foreach (string key in keys)
            {
                previous.TryGetValue(key, out string before);
                current.TryGetValue(key, out string after);
                if (string.Equals(before, after, StringComparison.Ordinal))
                {
                    continue;
                }

                changes.Add(key);
                if (changes.Count >= 48)
                {
                    break;
                }
            }

            return changes;
        }

        private static Dictionary<string, string> BuildInteractiveSignatureMap(IReadOnlyList<NativeAutomationUiNode> nodes)
        {
            Dictionary<string, string> map = new(StringComparer.Ordinal);
            foreach (NativeAutomationUiNode node in nodes)
            {
                string key = node.RefLabel ?? node.ElementId ?? $"{node.ControlType}:{node.Name}:{node.Text}";
                string signature = string.Join("|", new[]
                {
                    node.ControlType ?? string.Empty,
                    node.Name ?? string.Empty,
                    node.Text ?? string.Empty,
                    node.Visible.ToString(),
                    node.Enabled.ToString(),
                    node.Focused.ToString(),
                    node.Selected.ToString(),
                    node.X.ToString("F0"),
                    node.Y.ToString("F0"),
                    node.Width.ToString("F0"),
                    node.Height.ToString("F0"),
                });
                map[key] = signature;
            }

            return map;
        }

        private static void CollectJsonChanges(
            string path,
            JsonElement previous,
            JsonElement current,
            List<string> changes,
            int maxChanges)
        {
            if (changes.Count >= maxChanges)
            {
                return;
            }

            if (previous.ValueKind != current.ValueKind)
            {
                changes.Add(path);
                return;
            }

            switch (previous.ValueKind)
            {
                case JsonValueKind.Object:
                    Dictionary<string, JsonElement> previousProperties = new(StringComparer.Ordinal);
                    foreach (JsonProperty property in previous.EnumerateObject())
                    {
                        previousProperties[property.Name] = property.Value;
                    }

                    Dictionary<string, JsonElement> currentProperties = new(StringComparer.Ordinal);
                    foreach (JsonProperty property in current.EnumerateObject())
                    {
                        currentProperties[property.Name] = property.Value;
                    }

                    HashSet<string> propertyNames = new(previousProperties.Keys, StringComparer.Ordinal);
                    propertyNames.UnionWith(currentProperties.Keys);
                    foreach (string propertyName in propertyNames)
                    {
                        bool hasPrevious = previousProperties.TryGetValue(propertyName, out JsonElement previousValue);
                        bool hasCurrent = currentProperties.TryGetValue(propertyName, out JsonElement currentValue);
                        string nextPath = $"{path}.{propertyName}";
                        if (!hasPrevious || !hasCurrent)
                        {
                            changes.Add(nextPath);
                            if (changes.Count >= maxChanges)
                            {
                                return;
                            }

                            continue;
                        }

                        CollectJsonChanges(nextPath, previousValue, currentValue, changes, maxChanges);
                        if (changes.Count >= maxChanges)
                        {
                            return;
                        }
                    }

                    break;

                case JsonValueKind.Array:
                    int previousLength = previous.GetArrayLength();
                    int currentLength = current.GetArrayLength();
                    if (previousLength != currentLength)
                    {
                        changes.Add(path);
                        return;
                    }

                    for (int index = 0; index < previousLength; index++)
                    {
                        CollectJsonChanges($"{path}[{index}]", previous[index], current[index], changes, maxChanges);
                        if (changes.Count >= maxChanges)
                        {
                            return;
                        }
                    }

                    break;

                default:
                    if (!string.Equals(previous.GetRawText(), current.GetRawText(), StringComparison.Ordinal))
                    {
                        changes.Add(path);
                    }

                    break;
            }
        }

        private unsafe void LoadIcon(HWND hwnd, string iconName)
        {
            const int ICON_SMALL = 0;
            const int ICON_BIG = 1;

            fixed (char* nameLocal = iconName)
            {
                HANDLE smallIcon = LoadImage(default,
                    nameLocal,
                    GDI_IMAGE_TYPE.IMAGE_ICON,
                    GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSMICON),
                    GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYSMICON),
                    IMAGE_FLAGS.LR_LOADFROMFILE | IMAGE_FLAGS.LR_SHARED);
                SendMessage(hwnd, WM_SETICON, ICON_SMALL, smallIcon.Value);
            }

            fixed (char* nameLocal = iconName)
            {
                HANDLE bigIcon = LoadImage(default,
                    nameLocal,
                    GDI_IMAGE_TYPE.IMAGE_ICON,
                    GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSMICON),
                    GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYSMICON),
                    IMAGE_FLAGS.LR_LOADFROMFILE | IMAGE_FLAGS.LR_SHARED);
                SendMessage(hwnd, WM_SETICON, ICON_BIG, bigIcon.Value);
            }
        }

        private void SetWindowSize(HWND hwnd, int width, int height)
        {
            // Win32 uses pixels and WinUI 3 uses effective pixels, so you should apply the DPI scale factor
            uint dpi = GetDpiForWindow(hwnd);
            float scalingFactor = (float)dpi / 96;
            width = (int)(width * scalingFactor);
            height = (int)(height * scalingFactor);

            SetWindowPos(hwnd, default, 0, 0, width, height, SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER);
        }

        private void PlacementCenterWindowInMonitorWin32(HWND hwnd)
        {
            RECT windowMonitorRectToAdjust;
            GetWindowRect(hwnd, out windowMonitorRectToAdjust);
            ClipOrCenterRectToMonitorWin32(ref windowMonitorRectToAdjust);
            SetWindowPos(hwnd, default, windowMonitorRectToAdjust.left,
                windowMonitorRectToAdjust.top, 0, 0,
                SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
                SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
        }

        private void ClipOrCenterRectToMonitorWin32(ref RECT adjustedWindowRect)
        {
            MONITORINFO mi = new MONITORINFO();
            mi.cbSize = (uint)Marshal.SizeOf<MONITORINFO>();
            GetMonitorInfo(MonitorFromRect(adjustedWindowRect, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST), ref mi);

            RECT rcWork = mi.rcWork;
            int w = adjustedWindowRect.right - adjustedWindowRect.left;
            int h = adjustedWindowRect.bottom - adjustedWindowRect.top;

            adjustedWindowRect.left = rcWork.left + (rcWork.right - rcWork.left - w) / 2;
            adjustedWindowRect.top = rcWork.top + (rcWork.bottom - rcWork.top - h) / 2;
            adjustedWindowRect.right = adjustedWindowRect.left + w;
            adjustedWindowRect.bottom = adjustedWindowRect.top + h;
        }
    }
}
