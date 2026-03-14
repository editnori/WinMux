using System;
using System.Text;

namespace SelfContainedDeployment
{
    public sealed class TerminalBuffer
    {
        private readonly object sync = new();
        private readonly StringBuilder builder = new();
        private readonly int maxCharacters;
        private long revision;

        public TerminalBuffer(int maxCharacters = 128 * 1024)
        {
            if (maxCharacters <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxCharacters));
            }

            this.maxCharacters = maxCharacters;
        }

        public long Revision
        {
            get
            {
                lock (sync)
                {
                    return revision;
                }
            }
        }

        public void Append(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            lock (sync)
            {
                builder.Append(text);
                TrimIfNeeded();
                revision++;
            }
        }

        public void Clear()
        {
            lock (sync)
            {
                builder.Clear();
                revision++;
            }
        }

        public TerminalBufferSnapshot Snapshot()
        {
            lock (sync)
            {
                return new TerminalBufferSnapshot(builder.ToString(), revision);
            }
        }

        private void TrimIfNeeded()
        {
            if (builder.Length <= maxCharacters)
            {
                return;
            }

            builder.Remove(0, builder.Length - maxCharacters);
        }
    }

    public readonly struct TerminalBufferSnapshot
    {
        public TerminalBufferSnapshot(string text, long revision)
        {
            Text = text;
            Revision = revision;
        }

        public string Text { get; }

        public long Revision { get; }
    }
}
