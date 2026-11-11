using System;
using System.Collections.Generic;
using System.IO;

namespace SubtitleForge.Services
{
    /// <summary>
    /// Finds ffmpeg / ffprobe: configured path, then well-known roots, then PATH.
    /// </summary>
    public static class FfmpegLocator
    {
        private static readonly Dictionary<string, string?> Cache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        private static readonly object CacheLock = new object();

        public static string? Resolve(string toolName, string? configuredPath)
        {
            string key = toolName + "|" + (configuredPath ?? string.Empty);
            lock (CacheLock)
            {
                if (Cache.TryGetValue(key, out string? cached))
                {
                    return cached;
                }
            }

            string? resolved = ResolveCore(toolName, configuredPath);
            lock (CacheLock)
            {
                Cache[key] = resolved;
            }

            return resolved;
        }

        public static void Invalidate()
        {
            lock (CacheLock)
            {
                Cache.Clear();
            }
        }

        private static string? ResolveCore(string toolName, string? configuredPath)
        {
            string fileName = toolName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? toolName : toolName + ".exe";

            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                string configured = configuredPath.Trim().Trim('"');
                if (File.Exists(configured))
                {
                    // Accept a full path to either ffmpeg.exe or its folder.
                    string leaf = Path.GetFileName(configured);
                    if (leaf.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                    {
                        return Path.GetFullPath(configured);
                    }

                    string sibling = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configured)) ?? string.Empty, fileName);
                    if (File.Exists(sibling))
                    {
                        return sibling;
                    }
                }

                if (Directory.Exists(configured))
                {
                    string inFolder = Path.Combine(configured, fileName);
                    if (File.Exists(inFolder))
                    {
                        return Path.GetFullPath(inFolder);
                    }
                }
            }

            foreach (string root in GetWellKnownRoots())
            {
                try
                {
                    string candidate = Path.Combine(root, fileName);
                    if (File.Exists(candidate))
                    {
                        return Path.GetFullPath(candidate);
                    }
                }
                catch (Exception)
                {
                    // Malformed root (bad drive letter, etc.) — keep looking.
                }
            }

            string? pathVariable = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathVariable))
            {
                foreach (string folder in pathVariable.Split(Path.PathSeparator))
                {
                    if (string.IsNullOrWhiteSpace(folder))
                    {
                        continue;
                    }

                    try
                    {
                        string candidate = Path.Combine(folder.Trim(), fileName);
                        if (File.Exists(candidate))
                        {
                            return Path.GetFullPath(candidate);
                        }
                    }
                    catch (Exception)
                    {
                        // Skip unusable PATH entries.
                    }
                }
            }

            return null;
        }

        private static IEnumerable<string> GetWellKnownRoots()
        {
            List<string> roots = new List<string>();
            roots.Add(@"C:\ffmpeg\bin");
            roots.Add(@"C:\ffmpeg");
            roots.Add(@"C:\tools\ffmpeg\bin");
            roots.Add(@"C:\ProgramData\chocolatey\bin");

            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrEmpty(programFiles))
            {
                roots.Add(Path.Combine(programFiles, "ffmpeg", "bin"));
            }

            string localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localApplicationData))
            {
                roots.Add(Path.Combine(localApplicationData, "Microsoft", "WinGet", "Links"));
                roots.Add(Path.Combine(localApplicationData, "Programs", "ffmpeg", "bin"));
            }

            return roots;
        }
    }
}
