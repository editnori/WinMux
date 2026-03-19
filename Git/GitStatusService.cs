using SelfContainedDeployment.Shell;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SelfContainedDeployment.Git
{
    public sealed class GitChangedFile
    {
        public string Status { get; set; }

        public string Path { get; set; }

        public string OriginalPath { get; set; }

        public int AddedLines { get; set; }

        public int RemovedLines { get; set; }

        public string DiffText { get; set; }

        public string OriginalText { get; set; }

        public string ModifiedText { get; set; }

        public string DisplayName => string.IsNullOrWhiteSpace(Path)
            ? string.Empty
            : Path.Replace('\\', '/');

        public override string ToString()
        {
            if (string.IsNullOrWhiteSpace(Status))
            {
                return DisplayName;
            }

            return $"{Status} {DisplayName}";
        }
    }

    public sealed class GitThreadSnapshot
    {
        public string BranchName { get; set; }

        public string RepositoryRootPath { get; set; }

        public string WorktreePath { get; set; }

        public string StatusSummary { get; set; }

        public string DiffSummary { get; set; }

        public string SelectedPath { get; set; }

        public string SelectedDiff { get; set; }

        public string Error { get; set; }

        public List<GitChangedFile> ChangedFiles { get; set; } = new();
    }

    internal static class GitStatusService
    {
        private const string RootMarker = "__WINMUX_ROOT__";
        private const string StatusMarker = "__WINMUX_STATUS__";
        private const string NumStatMarker = "__WINMUX_NUMSTAT__";
        private static readonly TimeSpan SnapshotCacheMaxAge = TimeSpan.FromMinutes(5);
        private const int MaxSnapshotCacheEntries = 32;
        private static readonly object SnapshotCacheLock = new();
        private static readonly Dictionary<string, CachedGitSnapshotEntry> SnapshotCache = new(StringComparer.OrdinalIgnoreCase);

        private sealed class CachedGitSnapshotEntry
        {
            public DateTimeOffset CapturedAt { get; init; }

            public GitThreadSnapshot Snapshot { get; init; }
        }

        public static GitThreadSnapshot Capture(string workingPath, string selectedPath = null, bool includeSelectedDiff = true)
        {
            string normalizedPath = ShellProfiles.NormalizeProjectPath(workingPath);
            GitThreadSnapshot snapshot = TryGetCachedSnapshot(normalizedPath);
            if (snapshot is null)
            {
                snapshot = new()
                {
                    WorktreePath = normalizedPath,
                };

                GitCommandResult metadataResult = RunGitSnapshot(normalizedPath, includeNumStat: true, untrackedFilesMode: "all");
                if (!metadataResult.Ok)
                {
                    snapshot.Error = NormalizeGitError(metadataResult.Error);
                    return snapshot;
                }

                ParseMetadataSnapshot(snapshot, metadataResult.Output);
                snapshot.StatusSummary = BuildStatusSummary(snapshot.ChangedFiles);
                StoreCachedSnapshot(normalizedPath, snapshot);
            }
            else if (string.IsNullOrWhiteSpace(snapshot.StatusSummary))
            {
                snapshot.StatusSummary = BuildStatusSummary(snapshot.ChangedFiles);
            }

            string resolvedSelectedPath = selectedPath;
            bool selectedPathExists = !string.IsNullOrWhiteSpace(resolvedSelectedPath) &&
                snapshot.ChangedFiles.Any(file => string.Equals(file.Path, resolvedSelectedPath, StringComparison.Ordinal));
            if (!selectedPathExists)
            {
                resolvedSelectedPath = snapshot.ChangedFiles.FirstOrDefault()?.Path;
            }

            snapshot.SelectedPath = resolvedSelectedPath;
            if (includeSelectedDiff && !string.IsNullOrWhiteSpace(resolvedSelectedPath))
            {
                GitChangedFile changedFile = snapshot.ChangedFiles.FirstOrDefault(file => string.Equals(file.Path, resolvedSelectedPath, StringComparison.Ordinal));
                if (changedFile is not null && string.IsNullOrWhiteSpace(changedFile.DiffText))
                {
                    GitCommandResult diffResult = RunGitDiff(normalizedPath, resolvedSelectedPath, changedFile.Status);
                    changedFile.DiffText = diffResult.Ok
                        ? diffResult.Output
                        : NormalizeGitError(diffResult.Error);
                    UpdateCachedSnapshot(normalizedPath, cachedSnapshot =>
                    {
                        GitChangedFile cachedFile = cachedSnapshot.ChangedFiles.FirstOrDefault(file => string.Equals(file.Path, resolvedSelectedPath, StringComparison.Ordinal));
                        if (cachedFile is null)
                        {
                            return;
                        }

                        cachedFile.DiffText = changedFile.DiffText;
                        if (string.IsNullOrWhiteSpace(cachedFile.OriginalPath))
                        {
                            cachedFile.OriginalPath = changedFile.OriginalPath;
                        }
                    });
                }

                snapshot.SelectedDiff = changedFile?.DiffText;
            }

            return snapshot;
        }

        public static GitThreadSnapshot CaptureMetadata(string workingPath, string selectedPath = null)
        {
            return Capture(workingPath, selectedPath, includeSelectedDiff: false);
        }

        public static GitThreadSnapshot CaptureStatusOnly(string workingPath, string selectedPath = null)
        {
            string normalizedPath = ShellProfiles.NormalizeProjectPath(workingPath);
            GitThreadSnapshot cachedSnapshot = TryGetCachedSnapshot(normalizedPath);
            GitThreadSnapshot snapshot = new()
            {
                WorktreePath = normalizedPath,
                RepositoryRootPath = string.IsNullOrWhiteSpace(cachedSnapshot?.RepositoryRootPath)
                    ? normalizedPath
                    : cachedSnapshot.RepositoryRootPath,
            };

            GitCommandResult statusResult = RunGitStatusOnly(normalizedPath);
            if (!statusResult.Ok)
            {
                snapshot.Error = NormalizeGitError(statusResult.Error);
                return snapshot;
            }

            ParseStatus(snapshot, statusResult.Output);
            snapshot.StatusSummary = BuildStatusSummary(snapshot.ChangedFiles);

            string resolvedSelectedPath = selectedPath;
            bool selectedPathExists = !string.IsNullOrWhiteSpace(resolvedSelectedPath) &&
                snapshot.ChangedFiles.Any(file => string.Equals(file.Path, resolvedSelectedPath, StringComparison.Ordinal));
            if (!selectedPathExists)
            {
                resolvedSelectedPath = snapshot.ChangedFiles.FirstOrDefault()?.Path;
            }

            snapshot.SelectedPath = resolvedSelectedPath;
            return snapshot;
        }

        public static GitThreadSnapshot CaptureComplete(string workingPath, string selectedPath = null)
        {
            string normalizedPath = ShellProfiles.NormalizeProjectPath(workingPath);
            GitThreadSnapshot snapshot = Capture(normalizedPath, selectedPath, includeSelectedDiff: true);
            if (!string.IsNullOrWhiteSpace(snapshot.Error) || snapshot.ChangedFiles.Count == 0)
            {
                return snapshot;
            }

            foreach (GitChangedFile changedFile in snapshot.ChangedFiles.Where(file => !string.IsNullOrWhiteSpace(file.Path)))
            {
                if (!string.IsNullOrWhiteSpace(changedFile.DiffText))
                {
                    continue;
                }

                GitCommandResult diffResult = RunGitDiff(normalizedPath, changedFile.Path, changedFile.Status);
                changedFile.DiffText = diffResult.Ok
                    ? diffResult.Output
                    : NormalizeGitError(diffResult.Error);
            }

            StoreCachedSnapshot(normalizedPath, snapshot);
            SelectDiffPath(snapshot, string.IsNullOrWhiteSpace(selectedPath) ? snapshot.SelectedPath : selectedPath);
            return snapshot;
        }

        public static GitThreadSnapshot CloneSnapshot(GitThreadSnapshot snapshot)
        {
            if (snapshot is null)
            {
                return null;
            }

            List<GitChangedFile> changedFiles = new(snapshot.ChangedFiles.Count);
            foreach (GitChangedFile file in snapshot.ChangedFiles)
            {
                changedFiles.Add(new GitChangedFile
                {
                    Status = file.Status,
                    Path = file.Path,
                    OriginalPath = file.OriginalPath,
                    AddedLines = file.AddedLines,
                    RemovedLines = file.RemovedLines,
                    DiffText = file.DiffText,
                });
            }

            return new GitThreadSnapshot
            {
                BranchName = snapshot.BranchName,
                RepositoryRootPath = snapshot.RepositoryRootPath,
                WorktreePath = snapshot.WorktreePath,
                StatusSummary = snapshot.StatusSummary,
                DiffSummary = snapshot.DiffSummary,
                SelectedPath = snapshot.SelectedPath,
                SelectedDiff = snapshot.SelectedDiff,
                Error = snapshot.Error,
                ChangedFiles = changedFiles,
            };
        }

        public static void SelectDiffPath(GitThreadSnapshot snapshot, string selectedPath)
        {
            if (snapshot is null)
            {
                return;
            }

            GitChangedFile selectedFile = null;
            GitChangedFile firstFile = snapshot.ChangedFiles.Count > 0 ? snapshot.ChangedFiles[0] : null;
            if (!string.IsNullOrWhiteSpace(selectedPath))
            {
                foreach (GitChangedFile file in snapshot.ChangedFiles)
                {
                    if (string.Equals(file.Path, selectedPath, StringComparison.Ordinal))
                    {
                        selectedFile = file;
                        break;
                    }
                }
            }

            selectedFile ??= firstFile;
            snapshot.SelectedPath = selectedFile?.Path;
            snapshot.SelectedDiff = selectedFile?.DiffText;
        }

        public static void EnsureCompareTexts(string workingPath, GitChangedFile changedFile)
        {
            if (changedFile is null ||
                string.IsNullOrWhiteSpace(changedFile.Path) ||
                (!string.IsNullOrWhiteSpace(changedFile.OriginalText) || !string.IsNullOrWhiteSpace(changedFile.ModifiedText)))
            {
                return;
            }

            PopulateCompareTexts(workingPath, changedFile);
        }

        public static void EnsureDiffText(string workingPath, GitChangedFile changedFile)
        {
            if (changedFile is null ||
                string.IsNullOrWhiteSpace(changedFile.Path) ||
                !string.IsNullOrWhiteSpace(changedFile.DiffText))
            {
                return;
            }

            GitCommandResult diffResult = RunGitDiff(workingPath, changedFile.Path, changedFile.Status);
            changedFile.DiffText = diffResult.Ok
                ? diffResult.Output
                : NormalizeGitError(diffResult.Error);
            UpdateCachedSnapshot(ShellProfiles.NormalizeProjectPath(workingPath), cachedSnapshot =>
            {
                GitChangedFile cachedFile = cachedSnapshot.ChangedFiles.FirstOrDefault(file => string.Equals(file.Path, changedFile.Path, StringComparison.Ordinal));
                if (cachedFile is null)
                {
                    return;
                }

                cachedFile.DiffText = changedFile.DiffText;
                if (string.IsNullOrWhiteSpace(cachedFile.OriginalPath))
                {
                    cachedFile.OriginalPath = changedFile.OriginalPath;
                }
            });
        }

        private static string NormalizeGitError(string error)
        {
            if (string.IsNullOrWhiteSpace(error))
            {
                return "Git metadata unavailable.";
            }

            if (error.Contains("not a git repository", StringComparison.OrdinalIgnoreCase))
            {
                return "No git repository detected for this thread.";
            }

            if (error.Contains("Git command timed out", StringComparison.OrdinalIgnoreCase))
            {
                return "Git metadata timed out.";
            }

            if (error.Contains("ambiguous argument", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("bad revision", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("unknown revision", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("pathspec", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("fatal:", StringComparison.OrdinalIgnoreCase))
            {
                return "Patch unavailable for this file.";
            }

            return error.Trim();
        }

        private static void ParseStatus(GitThreadSnapshot snapshot, string output)
        {
            string[] lines = output?.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None) ?? Array.Empty<string>();
            foreach (string rawLine in lines)
            {
                string line = rawLine?.TrimEnd() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (line.StartsWith("##", StringComparison.Ordinal))
                {
                    string branch = line[2..].Trim();
                    int separatorIndex = branch.IndexOf("...");
                    snapshot.BranchName = separatorIndex > 0 ? branch[..separatorIndex].Trim() : branch;
                    continue;
                }

                if (line.Length < 4)
                {
                    continue;
                }

                (string currentPath, string originalPath) = ParseTrackedPaths(line[3..].Trim());
                snapshot.ChangedFiles.Add(new GitChangedFile
                {
                    Status = line[..2].Trim(),
                    Path = currentPath,
                    OriginalPath = originalPath,
                });
            }
        }

        private static (string CurrentPath, string OriginalPath) ParseTrackedPaths(string path)
        {
            string normalized = path?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return (string.Empty, string.Empty);
            }

            int renameSeparator = normalized.LastIndexOf(" -> ", StringComparison.Ordinal);
            if (renameSeparator >= 0 && renameSeparator + 4 < normalized.Length)
            {
                string originalPath = NormalizeTrackedPath(normalized[..renameSeparator].Trim());
                string currentPath = NormalizeTrackedPath(normalized[(renameSeparator + 4)..].Trim());
                return (currentPath, originalPath);
            }

            string current = NormalizeTrackedPath(normalized);
            return (current, current);
        }

        private static string BuildStatusSummary(IReadOnlyCollection<GitChangedFile> files)
        {
            if (files is null || files.Count == 0)
            {
                return "No working tree changes";
            }

            return files.Count == 1 ? "1 changed file" : $"{files.Count} changed files";
        }

        private static void ParseMetadataSnapshot(GitThreadSnapshot snapshot, string output)
        {
            StringBuilder rootOutput = new();
            StringBuilder statusOutput = new();
            StringBuilder numStatOutput = new();
            StringBuilder activeOutput = null;

            string[] lines = output?.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None) ?? Array.Empty<string>();
            foreach (string line in lines)
            {
                if (string.Equals(line, RootMarker, StringComparison.Ordinal))
                {
                    activeOutput = rootOutput;
                    continue;
                }

                if (string.Equals(line, StatusMarker, StringComparison.Ordinal))
                {
                    activeOutput = statusOutput;
                    continue;
                }

                if (string.Equals(line, NumStatMarker, StringComparison.Ordinal))
                {
                    activeOutput = numStatOutput;
                    continue;
                }

                activeOutput?.AppendLine(line);
            }

            snapshot.RepositoryRootPath = rootOutput.ToString().Trim();
            ParseStatus(snapshot, statusOutput.ToString());
            ApplyNumStat(snapshot, numStatOutput.ToString());
        }

        private static void ApplyNumStat(GitThreadSnapshot snapshot, string output)
        {
            if (snapshot.ChangedFiles.Count == 0 || string.IsNullOrWhiteSpace(output))
            {
                return;
            }

            Dictionary<string, GitChangedFile> filesByPath = snapshot.ChangedFiles
                .Where(file => !string.IsNullOrWhiteSpace(file.Path))
                .ToDictionary(file => file.Path, StringComparer.Ordinal);

            string[] lines = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string rawLine in lines)
            {
                string[] parts = rawLine.Split('\t');
                if (parts.Length < 3)
                {
                    continue;
                }

                string path = NormalizeTrackedPath(parts[^1].Trim());
                if (!filesByPath.TryGetValue(path, out GitChangedFile changedFile))
                {
                    continue;
                }

                changedFile.AddedLines = ParseNumStatValue(parts[0]);
                changedFile.RemovedLines = ParseNumStatValue(parts[1]);
            }
        }

        private static int ParseNumStatValue(string value)
        {
            return int.TryParse(value, out int parsed) ? parsed : 0;
        }

        private static void PopulateCompareTexts(string workingPath, GitChangedFile changedFile)
        {
            if (changedFile is null || string.IsNullOrWhiteSpace(changedFile.Path))
            {
                return;
            }

            string status = changedFile.Status?.Trim() ?? string.Empty;
            changedFile.OriginalText = ResolveOriginalText(workingPath, changedFile, status);
            changedFile.ModifiedText = ResolveModifiedText(workingPath, changedFile, status);
        }

        private static string ResolveOriginalText(string workingPath, GitChangedFile changedFile, string status)
        {
            if (string.Equals(status, "??", StringComparison.Ordinal))
            {
                return string.Empty;
            }

            if (status.IndexOf('A') >= 0 &&
                status.IndexOf('R') < 0 &&
                status.IndexOf('C') < 0 &&
                status.IndexOf('M') < 0)
            {
                return string.Empty;
            }

            string path = string.IsNullOrWhiteSpace(changedFile.OriginalPath) ? changedFile.Path : changedFile.OriginalPath;
            return ReadGitTextFile(workingPath, path);
        }

        private static string ResolveModifiedText(string workingPath, GitChangedFile changedFile, string status)
        {
            if (status.IndexOf('D') >= 0)
            {
                return string.Empty;
            }

            string projectPath = ShellProfiles.NormalizeProjectPath(workingPath);
            string fullPath = Path.Combine(projectPath, changedFile.Path.Replace('/', Path.DirectorySeparatorChar));
            return ReadLocalTextFile(fullPath);
        }

        private static string ReadGitTextFile(string workingPath, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            string spec = $"HEAD:{NormalizeTrackedPath(path)}";
            if (TryResolveLocalGitPath(workingPath, out string localPath))
            {
                GitBinaryCommandResult localResult = RunWindowsGitBinary(localPath, new[] { "show", spec });
                if (localResult.Ok)
                {
                    return DecodeTextBytes(localResult.Output);
                }

                if (!IsWindowsGitUnavailable(localResult.Error))
                {
                    return string.Empty;
                }
            }

            string script = $"git --no-optional-locks show {QuoteShellArgument(spec)} | base64 -w0";
            GitCommandResult result = RunWslShell(workingPath, script);
            if (!result.Ok || string.IsNullOrWhiteSpace(result.Output))
            {
                return string.Empty;
            }

            try
            {
                byte[] bytes = Convert.FromBase64String(result.Output);
                return DecodeTextBytes(bytes);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string ReadLocalTextFile(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
            {
                return string.Empty;
            }

            try
            {
                byte[] bytes = File.ReadAllBytes(fullPath);
                return DecodeTextBytes(bytes);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string DecodeTextBytes(byte[] bytes)
        {
            if (bytes is null || bytes.Length == 0)
            {
                return string.Empty;
            }

            if (LooksBinary(bytes))
            {
                return string.Empty;
            }

            using MemoryStream stream = new(bytes);
            using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }

        private static bool LooksBinary(byte[] bytes)
        {
            int sampleLength = Math.Min(bytes.Length, 2048);
            for (int index = 0; index < sampleLength; index++)
            {
                if (bytes[index] == 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static GitCommandResult RunGitSnapshot(string workingPath, bool includeNumStat, string untrackedFilesMode)
        {
            if (TryResolveLocalGitPath(workingPath, out string localPath))
            {
                GitCommandResult localResult = RunWindowsGitSnapshot(localPath, includeNumStat, untrackedFilesMode);
                if (localResult.Ok || !IsWindowsGitUnavailable(localResult.Error))
                {
                    return localResult;
                }
            }

            string resolvedUntrackedFilesMode = string.Equals(untrackedFilesMode, "normal", StringComparison.OrdinalIgnoreCase)
                ? "normal"
                : "all";

            string script = string.Join(" && ", new[]
            {
                $"printf '%s\\n' '{RootMarker}'",
                "git --no-optional-locks rev-parse --show-toplevel",
                $"printf '%s\\n' '{StatusMarker}'",
                $"git --no-optional-locks -c core.quotepath=false status --porcelain=v1 -b --untracked-files={resolvedUntrackedFilesMode}",
                $"printf '%s\\n' '{NumStatMarker}'",
                includeNumStat
                    ? "git --no-optional-locks -c core.quotepath=false diff --numstat"
                    : "printf ''",
            });

            return RunWslShell(workingPath, script);
        }

        private static GitCommandResult RunGitStatusOnly(string workingPath)
        {
            if (TryResolveLocalGitPath(workingPath, out string localPath))
            {
                GitCommandResult localResult = RunWindowsGitStatusOnly(localPath);
                if (localResult.Ok || !IsWindowsGitUnavailable(localResult.Error))
                {
                    return localResult;
                }
            }

            return RunWslShell(
                workingPath,
                "git --no-optional-locks -c core.quotepath=false status --porcelain=v1 -b --untracked-files=normal --ignore-submodules=all --no-renames");
        }

        private static GitCommandResult RunGitDiff(string workingPath, string selectedPath, string status)
        {
            string normalizedStatus = status?.Trim() ?? string.Empty;
            selectedPath = NormalizeTrackedPath(selectedPath);
            if (TryResolveLocalGitPath(workingPath, out string localPath))
            {
                GitCommandResult localResult = RunWindowsGitDiff(localPath, selectedPath, normalizedStatus);
                if (localResult.Ok || !IsWindowsGitUnavailable(localResult.Error))
                {
                    return localResult;
                }
            }

            string quotedSelectedPath = QuoteShellArgument(selectedPath);
            if (string.Equals(normalizedStatus, "??", StringComparison.Ordinal))
            {
                string untrackedScript = $"if [ -e {quotedSelectedPath} ]; then git --no-optional-locks -c core.quotepath=false diff --no-index -- /dev/null -- {quotedSelectedPath}; code=$?; if [ $code -le 1 ]; then exit 0; fi; exit $code; fi";
                return RunWslShell(workingPath, untrackedScript);
            }

            if (normalizedStatus.IndexOf('A') >= 0 ||
                normalizedStatus.IndexOf('R') >= 0 ||
                normalizedStatus.IndexOf('C') >= 0)
            {
                string stagedFirstScript = $"git --no-optional-locks -c core.quotepath=false diff --cached -- {quotedSelectedPath}";
                GitCommandResult stagedDiff = RunWslShell(workingPath, stagedFirstScript);
                if (stagedDiff.Ok && !string.IsNullOrWhiteSpace(stagedDiff.Output))
                {
                    return stagedDiff;
                }
            }

            string headScript = $"git --no-optional-locks -c core.quotepath=false diff HEAD -- {quotedSelectedPath}";
            GitCommandResult headDiff = RunWslShell(workingPath, headScript);
            if (headDiff.Ok && !string.IsNullOrWhiteSpace(headDiff.Output))
            {
                return headDiff;
            }

            string cachedScript = $"git --no-optional-locks -c core.quotepath=false diff --cached -- {quotedSelectedPath}";
            GitCommandResult cachedDiff = RunWslShell(workingPath, cachedScript);
            if (cachedDiff.Ok && !string.IsNullOrWhiteSpace(cachedDiff.Output))
            {
                return cachedDiff;
            }

            string workingTreeScript = $"git --no-optional-locks -c core.quotepath=false diff -- {quotedSelectedPath}";
            GitCommandResult workingTreeDiff = RunWslShell(workingPath, workingTreeScript);
            if (workingTreeDiff.Ok || string.IsNullOrWhiteSpace(workingTreeDiff.Error))
            {
                return workingTreeDiff;
            }

            return new GitCommandResult(false, string.Empty, NormalizeGitError(workingTreeDiff.Error));
        }

        private static string NormalizeTrackedPath(string path)
        {
            string normalized = path?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return string.Empty;
            }

            int renameSeparator = normalized.LastIndexOf(" -> ", StringComparison.Ordinal);
            if (renameSeparator >= 0 && renameSeparator + 4 < normalized.Length)
            {
                return normalized[(renameSeparator + 4)..].Trim();
            }

            return normalized;
        }

        private static string QuoteShellArgument(string value)
        {
            string normalized = value ?? string.Empty;
            return $"'{normalized.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
        }

        private static bool TryResolveLocalGitPath(string workingPath, out string localPath)
        {
            if (ShellProfiles.TryResolveLocalStoragePath(workingPath, out localPath) &&
                !string.IsNullOrWhiteSpace(localPath) &&
                Directory.Exists(localPath))
            {
                return true;
            }

            localPath = null;
            return false;
        }

        private static bool IsWindowsGitUnavailable(string error)
        {
            return !string.IsNullOrWhiteSpace(error) &&
                error.StartsWith("Windows git unavailable:", StringComparison.Ordinal);
        }

        private static GitCommandResult RunWindowsGitSnapshot(string workingPath, bool includeNumStat, string untrackedFilesMode)
        {
            string resolvedUntrackedFilesMode = string.Equals(untrackedFilesMode, "normal", StringComparison.OrdinalIgnoreCase)
                ? "normal"
                : "all";
            GitCommandResult rootResult = RunWindowsGit(
                workingPath,
                new[] { "--no-optional-locks", "rev-parse", "--show-toplevel" });
            if (!rootResult.Ok)
            {
                return rootResult;
            }

            GitCommandResult statusResult = RunWindowsGit(
                workingPath,
                new[] { "--no-optional-locks", "-c", "core.quotepath=false", "status", "--porcelain=v1", "-b", $"--untracked-files={resolvedUntrackedFilesMode}" });
            if (!statusResult.Ok)
            {
                return statusResult;
            }

            StringBuilder output = new();
            AppendSnapshotSection(output, RootMarker, rootResult.Output);
            AppendSnapshotSection(output, StatusMarker, statusResult.Output);
            if (includeNumStat)
            {
                GitCommandResult numStatResult = RunWindowsGit(
                    workingPath,
                    new[] { "--no-optional-locks", "-c", "core.quotepath=false", "diff", "--numstat" });
                if (!numStatResult.Ok)
                {
                    return numStatResult;
                }

                AppendSnapshotSection(output, NumStatMarker, numStatResult.Output);
            }
            else
            {
                AppendSnapshotSection(output, NumStatMarker, string.Empty);
            }

            return new GitCommandResult(true, output.ToString().TrimEnd(), string.Empty);
        }

        private static GitCommandResult RunWindowsGitStatusOnly(string workingPath)
        {
            return RunWindowsGit(
                workingPath,
                new[]
                {
                    "--no-optional-locks",
                    "-c",
                    "core.quotepath=false",
                    "status",
                    "--porcelain=v1",
                    "-b",
                    "--untracked-files=normal",
                    "--ignore-submodules=all",
                    "--no-renames",
                });
        }

        private static GitThreadSnapshot TryGetCachedSnapshot(string normalizedPath)
        {
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                return null;
            }

            lock (SnapshotCacheLock)
            {
                PruneSnapshotCache(DateTimeOffset.UtcNow);
                if (!SnapshotCache.TryGetValue(normalizedPath, out CachedGitSnapshotEntry entry))
                {
                    return null;
                }

                return CloneSnapshot(entry.Snapshot);
            }
        }

        private static void StoreCachedSnapshot(string normalizedPath, GitThreadSnapshot snapshot)
        {
            if (string.IsNullOrWhiteSpace(normalizedPath) ||
                snapshot is null ||
                !string.IsNullOrWhiteSpace(snapshot.Error))
            {
                return;
            }

            lock (SnapshotCacheLock)
            {
                DateTimeOffset capturedAt = DateTimeOffset.UtcNow;
                PruneSnapshotCache(capturedAt);
                SnapshotCache[normalizedPath] = new CachedGitSnapshotEntry
                {
                    CapturedAt = capturedAt,
                    Snapshot = CloneSnapshot(snapshot),
                };
                PruneSnapshotCache(capturedAt);
            }
        }

        private static void UpdateCachedSnapshot(string normalizedPath, Action<GitThreadSnapshot> applyUpdate)
        {
            if (string.IsNullOrWhiteSpace(normalizedPath) || applyUpdate is null)
            {
                return;
            }

            lock (SnapshotCacheLock)
            {
                DateTimeOffset capturedAt = DateTimeOffset.UtcNow;
                PruneSnapshotCache(capturedAt);
                if (!SnapshotCache.TryGetValue(normalizedPath, out CachedGitSnapshotEntry entry))
                {
                    return;
                }

                GitThreadSnapshot snapshot = entry.Snapshot;
                applyUpdate(snapshot);
                SnapshotCache[normalizedPath] = new CachedGitSnapshotEntry
                {
                    CapturedAt = capturedAt,
                    Snapshot = snapshot,
                };
                PruneSnapshotCache(capturedAt);
            }
        }

        private static void PruneSnapshotCache(DateTimeOffset now)
        {
            List<string> expiredKeys = null;
            string oldestKey = null;
            DateTimeOffset oldestCapturedAt = DateTimeOffset.MaxValue;

            foreach ((string key, CachedGitSnapshotEntry entry) in SnapshotCache)
            {
                if (now - entry.CapturedAt > SnapshotCacheMaxAge)
                {
                    expiredKeys ??= new List<string>();
                    expiredKeys.Add(key);
                    continue;
                }

                if (entry.CapturedAt < oldestCapturedAt)
                {
                    oldestCapturedAt = entry.CapturedAt;
                    oldestKey = key;
                }
            }

            if (expiredKeys is not null)
            {
                foreach (string expiredKey in expiredKeys)
                {
                    SnapshotCache.Remove(expiredKey);
                }
            }

            while (SnapshotCache.Count > MaxSnapshotCacheEntries &&
                !string.IsNullOrWhiteSpace(oldestKey))
            {
                SnapshotCache.Remove(oldestKey);
                oldestKey = null;
                oldestCapturedAt = DateTimeOffset.MaxValue;
                foreach ((string key, CachedGitSnapshotEntry entry) in SnapshotCache)
                {
                    if (entry.CapturedAt < oldestCapturedAt)
                    {
                        oldestCapturedAt = entry.CapturedAt;
                        oldestKey = key;
                    }
                }
            }
        }

        private static GitCommandResult RunWindowsGitDiff(string workingPath, string selectedPath, string normalizedStatus)
        {
            if (string.IsNullOrWhiteSpace(selectedPath))
            {
                return new GitCommandResult(true, string.Empty, string.Empty);
            }

            if (string.Equals(normalizedStatus, "??", StringComparison.Ordinal))
            {
                string fullPath = Path.Combine(workingPath, selectedPath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(fullPath))
                {
                    return new GitCommandResult(true, string.Empty, string.Empty);
                }

                GitCommandResult untrackedDiff = RunWindowsGit(
                    workingPath,
                    new[] { "--no-optional-locks", "-c", "core.quotepath=false", "diff", "--no-index", "--", "NUL", selectedPath },
                    successExitCodes: new[] { 0, 1 });
                return untrackedDiff.Ok
                    ? new GitCommandResult(true, untrackedDiff.Output, untrackedDiff.Error)
                    : untrackedDiff;
            }

            if (normalizedStatus.IndexOf('A') >= 0 ||
                normalizedStatus.IndexOf('R') >= 0 ||
                normalizedStatus.IndexOf('C') >= 0)
            {
                GitCommandResult stagedDiff = RunWindowsGit(
                    workingPath,
                    new[] { "--no-optional-locks", "-c", "core.quotepath=false", "diff", "--cached", "--", selectedPath });
                if (stagedDiff.Ok && !string.IsNullOrWhiteSpace(stagedDiff.Output))
                {
                    return stagedDiff;
                }
            }

            GitCommandResult headDiff = RunWindowsGit(
                workingPath,
                new[] { "--no-optional-locks", "-c", "core.quotepath=false", "diff", "HEAD", "--", selectedPath });
            if (headDiff.Ok && !string.IsNullOrWhiteSpace(headDiff.Output))
            {
                return headDiff;
            }

            GitCommandResult cachedDiff = RunWindowsGit(
                workingPath,
                new[] { "--no-optional-locks", "-c", "core.quotepath=false", "diff", "--cached", "--", selectedPath });
            if (cachedDiff.Ok && !string.IsNullOrWhiteSpace(cachedDiff.Output))
            {
                return cachedDiff;
            }

            GitCommandResult workingTreeDiff = RunWindowsGit(
                workingPath,
                new[] { "--no-optional-locks", "-c", "core.quotepath=false", "diff", "--", selectedPath });
            if (workingTreeDiff.Ok || string.IsNullOrWhiteSpace(workingTreeDiff.Error))
            {
                return workingTreeDiff;
            }

            return new GitCommandResult(false, string.Empty, NormalizeGitError(workingTreeDiff.Error));
        }

        private static void AppendSnapshotSection(StringBuilder builder, string marker, string output)
        {
            builder.AppendLine(marker);
            if (!string.IsNullOrWhiteSpace(output))
            {
                builder.AppendLine(output.TrimEnd());
            }
        }

        private static GitCommandResult RunWindowsGit(
            string workingPath,
            IReadOnlyList<string> arguments,
            IReadOnlyCollection<int> successExitCodes = null)
        {
            GitBinaryCommandResult result = RunWindowsGitBinary(workingPath, arguments, successExitCodes);
            if (!result.Ok)
            {
                return new GitCommandResult(false, DecodeTextBytes(result.Output), result.Error);
            }

            return new GitCommandResult(true, DecodeTextBytes(result.Output).TrimEnd(), string.Empty);
        }

        private static GitBinaryCommandResult RunWindowsGitBinary(
            string workingPath,
            IReadOnlyList<string> arguments,
            IReadOnlyCollection<int> successExitCodes = null)
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = "git.exe",
                WorkingDirectory = workingPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using Process process = StartProcess(startInfo, out string startError);
            if (process is null)
            {
                return new GitBinaryCommandResult(false, Array.Empty<byte>(), startError);
            }

            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            using MemoryStream stdoutStream = new();
            Task stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(stdoutStream);

            if (!process.WaitForExit(4000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                WaitForStreamTasks(stdoutTask, stderrTask);
                return new GitBinaryCommandResult(false, stdoutStream.ToArray(), "Git command timed out.");
            }

            WaitForStreamTasks(stdoutTask, stderrTask);
            string stderr = GetTaskResult(stderrTask)?.Trim() ?? string.Empty;
            byte[] stdout = stdoutStream.ToArray();
            IReadOnlyCollection<int> acceptedExitCodes = successExitCodes ?? new[] { 0 };
            if (acceptedExitCodes.Contains(process.ExitCode))
            {
                return new GitBinaryCommandResult(true, stdout, stderr);
            }

            string stdoutText = DecodeTextBytes(stdout).Trim();
            return new GitBinaryCommandResult(
                false,
                stdout,
                string.IsNullOrWhiteSpace(stderr) ? stdoutText : stderr);
        }

        private static Process StartProcess(ProcessStartInfo startInfo, out string error)
        {
            try
            {
                error = null;
                return Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                error = $"Windows git unavailable: {ex.Message}";
                return null;
            }
        }

        private static void WaitForStreamTasks(Task stdoutTask, Task<string> stderrTask)
        {
            try
            {
                stdoutTask?.GetAwaiter().GetResult();
            }
            catch
            {
            }

            try
            {
                stderrTask?.GetAwaiter().GetResult();
            }
            catch
            {
            }
        }

        private static string GetTaskResult(Task<string> task)
        {
            try
            {
                return task?.GetAwaiter().GetResult();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static GitCommandResult RunWslShell(string workingPath, string shellScript)
        {
            string linuxPath = ShellProfiles.ResolveDisplayPath(workingPath, ShellProfileIds.Wsl);
            ProcessStartInfo startInfo = new()
            {
                FileName = "wsl.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("--cd");
            startInfo.ArgumentList.Add(linuxPath);
            startInfo.ArgumentList.Add("sh");
            startInfo.ArgumentList.Add("-lc");
            startInfo.ArgumentList.Add(shellScript);

            using Process process = Process.Start(startInfo);
            if (process is null)
            {
                return new GitCommandResult(false, string.Empty, "Failed to start git process.");
            }

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(4000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                return new GitCommandResult(false, stdout, "Git command timed out.");
            }

            return process.ExitCode == 0
                ? new GitCommandResult(true, stdout.TrimEnd(), stderr.Trim())
                : new GitCommandResult(false, stdout.TrimEnd(), string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim());
        }

        private readonly record struct GitCommandResult(bool Ok, string Output, string Error);
        private readonly record struct GitBinaryCommandResult(bool Ok, byte[] Output, string Error);
    }
}
