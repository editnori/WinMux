using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace SelfContainedDeployment.Automation
{
    internal sealed class NativeAutomationSessionInfo
    {
        public int Port { get; init; }

        public string Token { get; init; }

        public string SessionFilePath { get; init; }

        public int ProcessId { get; init; }
    }

    internal static class NativeAutomationAccess
    {
        private const string SessionFileName = "automation-session.json";

        public static NativeAutomationSessionInfo CreateSession(int port)
        {
            string token = ResolveToken();
            string sessionFilePath = ResolveSessionFilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(sessionFilePath)!);

            Environment.SetEnvironmentVariable("NATIVE_TERMINAL_AUTOMATION_PORT", port.ToString());
            Environment.SetEnvironmentVariable("WINMUX_AUTOMATION_PORT", port.ToString());
            Environment.SetEnvironmentVariable("NATIVE_TERMINAL_AUTOMATION_TOKEN", token);
            Environment.SetEnvironmentVariable("WINMUX_AUTOMATION_TOKEN", token);

            File.WriteAllText(sessionFilePath, JsonSerializer.Serialize(new
            {
                port,
                token,
                pid = Environment.ProcessId,
                createdAt = DateTimeOffset.UtcNow.ToString("O"),
            }));

            return new NativeAutomationSessionInfo
            {
                Port = port,
                Token = token,
                SessionFilePath = sessionFilePath,
                ProcessId = Environment.ProcessId,
            };
        }

        public static void DeleteSession(NativeAutomationSessionInfo session)
        {
            if (session is null || string.IsNullOrWhiteSpace(session.SessionFilePath) || !File.Exists(session.SessionFilePath))
            {
                return;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(session.SessionFilePath));
                if (document.RootElement.TryGetProperty("pid", out JsonElement pidElement) &&
                    pidElement.ValueKind == JsonValueKind.Number &&
                    pidElement.TryGetInt32(out int pid) &&
                    pid == session.ProcessId)
                {
                    File.Delete(session.SessionFilePath);
                }
            }
            catch
            {
            }
        }

        private static string ResolveToken()
        {
            string configured = Environment.GetEnvironmentVariable("NATIVE_TERMINAL_AUTOMATION_TOKEN");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured.Trim();
            }

            byte[] bytes = new byte[32];
            RandomNumberGenerator.Fill(bytes);
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        private static string ResolveSessionFilePath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinMux",
                SessionFileName);
        }
    }
}
