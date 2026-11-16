using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace SubtitleForge.Services
{
    public sealed class CudaDeviceInfo
    {
        public int Index { get; set; }

        public string Name { get; set; } = string.Empty;

        public long MemoryBytes { get; set; }

        public string DriverVersion { get; set; } = string.Empty;

        public string Describe()
        {
            const double Megabyte = 1024.0 * 1024.0;
            double megabytes = MemoryBytes / Megabyte;
            string memory = megabytes >= 1024.0
                ? (megabytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " GB"
                : megabytes.ToString("0", CultureInfo.InvariantCulture) + " MB";

            return "#" + Index.ToString(CultureInfo.InvariantCulture) + " " + Name + " (" + memory + ")";
        }
    }

    /// <summary>
    /// Lists the CUDA devices and picks which one whisper may use.
    ///
    /// CUDA only ever enumerates NVIDIA adapters, so an integrated AMD or Intel GPU is not "device 0"
    /// and does not shift the numbering: one NVIDIA card is always index 0 no matter what else the
    /// machine has, whether it sits on the PCIe bus, in an M.2 slot over Oculink or on a riser. Where
    /// a pick is needed is a machine with more than one NVIDIA adapter — the eGPU is then usually
    /// index 1 — and <c>CUDA_VISIBLE_DEVICES</c> is how whisper.cpp is redirected, because
    /// whisper.net has no option for it. <c>CUDA_DEVICE_ORDER=PCI_BUS_ID</c> makes CUDA number the
    /// cards the way nvidia-smi lists them, so the number in the picker is the number that is used.
    /// </summary>
    public static class CudaDevices
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(6);

        /// <summary>Asks nvidia-smi for every CUDA device, in the order CUDA will number them.</summary>
        public static List<CudaDeviceInfo> Query(out string detail)
        {
            detail = string.Empty;
            List<CudaDeviceInfo> devices = new List<CudaDeviceInfo>();

            string output;
            if (!Run("--query-gpu=index,name,memory.total,driver_version --format=csv,noheader,nounits", out output, out detail))
            {
                return devices;
            }

            string[] rows = output.Split('\n');
            for (int rowIndex = 0; rowIndex < rows.Length; rowIndex++)
            {
                string row = rows[rowIndex].Trim();
                if (row.Length == 0)
                {
                    continue;
                }

                string[] columns = row.Split(',');
                if (columns.Length < 4)
                {
                    continue;
                }

                CudaDeviceInfo device = new CudaDeviceInfo();
                int index;
                if (int.TryParse(columns[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out index))
                {
                    device.Index = index;
                }

                device.Name = columns[1].Trim();

                long megabytes;
                if (long.TryParse(columns[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out megabytes))
                {
                    device.MemoryBytes = megabytes * 1024L * 1024L;
                }

                device.DriverVersion = columns[3].Trim();
                devices.Add(device);
            }

            if (devices.Count == 0 && detail.Length == 0)
            {
                detail = "nvidia-smi listed no CUDA devices";
            }

            return devices;
        }

        /// <summary>
        /// Points CUDA at one device. Must run before the first CUDA call, which for this app means
        /// before a whisper model is opened. A negative index leaves the environment alone.
        /// </summary>
        public static string ApplySelection(int index)
        {
            if (index < 0)
            {
                return "automatic (CUDA picks the first NVIDIA adapter)";
            }

            try
            {
                // nvidia-smi numbers the cards in PCI order; asking CUDA to do the same is what makes
                // the number the user picked mean what they saw.
                Environment.SetEnvironmentVariable("CUDA_DEVICE_ORDER", "PCI_BUS_ID");
                Environment.SetEnvironmentVariable(
                    "CUDA_VISIBLE_DEVICES",
                    index.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception exception)
            {
                return "could not restrict CUDA to device "
                    + index.ToString(CultureInfo.InvariantCulture) + ": " + exception.Message;
            }

            return "device " + index.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>One line per device, with the selected one marked, for the diagnostics report.</summary>
        public static string DescribeSelection(int selectedIndex)
        {
            StringBuilder text = new StringBuilder();
            string detail;
            List<CudaDeviceInfo> devices = Query(out detail);

            if (devices.Count == 0)
            {
                text.Append("   no CUDA devices: " + detail);
                return text.ToString();
            }

            for (int index = 0; index < devices.Count; index++)
            {
                CudaDeviceInfo device = devices[index];
                bool chosen = selectedIndex < 0 ? index == 0 : device.Index == selectedIndex;
                text.Append("   ");
                text.Append(chosen ? "* " : "  ");
                text.Append(device.Describe());
                text.Append("  driver ");
                text.Append(device.DriverVersion);
                text.AppendLine();
            }

            if (devices.Count == 1)
            {
                text.AppendLine("   only one CUDA device, so it is used whatever the selection says: an");
                text.AppendLine("   integrated AMD/Intel GPU is not a CUDA device and does not take index 0.");
            }
            else if (selectedIndex < 0)
            {
                text.AppendLine("   more than one CUDA device: the first one is used. Set \"CUDA device\" in the");
                text.AppendLine("   app settings if the card that should do the work is another one.");
            }

            text.Append("   CUDA_VISIBLE_DEVICES: ");
            string visible = Environment.GetEnvironmentVariable("CUDA_VISIBLE_DEVICES") ?? string.Empty;
            text.AppendLine(visible.Length > 0 ? visible : "(not set)");
            return text.ToString().TrimEnd();
        }

        private static bool Run(string arguments, out string output, out string detail)
        {
            output = string.Empty;
            detail = string.Empty;
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = "nvidia-smi";
                startInfo.Arguments = arguments;
                startInfo.UseShellExecute = false;
                startInfo.RedirectStandardOutput = true;
                startInfo.RedirectStandardError = true;
                startInfo.CreateNoWindow = true;

                using (Process process = new Process())
                {
                    process.StartInfo = startInfo;
                    if (!process.Start())
                    {
                        detail = "nvidia-smi did not start";
                        return false;
                    }

                    string standardOutput = process.StandardOutput.ReadToEnd();
                    string standardError = process.StandardError.ReadToEnd();
                    if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch (Exception)
                        {
                        }

                        detail = "nvidia-smi did not answer in time";
                        return false;
                    }

                    if (process.ExitCode != 0)
                    {
                        detail = "nvidia-smi exited with " + process.ExitCode.ToString(CultureInfo.InvariantCulture)
                            + (standardError.Length > 0 ? ": " + standardError.Trim() : string.Empty);
                        return false;
                    }

                    output = standardOutput;
                    return true;
                }
            }
            catch (Exception exception)
            {
                detail = exception.GetType().Name + ": " + exception.Message;
                return false;
            }
        }
    }
}
