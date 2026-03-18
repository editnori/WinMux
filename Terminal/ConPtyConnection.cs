using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace SelfContainedDeployment.Terminal
{
    public sealed class ConPtyConnection : IDisposable
    {
        private const int ExtendedStartupInfoPresent = 0x00080000;
        private const int CreateUnicodeEnvironment = 0x00000400;
        private const int ProcThreadAttributePseudoConsole = 0x00020016;
        private static readonly UTF8Encoding Utf8NoBom = new(false);
        private static readonly object LaunchEnvironmentSync = new();
        private static IReadOnlyDictionary<string, string> CachedBaseLaunchEnvironment;

        private Process _process;
        private StreamWriter _inputWriter;
        private StreamReader _outputReader;
        private CancellationTokenSource _readerCancellation;
        private Task _readerTask;
        private IntPtr _pseudoConsole;
        private int _exitRaised;
        private bool _disposed;

        public event Action<string> OutputReceived;
        public event Action ProcessExited;

        public bool IsRunning => _process is not null && !_process.HasExited;

        public void Start(int cols, int rows, string shellCommand = null, string workingDirectory = null, IReadOnlyDictionary<string, string> environmentVariables = null)
        {
            ThrowIfDisposed();

            if (_process is not null)
            {
                throw new InvalidOperationException("The terminal connection is already running.");
            }

            shellCommand ??= Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
            workingDirectory ??= Environment.CurrentDirectory;
            IReadOnlyDictionary<string, string> launchEnvironment = BuildLaunchEnvironment(workingDirectory, environmentVariables);

            SECURITY_ATTRIBUTES securityAttributes = new()
            {
                nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                bInheritHandle = true,
            };

            IntPtr pseudoConsoleInput = IntPtr.Zero;
            IntPtr pseudoConsoleOutput = IntPtr.Zero;
            IntPtr appInput = IntPtr.Zero;
            IntPtr appOutput = IntPtr.Zero;
            IntPtr attributeList = IntPtr.Zero;
            IntPtr environmentBlock = IntPtr.Zero;
            PROCESS_INFORMATION processInfo = default;

            try
            {
                CreatePipeOrThrow(out pseudoConsoleInput, out appInput, ref securityAttributes);
                CreatePipeOrThrow(out appOutput, out pseudoConsoleOutput, ref securityAttributes);

                int createPseudoConsoleResult = CreatePseudoConsole(
                    new COORD((short)Math.Max(1, cols), (short)Math.Max(1, rows)),
                    pseudoConsoleInput,
                    pseudoConsoleOutput,
                    0,
                    out _pseudoConsole);
                ThrowIfHResultFailed(createPseudoConsoleResult, "CreatePseudoConsole");

                CloseHandleIfOpen(ref pseudoConsoleInput);
                CloseHandleIfOpen(ref pseudoConsoleOutput);

                IntPtr attributeListSize = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);

                attributeList = Marshal.AllocHGlobal(attributeListSize);
                if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList failed.");
                }

                if (!UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    (IntPtr)ProcThreadAttributePseudoConsole,
                    _pseudoConsole,
                    (IntPtr)IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute failed.");
                }

                STARTUPINFOEX startupInfo = new()
                {
                    StartupInfo =
                    {
                        cb = Marshal.SizeOf<STARTUPINFOEX>(),
                    },
                    lpAttributeList = attributeList,
                };

                StringBuilder commandLine = new(shellCommand);
                environmentBlock = BuildEnvironmentBlockPointer(launchEnvironment);

                if (!CreateProcessW(
                    null,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    ExtendedStartupInfoPresent | CreateUnicodeEnvironment,
                    environmentBlock,
                    workingDirectory,
                    ref startupInfo,
                    out processInfo))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessW failed.");
                }

                CloseHandleIfOpen(ref processInfo.hThread);

                _process = Process.GetProcessById((int)processInfo.dwProcessId);
                _process.EnableRaisingEvents = true;
                _process.Exited += OnProcessExited;

                CloseHandleIfOpen(ref processInfo.hProcess);

                _inputWriter = new StreamWriter(
                    new FileStream(new SafeFileHandle(appInput, ownsHandle: true), FileAccess.Write, 4096, false),
                    Utf8NoBom)
                {
                    AutoFlush = true,
                    NewLine = "\r\n",
                };
                appInput = IntPtr.Zero;

                _outputReader = new StreamReader(
                    new FileStream(new SafeFileHandle(appOutput, ownsHandle: true), FileAccess.Read, 4096, false),
                    Utf8NoBom,
                    detectEncodingFromByteOrderMarks: false,
                    bufferSize: 4096,
                    leaveOpen: false);
                appOutput = IntPtr.Zero;

                _readerCancellation = new CancellationTokenSource();
                _readerTask = Task.Run(() => ReadOutputLoop(_readerCancellation.Token));
            }
            catch
            {
                Dispose();
                throw;
            }
            finally
            {
                if (attributeList != IntPtr.Zero)
                {
                    DeleteProcThreadAttributeList(attributeList);
                    Marshal.FreeHGlobal(attributeList);
                }

                CloseHandleIfOpen(ref pseudoConsoleInput);
                CloseHandleIfOpen(ref pseudoConsoleOutput);
                CloseHandleIfOpen(ref appInput);
                CloseHandleIfOpen(ref appOutput);
                CloseHandleIfOpen(ref processInfo.hThread);
                CloseHandleIfOpen(ref processInfo.hProcess);
                if (environmentBlock != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(environmentBlock);
                }
            }
        }

        private static IReadOnlyDictionary<string, string> BuildLaunchEnvironment(string workingDirectory, IReadOnlyDictionary<string, string> overrides)
        {
            Dictionary<string, string> values = new(GetBaseLaunchEnvironment(), StringComparer.OrdinalIgnoreCase)
            {
                ["WINMUX_REPO_ROOT"] = GetBaseLaunchEnvironment()["WINMUX_REPO_ROOT"],
                ["WINMUX_THREAD_ROOT"] = string.IsNullOrWhiteSpace(workingDirectory) ? Environment.CurrentDirectory : workingDirectory,
            };

            if (overrides is not null)
            {
                foreach ((string key, string value) in overrides)
                {
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        continue;
                    }

                    values[key] = value ?? string.Empty;
                }
            }

            return values;
        }

        private static IReadOnlyDictionary<string, string> GetBaseLaunchEnvironment()
        {
            lock (LaunchEnvironmentSync)
            {
                if (CachedBaseLaunchEnvironment is not null)
                {
                    return CachedBaseLaunchEnvironment;
                }

                Dictionary<string, string> values = Environment.GetEnvironmentVariables()
                    .Cast<System.Collections.DictionaryEntry>()
                    .Where(entry => entry.Key is string && entry.Value is not null)
                    .ToDictionary(
                        entry => (string)entry.Key,
                        entry => entry.Value?.ToString() ?? string.Empty,
                        StringComparer.OrdinalIgnoreCase);

                values["TERM_PROGRAM"] = SelfContainedDeployment.SampleConfig.FeatureName;
                values["TERM_PROGRAM_VERSION"] = "0.1";
                values["COLORTERM"] = "truecolor";
                values["TERM"] = "xterm-256color";

                string automationPort = values.TryGetValue("NATIVE_TERMINAL_AUTOMATION_PORT", out string portValue)
                    ? portValue
                    : Environment.GetEnvironmentVariable("NATIVE_TERMINAL_AUTOMATION_PORT");
                if (!string.IsNullOrWhiteSpace(automationPort))
                {
                    string baseUrl = $"http://127.0.0.1:{automationPort}";
                    values["WINMUX_AUTOMATION_PORT"] = automationPort;
                    values["WINMUX_AUTOMATION_URL"] = baseUrl;
                    values["WINMUX_BROWSER_STATE_URL"] = $"{baseUrl}/browser-state";
                    values["WINMUX_BROWSER_EVAL_URL"] = $"{baseUrl}/browser-eval";
                    values["WINMUX_BROWSER_SCREENSHOT_URL"] = $"{baseUrl}/browser-screenshot";

                    string automationToken = values.TryGetValue("NATIVE_TERMINAL_AUTOMATION_TOKEN", out string tokenValue)
                        ? tokenValue
                        : Environment.GetEnvironmentVariable("NATIVE_TERMINAL_AUTOMATION_TOKEN");
                    if (!string.IsNullOrWhiteSpace(automationToken))
                    {
                        values["WINMUX_AUTOMATION_TOKEN"] = automationToken;
                    }
                }

                values["WINMUX_BROWSER_PROFILE_MODE"] = "shared";
                values["WINMUX_REPO_ROOT"] = values.TryGetValue("WINMUX_REPO_ROOT", out string repoRoot) && !string.IsNullOrWhiteSpace(repoRoot)
                    ? repoRoot
                    : Environment.CurrentDirectory;
                values["WINMUX_THREAD_ROOT"] = Environment.CurrentDirectory;

                values["WSLENV"] = AppendWslenvEntry(values.TryGetValue("WSLENV", out string wslenv) ? wslenv : null, "WINMUX_BROWSER_PROFILE_MODE/u");
                values["WSLENV"] = AppendWslenvEntry(values["WSLENV"], "WINMUX_AUTOMATION_PORT/u");
                values["WSLENV"] = AppendWslenvEntry(values["WSLENV"], "WINMUX_AUTOMATION_URL/u");
                values["WSLENV"] = AppendWslenvEntry(values["WSLENV"], "WINMUX_AUTOMATION_TOKEN/u");
                values["WSLENV"] = AppendWslenvEntry(values["WSLENV"], "WINMUX_BROWSER_STATE_URL/u");
                values["WSLENV"] = AppendWslenvEntry(values["WSLENV"], "WINMUX_BROWSER_EVAL_URL/u");
                values["WSLENV"] = AppendWslenvEntry(values["WSLENV"], "WINMUX_BROWSER_SCREENSHOT_URL/u");
                values["WSLENV"] = AppendWslenvEntry(values["WSLENV"], "WINMUX_REPO_ROOT/p");
                values["WSLENV"] = AppendWslenvEntry(values["WSLENV"], "WINMUX_PROJECT_ROOT/p");
                values["WSLENV"] = AppendWslenvEntry(values["WSLENV"], "WINMUX_THREAD_ROOT/p");
                values["WSLENV"] = AppendWslenvEntry(values["WSLENV"], "WINMUX_BROWSER_BRIDGE/p");

                CachedBaseLaunchEnvironment = values;
                return CachedBaseLaunchEnvironment;
            }
        }

        private static string AppendWslenvEntry(string existing, string entry)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                return existing ?? string.Empty;
            }

            string[] entries = string.IsNullOrWhiteSpace(existing)
                ? Array.Empty<string>()
                : existing.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (Array.Exists(entries, candidate => string.Equals(candidate, entry, StringComparison.OrdinalIgnoreCase)))
            {
                return existing;
            }

            return entries.Length == 0
                ? entry
                : $"{existing}:{entry}";
        }

        private static IntPtr BuildEnvironmentBlockPointer(IReadOnlyDictionary<string, string> values)
        {
            if (values is null || values.Count == 0)
            {
                return IntPtr.Zero;
            }

            string environmentBlock = string.Join("\0", values
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"{pair.Key}={pair.Value}")) + "\0\0";

            return Marshal.StringToHGlobalUni(environmentBlock);
        }

        public void WriteInput(string text)
        {
            if (_disposed || _inputWriter is null || string.IsNullOrEmpty(text))
            {
                return;
            }

            try
            {
                _inputWriter.Write(text);
            }
            catch (ObjectDisposedException)
            {
            }
            catch (IOException)
            {
            }
        }

        public void Resize(int cols, int rows)
        {
            if (_disposed || _pseudoConsole == IntPtr.Zero)
            {
                return;
            }

            int resizeResult = ResizePseudoConsole(_pseudoConsole, new COORD((short)Math.Max(1, cols), (short)Math.Max(1, rows)));
            ThrowIfHResultFailed(resizeResult, "ResizePseudoConsole");
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                _readerCancellation?.Cancel();
            }
            catch
            {
            }

            try
            {
                _inputWriter?.Dispose();
            }
            catch
            {
            }

            try
            {
                _outputReader?.Dispose();
            }
            catch
            {
            }

            try
            {
                if (_pseudoConsole != IntPtr.Zero)
                {
                    ClosePseudoConsole(_pseudoConsole);
                    _pseudoConsole = IntPtr.Zero;
                }
            }
            catch
            {
            }

            try
            {
                if (_process is not null)
                {
                    _process.Exited -= OnProcessExited;

                    if (!_process.HasExited)
                    {
                        TryRequestGracefulExit();
                        if (!_process.WaitForExit(400))
                        {
                            _process.Kill(entireProcessTree: true);
                        }
                    }

                    _process.Dispose();
                }
            }
            catch
            {
            }
            finally
            {
                _process = null;
            }

            RaiseProcessExited();
        }

        private void TryRequestGracefulExit()
        {
            if (_disposed || _inputWriter is null)
            {
                return;
            }

            try
            {
                _inputWriter.Write("exit\r");
                _inputWriter.Flush();
            }
            catch
            {
            }
        }

        private async Task ReadOutputLoop(CancellationToken cancellationToken)
        {
            char[] buffer = new char[4096];

            try
            {
                while (!cancellationToken.IsCancellationRequested && _outputReader is not null)
                {
                    int count = await _outputReader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                    if (count <= 0)
                    {
                        break;
                    }

                    OutputReceived?.Invoke(new string(buffer, 0, count));
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (IOException)
            {
            }
            finally
            {
                RaiseProcessExited();
            }
        }

        private void OnProcessExited(object sender, EventArgs e)
        {
            RaiseProcessExited();
        }

        private void RaiseProcessExited()
        {
            if (Interlocked.Exchange(ref _exitRaised, 1) == 0)
            {
                ProcessExited?.Invoke();
            }
        }

        private static void CreatePipeOrThrow(out IntPtr readPipe, out IntPtr writePipe, ref SECURITY_ATTRIBUTES securityAttributes)
        {
            if (!CreatePipe(out readPipe, out writePipe, ref securityAttributes, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe failed.");
            }
        }

        private static void ThrowIfHResultFailed(int hResult, string operation)
        {
            if (hResult < 0)
            {
                Marshal.ThrowExceptionForHR(hResult);
            }

            if (hResult > 0)
            {
                throw new InvalidOperationException($"{operation} failed with HRESULT 0x{hResult:X8}.");
            }
        }

        private static void CloseHandleIfOpen(ref IntPtr handle)
        {
            if (handle != IntPtr.Zero)
            {
                CloseHandle(handle);
                handle = IntPtr.Zero;
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ConPtyConnection));
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct COORD
        {
            public short X;
            public short Y;

            public COORD(short x, short y)
            {
                X = x;
                Y = y;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;

            [MarshalAs(UnmanagedType.Bool)]
            public bool bInheritHandle;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFOEX
        {
            public STARTUPINFO StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public uint dwProcessId;
            public uint dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreatePipe(
            out IntPtr hReadPipe,
            out IntPtr hWritePipe,
            ref SECURITY_ATTRIBUTES lpPipeAttributes,
            int nSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int CreatePseudoConsole(
            COORD size,
            IntPtr hInput,
            IntPtr hOutput,
            uint dwFlags,
            out IntPtr phPC);

        [DllImport("kernel32.dll")]
        private static extern void ClosePseudoConsole(IntPtr hPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool InitializeProcThreadAttributeList(
            IntPtr lpAttributeList,
            int dwAttributeCount,
            int dwFlags,
            ref IntPtr lpSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UpdateProcThreadAttribute(
            IntPtr lpAttributeList,
            uint dwFlags,
            IntPtr attribute,
            IntPtr lpValue,
            IntPtr cbSize,
            IntPtr lpPreviousValue,
            IntPtr lpReturnSize);

        [DllImport("kernel32.dll")]
        private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateProcessW(
            string lpApplicationName,
            StringBuilder lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
            int dwCreationFlags,
            IntPtr lpEnvironment,
            string lpCurrentDirectory,
            [In] ref STARTUPINFOEX lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);
    }
}
