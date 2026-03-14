using System;
using System.Collections.Generic;
using System.Linq;

namespace SelfContainedDeployment.Automation
{
    public static class NativeAutomationEventLog
    {
        private static readonly object Sync = new();
        private static readonly LinkedList<NativeAutomationEventEntry> Entries = new();
        private static long _nextSequence = 1;
        private const int MaxEntries = 600;

        public static void Record(string category, string name, string message = null, IReadOnlyDictionary<string, string> data = null)
        {
            lock (Sync)
            {
                Entries.AddLast(new NativeAutomationEventEntry
                {
                    Sequence = _nextSequence++,
                    Timestamp = DateTimeOffset.UtcNow.ToString("O"),
                    Category = category ?? "app",
                    Name = name ?? "event",
                    Message = message,
                    Data = data is null ? new Dictionary<string, string>() : new Dictionary<string, string>(data),
                });

                while (Entries.Count > MaxEntries)
                {
                    Entries.RemoveFirst();
                }
            }
        }

        public static NativeAutomationEventsResponse Snapshot()
        {
            lock (Sync)
            {
                return new NativeAutomationEventsResponse
                {
                    NextSequence = _nextSequence,
                    Events = Entries.Select(Clone).ToList(),
                };
            }
        }

        public static void Clear()
        {
            lock (Sync)
            {
                Entries.Clear();
            }
        }

        private static NativeAutomationEventEntry Clone(NativeAutomationEventEntry entry)
        {
            return new NativeAutomationEventEntry
            {
                Sequence = entry.Sequence,
                Timestamp = entry.Timestamp,
                Category = entry.Category,
                Name = entry.Name,
                Message = entry.Message,
                Data = new Dictionary<string, string>(entry.Data),
            };
        }
    }
}
