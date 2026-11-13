using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using SubtitleForge.Services;

namespace SubtitleForge
{
    /// <summary>
    /// Shows the GPU report as text, so a failed GPU path can be read and copied instead of being
    /// guessed at from a status line.
    /// </summary>
    public partial class GpuDiagnosticsWindow : Window
    {
        public GpuDiagnosticsWindow(string report, bool gpuUsable)
        {
            InitializeComponent();

            FixLayoutButton.IsEnabled = CudaRuntimeLayout.NeedsMove();

            ReportBox.Text = report;
            SummaryText.Text = gpuUsable
                ? "CUDA works: the model opened on the GPU. If a transcription still runs slowly, check the model size and the number of threads."
                : "CUDA did not work. The report below names the first piece that is missing or the exception whisper raised.";

            // The interesting lines are usually the failures, so land the caret on the first one.
            int failureIndex = report.IndexOf("FAILED", StringComparison.OrdinalIgnoreCase);
            if (failureIndex < 0)
            {
                failureIndex = report.IndexOf("MISSING", StringComparison.OrdinalIgnoreCase);
            }

            if (failureIndex > 0)
            {
                ReportBox.ScrollToLine(CountLines(report, failureIndex));
            }
        }

        private void FixLayoutClick(object sender, RoutedEventArgs e)
        {
            string report;
            bool ready = CudaRuntimeLayout.EnsureRuntimeFolder(out report);
            FixLayoutButton.IsEnabled = false;

            if (string.IsNullOrEmpty(report))
            {
                MessageBox.Show(
                    "There is nothing to move: no ggml-cuda-whisper.dll was found outside the runtime folder.",
                    "GPU diagnostics",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            ReportBox.Text = "Moving the CUDA runtime" + Environment.NewLine
                + new string('-', 40) + Environment.NewLine
                + report + Environment.NewLine
                + (ready
                    ? "Restart SubtitleForge, then run the diagnostics again to confirm CUDA is used."
                    : "The folder is still incomplete; the lines above name what is missing.");
        }

        private void CopyClick(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(ReportBox.Text);
            }
            catch (Exception exception)
            {
                CrashLog.Write(exception, "GpuDiagnosticsWindow.Copy");
            }
        }

        private void OpenFolderClick(object sender, RoutedEventArgs e)
        {
            try
            {
                string? folder = Path.GetDirectoryName(GpuDiagnostics.ReportPath);
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                {
                    return;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = folder,
                    UseShellExecute = true
                });
            }
            catch (Exception exception)
            {
                CrashLog.Write(exception, "GpuDiagnosticsWindow.OpenFolder");
            }
        }

        private static int CountLines(string text, int index)
        {
            int lines = 0;
            for (int position = 0; position < index && position < text.Length; position++)
            {
                if (text[position] == '\n')
                {
                    lines++;
                }
            }

            return lines;
        }
    }
}
