using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace SelfContainedDeployment.Automation
{
    internal static class NativeDesktopAutomation
    {
        private const int SwRestore = 9;
        private const uint MouseEventMove = 0x0001;
        private const uint MouseEventLeftDown = 0x0002;
        private const uint MouseEventLeftUp = 0x0004;
        private const uint MouseEventRightDown = 0x0008;
        private const uint MouseEventRightUp = 0x0010;
        private const uint KeyEventKeyUp = 0x0002;
        private const byte VkShift = 0x10;
        private const byte VkControl = 0x11;
        private const byte VkMenu = 0x12;

        public static NativeAutomationDesktopWindowsResponse GetWindows()
        {
            List<NativeAutomationDesktopWindowNode> windows = new();
            IntPtr foreground = GetForegroundWindow();

            EnumWindows((hwnd, _) =>
            {
                NativeAutomationDesktopWindowNode node = BuildWindowNode(hwnd, foreground, includeChildren: true);
                if (node is not null)
                {
                    windows.Add(node);
                }

                return true;
            }, IntPtr.Zero);

            return new NativeAutomationDesktopWindowsResponse
            {
                Windows = windows,
            };
        }

        public static NativeAutomationDesktopActionResponse PerformAction(NativeAutomationDesktopActionRequest request)
        {
            request ??= new NativeAutomationDesktopActionRequest();

            try
            {
                IntPtr target = ResolveWindowHandle(request);
                switch (request.Action?.Trim().ToLowerInvariant())
                {
                    case "focuswindow":
                        FocusWindow(target);
                        break;
                    case "clickpoint":
                        ClickAt(request, target, button: "left", clickCount: 1);
                        break;
                    case "doubleclickpoint":
                        ClickAt(request, target, button: "left", clickCount: 2);
                        break;
                    case "rightclickpoint":
                        ClickAt(request, target, button: "right", clickCount: 1);
                        break;
                    case "hoverpoint":
                        MoveToResolvedPoint(request, target);
                        break;
                    case "dragpoint":
                        DragBetweenPoints(request, target);
                        break;
                    case "sendkeys":
                        FocusWindow(target);
                        SendKeyChord(request.Value);
                        break;
                    case "typetext":
                        FocusWindow(target);
                        TypeText(request.Value);
                        break;
                    default:
                        return new NativeAutomationDesktopActionResponse
                        {
                            Ok = false,
                            Message = $"Unknown desktop action '{request.Action}'.",
                        };
                }

                NativeAutomationEventLog.Record("desktop", "action.executed", $"Executed desktop action '{request.Action}'", new Dictionary<string, string>
                {
                    ["action"] = request.Action ?? string.Empty,
                    ["handle"] = FormatHandle(target),
                    ["titleContains"] = request.TitleContains ?? string.Empty,
                    ["className"] = request.ClassName ?? string.Empty,
                });

                return new NativeAutomationDesktopActionResponse
                {
                    Ok = true,
                    Target = BuildWindowNode(target, GetForegroundWindow(), includeChildren: false),
                };
            }
            catch (Exception ex)
            {
                return new NativeAutomationDesktopActionResponse
                {
                    Ok = false,
                    Message = ex.Message,
                };
            }
        }

        public static void ClickWindowCenter(IntPtr hwnd, string button = "left", int clickCount = 1)
        {
            Point point = ResolvePoint(null, hwnd);
            ClickAbsolute(point, button, clickCount);
        }

        private static NativeAutomationDesktopWindowNode BuildWindowNode(IntPtr hwnd, IntPtr foreground, bool includeChildren)
        {
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out Rect rect))
            {
                return null;
            }

            string title = GetWindowTextValue(hwnd);
            string className = GetClassNameValue(hwnd);
            bool visible = IsWindowVisible(hwnd);
            bool enabled = IsWindowEnabled(hwnd);

            if (!visible && string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(className))
            {
                return null;
            }

            NativeAutomationDesktopWindowNode node = new()
            {
                Handle = FormatHandle(hwnd),
                Title = title,
                ClassName = className,
                Visible = visible,
                Enabled = enabled,
                Focused = hwnd == foreground,
                X = rect.Left,
                Y = rect.Top,
                Width = rect.Right - rect.Left,
                Height = rect.Bottom - rect.Top,
            };

            if (includeChildren)
            {
                EnumChildWindows(hwnd, (child, _) =>
                {
                    NativeAutomationDesktopWindowNode childNode = BuildWindowNode(child, foreground, includeChildren: false);
                    if (childNode is not null)
                    {
                        node.Children.Add(childNode);
                    }

                    return true;
                }, IntPtr.Zero);
            }

            return node;
        }

        private static IntPtr ResolveWindowHandle(NativeAutomationDesktopActionRequest request)
        {
            if (!string.IsNullOrWhiteSpace(request.Handle))
            {
                return ParseHandle(request.Handle);
            }

            IntPtr match = IntPtr.Zero;
            EnumWindows((hwnd, _) =>
            {
                string title = GetWindowTextValue(hwnd);
                string className = GetClassNameValue(hwnd);

                bool titleMatches = string.IsNullOrWhiteSpace(request.TitleContains)
                    || (!string.IsNullOrWhiteSpace(title) && title.Contains(request.TitleContains, StringComparison.OrdinalIgnoreCase));
                bool classMatches = string.IsNullOrWhiteSpace(request.ClassName)
                    || string.Equals(className, request.ClassName, StringComparison.OrdinalIgnoreCase);

                if (titleMatches && classMatches)
                {
                    match = hwnd;
                    return false;
                }

                return true;
            }, IntPtr.Zero);

            if (match == IntPtr.Zero)
            {
                throw new InvalidOperationException("No matching desktop window was found.");
            }

            return match;
        }

        private static void FocusWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            ShowWindow(hwnd, SwRestore);
            SetForegroundWindow(hwnd);
        }

        private static void ClickAt(NativeAutomationDesktopActionRequest request, IntPtr hwnd, string button, int clickCount)
        {
            FocusWindow(hwnd);
            Point point = ResolvePoint(request, hwnd);
            ClickAbsolute(point, button, clickCount);
        }

        private static void MoveToResolvedPoint(NativeAutomationDesktopActionRequest request, IntPtr hwnd)
        {
            FocusWindow(hwnd);
            Point point = ResolvePoint(request, hwnd);
            SetCursorPos((int)point.X, (int)point.Y);
            NativeAutomationEventLog.Record("desktop", "pointer.move", $"Moved pointer to {point.X:0},{point.Y:0}", new Dictionary<string, string>
            {
                ["x"] = ((int)point.X).ToString(),
                ["y"] = ((int)point.Y).ToString(),
                ["handle"] = FormatHandle(hwnd),
            });
        }

        private static void DragBetweenPoints(NativeAutomationDesktopActionRequest request, IntPtr hwnd)
        {
            FocusWindow(hwnd);
            Point start = ResolvePoint(request, hwnd);
            Point end = ResolveEndPoint(request, hwnd, start);
            int durationMs = request.DurationMs <= 0 ? 180 : request.DurationMs;
            int steps = Math.Max(4, durationMs / 16);

            SetCursorPos((int)start.X, (int)start.Y);
            mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);

            for (int i = 1; i <= steps; i++)
            {
                double progress = (double)i / steps;
                int x = (int)Math.Round(start.X + ((end.X - start.X) * progress));
                int y = (int)Math.Round(start.Y + ((end.Y - start.Y) * progress));
                SetCursorPos(x, y);
                System.Threading.Thread.Sleep(Math.Max(1, durationMs / steps));
            }

            mouse_event(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
            NativeAutomationEventLog.Record("desktop", "pointer.drag", $"Dragged pointer from {start.X:0},{start.Y:0} to {end.X:0},{end.Y:0}", new Dictionary<string, string>
            {
                ["handle"] = FormatHandle(hwnd),
                ["start"] = $"{(int)start.X},{(int)start.Y}",
                ["end"] = $"{(int)end.X},{(int)end.Y}",
            });
        }

        private static void ClickAbsolute(Point point, string button, int clickCount)
        {
            SetCursorPos((int)point.X, (int)point.Y);
            uint down = string.Equals(button, "right", StringComparison.OrdinalIgnoreCase) ? MouseEventRightDown : MouseEventLeftDown;
            uint up = string.Equals(button, "right", StringComparison.OrdinalIgnoreCase) ? MouseEventRightUp : MouseEventLeftUp;

            for (int index = 0; index < clickCount; index++)
            {
                mouse_event(down, 0, 0, 0, UIntPtr.Zero);
                mouse_event(up, 0, 0, 0, UIntPtr.Zero);
                if (clickCount > 1)
                {
                    System.Threading.Thread.Sleep(50);
                }
            }

            NativeAutomationEventLog.Record("desktop", "pointer.click", $"Clicked {button} at {point.X:0},{point.Y:0}", new Dictionary<string, string>
            {
                ["button"] = button,
                ["clickCount"] = clickCount.ToString(),
                ["x"] = ((int)point.X).ToString(),
                ["y"] = ((int)point.Y).ToString(),
            });
        }

        private static void SendKeyChord(string chord)
        {
            if (string.IsNullOrWhiteSpace(chord))
            {
                return;
            }

            string[] parts = chord.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0)
            {
                return;
            }

            List<byte> modifiers = new();
            for (int i = 0; i < parts.Length - 1; i++)
            {
                modifiers.Add(ParseVirtualKey(parts[i]));
            }

            byte key = ParseVirtualKey(parts[^1]);

            foreach (byte modifier in modifiers)
            {
                KeyDown(modifier);
            }

            KeyPress(key);

            for (int i = modifiers.Count - 1; i >= 0; i--)
            {
                KeyUp(modifiers[i]);
            }
        }

        private static void TypeText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            foreach (char ch in text)
            {
                short vk = VkKeyScan(ch);
                if (vk == -1)
                {
                    continue;
                }

                byte keyCode = (byte)(vk & 0xFF);
                byte modifiers = (byte)((vk >> 8) & 0xFF);

                if ((modifiers & 1) != 0)
                {
                    KeyDown(VkShift);
                }
                if ((modifiers & 2) != 0)
                {
                    KeyDown(VkControl);
                }
                if ((modifiers & 4) != 0)
                {
                    KeyDown(VkMenu);
                }

                KeyPress(keyCode);

                if ((modifiers & 4) != 0)
                {
                    KeyUp(VkMenu);
                }
                if ((modifiers & 2) != 0)
                {
                    KeyUp(VkControl);
                }
                if ((modifiers & 1) != 0)
                {
                    KeyUp(VkShift);
                }
            }
        }

        private static void KeyPress(byte vk)
        {
            KeyDown(vk);
            KeyUp(vk);
        }

        private static void KeyDown(byte vk)
        {
            keybd_event(vk, (byte)MapVirtualKey(vk, 0), 0, UIntPtr.Zero);
        }

        private static void KeyUp(byte vk)
        {
            keybd_event(vk, (byte)MapVirtualKey(vk, 0), KeyEventKeyUp, UIntPtr.Zero);
        }

        private static byte ParseVirtualKey(string token)
        {
            return token.Trim().ToLowerInvariant() switch
            {
                "ctrl" or "control" => VkControl,
                "shift" => VkShift,
                "alt" => VkMenu,
                "tab" => 0x09,
                "enter" => 0x0D,
                "esc" or "escape" => 0x1B,
                "backspace" => 0x08,
                "delete" or "del" => 0x2E,
                "insert" => 0x2D,
                "space" => 0x20,
                "left" => 0x25,
                "up" => 0x26,
                "right" => 0x27,
                "down" => 0x28,
                "home" => 0x24,
                "end" => 0x23,
                "pageup" => 0x21,
                "pagedown" => 0x22,
                "f1" => 0x70,
                "f2" => 0x71,
                "f3" => 0x72,
                "f4" => 0x73,
                "f5" => 0x74,
                "f6" => 0x75,
                "f7" => 0x76,
                "f8" => 0x77,
                "f9" => 0x78,
                "f10" => 0x79,
                "f11" => 0x7A,
                "f12" => 0x7B,
                _ when token.Length == 1 => (byte)char.ToUpperInvariant(token[0]),
                _ => throw new InvalidOperationException($"Unknown key token '{token}'."),
            };
        }

        private static Point ResolvePoint(NativeAutomationDesktopActionRequest request, IntPtr hwnd)
        {
            if (request?.X is null && request?.Y is null)
            {
                if (!GetWindowRect(hwnd, out Rect rect))
                {
                    throw new InvalidOperationException("Could not resolve window bounds.");
                }

                return new Point(rect.Left + ((rect.Right - rect.Left) / 2.0), rect.Top + ((rect.Bottom - rect.Top) / 2.0));
            }

            if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out Rect windowRect))
            {
                return new Point(windowRect.Left + (request.X ?? 0), windowRect.Top + (request.Y ?? 0));
            }

            return new Point(request?.X ?? 0, request?.Y ?? 0);
        }

        private static Point ResolveEndPoint(NativeAutomationDesktopActionRequest request, IntPtr hwnd, Point start)
        {
            if (request.EndX is null && request.EndY is null)
            {
                return new Point(start.X + 40, start.Y + 40);
            }

            if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out Rect windowRect))
            {
                return new Point(windowRect.Left + (request.EndX ?? request.X ?? 0), windowRect.Top + (request.EndY ?? request.Y ?? 0));
            }

            return new Point(request.EndX ?? start.X, request.EndY ?? start.Y);
        }

        private static string FormatHandle(IntPtr hwnd) => $"0x{hwnd.ToInt64():X}";

        private static IntPtr ParseHandle(string handle)
        {
            string normalized = handle.Trim();
            if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return new IntPtr(Convert.ToInt64(normalized[2..], 16));
            }

            return new IntPtr(Convert.ToInt64(normalized));
        }

        private static string GetWindowTextValue(IntPtr hwnd)
        {
            int length = GetWindowTextLength(hwnd);
            StringBuilder buffer = new(Math.Max(length + 1, 256));
            GetWindowText(hwnd, buffer, buffer.Capacity);
            return buffer.ToString();
        }

        private static string GetClassNameValue(IntPtr hwnd)
        {
            StringBuilder buffer = new(256);
            GetClassName(hwnd, buffer, buffer.Capacity);
            return buffer.ToString();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private readonly struct Point
        {
            public Point(double x, double y)
            {
                X = x;
                Y = y;
            }

            public double X { get; }

            public double Y { get; }
        }

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowEnabled(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern short VkKeyScan(char ch);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);
    }
}
