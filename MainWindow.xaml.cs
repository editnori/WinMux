// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using SelfContainedDeployment.Automation;
using System;
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
        public MainWindow()
        {
            this.InitializeComponent();

            Title = SampleConfig.FeatureName;

            HWND hwnd = (HWND)WinRT.Interop.WindowNative.GetWindowHandle(this);
            LoadIcon(hwnd, "Assets/windows-sdk.ico");
            SetWindowSize(hwnd, 1240, 860);
            PlacementCenterWindowInMonitorWin32(hwnd);
        }

        internal MainPage MainPage => Content as MainPage;

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
            HWND hwnd = (HWND)WinRT.Interop.WindowNative.GetWindowHandle(this);
            GetWindowRect(hwnd, out RECT rect);

            int width = rect.right - rect.left;
            int height = rect.bottom - rect.top;

            string finalPath = string.IsNullOrWhiteSpace(outputPath)
                ? Path.Combine(Path.GetTempPath(), $"native-terminal-{Environment.ProcessId}.png")
                : outputPath;

            string targetDirectory = Path.GetDirectoryName(finalPath);
            if (!string.IsNullOrWhiteSpace(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            using var bitmap = new Bitmap(width, height);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(rect.left, rect.top, 0, 0, bitmap.Size);
            bitmap.Save(finalPath, ImageFormat.Png);

            return new NativeAutomationScreenshotResponse
            {
                Ok = true,
                Path = finalPath,
                Width = width,
                Height = height,
            };
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
