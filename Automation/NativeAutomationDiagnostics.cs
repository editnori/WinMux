using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace SelfContainedDeployment.Automation
{
    internal sealed class NativeAutomationActionScope
    {
        internal NativeAutomationActionScope(
            string correlationId,
            string kind,
            string name,
            DateTimeOffset startedAt,
            Dictionary<string, long> counterBaseline,
            IReadOnlyDictionary<string, string> data)
        {
            CorrelationId = correlationId;
            StartedAt = startedAt;
            CounterBaseline = counterBaseline;
            Profile = new NativeAutomationActionProfile
            {
                CorrelationId = correlationId,
                Kind = kind,
                Name = name,
                StartedAt = startedAt.ToString("O"),
                Data = data is null
                    ? new Dictionary<string, string>()
                    : new Dictionary<string, string>(data),
            };
        }

        public string CorrelationId { get; }

        public DateTimeOffset StartedAt { get; }

        internal Stopwatch Stopwatch { get; } = Stopwatch.StartNew();

        internal Dictionary<string, long> CounterBaseline { get; }

        internal NativeAutomationActionProfile Profile { get; }
    }

    internal sealed class NativeAutomationTimedScope : IDisposable
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private readonly string _name;
        private readonly string _correlationId;
        private readonly bool _background;
        private readonly IReadOnlyDictionary<string, string> _data;
        private bool _disposed;

        internal NativeAutomationTimedScope(
            string name,
            string correlationId,
            bool background,
            IReadOnlyDictionary<string, string> data)
        {
            _name = name;
            _correlationId = correlationId;
            _background = background;
            _data = data;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stopwatch.Stop();
            NativeAutomationDiagnostics.RecordDuration(_name, _stopwatch.Elapsed.TotalMilliseconds, _correlationId, _background, _data);
        }
    }

    internal static class NativeAutomationDiagnostics
    {
        private static readonly object Sync = new();
        private static readonly AsyncLocal<NativeAutomationActionScope> CurrentAction = new();
        private static readonly Dictionary<string, long> Counters = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, double> LastDurationsMs = new(StringComparer.Ordinal);
        private static readonly LinkedList<NativeAutomationPerfOperation> RecentOperations = new();
        private const int MaxRecentOperations = 240;
        private static long _nextCorrelationSequence = 1;
        private static DateTimeOffset _lastUiHeartbeat = DateTimeOffset.UtcNow;
        private static NativeAutomationActionProfile _lastAction;
        private static string _lastUnhandledExceptionMessage;
        private static string _lastUnhandledExceptionDetails;
        private static string _startupErrorLogPath = GetStartupErrorLogPath();
        private static NativeAutomationHangDump _lastHangDump;

        public static NativeAutomationActionScope BeginAction(
            string kind,
            string name,
            IReadOnlyDictionary<string, string> data = null)
        {
            lock (Sync)
            {
                string correlationId = $"auto-{Interlocked.Increment(ref _nextCorrelationSequence):x}";
                var scope = new NativeAutomationActionScope(
                    correlationId,
                    kind ?? "automation",
                    string.IsNullOrWhiteSpace(name) ? "unknown" : name.Trim(),
                    DateTimeOffset.UtcNow,
                    new Dictionary<string, long>(Counters, StringComparer.Ordinal),
                    data);
                CurrentAction.Value = scope;
                return scope;
            }
        }

        public static void CompleteAction(NativeAutomationActionScope scope, Exception ex = null)
        {
            if (scope is null)
            {
                return;
            }

            scope.Stopwatch.Stop();

            lock (Sync)
            {
                scope.Profile.CompletedAt = DateTimeOffset.UtcNow.ToString("O");
                scope.Profile.TotalMs = scope.Stopwatch.Elapsed.TotalMilliseconds;
                scope.Profile.UiThreadWorkMs = scope.Stopwatch.Elapsed.TotalMilliseconds;
                if (ex is not null)
                {
                    scope.Profile.Error = ex.Message;
                }

                foreach ((string key, long value) in Counters)
                {
                    long baseline = scope.CounterBaseline.TryGetValue(key, out long baselineValue) ? baselineValue : 0;
                    long delta = value - baseline;
                    if (delta != 0)
                    {
                        scope.Profile.CounterDeltas[key] = delta;
                    }
                }

                _lastAction = CloneActionProfile(scope.Profile);

                if (ReferenceEquals(CurrentAction.Value, scope))
                {
                    CurrentAction.Value = null;
                }
            }
        }

        public static NativeAutomationTimedScope TrackOperation(
            string name,
            string correlationId = null,
            bool background = false,
            IReadOnlyDictionary<string, string> data = null)
        {
            return new NativeAutomationTimedScope(name, correlationId ?? CaptureCurrentCorrelationId(), background, data);
        }

        public static void IncrementCounter(string name, long amount = 1)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            lock (Sync)
            {
                Counters[name] = Counters.TryGetValue(name, out long current)
                    ? current + amount
                    : amount;
            }
        }

        public static string CaptureCurrentCorrelationId()
        {
            return CurrentAction.Value?.CorrelationId;
        }

        public static void RecordDuration(
            string name,
            double durationMs,
            string correlationId = null,
            bool background = false,
            IReadOnlyDictionary<string, string> data = null)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            correlationId ??= CaptureCurrentCorrelationId();
            string timestamp = DateTimeOffset.UtcNow.ToString("O");
            string threadKind = Thread.CurrentThread.IsThreadPoolThread ? "background" : "ui";

            lock (Sync)
            {
                LastDurationsMs[name] = durationMs;
                RecentOperations.AddLast(new NativeAutomationPerfOperation
                {
                    Timestamp = timestamp,
                    CorrelationId = correlationId,
                    Name = name,
                    DurationMs = durationMs,
                    ThreadKind = threadKind,
                    Background = background,
                    Data = data is null
                        ? new Dictionary<string, string>()
                        : new Dictionary<string, string>(data),
                });

                while (RecentOperations.Count > MaxRecentOperations)
                {
                    RecentOperations.RemoveFirst();
                }

                if (CurrentAction.Value is not null &&
                    string.Equals(CurrentAction.Value.CorrelationId, correlationId, StringComparison.Ordinal))
                {
                    ApplyDurationToProfile(CurrentAction.Value, name, durationMs, background);
                }
                else if (_lastAction is not null &&
                    string.Equals(_lastAction.CorrelationId, correlationId, StringComparison.Ordinal))
                {
                    ApplyDurationToProfile(_lastAction, name, durationMs, background);
                }
            }
        }

        public static void MarkUiHeartbeat()
        {
            lock (Sync)
            {
                _lastUiHeartbeat = DateTimeOffset.UtcNow;
            }
        }

        public static NativeAutomationPerfSnapshot CaptureSnapshot()
        {
            lock (Sync)
            {
                return new NativeAutomationPerfSnapshot
                {
                    Timestamp = DateTimeOffset.UtcNow.ToString("O"),
                    LastUiHeartbeat = _lastUiHeartbeat.ToString("O"),
                    UiResponsive = DateTimeOffset.UtcNow - _lastUiHeartbeat <= TimeSpan.FromSeconds(2),
                    ActiveCorrelationId = CurrentAction.Value?.CorrelationId,
                    ActiveAction = CurrentAction.Value?.Profile?.Name,
                    Counters = new Dictionary<string, long>(Counters, StringComparer.Ordinal),
                    LastDurationsMs = new Dictionary<string, double>(LastDurationsMs, StringComparer.Ordinal),
                    LastAction = _lastAction is null ? null : CloneActionProfile(_lastAction),
                    RecentOperations = RecentOperations.Select(CloneOperation).ToList(),
                };
            }
        }

        public static void RecordUnhandledException(string message, string details, string startupErrorLogPath)
        {
            lock (Sync)
            {
                _lastUnhandledExceptionMessage = message;
                _lastUnhandledExceptionDetails = details;
                if (!string.IsNullOrWhiteSpace(startupErrorLogPath))
                {
                    _startupErrorLogPath = startupErrorLogPath;
                }
            }
        }

        public static string GetStartupErrorLogPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinMux",
                "startup-error.log");
        }

        public static string GetStartupErrorLogTail(int maxChars = 12000)
        {
            return ReadTail(_startupErrorLogPath, maxChars);
        }

        public static string GetLastUnhandledExceptionMessage()
        {
            lock (Sync)
            {
                return _lastUnhandledExceptionMessage;
            }
        }

        public static string GetLastUnhandledExceptionDetails()
        {
            lock (Sync)
            {
                return _lastUnhandledExceptionDetails;
            }
        }

        public static void RecordHangDump(
            string correlationId,
            string action,
            string screenshotPath,
            string eventsPath,
            string message)
        {
            lock (Sync)
            {
                _lastHangDump = new NativeAutomationHangDump
                {
                    Timestamp = DateTimeOffset.UtcNow.ToString("O"),
                    CorrelationId = correlationId,
                    Action = action,
                    ScreenshotPath = screenshotPath,
                    EventsPath = eventsPath,
                    Message = message,
                };
            }
        }

        public static NativeAutomationHangDump GetLastHangDump()
        {
            lock (Sync)
            {
                return CloneHangDump(_lastHangDump);
            }
        }

        public static string ReadTail(string path, int maxChars = 12000)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return string.Empty;
            }

            try
            {
                string text = File.ReadAllText(path);
                if (text.Length <= maxChars)
                {
                    return text;
                }

                return text[^maxChars..];
            }
            catch
            {
                return string.Empty;
            }
        }

        private static void ApplyDurationToProfile(
            NativeAutomationActionScope scope,
            string name,
            double durationMs,
            bool background)
        {
            ApplyDurationToProfile(scope.Profile, scope.StartedAt, name, durationMs, background);
        }

        private static void ApplyDurationToProfile(
            NativeAutomationActionProfile profile,
            string name,
            double durationMs,
            bool background)
        {
            DateTimeOffset startedAt = DateTimeOffset.TryParse(profile.StartedAt, out DateTimeOffset parsed)
                ? parsed
                : DateTimeOffset.UtcNow;
            ApplyDurationToProfile(profile, startedAt, name, durationMs, background);
        }

        private static void ApplyDurationToProfile(
            NativeAutomationActionProfile profile,
            DateTimeOffset startedAt,
            string name,
            double durationMs,
            bool background)
        {
            profile.OperationTotalsMs[name] = profile.OperationTotalsMs.TryGetValue(name, out double current)
                ? current + durationMs
                : durationMs;

            if (background)
            {
                profile.AsyncBackgroundMs += durationMs;
            }

            switch (name)
            {
                case "render.project-tree":
                    profile.ProjectRailRefreshMs += durationMs;
                    break;
                case "pane.layout.apply":
                case "render.pane-workspace":
                    profile.PaneLayoutMs += durationMs;
                    break;
                case "inspector.build.background":
                case "inspector.build.apply":
                    profile.InspectorRebuildMs += durationMs;
                    break;
                case "git.refresh.active":
                    profile.GitRefreshMs += durationMs;
                    break;
            }

            if (profile.FirstRenderCompleteMs <= 0 &&
                (name.StartsWith("render.", StringComparison.Ordinal) ||
                 name.StartsWith("pane.layout", StringComparison.Ordinal) ||
                 string.Equals(name, "inspector.build.apply", StringComparison.Ordinal)))
            {
                profile.FirstRenderCompleteMs = Math.Max(0, (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds);
            }
        }

        private static NativeAutomationActionProfile CloneActionProfile(NativeAutomationActionProfile profile)
        {
            return new NativeAutomationActionProfile
            {
                CorrelationId = profile.CorrelationId,
                Kind = profile.Kind,
                Name = profile.Name,
                StartedAt = profile.StartedAt,
                CompletedAt = profile.CompletedAt,
                TotalMs = profile.TotalMs,
                UiThreadWorkMs = profile.UiThreadWorkMs,
                AsyncBackgroundMs = profile.AsyncBackgroundMs,
                FirstRenderCompleteMs = profile.FirstRenderCompleteMs,
                PaneLayoutMs = profile.PaneLayoutMs,
                ProjectRailRefreshMs = profile.ProjectRailRefreshMs,
                InspectorRebuildMs = profile.InspectorRebuildMs,
                GitRefreshMs = profile.GitRefreshMs,
                Error = profile.Error,
                Data = new Dictionary<string, string>(profile.Data),
                OperationTotalsMs = new Dictionary<string, double>(profile.OperationTotalsMs),
                CounterDeltas = new Dictionary<string, long>(profile.CounterDeltas),
            };
        }

        private static NativeAutomationPerfOperation CloneOperation(NativeAutomationPerfOperation operation)
        {
            return new NativeAutomationPerfOperation
            {
                Timestamp = operation.Timestamp,
                CorrelationId = operation.CorrelationId,
                Name = operation.Name,
                DurationMs = operation.DurationMs,
                ThreadKind = operation.ThreadKind,
                Background = operation.Background,
                Data = new Dictionary<string, string>(operation.Data),
            };
        }

        private static NativeAutomationHangDump CloneHangDump(NativeAutomationHangDump dump)
        {
            if (dump is null)
            {
                return null;
            }

            return new NativeAutomationHangDump
            {
                Timestamp = dump.Timestamp,
                CorrelationId = dump.CorrelationId,
                Action = dump.Action,
                ScreenshotPath = dump.ScreenshotPath,
                EventsPath = dump.EventsPath,
                Message = dump.Message,
            };
        }
    }
}
