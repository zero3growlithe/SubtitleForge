using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;

namespace SubtitleForge.Services
{
    /// <summary>
    /// Reports whether the CUDA 12 toolkit and the NVIDIA display driver are present, so the
    /// setup screen can name what is missing instead of letting whisper.cpp fail on its own.
    /// </summary>
    public static class CudaDetector
    {
        private const string ToolkitRootFolderName = "NVIDIA GPU Computing Toolkit";
        private const string ToolkitVersionFolderPattern = "v12*";
        private const string CompilerFileName = "nvcc.exe";
        private const string DriverFileName = "nvcuda.dll";
        private const string EnvironmentVariablePrefix = "CUDA_PATH";
        private const string DownloadPage = "https://developer.nvidia.com/cuda-downloads";
        private const string RuntimePackageUrl = "https://www.nuget.org/packages/Whisper.net.Runtime.Cuda12.Windows/1.9.1";
        private const string RuntimeFileName = "ggml-cuda-whisper.dll";
        private const string RuntimeFolderRelative = "runtimes\\cuda12\\win-x64";

        // ── Public API

        /// <summary>
        /// Where to send the user when no toolkit was found.
        /// </summary>
        public static string DownloadUrl
        {
            get { return DownloadPage; }
        }

        /// <summary>
        /// Where to send the user when the CUDA whisper natives are missing: the NuGet package that
        /// carries them, because that is exactly what has to be dropped next to the executable.
        /// </summary>
        public static string RuntimeDownloadUrl
        {
            get { return RuntimePackageUrl; }
        }

        /// <summary>
        /// True when the CUDA whisper natives sit next to the executable. The build ships without
        /// them (the package is about 560 MB and the CUDA toolkit cannot replace it), so GPU
        /// transcription needs both this and the toolkit.
        /// </summary>
        public static bool IsRuntimePresent(out string details)
        {
            // whisper.net only probes runtimes\cuda12\win-x64 (plus the same folder under any custom
            // LibraryPath). A ggml-cuda-whisper.dll dropped straight into the app folder is never seen,
            // which is the most common reason the GPU silently falls back to the CPU.
            string runtimeFolder = Path.Combine(AppContext.BaseDirectory, RuntimeFolderRelative);
            string runtimeFile = Path.Combine(runtimeFolder, RuntimeFileName);
            string whisperFile = Path.Combine(runtimeFolder, "whisper.dll");
            string misplacedFile = Path.Combine(AppContext.BaseDirectory, RuntimeFileName);

            try
            {
                if (File.Exists(runtimeFile) && File.Exists(whisperFile))
                {
                    details = "The CUDA runtime is in " + runtimeFolder + ".";
                    return true;
                }

                if (File.Exists(runtimeFile))
                {
                    details = runtimeFolder + " contains " + RuntimeFileName + " but no whisper.dll, so whisper.net "
                        + "cannot use that folder. Unzip the whole runtimes folder from the package into the app folder.";
                    return false;
                }

                if (File.Exists(misplacedFile))
                {
                    details = "There is a " + RuntimeFileName + " in the app folder itself, but whisper.net only "
                        + "looks in " + runtimeFolder + ". That folder is part of the published build, so a "
                        + "loose copy in the app folder means the layout was altered.";
                    return false;
                }
            }
            catch (Exception)
            {
                // A settings handler should report the negative case, not throw.
            }

            if (Directory.Exists(runtimeFolder))
            {
                details = runtimeFolder + " is empty. The CUDA natives are not part of the download ("
                    + RuntimeFileName + " alone is 538 MB); copy the package's own runtimes\\cuda12\\win-x64 "
                    + "contents into that folder to turn GPU transcription on.";
                return false;
            }

            details = "No " + RuntimeFolderRelative + " folder next to SubtitleForge.exe. Those libraries are "
                + "not part of the download; unzip the Whisper.net CUDA 12 runtime package into the app folder.";
            return false;
        }

        /// <summary>
        /// True when a CUDA 12 toolkit is installed. <paramref name="details"/> always carries a
        /// human-readable explanation, including for the negative cases.
        /// </summary>
        public static bool IsToolkitInstalled(out string details)
        {
            string toolkitPath = string.Empty;
            string version = string.Empty;
            bool driverPresent = IsDriverPresent();

            try
            {
                if (TryReadEnvironmentToolkit(out toolkitPath, out version))
                {
                    details = DescribeToolkit(version, toolkitPath);
                    return true;
                }

                if (TryReadProgramFilesToolkit(out toolkitPath, out version))
                {
                    details = DescribeToolkit(version, toolkitPath);
                    return true;
                }
            }
            catch (Exception)
            {
                // Unreadable environment or file system: report the negative case instead of throwing.
            }

            if (driverPresent)
            {
                details = "NVIDIA driver found but no CUDA 12 toolkit";
            }
            else
            {
                details = "no NVIDIA driver and no CUDA toolkit";
            }

            return false;
        }

