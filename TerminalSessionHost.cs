using Microsoft.Win32.SafeHandles;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SelfContainedDeployment
{
    public sealed class TerminalSessionOptions
    {
        public string FileName { get; set; } = string.Empty;

        public string Arguments { get; set; } = string.Empty;

        public string WorkingDirectory { get; set; } = string.Empty;

        public short InitialColumns { get; set; } = 120;

        public short InitialRows { get; set; } = 32;

        public Encoding InputEncoding { get; set; } = Encoding.UTF8;

        public Encoding OutputEncoding { get; set; } = Encoding.UTF8;

        public string BuildCommandLine()
        {
            var executable = Quote(FileName);
            if (string.IsNullOrWhiteSpace(Arguments))
            {
                return executable;
            }

            return executable + " " + Arguments;
        }

        private static string Quote(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException("Terminal session options require a file name.");
            }

            if (value.StartsWith("\"", StringComparison.Ordinal) && value.EndsWith("\"", StringComparison.Ordinal))
            {
                return value;
            }

            return "\"" + value + "\"";
        }
    }

    public sealed class TerminalSessionHost : IDisposable
    {
        private readonly object sync = new();
        private readonly TerminalBuffer buffer;
        private readonly Decoder decoder;
        private readonly byte[] trailingBytes = new byte[4];
        private readonly char[] decodeChars = new char[1024];

        private IntPtr pseudoConsole = IntPtr.Zero;
        private IntPtr processHandle = IntPtr.Zero;
        private IntPtr threadHandle = IntPtr.Zero;
        private SafeFileHandle inputWriterHandle;
        private SafeFileHandle outputReaderHandle;
        private FileStream inputStream;
        private FileStream outputStream;
        private CancellationTokenSource readLoopCts;
        private Task readLoopTask = Task.CompletedTask;
        private Task waitLoopTask = Task.CompletedTask;
        private int trailingByteCount;
        private bool disposed;
        private string statusText = "Not started";

        public TerminalSessionHost(TerminalSessionOptions options, TerminalBuffer buffer = null)
        {
            Options = options ?? throw new ArgumentNullException(nameof(options));
            this.buffer = buffer ?? new TerminalBuffer();
            decoder = options.OutputEncoding.GetDecoder();
        }

        public event EventHandler Changed;

        public event EventHandler<TerminalOutputEventArgs> OutputReceived;

        public TerminalSessionOptions Options { get; }

        public TerminalBuffer Buffer => buffer;

        public bool IsRunning
        {
            get
            {
                lock (sync)
                {
                    return processHandle != IntPtr.Zero;
                }
            }
        }

        public string StatusText
        {
            get
            {
                lock (sync)
                {
                    return statusText;
                }
            }
        }

        public string LaunchSummary => Options.BuildCommandLine();

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            lock (sync)
            {
                if (processHandle != IntPtr.Zero)
                {
                    return;
                }

                statusText = "Starting";
            }

            NotifyChanged();

            IntPtr ptyInputRead = IntPtr.Zero;
            IntPtr ptyInputWrite = IntPtr.Zero;
            IntPtr ptyOutputRead = IntPtr.Zero;
            IntPtr ptyOutputWrite = IntPtr.Zero;
            IntPtr attributeList = IntPtr.Zero;
            IntPtr commandLinePtr = IntPtr.Zero;

            try
            {
                var securityAttributes = new TerminalNative.SecurityAttributes
                {
                    nLength = Marshal.SizeOf<TerminalNative.SecurityAttributes>(),
                    bInheritHandle = true
                };

                if (!TerminalNative.CreatePipe(out ptyInputRead, out ptyInputWrite, ref securityAttributes, 0))
                {
                    throw CreateWin32Exception("CreatePipe for terminal input failed.");
                }

                if (!TerminalNative.CreatePipe(out ptyOutputRead, out ptyOutputWrite, ref securityAttributes, 0))
                {
                    throw CreateWin32Exception("CreatePipe for terminal output failed.");
                }

                var createResult = TerminalNative.CreatePseudoConsole(
                    new TerminalNative.Coord(Options.InitialColumns, Options.InitialRows),
                    ptyInputRead,
                    ptyOutputWrite,
                    0,
                    out pseudoConsole);

                if (createResult != 0)
                {
                    throw new InvalidOperationException("CreatePseudoConsole failed with code " + createResult + ".");
                }

                // The pseudoconsole owns these ends after creation.
                TerminalNative.CloseHandle(ptyInputRead);
                ptyInputRead = IntPtr.Zero;
                TerminalNative.CloseHandle(ptyOutputWrite);
                ptyOutputWrite = IntPtr.Zero;

                inputWriterHandle = TerminalNative.CreateOwnedHandle(ptyInputWrite);
                outputReaderHandle = TerminalNative.CreateOwnedHandle(ptyOutputRead);
                ptyInputWrite = IntPtr.Zero;
                ptyOutputRead = IntPtr.Zero;

                inputStream = new FileStream(inputWriterHandle, FileAccess.Write, 4096, isAsync: true);
                outputStream = new FileStream(outputReaderHandle, FileAccess.Read, 4096, isAsync: true);

                IntPtr attributeListSize = IntPtr.Zero;
                TerminalNative.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);
                attributeList = Marshal.AllocHGlobal(attributeListSize);

                if (!TerminalNative.InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
                {
                    throw CreateWin32Exception("InitializeProcThreadAttributeList failed.");
                }

                if (!TerminalNative.UpdateProcThreadAttribute(
                        attributeList,
                        0,
                        (IntPtr)TerminalNative.ProcThreadAttributePseudoConsole,
                        pseudoConsole,
                        (IntPtr)IntPtr.Size,
                        IntPtr.Zero,
                        IntPtr.Zero))
                {
                    throw CreateWin32Exception("UpdateProcThreadAttribute for pseudoconsole failed.");
                }

                var startupInfoEx = new TerminalNative.StartupInfoEx
                {
                    StartupInfo = new TerminalNative.StartupInfo
                    {
                        cb = Marshal.SizeOf<TerminalNative.StartupInfoEx>()
                    },
                    lpAttributeList = attributeList
                };

                commandLinePtr = Marshal.StringToHGlobalUni(Options.BuildCommandLine());

                if (!TerminalNative.CreateProcessW(
                        null,
                        commandLinePtr,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        false,
                        TerminalNative.ExtendedStartupInfoPresent,
                        IntPtr.Zero,
                        string.IsNullOrWhiteSpace(Options.WorkingDirectory) ? null : Options.WorkingDirectory,
                        ref startupInfoEx,
                        out var processInformation))
                {
                    throw CreateWin32Exception("CreateProcessW failed for terminal child process.");
                }

                processHandle = processInformation.hProcess;
                threadHandle = processInformation.hThread;
                statusText = "Running";
                readLoopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                readLoopTask = Task.Run(() => PumpOutputAsync(readLoopCts.Token));
                waitLoopTask = Task.Run(() => WaitForExitAsync(readLoopCts.Token));
                AppendSystemLine("[started] " + LaunchSummary);
            }
            catch
            {
                CleanupNativeResources();
                throw;
            }
            finally
            {
                if (attributeList != IntPtr.Zero)
                {
                    TerminalNative.DeleteProcThreadAttributeList(attributeList);
                    Marshal.FreeHGlobal(attributeList);
                }

                if (commandLinePtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(commandLinePtr);
                }

                CloseHandleIfNeeded(ref ptyInputRead);
                CloseHandleIfNeeded(ref ptyInputWrite);
                CloseHandleIfNeeded(ref ptyOutputRead);
                CloseHandleIfNeeded(ref ptyOutputWrite);
            }

            NotifyChanged();
            await Task.CompletedTask;
        }

        public async Task EnsureStartedAsync(CancellationToken cancellationToken = default)
        {
            if (IsRunning)
            {
                return;
            }

            await StartAsync(cancellationToken);
        }

        public async Task SendTextAsync(string text, CancellationToken cancellationToken = default)
        {
            if (text is null)
            {
                throw new ArgumentNullException(nameof(text));
            }

            await EnsureStartedAsync(cancellationToken);

            if (inputStream is null)
            {
                return;
            }

            var bytes = Options.InputEncoding.GetBytes(text);
            await inputStream.WriteAsync(bytes, 0, bytes.Length, cancellationToken);
            await inputStream.FlushAsync(cancellationToken);
        }

        public Task SendLineAsync(string line, CancellationToken cancellationToken = default)
        {
            return SendTextAsync(line + "\r\n", cancellationToken);
        }

        public Task ResizeAsync(short columns, short rows)
        {
            ThrowIfDisposed();

            if (columns <= 0 || rows <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(columns), "Terminal size must be positive.");
            }

            lock (sync)
            {
                if (pseudoConsole == IntPtr.Zero)
                {
                    return Task.CompletedTask;
                }

                var result = TerminalNative.ResizePseudoConsole(pseudoConsole, new TerminalNative.Coord(columns, rows));
                if (result != 0)
                {
                    throw new InvalidOperationException("ResizePseudoConsole failed with code " + result + ".");
                }

                statusText = "Running";
            }

            NotifyChanged();
            return Task.CompletedTask;
        }

        public async Task StopAsync()
        {
            ThrowIfDisposed();

            CancellationTokenSource cts;
            IntPtr activeProcessHandle;

            lock (sync)
            {
                cts = readLoopCts;
                activeProcessHandle = processHandle;
                statusText = "Stopping";
            }

            cts?.Cancel();

            if (activeProcessHandle != IntPtr.Zero)
            {
                TerminalNative.TerminateProcess(activeProcessHandle, 0);
            }

            try
            {
                await Task.WhenAll(readLoopTask, waitLoopTask);
            }
            catch
            {
            }

            CleanupNativeResources();
            AppendSystemLine("[stopped]");
            NotifyChanged();
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;

            try
            {
                StopAsync().GetAwaiter().GetResult();
            }
            catch
            {
            }
        }

        private async Task PumpOutputAsync(CancellationToken cancellationToken)
        {
            if (outputStream is null)
            {
                return;
            }

            var bytes = new byte[1024];

            while (!cancellationToken.IsCancellationRequested)
            {
                int read;
                try
                {
                    read = await outputStream.ReadAsync(bytes, 0, bytes.Length, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (read <= 0)
                {
                    break;
                }

                var text = DecodeChunk(bytes, read);
                if (text.Length == 0)
                {
                    continue;
                }

                buffer.Append(text);
                OutputReceived?.Invoke(this, new TerminalOutputEventArgs(text));
                NotifyChanged();
            }
        }

        private async Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            IntPtr activeHandle;

            lock (sync)
            {
                activeHandle = processHandle;
            }

            if (activeHandle == IntPtr.Zero)
            {
                return;
            }

            await Task.Run(() => TerminalNative.WaitForSingleObject(activeHandle, TerminalNative.Infinite), cancellationToken);

            lock (sync)
            {
                statusText = "Exited";
            }

            NotifyChanged();
        }

        private string DecodeChunk(byte[] bytes, int count)
        {
            if (count <= 0)
            {
                return string.Empty;
            }

            var source = bytes;
            var sourceCount = count;

            if (trailingByteCount > 0)
            {
                var merged = new byte[trailingByteCount + count];
                System.Buffer.BlockCopy(trailingBytes, 0, merged, 0, trailingByteCount);
                System.Buffer.BlockCopy(bytes, 0, merged, trailingByteCount, count);
                source = merged;
                sourceCount = merged.Length;
                trailingByteCount = 0;
            }

            decoder.Convert(source, 0, sourceCount, decodeChars, 0, decodeChars.Length, false, out var bytesUsed, out var charsUsed, out _);

            var remaining = sourceCount - bytesUsed;
            if (remaining > 0)
            {
                System.Buffer.BlockCopy(source, bytesUsed, trailingBytes, 0, remaining);
                trailingByteCount = remaining;
            }

            return new string(decodeChars, 0, charsUsed);
        }

        private void AppendSystemLine(string text)
        {
            buffer.Append(text + Environment.NewLine);
        }

        private void CleanupNativeResources()
        {
            lock (sync)
            {
                readLoopCts?.Dispose();
                readLoopCts = null;

                inputStream?.Dispose();
                inputStream = null;

                outputStream?.Dispose();
                outputStream = null;

                inputWriterHandle?.Dispose();
                inputWriterHandle = null;

                outputReaderHandle?.Dispose();
                outputReaderHandle = null;

                if (threadHandle != IntPtr.Zero)
                {
                    TerminalNative.CloseHandle(threadHandle);
                    threadHandle = IntPtr.Zero;
                }

                if (processHandle != IntPtr.Zero)
                {
                    TerminalNative.CloseHandle(processHandle);
                    processHandle = IntPtr.Zero;
                }

                if (pseudoConsole != IntPtr.Zero)
                {
                    TerminalNative.ClosePseudoConsole(pseudoConsole);
                    pseudoConsole = IntPtr.Zero;
                }

                statusText = "Stopped";
            }
        }

        private void NotifyChanged()
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private static void CloseHandleIfNeeded(ref IntPtr handle)
        {
            if (handle == IntPtr.Zero)
            {
                return;
            }

            TerminalNative.CloseHandle(handle);
            handle = IntPtr.Zero;
        }

        private static Exception CreateWin32Exception(string message)
        {
            return new InvalidOperationException(message + " Win32 error: " + Marshal.GetLastWin32Error() + ".");
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(TerminalSessionHost));
            }
        }
    }

    public sealed class TerminalOutputEventArgs : EventArgs
    {
        public TerminalOutputEventArgs(string text)
        {
            Text = text;
        }

        public string Text { get; }
    }
}
