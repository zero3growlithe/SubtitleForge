using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

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
        /// The names the backend module is linked under, most likely first.
        ///
        /// This is not cosmetic. The ggml inside whisper.net 1.9.1 does not look for a fixed export:
        /// it takes the module's <b>file name</b>, strips <c>ggml-</c> and <c>.dll</c>, and asks for
        /// <c>ggml_backend_&lt;that name&gt;_reg</c>. Renaming ggml's CUDA module with whisper.net's
        /// <c>-whisper</c> suffix therefore turns the lookup into
        /// <c>ggml_backend_cuda-whisper_reg</c>, which of course nobody exports, and the backend is
        /// dropped without a word. The module really exports <c>ggml_backend_cuda_reg</c>, so it has
        /// to also be visible under the plain name <c>ggml-cuda.dll</c>.
        /// </summary>
        private static readonly string[] LinkNames = new string[]
        {
            "ggml-cuda.dll",
            "ggml-cuda-whisper.dll"
        };

        /// <summary>
        /// Points <c>GGML_BACKEND_PATH</c> at the CUDA backend module when it is not next to the
        /// executable. Returns what it did, for the status line and the diagnostics report.
        /// </summary>
        public static string Apply()
        {
            string module = FindCudaModule(out string folder);
            if (module.Length == 0)
            {
                return "no CUDA backend module found next to the app (" + folder + ")";
            }

            string existing = Environment.GetEnvironmentVariable(VariableName) ?? string.Empty;
            if (existing.Length > 0)
            {
                // Still make sure the link exists: the variable alone does nothing in builds that do
                // not read it, and a run must not depend on which call came first.
                return "already set to " + existing + "; "
                    + EnsureModuleNextToExecutable(module);
            }

            string note;
            try
            {
                Environment.SetEnvironmentVariable(VariableName, module);
                note = "set to " + module;
            }
            catch (Exception exception)
            {
                note = "could not set " + VariableName + ": " + exception.Message;
            }

            // Belt and braces: the variable is ignored by whisper builds that predate it, and ggml
            // always scans the executable folder, so the module is hard-linked there as well. A link
            // costs no space, needs no admin rights on the app's own folder, and disappears with the
            // file it points at.
            string link = EnsureModuleNextToExecutable(module);
            return note + "; " + link;
        }

        /// <summary>
        /// Hard-links the CUDA backend module into the folder holding the executable, which is the one
        /// place ggml is guaranteed to look. Returns a one-line report.
        /// </summary>
        public static string EnsureModuleNextToExecutable(string modulePath)
        {
            if (modulePath.Length == 0)
            {
                return "nothing to link";
            }

            List<string> results = new List<string>();

            // Both folders matter. ggml scans the executable folder and the current directory; the
            // ggml inside whisper.dll additionally scans the folder it was loaded from, and that is
            // the CUDA folder the module really lives in. A link in each place covers the lot.
            string exeFolder = AppContext.BaseDirectory;
            string nativesFolder = Path.GetDirectoryName(modulePath) ?? string.Empty;

            for (int index = 0; index < LinkNames.Length; index++)
            {
                results.Add(EnsureOneLink(modulePath, LinkNames[index], exeFolder, "next to the exe"));
            }

            if (nativesFolder.Length > 0
                && !string.Equals(
                    Path.GetFullPath(nativesFolder).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(exeFolder).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                for (int index = 0; index < LinkNames.Length; index++)
                {
                    // Only the plain name matters here; the suffixed one is the real file already.
                    if (!string.Equals(LinkNames[index], Path.GetFileName(modulePath), StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(EnsureOneLink(modulePath, LinkNames[index], nativesFolder, "in the CUDA folder"));
                    }
                }
            }

            return string.Join("; ", results.ToArray());
        }

        private static string EnsureOneLink(string modulePath, string linkName, string folder, string where)
        {
            string target = Path.Combine(folder, linkName);

            try
            {
                if (File.Exists(target))
                {
                    long existingSize = new FileInfo(target).Length;
                    long moduleSize = new FileInfo(modulePath).Length;
                    if (existingSize == moduleSize)
                    {
                        return linkName + " already " + where;
                    }

                    return linkName + " " + where + " is a different file ("
                        + existingSize.ToString(CultureInfo.InvariantCulture) + " bytes instead of "
                        + moduleSize.ToString(CultureInfo.InvariantCulture) + ") — delete it";
                }

                if (!CreateHardLink(target, modulePath, IntPtr.Zero))
                {
                    int error = Marshal.GetLastWin32Error();
                    return "could not link " + linkName + " " + where + " (error "
                        + error.ToString(CultureInfo.InvariantCulture)
                        + "); copy the CUDA folder next to SubtitleForge.exe instead";
                }
            }
            catch (Exception exception)
            {
                return "could not link " + linkName + " " + where + ": " + exception.Message;
            }

            return "linked " + linkName + " " + where;
        }

        /// <summary>
        /// Shows which of the names above actually satisfy the export ggml asks for, and which
        /// <c>ggml-*</c> files the scans would see in the executable folder.
        /// </summary>
        public static string DescribeRegistration(string modulePath)
        {
            if (modulePath.Length == 0)
            {
                return "no CUDA module to inspect";
            }

            IntPtr module;
            try
            {
                module = LoadLibraryEx(modulePath, IntPtr.Zero, LoadWithAlteredSearchPath);
            }
            catch (Exception exception)
            {
                return "could not even open it: " + exception.Message;
            }

            if (module == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                return "will not load, so ggml can never register it (Win32 error "
                    + error.ToString(CultureInfo.InvariantCulture) + ": " + DescribeWin32Error(error) + ")";
            }

            List<string> lines = new List<string>();
            lines.Add("exports: ggml_backend_init "
                + (HasExport(module, "ggml_backend_init") ? "found" : "MISSING")
                + ", ggml_backend_score "
                + (HasExport(module, "ggml_backend_score") ? "found" : "MISSING"));

            for (int index = 0; index < LinkNames.Length; index++)
            {
                string name = LinkNames[index];
                string derived = DeriveEntryPoint(name);
                bool present = HasExport(module, derived);
                lines.Add(name + " -> " + derived + (present ? ": found" : ": MISSING")
                    + (present ? "" : " (ggml silently drops this name)"));
            }

            List<string> seen = new List<string>();
            try
            {
                string[] files = Directory.GetFiles(AppContext.BaseDirectory, "ggml-*.dll");
                for (int index = 0; index < files.Length; index++)
                {
                    seen.Add(Path.GetFileName(files[index]));
                }
            }
            catch (Exception)
            {
            }

            seen.Sort(StringComparer.OrdinalIgnoreCase);
            lines.Add("ggml-*.dll next to the exe: " + (seen.Count == 0 ? "(none)" : string.Join(", ", seen.ToArray())));
            return string.Join("; ", lines.ToArray());
        }

        /// <summary>The entry point this ggml build asks a module for, derived the way it does.</summary>
        public static string DeriveEntryPoint(string fileName)
        {
            string stem = Path.GetFileNameWithoutExtension(fileName);
            if (stem.StartsWith("ggml-", StringComparison.OrdinalIgnoreCase))
            {
                stem = stem.Substring("ggml-".Length);
            }

            return "ggml_backend_" + stem + "_reg";
        }

        private static bool HasExport(IntPtr module, string name)
        {
            return GetProcAddress(module, name) != IntPtr.Zero;
        }

        /// <summary>
        /// Tries to load the backend module by absolute path and looks for the exports ggml needs.
        ///
        /// This is the test that separates the two ways CUDA can fail: a module that will not load
        /// (a dependency it cannot find — the Win32 error says which) and a module that loads but
        /// has no registration entry point (a stub or a build from another version). whisper.cpp
        /// reports both as the same "no GPU found", and in a release build ggml does not even log it.
        /// </summary>
        public static string TestModuleLoad(string modulePath)
        {
            if (modulePath.Length == 0)
            {
                return "no module to test";
            }

            IntPtr module;
            try
            {
                // The altered search path makes the module's own folder the first place its
                // dependencies are looked for, which is what the CUDA folder needs.
                module = LoadLibraryEx(modulePath, IntPtr.Zero, LoadWithAlteredSearchPath);
            }
            catch (Exception exception)
            {
                return "could not even ask to load it: " + exception.Message;
            }

            if (module == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                return "will NOT load (Win32 error " + error.ToString(CultureInfo.InvariantCulture)
                    + ": " + DescribeWin32Error(error) + ")";
            }

            IntPtr register = GetProcAddress(module, "ggml_backend_cuda_reg");
            IntPtr score = GetProcAddress(module, "ggml_backend_score");
            string exports = "ggml_backend_cuda_reg: " + (register != IntPtr.Zero ? "found" : "MISSING")
                + ", ggml_backend_score: " + (score != IntPtr.Zero ? "found" : "MISSING");

            return "loads fine (" + exports + ")";
        }

        private static string DescribeWin32Error(int error)
        {
            switch (error)
            {
                case 126:
                    return "the module or one of its dependencies was not found";
                case 127:
                    return "a procedure a dependency should export was not found (version mismatch)";
                case 193:
                    return "not a valid Win32 image (32-bit file in a 64-bit process, or the reverse)";
                case 998:
                    return "invalid access to memory";
                default:
                    return "see Win32 error " + error.ToString(CultureInfo.InvariantCulture);
            }
        }

        /// <summary>
        /// Runs work with the current directory pointed at the CUDA natives folder.
        ///
        /// ggml also scans the current directory when it looks for backend modules, and the folder the
        /// natives live in is the one path whisper.net has already loaded them from — so ggml ends up
        /// with the same module, not a second copy. Used as a fallback when the hard link is refused
        /// (read-only install folder, another volume).
        /// </summary>
        public static T WithBackendFolder<T>(Func<T> work)
        {
            if (work == null)
            {
                throw new ArgumentNullException("work");
            }

            string folder = string.Empty;
            string module = FindCudaModule(out folder);
            if (module.Length == 0)
            {
                return work();
            }

            string nativesFolder = Path.GetDirectoryName(module) ?? string.Empty;
            if (nativesFolder.Length == 0)
            {
                return work();
            }

            string previous;
            try
            {
                previous = Directory.GetCurrentDirectory();
            }
            catch (Exception)
            {
                return work();
            }

            lock (FolderGate)
            {
                try
                {
                    Directory.SetCurrentDirectory(nativesFolder);
                }
                catch (Exception)
                {
                    return work();
                }

                try
                {
                    return work();
                }
                finally
                {
                    try
                    {
                        Directory.SetCurrentDirectory(previous);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }

        private static readonly object FolderGate = new object();

        private const int LoadWithAlteredSearchPath = 0x00000008;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string fileName, IntPtr file, int flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string procedureName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateHardLink(
            string lpFileName,
            string lpExistingFileName,
            IntPtr lpSecurityAttributes);

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
