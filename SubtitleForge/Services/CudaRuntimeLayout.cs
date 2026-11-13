using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SubtitleForge.Services
{
    /// <summary>
    /// Puts the CUDA whisper natives where whisper.net actually looks for them.
    /// </summary>
    /// <remarks>
    /// whisper.net only probes <c>runtimes\cuda12\win-x64</c>. A <c>ggml-cuda-whisper.dll</c> dropped into
    /// the app folder is invisible to it and the process silently runs on the CPU, so this moves the
    /// file into that folder and fills the folder with the CPU companions it also needs
    /// (<c>whisper.dll</c> and the ggml base / cpu / dispatcher libraries), which the shipped build
    /// already carries in <c>runtimes\win-x64</c>. Everything is idempotent and logged.
    /// </remarks>
    public static class CudaRuntimeLayout
    {
        private const string CudaRuntimeFileName = "ggml-cuda-whisper.dll";
        private const string WhisperFileName = "whisper.dll";

        /// <summary>Files the CUDA runtime folder needs; the first three come from the CPU runtime folder.</summary>
        private static readonly string[] CompanionFiles = new string[]
        {
            "whisper.dll",
            "ggml-whisper.dll",
            "ggml-base-whisper.dll",
            "ggml-cpu-whisper.dll"
        };

        public static string CudaRuntimeFolder
        {
            get { return Path.Combine(AppContext.BaseDirectory, "runtimes", "cuda12", "win-x64"); }
        }

        private static string CudaRuntimeFileInAppFolder
        {
            get { return Path.Combine(AppContext.BaseDirectory, CudaRuntimeFileName); }
        }

        private static string CudaRuntimeFileInRuntimeFolder
        {
            get { return Path.Combine(CudaRuntimeFolder, CudaRuntimeFileName); }
        }

        private static string CpuRuntimeFolder
        {
            get { return Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64"); }
        }

        /// <summary>
        /// True when the CUDA runtime is already in the folder whisper.net probes.
        /// </summary>
        public static bool IsRuntimeUsable()
        {
            try
            {
                return File.Exists(CudaRuntimeFileInRuntimeFolder)
                    && File.Exists(Path.Combine(CudaRuntimeFolder, WhisperFileName));
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// True when there is work to do: the CUDA library exists somewhere it is not seen, or the
        /// runtime folder is missing files it needs.
        /// </summary>
        public static bool NeedsMove()
        {
            try
            {
                if (IsRuntimeUsable())
                {
                    return false;
                }

                return File.Exists(CudaRuntimeFileInAppFolder) || File.Exists(CudaRuntimeFileInRuntimeFolder);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Moves the CUDA library into <c>runtimes\cuda12\win-x64</c> and copies the CPU companions next
        /// to it. Returns true when the folder ended up complete; <paramref name="report"/> always says
        /// what was done, and is empty when there was nothing to do.
        /// </summary>
        public static bool EnsureRuntimeFolder(out string report)
        {
            StringBuilder log = new StringBuilder();
            bool moved = false;

            try
            {
                if (IsRuntimeUsable())
                {
                    report = string.Empty;
                    return true;
                }

                string? source = FindCudaRuntimeFile();
                if (source == null)
                {
                    report = string.Empty;
                    return false;
                }

                Directory.CreateDirectory(CudaRuntimeFolder);
                log.AppendLine("runtime folder: " + CudaRuntimeFolder);

                if (!string.Equals(source, CudaRuntimeFileInRuntimeFolder, StringComparison.OrdinalIgnoreCase))
                {
                    if (MoveFile(source, CudaRuntimeFileInRuntimeFolder, log))
                    {
                        moved = true;
                        log.AppendLine("moved: " + source);
                    }
                }

                foreach (string companion in CompanionFiles)
                {
                    string target = Path.Combine(CudaRuntimeFolder, companion);
                    if (File.Exists(target))
                    {
                        continue;
                    }

                    string origin = Path.Combine(CpuRuntimeFolder, companion);
                    if (!File.Exists(origin))
                    {
                        log.AppendLine("missing: " + companion + " is neither in the runtime folder nor in " + CpuRuntimeFolder);
                        continue;
                    }

                    if (CopyFile(origin, target, log))
                    {
                        log.AppendLine("copied: " + companion + " from " + CpuRuntimeFolder);
                    }
                }

                bool ready = IsRuntimeUsable();
                log.AppendLine(ready
                    ? "done: the CUDA runtime is in place."
                    : "done: the folder is still incomplete (see the missing lines above).");

                if (moved)
                {
                    log.AppendLine("Restart SubtitleForge: whisper.net loads its native library once per process.");
                }

                report = log.ToString();
                return ready;
            }
            catch (Exception exception)
            {
                CrashLog.Write(exception, "CudaRuntimeLayout.EnsureRuntimeFolder");
                report = "the runtime folder could not be prepared: " + exception.Message;
                return false;
            }
        }

        // ── File helpers

        private static string? FindCudaRuntimeFile()
        {
            string[] candidates = new string[]
            {
                CudaRuntimeFileInRuntimeFolder,
                CudaRuntimeFileInAppFolder,
                Path.Combine(AppContext.BaseDirectory, "runtimes", "cuda", "win-x64", CudaRuntimeFileName)
            };

            foreach (string candidate in candidates)
            {
                try
                {
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (Exception)
                {
                    // An unreadable entry simply offers no candidate.
                }
            }

            return null;
        }

        private static bool MoveFile(string source, string target, StringBuilder log)
        {
            try
            {
                if (File.Exists(target))
                {
                    File.Delete(target);
                }

                File.Move(source, target);
                return true;
            }
            catch (Exception exception)
            {
                // A folder the user cannot write to is likely a read-only install; fall back to a copy
                // so the original download still works.
                log.AppendLine("could not move (" + exception.GetType().Name + "), trying a copy instead");
                return CopyFile(source, target, log);
            }
        }

        private static bool CopyFile(string source, string target, StringBuilder log)
        {
            try
            {
                File.Copy(source, target, false);
                return true;
            }
            catch (Exception exception)
            {
                CrashLog.Write(exception, "CudaRuntimeLayout.CopyFile");
                log.AppendLine("failed: " + Path.GetFileName(target) + " (" + exception.GetType().Name + ")");
                return false;
            }
        }
    }
}
