using Microsoft.VisualBasic.FileIO;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.DataProtection;
using Windows.Storage.Streams;

namespace SelfContainedDeployment.Browser
{
    internal sealed class BrowserCredentialImportResult
    {
        public bool Ok { get; init; }

        public int ImportedCount { get; init; }

        public string Message { get; init; }
    }

    internal sealed class BrowserCredentialMatch
    {
        public string Id { get; init; }

        public string Name { get; init; }

        public string Host { get; init; }

        public string Url { get; init; }

        public string Username { get; init; }

        public string Password { get; init; }

        public string Note { get; init; }
    }

    internal sealed class BrowserCredentialSummary
    {
        public string Id { get; init; }

        public string Name { get; init; }

        public string Host { get; init; }

        public string Url { get; init; }

        public string Username { get; init; }

        public string Note { get; init; }
    }

    internal static class BrowserCredentialStore
    {
        private const string StoreFileName = "browser-credentials.dat";
        private static readonly object Sync = new();

        private sealed class BrowserCredentialEntry
        {
            public string Id { get; set; }

            public string Name { get; set; }

            public string Url { get; set; }

            public string Host { get; set; }

            public string Username { get; set; }

            public string Password { get; set; }

            public string Note { get; set; }
        }

        private sealed class BrowserCredentialEnvelope
        {
            public DateTimeOffset ImportedAtUtc { get; set; }

            public string SourcePath { get; set; }

            public List<BrowserCredentialEntry> Entries { get; set; } = new();
        }

        public static BrowserCredentialImportResult ImportGooglePasswordsCsv(string csvPath)
        {
            if (string.IsNullOrWhiteSpace(csvPath))
            {
                return new BrowserCredentialImportResult
                {
                    Ok = false,
                    Message = "CSV path is required.",
                };
            }

            if (!File.Exists(csvPath))
            {
                return new BrowserCredentialImportResult
                {
                    Ok = false,
                    Message = $"CSV file '{csvPath}' was not found.",
                };
            }

            try
            {
                List<BrowserCredentialEntry> entries = ParseGooglePasswordsCsv(csvPath);
                if (entries.Count == 0)
                {
                    return new BrowserCredentialImportResult
                    {
                        Ok = false,
                        Message = "No importable web credentials were found in the CSV.",
                    };
                }

                BrowserCredentialEnvelope envelope = new()
                {
                    ImportedAtUtc = DateTimeOffset.UtcNow,
                    SourcePath = csvPath,
                    Entries = entries,
                };

                SaveEnvelope(envelope);

                return new BrowserCredentialImportResult
                {
                    Ok = true,
                    ImportedCount = entries.Count,
                    Message = $"Imported {entries.Count} browser credential{(entries.Count == 1 ? string.Empty : "s")}.",
                };
            }
            catch (Exception ex)
            {
                return new BrowserCredentialImportResult
                {
                    Ok = false,
                    Message = ex.Message,
                };
            }
        }

