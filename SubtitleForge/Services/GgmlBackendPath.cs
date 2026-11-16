using System;
using System.Collections.Generic;
using System.IO;

namespace SubtitleForge.Services
{
    /// <summary>
    /// Tells ggml where the CUDA backend module is.
    ///
    /// whisper.net loads <c>runtimes\cuda12\win-x64\ggml-cuda-whisper.dll</c> by absolute path, which
    /// is enough for the loader to report CUDA and for the DLL to sit in memory — but ggml's own
    /// backend registry only scans the <b>executable folder</b> and the <b>current directory</b> for
    /// <c>ggml-&lt;backend&gt;-*.dll</c>. The natives live in a subfolder, so the registry stays CPU-only
    /// and whisper.cpp says "no GPU found" even though every CUDA file is present and the driver is
    /// fine. <c>GGML_BACKEND_PATH</c> is the variable ggml reads for exactly this case: it registers
    /// a backend module from an arbitrary path.
    /// </summary>
    public static class GgmlBackendPath
    {
        public const string VariableName = "GGML_BACKEND_PATH";

        private static readonly string[] CudaFolders = new string[] { "cuda", "cuda12" };

        private static readonly string[] CudaModuleNames = new string[]
        {
            "ggml-cuda-whisper.dll",
            "ggml-cuda.dll"
        };

        /// <summary>
        /// Points <c>GGML_BACKEND_PATH</c> at the CUDA backend module when it is not next to the
        /// executable. Returns what it did, for the status line and the diagnostics report.
        /// </summary>
        public static string Apply()
        {
            string existing = Environment.GetEnvironmentVariable(VariableName) ?? string.Empty;
            if (existing.Length > 0)
            {
                return "already set to " + existing;
            }

            string module = FindCudaModule(out string folder);
            if (module.Length == 0)
            {
                return "no CUDA backend module found next to the app (" + folder + ")";
            }

            try
            {
                Environment.SetEnvironmentVariable(VariableName, module);
            }
            catch (Exception exception)
            {
                return "could not set " + VariableName + ": " + exception.Message;
            }

            return "set to " + module;
        }

        /// <summary>The CUDA backend module as it would be set right now, or empty.</summary>
        public static string CurrentValue()
        {
            return Environment.GetEnvironmentVariable(VariableName) ?? string.Empty;
        }

        /// <summary>
        /// Looks for the module in the folders whisper.net itself probes. The application folder is
        /// checked too: if somebody dropped the natives next to the exe, ggml finds them on its own
        /// and nothing has to be pointed anywhere.
        /// </summary>
        public static string FindCudaModule(out string searchedFolders)
        {
            List<string> searched = new List<string>();
            string baseFolder = AppContext.BaseDirectory;

            for (int index = 0; index < CudaFolders.Length; index++)
            {
                string folder = Path.Combine(baseFolder, "runtimes", CudaFolders[index], "win-x64");
                searched.Add(folder);
                string module = FirstModuleIn(folder);
                if (module.Length > 0)
                {
                    searchedFolders = string.Join("; ", searched.ToArray());
                    return module;
                }
            }

            searchedFolders = string.Join("; ", searched.ToArray());
            return string.Empty;
        }

        private static string FirstModuleIn(string folder)
        {
            if (!Directory.Exists(folder))
            {
                return string.Empty;
            }

            for (int index = 0; index < CudaModuleNames.Length; index++)
            {
                string candidate = Path.Combine(folder, CudaModuleNames[index]);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return string.Empty;
        }
    }
}
