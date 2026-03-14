using SelfContainedDeployment.Terminal;
using System;
using System.Collections.Generic;
using System.IO;

namespace SelfContainedDeployment.Shell
{
    public sealed class WorkspaceProject
    {
        public WorkspaceProject(string rootPath)
        {
            Id = Guid.NewGuid().ToString("N");
            RootPath = rootPath;

            string trimmed = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string leaf = Path.GetFileName(trimmed);
            Name = string.IsNullOrWhiteSpace(leaf) ? rootPath : leaf;
        }

        public string Id { get; }

        public string Name { get; }

        public string RootPath { get; }

        public List<WorkspaceThread> Threads { get; } = new();
    }

    public sealed class WorkspaceThread
    {
        public WorkspaceThread(string name)
        {
            Id = Guid.NewGuid().ToString("N");
            Name = name;
        }

        public string Id { get; }

        public string Name { get; set; }

        public string SelectedTabId { get; set; }

        public List<TerminalTabRecord> Tabs { get; } = new();

        public string TabSummary => Tabs.Count == 1 ? "1 tab" : $"{Tabs.Count} tabs";
    }

    public sealed class TerminalTabRecord
    {
        public TerminalTabRecord(string header, TerminalControl terminal)
        {
            Id = Guid.NewGuid().ToString("N");
            Header = header;
            Terminal = terminal;
        }

        public string Id { get; }

        public string Header { get; set; }

        public bool IsExited { get; set; }

        public TerminalControl Terminal { get; }
    }
}
