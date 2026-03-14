using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SelfContainedDeployment.Automation
{
    public sealed class NativeAutomationServer : IDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        private readonly MainWindow _window;
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _shutdown = new();
        private readonly int _port;
        private readonly string _logPath;
        private Task _listenTask;
        private bool _disposed;

        public NativeAutomationServer(MainWindow window, int port)
        {
            _window = window;
            _port = port;
            _listener = TcpListener.Create(port);
            _logPath = Path.Combine(Path.GetTempPath(), "native-terminal-automation.log");
        }

        public void Start()
        {
            ThrowIfDisposed();

            _listener.Start();
            Log($"Automation server listening on http://127.0.0.1:{_port}");
            _listenTask = Task.Run(() => ListenLoopAsync(_shutdown.Token));
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _shutdown.Cancel();

            try
            {
                _listener.Stop();
            }
            catch
            {
            }
        }

        private async Task ListenLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;

                try
                {
                    client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException)
                {
                    break;
                }

                _ = Task.Run(() => HandleClientAsync(client, cancellationToken), cancellationToken);
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
        {
            using var ownedClient = client;
            NetworkStream stream = null;

            try
            {
                stream = ownedClient.GetStream();
                IncomingRequest request = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);

                if (request is null)
                {
                    return;
                }

                string path = request.Path.TrimEnd('/');

                switch (request.Method, path)
                {
                    case ("GET", "/health"):
                        var health = await InvokeOnUiThreadAsync(() => new
                        {
                            ok = true,
                            pid = Environment.ProcessId,
                            title = _window.Title,
                            logPath = _logPath,
                        }).ConfigureAwait(false);
                        await WriteJsonAsync(stream, 200, health).ConfigureAwait(false);
                        break;

                    case ("GET", "/state"):
                        var state = await InvokeOnUiThreadAsync(() => _window.GetAutomationState()).ConfigureAwait(false);
                        await WriteJsonAsync(stream, 200, state).ConfigureAwait(false);
                        break;

                    case ("GET", "/ui-tree"):
                        var uiTree = await InvokeOnUiThreadAsync(() => _window.GetAutomationUiTree()).ConfigureAwait(false);
                        await WriteJsonAsync(stream, 200, uiTree).ConfigureAwait(false);
                        break;

                    case ("GET", "/desktop-windows"):
                        var desktopWindows = _window.GetDesktopWindows();
                        await WriteJsonAsync(stream, 200, desktopWindows).ConfigureAwait(false);
                        break;

                    case ("GET", "/recording-status"):
                        var recordingStatus = _window.GetRecordingStatus();
                        await WriteJsonAsync(stream, 200, recordingStatus).ConfigureAwait(false);
                        break;

                    case ("GET", "/events"):
                        await WriteJsonAsync(stream, 200, NativeAutomationEventLog.Snapshot()).ConfigureAwait(false);
                        break;

                    case ("POST", "/action"):
                        var actionRequest = ReadJson<NativeAutomationActionRequest>(request.Body);
                        var actionResult = await InvokeOnUiThreadAsync(() => _window.PerformAutomationAction(actionRequest)).ConfigureAwait(false);
                        await WriteJsonAsync(stream, 200, actionResult).ConfigureAwait(false);
                        break;

                    case ("POST", "/ui-action"):
                        var uiActionRequest = ReadJson<NativeAutomationUiActionRequest>(request.Body);
                        var uiActionResult = await InvokeOnUiThreadAsync(() => _window.PerformAutomationUiAction(uiActionRequest)).ConfigureAwait(false);
                        await WriteJsonAsync(stream, 200, uiActionResult).ConfigureAwait(false);
                        break;

                    case ("POST", "/terminal-state"):
                        var terminalStateRequest = ReadJson<NativeAutomationTerminalStateRequest>(request.Body);
                        var terminalState = await InvokeOnUiThreadAsync(() => _window.GetTerminalStateAsync(terminalStateRequest)).ConfigureAwait(false);
                        await WriteJsonAsync(stream, 200, terminalState).ConfigureAwait(false);
                        break;

                    case ("POST", "/desktop-action"):
                        var desktopActionRequest = ReadJson<NativeAutomationDesktopActionRequest>(request.Body);
                        var desktopAction = _window.PerformDesktopAction(desktopActionRequest);
                        await WriteJsonAsync(stream, 200, desktopAction).ConfigureAwait(false);
                        break;

                    case ("POST", "/render-trace"):
                        var renderTraceRequest = ReadJson<NativeAutomationRenderTraceRequest>(request.Body);
                        var renderTrace = await InvokeOnUiThreadAsync(() => _window.CaptureRenderTraceAsync(renderTraceRequest)).ConfigureAwait(false);
                        await WriteJsonAsync(stream, 200, renderTrace).ConfigureAwait(false);
                        break;

                    case ("POST", "/recording/start"):
                        var recordingRequest = ReadJson<NativeAutomationRecordingRequest>(request.Body);
                        var recordingStart = _window.StartRecording(recordingRequest);
                        await WriteJsonAsync(stream, 200, recordingStart).ConfigureAwait(false);
                        break;

                    case ("POST", "/recording/stop"):
                        var recordingStop = await _window.StopRecordingAsync().ConfigureAwait(false);
                        await WriteJsonAsync(stream, 200, recordingStop).ConfigureAwait(false);
                        break;

                    case ("POST", "/screenshot"):
                        var screenshotRequest = ReadJson<NativeAutomationScreenshotRequest>(request.Body);
                        var screenshotResult = await InvokeOnUiThreadAsync(() => _window.CaptureAutomationScreenshotAsync(screenshotRequest)).ConfigureAwait(false);
                        await WriteJsonAsync(stream, 200, screenshotResult).ConfigureAwait(false);
                        break;

                    case ("POST", "/events/clear"):
                        NativeAutomationEventLog.Clear();
                        await WriteJsonAsync(stream, 200, new
                        {
                            ok = true,
                        }).ConfigureAwait(false);
                        break;

                    default:
                        await WriteJsonAsync(stream, 404, new
                        {
                            ok = false,
                            error = "Not found",
                        }).ConfigureAwait(false);
                        break;
                }
            }
            catch (Exception ex)
            {
                Log(ex.ToString());

                if (stream is null || !ownedClient.Connected)
                {
                    return;
                }

                await WriteJsonAsync(stream, 500, new
                {
                    ok = false,
                    error = ex.Message,
                }).ConfigureAwait(false);
            }
            finally
            {
                stream?.Dispose();
            }
        }

        private static T ReadJson<T>(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return Activator.CreateInstance<T>();
            }

            return JsonSerializer.Deserialize<T>(body, JsonOptions);
        }

        private static async Task WriteJsonAsync(NetworkStream stream, int statusCode, object payload)
        {
            string json = JsonSerializer.Serialize(payload, JsonOptions);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            string header = $"HTTP/1.1 {statusCode} {ReasonPhrase(statusCode)}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
            byte[] headerBytes = Encoding.ASCII.GetBytes(header);

            await stream.WriteAsync(headerBytes, 0, headerBytes.Length).ConfigureAwait(false);
            await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }

        private Task<T> InvokeOnUiThreadAsync<T>(Func<T> action)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

            if (!_window.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
            {
                try
                {
                    tcs.SetResult(action());
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            }))
            {
                tcs.SetException(new InvalidOperationException("Could not enqueue automation work on the UI thread."));
            }

            return tcs.Task;
        }

        private Task<T> InvokeOnUiThreadAsync<T>(Func<Task<T>> action)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

            if (!_window.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, async () =>
            {
                try
                {
                    tcs.SetResult(await action().ConfigureAwait(true));
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            }))
            {
                tcs.SetException(new InvalidOperationException("Could not enqueue automation work on the UI thread."));
            }

            return tcs.Task;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(NativeAutomationServer));
            }
        }

        private async Task<IncomingRequest> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            var buffer = new List<byte>(2048);
            int headerEnd = -1;
            var scratch = new byte[1024];

            while (headerEnd < 0)
            {
                int read = await stream.ReadAsync(scratch, 0, scratch.Length, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return null;
                }

                buffer.AddRange(scratch[..read]);
                headerEnd = FindHeaderEnd(buffer);

                if (buffer.Count > 64 * 1024)
                {
                    throw new InvalidOperationException("Automation request headers are too large.");
                }
            }

            byte[] requestBytes = buffer.ToArray();
            string headerText = Encoding.ASCII.GetString(requestBytes, 0, headerEnd);
            string[] headerLines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);

            if (headerLines.Length == 0)
            {
                throw new InvalidOperationException("Automation request is missing a request line.");
            }

            string[] requestLine = headerLines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (requestLine.Length < 2)
            {
                throw new InvalidOperationException("Automation request line is invalid.");
            }

            int contentLength = 0;
            for (int i = 1; i < headerLines.Length; i++)
            {
                int separator = headerLines[i].IndexOf(':');
                if (separator <= 0)
                {
                    continue;
                }

                string name = headerLines[i][..separator].Trim();
                string value = headerLines[i][(separator + 1)..].Trim();

                if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(value, out contentLength);
                }
            }

            int bodyOffset = headerEnd + 4;
            int bodyBytesAvailable = requestBytes.Length - bodyOffset;
            while (bodyBytesAvailable < contentLength)
            {
                int read = await stream.ReadAsync(scratch, 0, scratch.Length, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                buffer.AddRange(scratch[..read]);
                requestBytes = buffer.ToArray();
                bodyBytesAvailable = requestBytes.Length - bodyOffset;
            }

            string body = contentLength > 0 && bodyOffset < requestBytes.Length
                ? Encoding.UTF8.GetString(requestBytes, bodyOffset, Math.Min(contentLength, requestBytes.Length - bodyOffset))
                : string.Empty;

            return new IncomingRequest(requestLine[0].Trim().ToUpperInvariant(), requestLine[1].Trim(), body);
        }

        private static int FindHeaderEnd(List<byte> buffer)
        {
            for (int i = 3; i < buffer.Count; i++)
            {
                if (buffer[i - 3] == '\r' && buffer[i - 2] == '\n' && buffer[i - 1] == '\r' && buffer[i] == '\n')
                {
                    return i - 3;
                }
            }

            return -1;
        }

        private static string ReasonPhrase(int statusCode)
        {
            return statusCode switch
            {
                200 => "OK",
                404 => "Not Found",
                500 => "Internal Server Error",
                _ => "OK",
            };
        }

        private void Log(string message)
        {
            try
            {
                File.AppendAllText(_logPath, $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}");
            }
            catch
            {
            }
        }

        private sealed record IncomingRequest(string Method, string Path, string Body);
    }
}