        /// <summary>
        /// True when the NVIDIA kernel-mode driver is installed (the user-mode DLL is what matters here).
        /// </summary>
        public static bool IsDriverPresent()
        {
            try
            {
                string systemDirectory = Environment.SystemDirectory;
                if (string.IsNullOrEmpty(systemDirectory))
                {
                    return false;
                }

                return File.Exists(Path.Combine(systemDirectory, DriverFileName));
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ── Toolkit lookup

        /// <summary>
        /// Reads the CUDA_PATH / CUDA_PATH_V12_x variables set by the NVIDIA installer.
        /// </summary>
        private static bool TryReadEnvironmentToolkit(out string toolkitPath, out string version)
        {
            toolkitPath = string.Empty;
            version = string.Empty;

            IDictionary variables;
            try
            {
                variables = Environment.GetEnvironmentVariables();
            }
            catch (Exception)
            {
                return false;
            }

            List<string> names = new List<string>();
            foreach (object key in variables.Keys)
            {
                string? name = key as string;
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                if (name.StartsWith(EnvironmentVariablePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    names.Add(name);
                }
            }

            names.Sort(StringComparer.OrdinalIgnoreCase);

            string candidatePath = string.Empty;
            string candidateVersion = string.Empty;
            string incompletePath = string.Empty;
            string incompleteVersion = string.Empty;

            foreach (string name in names)
            {
                string value = string.Empty;
                try
                {
                    value = Environment.GetEnvironmentVariable(name) ?? string.Empty;
                }
                catch (Exception)
                {
                    continue;
                }

                value = value.Trim();
                if (value.Length == 0)
                {
                    continue;
                }

                string variableVersion = DescribeVariableVersion(name, value);
                if (ContainsCompiler(value))
                {
                    candidatePath = value;
                    candidateVersion = variableVersion;
                    break;
                }

                if (incompletePath.Length == 0)
                {
                    incompletePath = value;
                    incompleteVersion = variableVersion;
                }
            }

            if (candidatePath.Length > 0)
            {
                toolkitPath = candidatePath;
                version = candidateVersion;
                return true;
            }

            // The variable is set, so the toolkit is installed even if the compiler is not where we looked.
            if (incompletePath.Length > 0)
            {
                toolkitPath = incompletePath;
                version = incompleteVersion;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Scans C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA for a v12.x folder holding bin\nvcc.exe.
        /// </summary>
        private static bool TryReadProgramFilesToolkit(out string toolkitPath, out string version)
        {
            toolkitPath = string.Empty;
            version = string.Empty;

            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (string.IsNullOrEmpty(programFiles))
            {
                return false;
            }

            string toolkitRoot = Path.Combine(programFiles, ToolkitRootFolderName, "CUDA");
            if (!Directory.Exists(toolkitRoot))
            {
                return false;
            }

            string[] versionFolders;
            try
            {
                versionFolders = Directory.GetDirectories(toolkitRoot, ToolkitVersionFolderPattern);
            }
            catch (Exception)
            {
                return false;
            }

            // Walk newest to oldest so a machine with several 12.x toolkits reports the latest one.
            Array.Sort(versionFolders, StringComparer.OrdinalIgnoreCase);
            for (int index = versionFolders.Length - 1; index >= 0; index--)
            {
                string folder = versionFolders[index];
                if (ContainsCompiler(folder))
                {
                    toolkitPath = folder;
                    version = DescribeFolderVersion(folder);
                    return true;
                }
            }

            return false;
        }

        // ── Helpers

        private static bool ContainsCompiler(string toolkitFolder)
        {
            if (string.IsNullOrWhiteSpace(toolkitFolder))
            {
                return false;
            }

            try
            {
                return File.Exists(Path.Combine(toolkitFolder, "bin", CompilerFileName));
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string DescribeToolkit(string version, string toolkitPath)
        {
            if (version.Length == 0)
            {
                return "CUDA toolkit found at " + toolkitPath;
            }

            return "CUDA toolkit " + version + " found at " + toolkitPath;
        }

        /// <summary>
        /// Turns CUDA_PATH_V12_6 into "12.6" and falls back to the folder name of the value.
        /// </summary>
        private static string DescribeVariableVersion(string variableName, string variableValue)
        {
            if (variableName.Length > EnvironmentVariablePrefix.Length)
            {
                string suffix = variableName.Substring(EnvironmentVariablePrefix.Length).TrimStart('_');
                if (suffix.Length > 0 && (suffix[0] == 'V' || suffix[0] == 'v'))
                {
                    suffix = suffix.Substring(1).Replace('_', '.');
                    if (suffix.Length > 0)
                    {
                        return suffix;
                    }
                }
            }

            return DescribeFolderVersion(variableValue);
        }

        /// <summary>
        /// Turns ...\CUDA\v12.6 into "12.6"; returns an empty string when the folder is not version-like.
        /// </summary>
        private static string DescribeFolderVersion(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return string.Empty;
            }

            try
            {
                string trimmed = folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string leaf = Path.GetFileName(trimmed);
                if (leaf.Length > 1 && (leaf[0] == 'v' || leaf[0] == 'V'))
                {
                    return leaf.Substring(1);
                }

                return string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }
}
