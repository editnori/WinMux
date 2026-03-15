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
        public static GitThreadSnapshot Capture(string workingPath, string selectedPath = null)
        {
            string normalizedPath = ShellProfiles.NormalizeProjectPath(workingPath);
            GitThreadSnapshot snapshot = new()
            {
                WorktreePath = normalizedPath,
            };

            GitCommandResult rootResult = RunGit(normalizedPath, "rev-parse --show-toplevel");
            if (!rootResult.Ok)
            {
                snapshot.Error = NormalizeGitError(rootResult.Error);
                return snapshot;
            }

            snapshot.RepositoryRootPath = rootResult.Output.Trim();

            GitCommandResult statusResult = RunGit(normalizedPath, "status --porcelain=v1 -b --untracked-files=all");
            if (!statusResult.Ok)
            {
                snapshot.Error = NormalizeGitError(statusResult.Error);
                return snapshot;
            }

            ParseStatus(snapshot, statusResult.Output);
            snapshot.StatusSummary = BuildStatusSummary(snapshot.ChangedFiles);
            snapshot.DiffSummary = RunGit(normalizedPath, "diff --stat --compact-summary").Output?.Trim() ?? string.Empty;

            string resolvedSelectedPath = selectedPath;
            if (string.IsNullOrWhiteSpace(resolvedSelectedPath))
            {
                resolvedSelectedPath = snapshot.ChangedFiles.FirstOrDefault()?.Path;
            }

            snapshot.SelectedPath = resolvedSelectedPath;
            if (!string.IsNullOrWhiteSpace(resolvedSelectedPath))
            {
                string escapedPath = EscapeForPosixDoubleQuotes(resolvedSelectedPath);
                GitCommandResult diffResult = RunGit(normalizedPath, $"diff -- \"{escapedPath}\"");
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
                    Path = line[3..].Trim(),
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

        private static GitCommandResult RunGit(string workingPath, string gitArguments)
        {
            string linuxPath = ShellProfiles.ResolveDisplayPath(workingPath, ShellProfileIds.Wsl);
            string script = $"git {gitArguments}";
            ProcessStartInfo startInfo = new()
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -Command \"wsl.exe --cd \\\"{linuxPath}\\\" sh -lc '{EscapeForSingleQuotedPosix(script)}'\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

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

        private static string EscapeForSingleQuotedPosix(string value)
        {
            return (value ?? string.Empty).Replace("'", "'\"'\"'");
        }

        private static string EscapeForPosixDoubleQuotes(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private readonly record struct GitCommandResult(bool Ok, string Output, string Error);
    }
}