        public static BrowserCredentialMatch ResolveForUri(string uri)
        {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out Uri targetUri) ||
                !(targetUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                  targetUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            BrowserCredentialEnvelope envelope = LoadEnvelope();
            if (envelope.Entries.Count == 0)
            {
                return null;
            }

            string host = NormalizeHost(targetUri.Host);
            BrowserCredentialEntry match = envelope.Entries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Password))
                .OrderByDescending(entry => entry.Host.Equals(host, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(entry => host.EndsWith("." + entry.Host, StringComparison.OrdinalIgnoreCase) || string.Equals(entry.Host, host, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(entry => entry.Url?.Length ?? 0)
                .FirstOrDefault(entry =>
                {
                    if (string.IsNullOrWhiteSpace(entry.Host))
                    {
                        return false;
                    }

                    if (string.Equals(entry.Host, host, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    return host.EndsWith("." + entry.Host, StringComparison.OrdinalIgnoreCase);
                });

            if (match is null)
            {
                return null;
            }

            return new BrowserCredentialMatch
            {
                Id = match.Id,
                Name = match.Name,
                Host = match.Host,
                Url = match.Url,
                Username = match.Username,
                Password = match.Password,
                Note = match.Note,
            };
        }

        public static IReadOnlyList<BrowserCredentialMatch> ResolveMatchesForUri(string uri)
        {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out Uri targetUri) ||
                !(targetUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                  targetUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            {
                return Array.Empty<BrowserCredentialMatch>();
            }

            string host = NormalizeHost(targetUri.Host);
            return LoadEnvelope().Entries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Password))
                .Where(entry =>
                    !string.IsNullOrWhiteSpace(entry.Host) &&
                    (string.Equals(entry.Host, host, StringComparison.OrdinalIgnoreCase) ||
                     host.EndsWith("." + entry.Host, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(entry => string.Equals(entry.Host, host, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(entry => entry.Url?.Length ?? 0)
                .Select(entry => new BrowserCredentialMatch
                {
                    Id = entry.Id,
                    Name = entry.Name,
                    Host = entry.Host,
                    Url = entry.Url,
                    Username = entry.Username,
                    Password = entry.Password,
                    Note = entry.Note,
                })
                .ToList();
        }

        public static int GetCredentialCount()
        {
            return LoadEnvelope().Entries.Count;
        }

        public static IReadOnlyList<BrowserCredentialSummary> GetCredentialSummaries()
        {
            return LoadEnvelope().Entries
                .OrderBy(entry => entry.Host, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Username, StringComparer.OrdinalIgnoreCase)
                .Select(entry => new BrowserCredentialSummary
                {
                    Id = entry.Id,
                    Name = entry.Name,
                    Host = entry.Host,
                    Url = entry.Url,
                    Username = entry.Username,
                    Note = entry.Note,
                })
                .ToList();
        }

        public static bool DeleteCredential(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return false;
            }

            lock (Sync)
            {
                BrowserCredentialEnvelope envelope = LoadEnvelopeCore();
                int removed = envelope.Entries.RemoveAll(entry => string.Equals(entry.Id, id, StringComparison.Ordinal));
                if (removed == 0)
                {
                    return false;
                }

                SaveEnvelopeCore(envelope);
                return true;
            }
        }

        public static int ClearCredentials()
        {
            lock (Sync)
            {
                BrowserCredentialEnvelope envelope = LoadEnvelopeCore();
                int removed = envelope.Entries.Count;
                envelope.Entries.Clear();
                envelope.ImportedAtUtc = DateTimeOffset.UtcNow;
                envelope.SourcePath = string.Empty;
                SaveEnvelopeCore(envelope);
                return removed;
            }
        }

        private static List<BrowserCredentialEntry> ParseGooglePasswordsCsv(string csvPath)
        {
            List<BrowserCredentialEntry> entries = new();
            using TextFieldParser parser = new(csvPath)
            {
                TextFieldType = FieldType.Delimited,
                HasFieldsEnclosedInQuotes = true,
                TrimWhiteSpace = false,
            };
            parser.SetDelimiters(",");

            if (parser.EndOfData)
            {
                return entries;
            }

            string[] header = parser.ReadFields() ?? Array.Empty<string>();
            Dictionary<string, int> columnIndex = header
                .Select((name, index) => new { Name = (name ?? string.Empty).Trim().ToLowerInvariant(), Index = index })
                .Where(item => !string.IsNullOrWhiteSpace(item.Name))
                .GroupBy(item => item.Name)
                .ToDictionary(group => group.Key, group => group.First().Index, StringComparer.OrdinalIgnoreCase);

            while (!parser.EndOfData)
            {
                string[] row = parser.ReadFields() ?? Array.Empty<string>();

                string url = ReadColumn(row, columnIndex, "url");
                if (!Uri.TryCreate(url, UriKind.Absolute, out Uri parsedUri) ||
                    !(parsedUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                      parsedUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                string password = ReadColumn(row, columnIndex, "password");
                if (string.IsNullOrWhiteSpace(password))
                {
                    continue;
                }

                entries.Add(new BrowserCredentialEntry
                {
                    Id = BuildCredentialId(url, ReadColumn(row, columnIndex, "username"), password),
                    Name = ReadColumn(row, columnIndex, "name"),
                    Url = url,
                    Host = NormalizeHost(parsedUri.Host),
                    Username = ReadColumn(row, columnIndex, "username"),
                    Password = password,
                    Note = ReadColumn(row, columnIndex, "note"),
                });
            }

            return entries
                .GroupBy(entry => $"{entry.Host}\n{entry.Url}\n{entry.Username}\n{entry.Password}", StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
        }

        private static string ReadColumn(string[] row, IReadOnlyDictionary<string, int> columnIndex, string columnName)
        {
            if (!columnIndex.TryGetValue(columnName, out int index) || index < 0 || index >= row.Length)
            {
                return string.Empty;
            }

            return row[index]?.Trim() ?? string.Empty;
        }

        private static string NormalizeHost(string host)
        {
            return (host ?? string.Empty).Trim().Trim('.').ToLowerInvariant();
        }

        private static BrowserCredentialEnvelope LoadEnvelope()
        {
            lock (Sync)
            {
                return LoadEnvelopeCore();
            }
        }

        private static void SaveEnvelope(BrowserCredentialEnvelope envelope)
        {
            lock (Sync)
            {
                SaveEnvelopeCore(envelope);
            }
        }

        private static BrowserCredentialEnvelope LoadEnvelopeCore()
        {
            string storePath = ResolveStorePath();
            if (!File.Exists(storePath))
            {
                return new BrowserCredentialEnvelope();
            }

            byte[] protectedBytes = File.ReadAllBytes(storePath);
            byte[] rawBytes = Unprotect(protectedBytes);
            BrowserCredentialEnvelope envelope = JsonSerializer.Deserialize<BrowserCredentialEnvelope>(rawBytes) ?? new BrowserCredentialEnvelope();

            bool updated = false;
            foreach (BrowserCredentialEntry entry in envelope.Entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Id))
                {
                    entry.Id = BuildCredentialId(entry.Url, entry.Username, entry.Password);
                    updated = true;
                }
            }

            if (updated)
            {
                SaveEnvelopeCore(envelope);
            }

            return envelope;
        }

        private static void SaveEnvelopeCore(BrowserCredentialEnvelope envelope)
        {
            string storePath = ResolveStorePath();
            Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);

            byte[] rawBytes = JsonSerializer.SerializeToUtf8Bytes(envelope, new JsonSerializerOptions
            {
                WriteIndented = false,
            });
            byte[] protectedBytes = Protect(rawBytes);
            File.WriteAllBytes(storePath, protectedBytes);
        }

        private static string BuildCredentialId(string url, string username, string password)
        {
            string value = $"{url ?? string.Empty}\n{username ?? string.Empty}\n{password ?? string.Empty}";
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            byte[] hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash[..12]).ToLowerInvariant();
        }

        private static byte[] Protect(byte[] rawBytes)
        {
            IBuffer buffer = CryptographicBuffer.CreateFromByteArray(rawBytes);
            DataProtectionProvider provider = new("LOCAL=user");
            IBuffer protectedBuffer = provider.ProtectAsync(buffer).AsTask().GetAwaiter().GetResult();
            CryptographicBuffer.CopyToByteArray(protectedBuffer, out byte[] protectedBytes);
            return protectedBytes;
        }

        private static byte[] Unprotect(byte[] protectedBytes)
        {
            IBuffer buffer = CryptographicBuffer.CreateFromByteArray(protectedBytes);
            DataProtectionProvider provider = new();
            IBuffer rawBuffer = provider.UnprotectAsync(buffer).AsTask().GetAwaiter().GetResult();
            CryptographicBuffer.CopyToByteArray(rawBuffer, out byte[] rawBytes);
            return rawBytes;
        }

        private static string ResolveStorePath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinMux",
                StoreFileName);
        }
    }
}
