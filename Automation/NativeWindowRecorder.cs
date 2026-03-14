using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SelfContainedDeployment.Automation
{
    internal sealed class NativeWindowRecorder
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        private readonly Func<(Bitmap Bitmap, int Width, int Height)> _captureFrame;
        private readonly object _sync = new();
        private CancellationTokenSource _cts;
        private Task _loopTask;
        private string _recordingId;
        private string _outputDirectory;
        private string _videoPath;
        private string _manifestPath;
        private int _targetFps;
        private int _capturedFrames;
        private int _jpegQuality;
        private readonly List<FrameManifestEntry> _frames = new();

        public NativeWindowRecorder(Func<(Bitmap Bitmap, int Width, int Height)> captureFrame)
        {
            _captureFrame = captureFrame;
        }

        public NativeAutomationRecordingStatusResponse GetStatus()
        {
            lock (_sync)
            {
                return new NativeAutomationRecordingStatusResponse
                {
                    Recording = _loopTask is not null && !_loopTask.IsCompleted,
                    RecordingId = _recordingId,
                    OutputDirectory = _outputDirectory,
                    TargetFps = _targetFps,
                    CapturedFrames = _capturedFrames,
                    VideoPath = _videoPath,
                    ManifestPath = _manifestPath,
                };
            }
        }

        public NativeAutomationRecordingStatusResponse Start(NativeAutomationRecordingRequest request)
        {
            request ??= new NativeAutomationRecordingRequest();

            lock (_sync)
            {
                if (_loopTask is not null && !_loopTask.IsCompleted)
                {
                    throw new InvalidOperationException("A recording is already in progress.");
                }

                _recordingId = Guid.NewGuid().ToString("N");
                _targetFps = Math.Clamp(request.Fps <= 0 ? 24 : request.Fps, 4, 60);
                _jpegQuality = Math.Clamp(request.JpegQuality <= 0 ? 82 : request.JpegQuality, 40, 100);
                _capturedFrames = 0;
                _videoPath = null;
                _manifestPath = null;
                _frames.Clear();
                _outputDirectory = string.IsNullOrWhiteSpace(request.OutputDirectory)
                    ? Path.Combine(Path.GetTempPath(), $"native-recording-{_recordingId}")
                    : request.OutputDirectory;

                Directory.CreateDirectory(_outputDirectory);
                _cts = new CancellationTokenSource();
                if (request.MaxDurationMs > 0)
                {
                    _cts.CancelAfter(request.MaxDurationMs);
                }

                CancellationToken token = _cts.Token;
                _loopTask = Task.Run(() => CaptureLoopAsync(token), token);

                NativeAutomationEventLog.Record("recording", "recording.started", $"Started native recording {_recordingId}", new Dictionary<string, string>
                {
                    ["recordingId"] = _recordingId,
                    ["fps"] = _targetFps.ToString(),
                    ["outputDirectory"] = _outputDirectory,
                });

                return GetStatus();
            }
        }

        public async Task<NativeAutomationRecordingStopResponse> StopAsync()
        {
            Task loopTask;
            lock (_sync)
            {
                if (_loopTask is null)
                {
                    return new NativeAutomationRecordingStopResponse
                    {
                        Ok = false,
                    };
                }

                _cts.Cancel();
                loopTask = _loopTask;
            }

            try
            {
                await loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            await FinalizeArtifactsAsync().ConfigureAwait(false);

            lock (_sync)
            {
                _cts.Dispose();
                _cts = null;
                _loopTask = null;

                NativeAutomationEventLog.Record("recording", "recording.stopped", $"Stopped native recording {_recordingId}", new Dictionary<string, string>
                {
                    ["recordingId"] = _recordingId,
                    ["capturedFrames"] = _capturedFrames.ToString(),
                    ["videoPath"] = _videoPath ?? string.Empty,
                    ["manifestPath"] = _manifestPath ?? string.Empty,
                });

                return new NativeAutomationRecordingStopResponse
                {
                    Ok = true,
                    RecordingId = _recordingId,
                    OutputDirectory = _outputDirectory,
                    CapturedFrames = _capturedFrames,
                    VideoPath = _videoPath,
                    ManifestPath = _manifestPath,
                };
            }
        }

        private async Task CaptureLoopAsync(CancellationToken token)
        {
            TimeSpan frameInterval = TimeSpan.FromSeconds(1.0 / _targetFps);
            Stopwatch stopwatch = Stopwatch.StartNew();
            TimeSpan nextFrame = TimeSpan.Zero;

            while (!token.IsCancellationRequested)
            {
                TimeSpan now = stopwatch.Elapsed;
                if (now < nextFrame)
                {
                    TimeSpan delay = nextFrame - now;
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, token).ConfigureAwait(false);
                    }
                }

                now = stopwatch.Elapsed;
                CaptureFrame(now);
                nextFrame += frameInterval;
            }
        }

        private void CaptureFrame(TimeSpan elapsed)
        {
            string framePath;
            lock (_sync)
            {
                framePath = Path.Combine(_outputDirectory, $"frame-{_capturedFrames:000000}.jpg");
            }

            (Bitmap bitmap, int width, int height) = _captureFrame();
            try
            {
                SaveJpeg(bitmap, framePath, _jpegQuality);
            }
            finally
            {
                bitmap.Dispose();
            }

            lock (_sync)
            {
                _frames.Add(new FrameManifestEntry
                {
                    Index = _capturedFrames,
                    TimestampMs = elapsed.TotalMilliseconds,
                    Path = framePath,
                    Width = width,
                    Height = height,
                });
                _capturedFrames++;
            }
        }

        private async Task FinalizeArtifactsAsync()
        {
            string manifestPath;
            string outputDirectory;
            string recordingId;
            List<FrameManifestEntry> frames;

            lock (_sync)
            {
                manifestPath = Path.Combine(_outputDirectory, "manifest.json");
                outputDirectory = _outputDirectory;
                recordingId = _recordingId;
                frames = _frames.ToList();
            }

            string json = JsonSerializer.Serialize(new
            {
                recordingId,
                targetFps = _targetFps,
                capturedFrames = frames.Count,
                frames,
            }, JsonOptions);
            await File.WriteAllTextAsync(manifestPath, json).ConfigureAwait(false);

            string videoPath = await TryEncodeVideoAsync(outputDirectory, manifestPath).ConfigureAwait(false);

            lock (_sync)
            {
                _manifestPath = manifestPath;
                _videoPath = videoPath;
            }
        }

        private async Task<string> TryEncodeVideoAsync(string outputDirectory, string manifestPath)
        {
            string ffmpeg = ResolveFfmpegExecutable();
            if (string.IsNullOrWhiteSpace(ffmpeg))
            {
                return null;
            }

            string videoPath = Path.Combine(outputDirectory, "recording.mp4");
            ProcessStartInfo startInfo = new()
            {
                FileName = ffmpeg,
                Arguments = $"-y -framerate {_targetFps} -i \"{Path.Combine(outputDirectory, "frame-%06d.jpg")}\" -pix_fmt yuv420p \"{videoPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };

            try
            {
                using Process process = Process.Start(startInfo);
                if (process is null)
                {
                    return null;
                }

                await process.WaitForExitAsync().ConfigureAwait(false);
                if (process.ExitCode == 0 && File.Exists(videoPath))
                {
                    NativeAutomationEventLog.Record("recording", "recording.encoded", "Encoded native recording to mp4", new Dictionary<string, string>
                    {
                        ["videoPath"] = videoPath,
                        ["manifestPath"] = manifestPath,
                    });
                    return videoPath;
                }
            }
            catch
            {
            }

            return null;
        }

        private static string ResolveFfmpegExecutable()
        {
            string[] candidates =
            {
                "ffmpeg.exe",
                "ffmpeg",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "shims", "ffmpeg.exe"),
            };

            foreach (string candidate in candidates)
            {
                try
                {
                    ProcessStartInfo startInfo = new()
                    {
                        FileName = candidate,
                        Arguments = "-version",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };
                    using Process process = Process.Start(startInfo);
                    if (process is null)
                    {
                        continue;
                    }

                    process.WaitForExit(1000);
                    if (process.ExitCode == 0)
                    {
                        return candidate;
                    }
                }
                catch
                {
                }
            }

            return null;
        }

        private static void SaveJpeg(Bitmap bitmap, string path, int quality)
        {
            ImageCodecInfo codec = ImageCodecInfo.GetImageEncoders().FirstOrDefault(candidate => candidate.MimeType == "image/jpeg");
            if (codec is null)
            {
                bitmap.Save(path, ImageFormat.Jpeg);
                return;
            }

            EncoderParameters encoderParameters = new(1);
            encoderParameters.Param[0] = new EncoderParameter(Encoder.Quality, quality);
            bitmap.Save(path, codec, encoderParameters);
        }

        private sealed class FrameManifestEntry
        {
            public int Index { get; set; }

            public double TimestampMs { get; set; }

            public string Path { get; set; }

            public int Width { get; set; }

            public int Height { get; set; }
        }
    }
}
