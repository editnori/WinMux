// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using SelfContainedDeployment.Automation;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
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
        }

        internal void ApplyChromeTheme(ElementTheme theme)
        {
            if (!AppWindowTitleBar.IsCustomizationSupported() || _appWindow?.TitleBar is not AppWindowTitleBar titleBar)
            {
                return;
            }

            ElementTheme resolvedTheme = ResolveChromeTheme(theme);
            Windows.UI.Color background = resolvedTheme == ElementTheme.Light
                ? Windows.UI.Color.FromArgb(0xFF, 0xFA, 0xFA, 0xFA)
                : Windows.UI.Color.FromArgb(0xFF, 0x09, 0x09, 0x0B);
            Windows.UI.Color hoverBackground = resolvedTheme == ElementTheme.Light
                ? Windows.UI.Color.FromArgb(0xFF, 0xF1, 0xF1, 0xF3)
                : Windows.UI.Color.FromArgb(0xFF, 0x17, 0x19, 0x1E);
            Windows.UI.Color pressedBackground = resolvedTheme == ElementTheme.Light
                ? Windows.UI.Color.FromArgb(0xFF, 0xEC, 0xEC, 0xF0)
                : Windows.UI.Color.FromArgb(0xFF, 0x1F, 0x22, 0x28);
            Windows.UI.Color foreground = resolvedTheme == ElementTheme.Light
                ? Windows.UI.Color.FromArgb(0xFF, 0x18, 0x18, 0x1B)
                : Windows.UI.Color.FromArgb(0xFF, 0xFA, 0xFA, 0xFA);
            Windows.UI.Color inactiveForeground = resolvedTheme == ElementTheme.Light
                ? Windows.UI.Color.FromArgb(0xFF, 0x71, 0x71, 0x7A)
                : Windows.UI.Color.FromArgb(0xFF, 0xA1, 0xA1, 0xAA);

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
                    frames.Add(new NativeAutomationRenderFrame
                    {
                        Index = index,
                        Timestamp = DateTimeOffset.UtcNow.ToString("O"),
                        ScreenshotPath = screenshotPath,
                        State = GetAutomationState(),
                        InteractiveNodes = tree?.InteractiveNodes ?? new System.Collections.Generic.List<NativeAutomationUiNode>(),
                    });
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
