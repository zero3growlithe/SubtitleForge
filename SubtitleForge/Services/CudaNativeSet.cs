using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace SubtitleForge.Services
{
    /// <summary>
    /// Proves that the CUDA natives in runtimes\cuda12\win-x64 are the CUDA build and not a copy of
    /// the CPU ones.
    ///
    /// This is the failure that cost several rounds of guessing: whisper.net ships three natives in
    /// both packages, and they are <b>not</b> the same file. The CUDA package's ggml-whisper.dll is
    /// built against ggml-cuda and therefore <b>imports</b> ggml_backend_cuda_reg from
    /// ggml-cuda-whisper.dll; ggml's registry is filled from that static import, not from any folder
    /// scan. Copy the CPU package's ggml-whisper.dll in there — which happens the moment somebody
    /// takes the wrong package, or a build wipes the folder and instructs them to copy "the runtimes
    /// folder" of a package that has none — and CUDA is never registered at all. whisper then reports
    /// the harmless-looking "backends = 1" and "no GPU found" while every file is present, the
    /// release hashes of ggml-base match, and the module loads perfectly when asked directly.
    /// </summary>
    public static class CudaNativeSet
    {
        private const string CudaEntryPoint = "ggml_backend_cuda_reg";
        private const string SharedGgmlFileName = "ggml-whisper.dll";
        private const string SharedRuntimeFileName = "whisper.dll";
        private const string BigModuleFileName = "ggml-cuda-whisper.dll";

        /// <summary>
        /// Null when the CUDA folder looks like a real CUDA set, otherwise one sentence naming what
        /// is wrong. Nothing here needs a GPU driver: it only reads two files.
        /// </summary>
        public static string? FindProblem(string cudaFolder, string cpuFolder)
        {
            if (!Directory.Exists(cudaFolder))
            {
                return null;
            }

            string cudaGgml = Path.Combine(cudaFolder, SharedGgmlFileName);
            if (!File.Exists(cudaGgml))
            {
                return null;
            }

            if (!MentionsCudaEntryPoint(cudaGgml))
            {
                string cpuGgml = Path.Combine(cpuFolder, SharedGgmlFileName);
                bool matchesCpu = File.Exists(cpuGgml) && HaveSameSizeAndHash(cudaGgml, cpuGgml);

                return SharedGgmlFileName + " in " + cudaFolder + " is the CPU build, not the CUDA one"
                    + (matchesCpu
                        ? " (it is byte for byte the CPU fallback's copy)"
                        : string.Empty)
                    + ": it does not import " + CudaEntryPoint + ", so ggml has one backend and no GPU";
            }

            string cudaRuntime = Path.Combine(cudaFolder, SharedRuntimeFileName);
            string cpuRuntime = Path.Combine(cpuFolder, SharedRuntimeFileName);
            if (File.Exists(cudaRuntime) && File.Exists(cpuRuntime) && HaveSameSizeAndHash(cudaRuntime, cpuRuntime))
            {
                return SharedRuntimeFileName + " in " + cudaFolder + " is the CPU fallback's copy: the CUDA"
                    + " set must use the package's own " + SharedRuntimeFileName;
            }

            return null;
        }

        /// <summary>The same information as a short report block, for GPU diagnostics.</summary>
        public static string Describe(string cudaFolder, string cpuFolder)
        {
            List<string> lines = new List<string>();

            string cudaGgml = Path.Combine(cudaFolder, SharedGgmlFileName);
            string cudaRuntime = Path.Combine(cudaFolder, SharedRuntimeFileName);
            string bigModule = Path.Combine(cudaFolder, BigModuleFileName);

            lines.Add("   is this a CUDA set or a copy of the CPU one?");
            lines.Add("     " + SharedGgmlFileName + ": " + DescribeFile(cudaGgml)
                + (File.Exists(cudaGgml)
                    ? (MentionsCudaEntryPoint(cudaGgml)
                        ? " — imports " + CudaEntryPoint + ", this is the CUDA build"
                        : " — does NOT import " + CudaEntryPoint + ", this is the CPU build")
                    : string.Empty));
            lines.Add("     " + SharedRuntimeFileName + ": " + DescribeFile(cudaRuntime)
                + DescribeAgainst(cudaRuntime, Path.Combine(cpuFolder, SharedRuntimeFileName)));
            lines.Add("     " + BigModuleFileName + ": " + DescribeFile(bigModule));

            string? problem = FindProblem(cudaFolder, cpuFolder);
            lines.Add(problem == null
                ? "     verdict: the set looks like a CUDA build"
                : "     verdict: " + problem);

            return string.Join(Environment.NewLine, lines.ToArray()) + Environment.NewLine;
        }

        private static string DescribeFile(string path)
        {
            if (!File.Exists(path))
            {
                return "(missing)";
            }

            long size = new FileInfo(path).Length;
            if (size > 1024L * 1024L * 1024L)
            {
                return (size / (1024.0 * 1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
            }

            if (size > 1024L * 1024L)
            {
                return (size / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
            }

            return (size / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB";
        }

        private static string DescribeAgainst(string cudaFile, string cpuFile)
        {
            if (!File.Exists(cudaFile) || !File.Exists(cpuFile))
            {
                return string.Empty;
            }

            if (HaveSameSizeAndHash(cudaFile, cpuFile))
            {
                return " — identical to the CPU fallback's copy, so it is not a CUDA file";
            }

            return new FileInfo(cudaFile).Length == new FileInfo(cpuFile).Length
                ? " — same size as the CPU copy but different bytes, so it is a CUDA build"
                : " — different from the CPU fallback's copy, so it is a CUDA build";
        }

        /// <summary>
        /// Import names live in the PE's import table as plain ASCII, so a substring search answers
        /// "was this built against that backend" without loading the module or parsing PE headers.
        /// </summary>
        private static bool MentionsCudaEntryPoint(string path)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                byte[] needle = System.Text.Encoding.ASCII.GetBytes(CudaEntryPoint);

                for (int index = 0; index <= bytes.Length - needle.Length; index++)
                {
                    if (bytes[index] != needle[0])
                    {
                        continue;
                    }

                    bool match = true;
                    for (int offset = 1; offset < needle.Length; offset++)
                    {
                        if (bytes[index + offset] != needle[offset])
                        {
                            match = false;
                            break;
                        }
                    }

                    if (match)
                    {
                        return true;
                    }
                }
            }
            catch (Exception)
            {
            }

            return false;
        }

        private static bool HaveSameSizeAndHash(string first, string second)
        {
            try
            {
                if (new FileInfo(first).Length != new FileInfo(second).Length)
                {
                    return false;
                }

                return string.Equals(Hash(first), Hash(second), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string Hash(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            {
                using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
                {
                    byte[] digest = sha.ComputeHash(stream);
                    return BitConverter.ToString(digest).Replace("-", string.Empty);
                }
            }
        }
    }
}
