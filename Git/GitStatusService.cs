using SelfContainedDeployment.Shell;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace SelfContainedDeployment.Git
{
    internal sealed class GitChangedFile
    {
        public string Status { get; set; }

        public string Path { get; set; }

        public int AddedLines { get; set; }

        public int RemovedLines { get; set; }

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

    internal sealed class GitThreadSnapshot
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
        private const string DiffStatMarker = "__WINMUX_DIFFSTAT__";
        private const string NumStatMarker = "__WINMUX_NUMSTAT__";

        public static GitThreadSnapshot Capture(string workingPath, string selectedPath = null)
        {
            string normalizedPath = ShellProfiles.NormalizeProjectPath(workingPath);
            GitThreadSnapshot snapshot = new()
            {
                WorktreePath = normalizedPath,
            };

            GitCommandResult metadataResult = RunGitSnapshot(normalizedPath);
            if (!metadataResult.Ok)
            {
                snapshot.Error = NormalizeGitError(metadataResult.Error);
                return snapshot;
            }

            ParseMetadataSnapshot(snapshot, metadataResult.Output);
            snapshot.StatusSummary = BuildStatusSummary(snapshot.ChangedFiles);

            string resolvedSelectedPath = selectedPath;
            bool selectedPathExists = !string.IsNullOrWhiteSpace(resolvedSelectedPath) &&
                snapshot.ChangedFiles.Any(file => string.Equals(file.Path, resolvedSelectedPath, StringComparison.Ordinal));
            if (!selectedPathExists)
            {
                resolvedSelectedPath = snapshot.ChangedFiles.FirstOrDefault()?.Path;
            }

            snapshot.SelectedPath = resolvedSelectedPath;
            if (!string.IsNullOrWhiteSpace(resolvedSelectedPath))
            {
                GitChangedFile changedFile = snapshot.ChangedFiles.FirstOrDefault(file => string.Equals(file.Path, resolvedSelectedPath, StringComparison.Ordinal));
                GitCommandResult diffResult = RunGitDiff(normalizedPath, resolvedSelectedPath, changedFile?.Status);
                snapshot.SelectedDiff = diffResult.Ok
                    ? diffResult.Output
                    : NormalizeGitError(diffResult.Error);
            }

            return snapshot;
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

                snapshot.ChangedFiles.Add(new GitChangedFile
                {
                    Status = line[..2].Trim(),
                    Path = NormalizeTrackedPath(line[3..].Trim()),
                });
            }
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
            StringBuilder diffStatOutput = new();
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

                if (string.Equals(line, DiffStatMarker, StringComparison.Ordinal))
                {
                    activeOutput = diffStatOutput;
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
            snapshot.DiffSummary = diffStatOutput.ToString().Trim();
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

        private static GitCommandResult RunGitSnapshot(string workingPath)
        {
            string script = string.Join(" && ", new[]
            {
                $"printf '%s\\n' '{RootMarker}'",
                "git --no-optional-locks rev-parse --show-toplevel",
                $"printf '%s\\n' '{StatusMarker}'",
                "git --no-optional-locks -c core.quotepath=false status --porcelain=v1 -b --untracked-files=all",
                $"printf '%s\\n' '{DiffStatMarker}'",
                "git --no-optional-locks -c core.quotepath=false diff --stat --compact-summary",
                $"printf '%s\\n' '{NumStatMarker}'",
                "git --no-optional-locks -c core.quotepath=false diff --numstat",
            });

            return RunWslShell(workingPath, script);
        }

        private static GitCommandResult RunGitDiff(string workingPath, string selectedPath, string status)
        {
            string normalizedStatus = status?.Trim() ?? string.Empty;
            selectedPath = NormalizeTrackedPath(selectedPath);
            if (string.Equals(normalizedStatus, "??", StringComparison.Ordinal))
            {
                const string untrackedScript = "if [ -e \"$1\" ]; then git --no-optional-locks -c core.quotepath=false diff --no-index -- /dev/null -- \"$1\"; code=$?; if [ $code -le 1 ]; then exit 0; fi; exit $code; fi";
                return RunWslShell(workingPath, untrackedScript, selectedPath);
            }

            if (normalizedStatus.IndexOf('A') >= 0 ||
                normalizedStatus.IndexOf('R') >= 0 ||
                normalizedStatus.IndexOf('C') >= 0)
            {
                const string stagedFirstScript = "git --no-optional-locks -c core.quotepath=false diff --cached -- \"$1\"";
                GitCommandResult stagedDiff = RunWslShell(workingPath, stagedFirstScript, selectedPath);
                if (stagedDiff.Ok && !string.IsNullOrWhiteSpace(stagedDiff.Output))
                {
                    return stagedDiff;
                }
            }

            const string headScript = "git --no-optional-locks -c core.quotepath=false diff HEAD -- \"$1\"";
            GitCommandResult headDiff = RunWslShell(workingPath, headScript, selectedPath);
            if (headDiff.Ok && !string.IsNullOrWhiteSpace(headDiff.Output))
            {
                return headDiff;
            }

            const string cachedScript = "git --no-optional-locks -c core.quotepath=false diff --cached -- \"$1\"";
            GitCommandResult cachedDiff = RunWslShell(workingPath, cachedScript, selectedPath);
            if (cachedDiff.Ok && !string.IsNullOrWhiteSpace(cachedDiff.Output))
            {
                return cachedDiff;
            }

            const string workingTreeScript = "git --no-optional-locks -c core.quotepath=false diff -- \"$1\"";
            GitCommandResult workingTreeDiff = RunWslShell(workingPath, workingTreeScript, selectedPath);
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

        private static GitCommandResult RunWslShell(string workingPath, string shellScript, string argument = null)
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
            startInfo.ArgumentList.Add("winmux");
            if (!string.IsNullOrWhiteSpace(argument))
            {
                startInfo.ArgumentList.Add(argument);
            }

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
    }
}
