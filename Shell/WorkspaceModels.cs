using SelfContainedDeployment.Terminal;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SelfContainedDeployment.Shell
{
    public static class ShellProfileIds
    {
        public const string CommandPrompt = "cmd";
        public const string PowerShell = "powershell";
        public const string Wsl = "wsl";
    }

    public sealed class ShellProfileDefinition
    {
        public ShellProfileDefinition(string id, string name, string description, bool usesTranslatedWslPath)
        {
            Id = id;
            Name = name;
            Description = description;
            UsesTranslatedWslPath = usesTranslatedWslPath;
        }

        public string Id { get; }

        public string Name { get; }

        public string Description { get; }

        public bool UsesTranslatedWslPath { get; }
    }

    public static class ShellProfiles
    {
        private static readonly IReadOnlyDictionary<string, ShellProfileDefinition> Definitions =
            new Dictionary<string, ShellProfileDefinition>(StringComparer.OrdinalIgnoreCase)
            {
                [ShellProfileIds.CommandPrompt] = new ShellProfileDefinition(
                    ShellProfileIds.CommandPrompt,
                    "Command Prompt",
                    "Classic cmd.exe session rooted in the project directory.",
                    usesTranslatedWslPath: false),
                [ShellProfileIds.PowerShell] = new ShellProfileDefinition(
                    ShellProfileIds.PowerShell,
                    "PowerShell",
                    "Windows PowerShell session rooted in the project directory.",
                    usesTranslatedWslPath: false),
                [ShellProfileIds.Wsl] = new ShellProfileDefinition(
                    ShellProfileIds.Wsl,
                    "WSL",
                    "Launch the default WSL distro and start in the translated Linux path.",
                    usesTranslatedWslPath: true),
            };

        public static IReadOnlyList<ShellProfileDefinition> All => Definitions.Values.ToList();

        public static ShellProfileDefinition Resolve(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return Definitions[SelfContainedDeployment.SampleConfig.DefaultShellProfileId];
            }

            return Definitions.TryGetValue(id, out ShellProfileDefinition profile)
                ? profile
                : Definitions[SelfContainedDeployment.SampleConfig.DefaultShellProfileId];
        }

        public static string BuildLaunchCommand(string shellProfileId, string projectPath)
        {
            return Resolve(shellProfileId).Id switch
            {
                ShellProfileIds.PowerShell => "powershell.exe -NoLogo",
                ShellProfileIds.Wsl => $"wsl.exe --cd \"{ResolveDisplayPath(projectPath, ShellProfileIds.Wsl)}\"",
                _ => Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
            };
        }

        public static string ResolveDisplayPath(string projectPath, string shellProfileId)
        {
            string normalizedPath = NormalizeProjectPath(projectPath);
            if (Resolve(shellProfileId).UsesTranslatedWslPath)
            {
                return ToWslPath(normalizedPath);
            }

            return normalizedPath;
        }

        public static string ResolveProcessWorkingDirectory(string projectPath)
        {
            string normalizedPath = NormalizeProjectPath(projectPath);

            if (TryResolveLocalStoragePath(normalizedPath, out string localPath) &&
                Directory.Exists(localPath))
            {
                return localPath;
            }

            return Environment.CurrentDirectory;
        }

        public static string NormalizeProjectPath(string projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath))
            {
                return Environment.CurrentDirectory;
            }

            string trimmed = projectPath.Trim();
            if (TryConvertToWindowsPath(trimmed, out string windowsPath))
            {
                return Path.GetFullPath(windowsPath);
            }

            if (IsWslPath(trimmed))
            {
                return trimmed.Replace('\\', '/');
            }

            return Path.GetFullPath(trimmed);
        }

        public static bool TryResolveLocalStoragePath(string projectPath, out string localPath)
        {
            string normalizedPath = NormalizeProjectPath(projectPath);
            if (!string.IsNullOrWhiteSpace(normalizedPath) && !IsWslPath(normalizedPath))
            {
                localPath = normalizedPath;
                return true;
            }

            localPath = null;
            return false;
        }

        public static bool EnsureProjectDirectory(string projectPath, out string localPath)
        {
            if (!TryResolveLocalStoragePath(projectPath, out localPath))
            {
                return false;
            }

            Directory.CreateDirectory(localPath);
            return true;
        }

        public static string DeriveName(string path)
        {
            string trimmed = path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/');
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return path;
            }

            int slashIndex = Math.Max(trimmed.LastIndexOf('\\'), trimmed.LastIndexOf('/'));
            if (slashIndex >= 0 && slashIndex < trimmed.Length - 1)
            {
                return trimmed[(slashIndex + 1)..];
            }

            return trimmed;
        }

        private static string ToWslPath(string path)
        {
            if (IsWslPath(path))
            {
                return path.Replace('\\', '/');
            }

            string fullPath = Path.GetFullPath(path);
            string root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root) || root.Length < 2 || root[1] != ':')
            {
                return fullPath.Replace('\\', '/');
            }

            char drive = char.ToLowerInvariant(root[0]);
            string suffix = fullPath[root.Length..].Replace('\\', '/').TrimStart('/');
            return string.IsNullOrWhiteSpace(suffix)
                ? $"/mnt/{drive}"
                : $"/mnt/{drive}/{suffix}";
        }

        private static bool IsWslPath(string path)
        {
            return path.StartsWith("/", StringComparison.Ordinal);
        }

        private static bool TryConvertToWindowsPath(string path, out string windowsPath)
        {
            windowsPath = null;
            if (!IsWslPath(path))
            {
                return false;
            }

            string normalized = path.Replace('\\', '/').Trim();
            string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2 || !string.Equals(segments[0], "mnt", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string driveSegment = segments[1];
            if (driveSegment.Length != 1 || !char.IsLetter(driveSegment[0]))
            {
                return false;
            }

            string suffix = string.Join(Path.DirectorySeparatorChar, segments.Skip(2));
            string root = $"{char.ToUpperInvariant(driveSegment[0])}:{Path.DirectorySeparatorChar}";
            windowsPath = string.IsNullOrWhiteSpace(suffix)
                ? root
                : Path.Combine(root, suffix);
            return true;
        }
    }

    public sealed class WorkspaceProject
    {
        public WorkspaceProject(string rootPath, string shellProfileId = null, string name = null)
        {
            Id = Guid.NewGuid().ToString("N");
            RootPath = ShellProfiles.NormalizeProjectPath(rootPath);
            ShellProfileId = ShellProfiles.Resolve(shellProfileId).Id;
            Name = string.IsNullOrWhiteSpace(name) ? ShellProfiles.DeriveName(RootPath) : name.Trim();
        }

        public string Id { get; }

        public string Name { get; }

        public string RootPath { get; }

        public string ShellProfileId { get; set; }

        public string DisplayPath => ShellProfiles.ResolveDisplayPath(RootPath, ShellProfileId);

        public string SelectedThreadId { get; set; }

        public List<WorkspaceThread> Threads { get; } = new();
    }

    public sealed class WorkspaceThread
    {
        public WorkspaceThread(WorkspaceProject project, string name)
        {
            Project = project ?? throw new ArgumentNullException(nameof(project));
            Id = Guid.NewGuid().ToString("N");
            Name = name;
        }

        public string Id { get; }

        public string Name { get; set; }

        public WorkspaceProject Project { get; }

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
