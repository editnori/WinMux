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

        public string SelectedPaneId { get; set; }

        public string Layout { get; set; }

        public double PrimarySplitRatio { get; set; }

        public double SecondarySplitRatio { get; set; }

        public List<PaneSessionSnapshot> Panes { get; set; } = new();
    }

    internal sealed class PaneSessionSnapshot
    {
        public string Id { get; set; }

        public string Kind { get; set; }

        public string Title { get; set; }

        public bool HasCustomTitle { get; set; }

        public string BrowserUri { get; set; }
    }

    internal static class WorkspaceSessionStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };

        private static readonly string SessionDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinMux");

        private static readonly string SessionPath = Path.Combine(SessionDirectory, "workspace-session.json");

        public static WorkspaceSessionSnapshot Load()
        {
            try
            {
                if (!File.Exists(SessionPath))
                {
                    return null;
                }

                string json = File.ReadAllText(SessionPath);
                return JsonSerializer.Deserialize<WorkspaceSessionSnapshot>(json, JsonOptions);
            }
            catch
            {
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
            string json = JsonSerializer.Serialize(snapshot, JsonOptions);
            File.WriteAllText(tempPath, json);

            if (File.Exists(SessionPath))
            {
                File.Copy(tempPath, SessionPath, overwrite: true);
                File.Delete(tempPath);
            }
            else
            {
                File.Move(tempPath, SessionPath);
            }
        }

        public static string GetSessionPath() => SessionPath;

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
