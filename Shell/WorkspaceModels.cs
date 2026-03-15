using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SelfContainedDeployment.Panes;
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
        private const string WslLocalhostPrefix = @"\\wsl.localhost\";
        private const string WslDollarPrefix = @"\\wsl$\";

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
                ShellProfileIds.Wsl => BuildWslLaunchCommand(projectPath),
                _ => Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
            };
        }

        public static string ResolveDisplayPath(string projectPath, string shellProfileId)
        {
            string normalizedPath = NormalizeProjectPath(projectPath);
            if (Resolve(shellProfileId).UsesTranslatedWslPath)
            {
                if (TryParseWslSharePath(normalizedPath, out WslSharePath wslSharePath))
                {
                    return wslSharePath.LinuxPath;
                }

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
            if (TryParseWslSharePath(trimmed, out WslSharePath wslSharePath))
            {
                return wslSharePath.CanonicalUncPath;
            }

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
            if (!string.IsNullOrWhiteSpace(normalizedPath) &&
                !IsWslPath(normalizedPath) &&
                !TryParseWslSharePath(normalizedPath, out _))
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
            if (TryParseWslSharePath(path, out WslSharePath wslSharePath))
            {
                return wslSharePath.LinuxPath;
            }

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

        private static string BuildWslLaunchCommand(string projectPath)
        {
            string normalizedPath = NormalizeProjectPath(projectPath);
            if (TryParseWslSharePath(normalizedPath, out WslSharePath wslSharePath))
            {
                return $"wsl.exe --distribution {wslSharePath.DistroName} --cd \"{wslSharePath.LinuxPath}\"";
            }

            return $"wsl.exe --cd \"{ResolveDisplayPath(normalizedPath, ShellProfileIds.Wsl)}\"";
        }

        private static bool TryParseWslSharePath(string path, out WslSharePath wslSharePath)
        {
            wslSharePath = null;
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            string trimmed = path.Trim();
            if (trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                trimmed = @"\\" + trimmed[2..].Replace('/', '\\');
            }
            else
            {
                trimmed = trimmed.Replace('/', '\\');
            }

            string prefix = trimmed.StartsWith(WslLocalhostPrefix, StringComparison.OrdinalIgnoreCase)
                ? WslLocalhostPrefix
                : trimmed.StartsWith(WslDollarPrefix, StringComparison.OrdinalIgnoreCase)
                    ? WslDollarPrefix
                    : null;

            if (prefix is null)
            {
                return false;
            }

            string remainder = trimmed[prefix.Length..];
            string[] segments = remainder.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                return false;
            }

            string distroName = segments[0];
            string linuxSuffix = string.Join('/', segments.Skip(1));
            string linuxPath = string.IsNullOrWhiteSpace(linuxSuffix) ? "/" : "/" + linuxSuffix;
            string canonicalUncPath = WslLocalhostPrefix + distroName;
            if (segments.Length > 1)
            {
                canonicalUncPath += @"\" + string.Join('\\', segments.Skip(1));
            }

            wslSharePath = new WslSharePath(distroName, linuxPath, canonicalUncPath);
            return true;
        }

        private sealed record WslSharePath(string DistroName, string LinuxPath, string CanonicalUncPath);
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

    public enum WorkspacePaneKind
    {
        Terminal,
        Browser,
        Editor,
    }

    public enum WorkspaceLayoutPreset
    {
        Solo = 1,
        Dual = 2,
        Triple = 3,
        Quad = 4,
    }

    public sealed class WorkspaceThread
    {
        public WorkspaceThread(WorkspaceProject project, string name)
        {
            Project = project ?? throw new ArgumentNullException(nameof(project));
            Id = Guid.NewGuid().ToString("N");
            Name = name;
            LayoutPreset = WorkspaceLayoutPreset.Dual;
        }

        public string Id { get; }

        public string Name { get; set; }

        public WorkspaceProject Project { get; }

        public string SelectedPaneId { get; set; }

        public string SelectedTabId
        {
            get => SelectedPaneId;
            set => SelectedPaneId = value;
        }

        public WorkspaceLayoutPreset LayoutPreset { get; set; }

        public int PaneLimit => Math.Clamp(SelfContainedDeployment.SampleConfig.MaxPaneCountPerThread, 2, 4);

        public int VisiblePaneCapacity => Math.Min(PaneLimit, Math.Clamp((int)LayoutPreset, 1, 4));

        public double PrimarySplitRatio { get; set; } = 0.58;

        public double SecondarySplitRatio { get; set; } = 0.5;

        public List<WorkspacePaneRecord> Panes { get; } = new();

        public string PaneSummary => Panes.Count == 1 ? "1 pane" : $"{Panes.Count} panes";

        public string TabSummary => PaneSummary;
    }

    public abstract class WorkspacePaneRecord
    {
        protected WorkspacePaneRecord(string title, WorkspacePaneKind kind)
        {
            Id = Guid.NewGuid().ToString("N");
            Title = title;
            Kind = kind;
        }

        public string Id { get; }

        public string Title { get; set; }

        public WorkspacePaneKind Kind { get; }

        public virtual bool IsExited { get; protected set; }

        public abstract FrameworkElement View { get; }

        public abstract void ApplyTheme(ElementTheme theme);

        public abstract void FocusPane();

        public abstract void RequestLayout();

        public abstract void DisposePane();
    }

    public sealed class TerminalPaneRecord : WorkspacePaneRecord
    {
        public TerminalPaneRecord(string title, TerminalControl terminal, WorkspacePaneKind kind = WorkspacePaneKind.Terminal)
            : base(title, kind)
        {
            Terminal = terminal;
        }

        public TerminalControl Terminal { get; }

        public override FrameworkElement View => Terminal;

        public override void ApplyTheme(ElementTheme theme) => Terminal.ApplyTheme(theme);

        public override void FocusPane() => Terminal.FocusTerminal();

        public override void RequestLayout() => Terminal.RequestFit();

        public override void DisposePane() => Terminal.DisposeTerminal();

        public void MarkExited()
        {
            IsExited = true;
        }
    }

    public sealed class BrowserPaneRecord : WorkspacePaneRecord
    {
        public BrowserPaneRecord(string title, BrowserPaneControl browser)
            : base(title, WorkspacePaneKind.Browser)
        {
            Browser = browser;
        }

        public BrowserPaneControl Browser { get; }

        public override FrameworkElement View => Browser;

        public override void ApplyTheme(ElementTheme theme) => Browser.ApplyTheme(theme);

        public override void FocusPane() => Browser.FocusPane();

        public override void RequestLayout() => Browser.RequestLayout();

        public override void DisposePane() => Browser.DisposePane();
    }
}
