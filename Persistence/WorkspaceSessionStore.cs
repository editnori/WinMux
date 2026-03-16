using Microsoft.UI.Xaml;
using SelfContainedDeployment.Shell;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SelfContainedDeployment.Persistence
{
    internal sealed class WorkspaceSessionSnapshot
    {
        public int Version { get; set; } = 1;

        public string SavedAt { get; set; }

        public string Theme { get; set; }

        public string DefaultShellProfileId { get; set; }

        public int MaxPaneCountPerThread { get; set; }

        public bool PaneOpen { get; set; }

        public bool InspectorOpen { get; set; } = true;

        public string ActiveView { get; set; }

        public string ActiveProjectId { get; set; }

        public string ActiveThreadId { get; set; }

        public int ThreadSequence { get; set; } = 1;

        public List<ProjectSessionSnapshot> Projects { get; set; } = new();
    }

    internal sealed class ProjectSessionSnapshot
    {
        public string Id { get; set; }

        public string Name { get; set; }

        public string RootPath { get; set; }

        public string ShellProfileId { get; set; }

        public string SelectedThreadId { get; set; }

        public List<ThreadSessionSnapshot> Threads { get; set; } = new();
    }

    internal sealed class ThreadSessionSnapshot
    {
        public string Id { get; set; }

        public string Name { get; set; }

        public string WorktreePath { get; set; }

        public string BranchName { get; set; }

        public string SelectedDiffPath { get; set; }

        public string DiffReviewSource { get; set; }

        public string SelectedCheckpointId { get; set; }

        public GitSnapshotSessionSnapshot BaselineSnapshot { get; set; }

        public string SelectedPaneId { get; set; }

        public string Layout { get; set; }

        public double PrimarySplitRatio { get; set; }

        public double SecondarySplitRatio { get; set; }

        public List<GitCheckpointSessionSnapshot> DiffCheckpoints { get; set; } = new();

        public List<PaneSessionSnapshot> Panes { get; set; } = new();
    }

    internal sealed class GitCheckpointSessionSnapshot
    {
        public string Id { get; set; }

        public string Name { get; set; }

        public string CapturedAt { get; set; }

        public GitSnapshotSessionSnapshot Snapshot { get; set; }
    }

    internal sealed class GitSnapshotSessionSnapshot
    {
        public string BranchName { get; set; }

        public string RepositoryRootPath { get; set; }

        public string WorktreePath { get; set; }

        public string StatusSummary { get; set; }

        public string DiffSummary { get; set; }

        public string SelectedPath { get; set; }

        public string SelectedDiff { get; set; }

        public string Error { get; set; }

        public List<GitChangedFileSessionSnapshot> ChangedFiles { get; set; } = new();
    }

    internal sealed class GitChangedFileSessionSnapshot
    {
        public string Status { get; set; }

        public string Path { get; set; }

        public int AddedLines { get; set; }

        public int RemovedLines { get; set; }

        public string DiffText { get; set; }
    }

    internal sealed class PaneSessionSnapshot
    {
        public string Id { get; set; }

        public string Kind { get; set; }

        public string Title { get; set; }

        public bool HasCustomTitle { get; set; }

        public bool IsExited { get; set; }

        public bool ReplayRestoreFailed { get; set; }

        public string BrowserUri { get; set; }

        public string SelectedBrowserTabId { get; set; }

        public List<BrowserTabSessionSnapshot> BrowserTabs { get; set; } = new();

        public string DiffPath { get; set; }

        public string EditorFilePath { get; set; }

        public string ReplayTool { get; set; }

        public string ReplaySessionId { get; set; }

        public string ReplayCommand { get; set; }
    }

    internal sealed class BrowserTabSessionSnapshot
    {
        public string Id { get; set; }

        public string Title { get; set; }

        public string Uri { get; set; }
    }

    internal static class WorkspaceSessionStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };

        private static readonly string SessionDirectory = ResolveSessionDirectory();

        private static readonly string SessionPath = ResolveSessionPath(SessionDirectory);

        public static WorkspaceSessionSnapshot Load(out string error)
        {
            try
            {
                string loadPath = ResolveLoadPath();
                if (!File.Exists(loadPath))
                {
                    error = null;
                    return null;
                }

                string json = File.ReadAllText(loadPath);
                error = null;
                return JsonSerializer.Deserialize<WorkspaceSessionSnapshot>(json, JsonOptions);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        public static void Save(WorkspaceSessionSnapshot snapshot)
        {
            if (snapshot is null)
            {
                return;
            }

            Directory.CreateDirectory(SessionDirectory);
            string tempPath = SessionPath + ".tmp";
            string backupPath = SessionPath + ".bak";
            string json = JsonSerializer.Serialize(snapshot, JsonOptions);
            File.WriteAllText(tempPath, json);

            if (File.Exists(SessionPath))
            {
                File.Copy(SessionPath, backupPath, overwrite: true);
                File.Replace(tempPath, SessionPath, backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, SessionPath);
            }
        }

        public static string GetSessionPath() => SessionPath;

        private static string ResolveLoadPath()
        {
            if (File.Exists(SessionPath))
            {
                return SessionPath;
            }

            string backupPath = SessionPath + ".bak";
            return File.Exists(backupPath) ? backupPath : SessionPath;
        }

        private static string ResolveSessionDirectory()
        {
            string overridePath = Environment.GetEnvironmentVariable("WINMUX_SESSION_DIRECTORY");
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                return Path.GetFullPath(overridePath.Trim());
            }

            string sessionPathOverride = Environment.GetEnvironmentVariable("WINMUX_SESSION_PATH");
            if (!string.IsNullOrWhiteSpace(sessionPathOverride))
            {
                string fullPath = Path.GetFullPath(sessionPathOverride.Trim());
                string directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    return directory;
                }
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinMux");
        }

        private static string ResolveSessionPath(string sessionDirectory)
        {
            string sessionPathOverride = Environment.GetEnvironmentVariable("WINMUX_SESSION_PATH");
            if (!string.IsNullOrWhiteSpace(sessionPathOverride))
            {
                return Path.GetFullPath(sessionPathOverride.Trim());
            }

            return Path.Combine(sessionDirectory, "workspace-session.json");
        }

        public static ElementTheme ParseTheme(string theme)
        {
            return theme?.Trim().ToLowerInvariant() switch
            {
                "light" => ElementTheme.Light,
                "dark" => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }

        public static string FormatTheme(ElementTheme theme)
        {
            return theme switch
            {
                ElementTheme.Light => "light",
                ElementTheme.Dark => "dark",
                _ => "default",
            };
        }

        public static WorkspaceLayoutPreset ParseLayout(string layout)
        {
            return layout?.Trim().ToLowerInvariant() switch
            {
                "solo" => WorkspaceLayoutPreset.Solo,
                "dual" => WorkspaceLayoutPreset.Dual,
                "triple" => WorkspaceLayoutPreset.Triple,
                "quad" => WorkspaceLayoutPreset.Quad,
                _ => WorkspaceLayoutPreset.Dual,
            };
        }

        public static string FormatLayout(WorkspaceLayoutPreset layout)
        {
            return layout.ToString().ToLowerInvariant();
        }
    }
}
