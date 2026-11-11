using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace SubtitleForge.Services
{
    /// <summary>
    /// Append-only crash log at %LocalAppData%\SubtitleForge\crash.log.
    /// Every entry starts with the build sha so a report can be tied to a commit.
    /// </summary>
    internal static class CrashLog
    {
        private static readonly object WriteLock = new object();

        public static string LogPath
        {
            get
            {
                string folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SubtitleForge");
                return Path.Combine(folder, "crash.log");
            }
        }

        public static void Write(Exception exception, string source)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append("=== ")
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
                .Append(" build: ")
                .Append(BuildInfo.Sha)
                .Append(" source: ")
                .Append(source)
                .AppendLine();
            builder.AppendLine(exception.ToString());
            builder.AppendLine();

            Append(builder.ToString());
        }

        public static void WriteNote(string note)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append("--- ")
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
                .Append(" build: ")
                .Append(BuildInfo.Sha)
                .Append(' ')
                .AppendLine(note);
            Append(builder.ToString());
        }

        private static void Append(string content)
        {
            try
            {
                lock (WriteLock)
                {
                    string path = LogPath;
                    string? folder = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
                    {
                        Directory.CreateDirectory(folder);
                    }

                    File.AppendAllText(path, content, new UTF8Encoding(false));
                }
            }
            catch (Exception)
            {
                // Logging must never take the app down.
            }
        }
    }
}
