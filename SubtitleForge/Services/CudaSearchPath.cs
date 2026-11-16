using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace SubtitleForge.Services
{
    /// <summary>
    /// Makes the CUDA 12 toolkit libraries findable at load time.
    /// </summary>
    /// <remarks>
    /// whisper.net decides whether CUDA exists by calling <c>NativeLibrary.TryLoad("cudart64_12")</c>,
    /// which uses the normal Windows search order: the app folder, System32, then PATH. A toolkit
    /// installed into <c>C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.x\bin</c> is on none of
    /// those, so the CUDA runtime is skipped and the CPU library is used without a word. Adding the
    /// toolkit's bin folder to the search path here fixes that, and also lets
    /// <c>ggml-cuda-whisper.dll</c> resolve its own cublas / cudart dependencies.
    /// </remarks>
    public static class CudaSearchPath
    {
        private const string CudartFileName = "cudart64_12.dll";
        private const string CublasFileName = "cublas64_12.dll";
        private const string ToolkitRootFolderName = "NVIDIA GPU Computing Toolkit";
        private const string ToolkitVersionFolderPattern = "v12*";

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetDllDirectory(string? pathName);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr AddDllDirectory(string newDirectory);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetDefaultDllDirectories(int directoryFlags);

        private const int LoadLibrarySearchDefaultDirectories = 0x00001000;
        private const int LoadLibrarySearchUserDirectories = 0x00000400;

        private static readonly string[] LibrariesToPreload = new string[]
        {
            "cudart64_12.dll",
            "cublas64_12.dll",
            "cublasLt64_12.dll"
        };

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string fileName);

        private static readonly List<string> preloaded = new List<string>();

        private static bool applied;
        private static string? appliedFolder;

        /// <summary>
        /// The CUDA runtime libraries this process already has open, newest toolkit first.
        ///
        /// They are loaded by absolute path before anything else asks for them by name, so every
        /// consumer — whisper.net's CUDA probe and ggml-cuda's own imports — ends up on the same
        /// version. A machine with v12.0 and v12.4 installed otherwise mixes them, and a cublas that
        /// does not export what ggml-cuda expects makes the backend module fail to load.
        /// </summary>
        public static List<string> PreloadedLibraries
        {
            get { return new List<string>(preloaded); }
        }

        /// <summary>The folder that was added to the search path, or null when none was usable.</summary>
        public static string? AppliedFolder
        {
            get { return appliedFolder; }
        }

        /// <summary>
        /// Adds the CUDA 12 bin folder to the DLL search path once per process. Safe to call from
        /// anywhere: later calls do nothing.
        /// </summary>
        public static void Apply()
        {
            if (applied)
            {
                return;
            }

            applied = true;

            string? folder = FindUsableBinFolder();
            if (folder == null)
            {
                return;
            }

            try
            {
                // SetDllDirectory is what plain LoadLibrary / NativeLibrary.TryLoad honour for bare
                // library names, which is exactly how whisper.net probes for cudart.
                if (SetDllDirectory(folder))
                {
                    appliedFolder = folder;
                    PreloadRuntimeLibraries(folder);
                    return;
                }

                // Fall back to a user directory registration for loaders that pass the flag.
                SetDefaultDllDirectories(LoadLibrarySearchDefaultDirectories | LoadLibrarySearchUserDirectories);
                if (AddDllDirectory(folder) != IntPtr.Zero)
                {
                    appliedFolder = folder;
                    PreloadRuntimeLibraries(folder);
                    return;
                }

                CrashLog.WriteNote(
                    "The CUDA bin folder could not be added to the DLL search path: " + folder
                    + " (error " + Marshal.GetLastWin32Error().ToString(System.Globalization.CultureInfo.InvariantCulture) + ").");
            }
            catch (Exception exception)
            {
                CrashLog.Write(exception, "CudaSearchPath.Apply");
            }
        }

        /// <summary>
        /// Every CUDA 12 bin folder on this machine, newest first, whether or not it holds the
        /// libraries whisper needs.
        /// </summary>
        public static List<string> CandidateFolders()
        {
            List<string> candidates = new List<string>();

            string? configured = Environment.GetEnvironmentVariable("CUDA_PATH");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                candidates.Add(Path.Combine(configured.Trim(), "bin"));
            }

            string toolkitRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                ToolkitRootFolderName,
                "CUDA");

            try
            {
                if (Directory.Exists(toolkitRoot))
                {
                    string[] versionFolders = Directory.GetDirectories(toolkitRoot, ToolkitVersionFolderPattern);
                    Array.Sort(versionFolders, StringComparer.OrdinalIgnoreCase);
                    for (int index = versionFolders.Length - 1; index >= 0; index--)
                    {
                        candidates.Add(Path.Combine(versionFolders[index], "bin"));
                    }
                }
            }
            catch (Exception)
            {
                // A missing or unreadable toolkit folder simply offers no candidates.
            }

            return candidates;
        }

        /// <summary>
        /// The first folder that actually contains the CUDA runtime whisper links against, preferring
        /// the toolkit CUDA_PATH points at.
        /// </summary>
        public static string? FindUsableBinFolder()
        {
            foreach (string folder in CandidateFolders())
            {
                if (HasRuntimeLibraries(folder))
                {
                    return folder;
                }
            }

            return null;
        }

        private static void PreloadRuntimeLibraries(string folder)
        {
            foreach (string name in LibrariesToPreload)
            {
                string path = Path.Combine(folder, name);
                try
                {
                    if (!File.Exists(path))
                    {
                        continue;
                    }

                    if (LoadLibrary(path) == IntPtr.Zero)
                    {
                        CrashLog.WriteNote("Preloading " + path + " failed (error "
                            + Marshal.GetLastWin32Error().ToString(System.Globalization.CultureInfo.InvariantCulture) + ").");
                        continue;
                    }

                    preloaded.Add(path);
                }
                catch (Exception exception)
                {
                    CrashLog.Write(exception, "CudaSearchPath.Preload");
                }
            }
        }

        public static bool HasRuntimeLibraries(string folder)
        {
            try
            {
                return Directory.Exists(folder)
                    && File.Exists(Path.Combine(folder, CudartFileName))
                    && File.Exists(Path.Combine(folder, CublasFileName));
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
