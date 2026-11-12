using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SubtitleForge.Controls;
using SubtitleForge.Models;

namespace SubtitleForge.Services
{
    /// <summary>
    /// One cue exactly the way the burn-in needs it: the text, its timing and the profiles that
    /// were already resolved for it. Nothing here looks anything up, so the renderer can run off
    /// the UI thread.
    /// </summary>
    public sealed class BurnInCue
    {
        public string Text { get; set; } = string.Empty;

        public double StartSeconds { get; set; }

        public double EndSeconds { get; set; }

        public StyleProfile Style { get; set; } = new StyleProfile();

        public IdleAnimationProfile? Idle { get; set; }

        public PopInProfile? PopIn { get; set; }

        public double PositionX { get; set; } = 0.5;

        public double PositionY { get; set; } = 0.86;

        public double Scale { get; set; } = 1.0;
    }

    /// <summary>
    /// Burns subtitles in by drawing them with the very same control the preview uses and piping
    /// the raw frames into ffmpeg as an alpha overlay. That is what makes an export look like the
    /// preview: font, effects, gradient fills and the idle / pop-in animations are all included,
    /// which a plain ASS file could never do. Rendering happens on its own STA thread so a long
    /// export never freezes the window, and frames without a cue are skipped without drawing.
    /// </summary>
    public static class SubtitleBurnInRenderer
    {
        /// <summary>
        /// Starts <paramref name="executablePath"/> and pumps subtitle frames into its standard
        /// input as BGRA. The argument list must already declare that input as rawvideo pipe:0.
        /// </summary>
        public static async Task<ProcessResult> RunAsync(
            string executablePath,
            IEnumerable<string> arguments,
            int width,
            int height,
            double frameRate,
            double durationSeconds,
            IReadOnlyList<BurnInCue> cues,
            string? workingDirectory,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            if (width < 2 || height < 2)
            {
                throw new ArgumentOutOfRangeException(nameof(width), "The burn-in needs a real frame size.");
            }

            WriteCommand(executablePath, arguments, width, height);

            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = executablePath;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.RedirectStandardInput = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;

            if (!string.IsNullOrEmpty(workingDirectory))
            {
                startInfo.WorkingDirectory = workingDirectory;
            }

            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            ProcessResult result = new ProcessResult();
            StringBuilder errors = new StringBuilder();
            StringBuilder output = new StringBuilder();

            using (Process process = new Process())
            {
                process.StartInfo = startInfo;

                process.ErrorDataReceived += delegate (object sender, DataReceivedEventArgs eventArgs)
                {
                    if (eventArgs.Data != null)
                    {
                        errors.AppendLine(eventArgs.Data);
                    }
                };

                process.OutputDataReceived += delegate (object sender, DataReceivedEventArgs eventArgs)
                {
                    if (eventArgs.Data != null)
                    {
                        output.AppendLine(eventArgs.Data);
                    }
                };

                if (!process.Start())
                {
                    throw new InvalidOperationException("Could not start " + executablePath);
                }

                process.BeginErrorReadLine();
                process.BeginOutputReadLine();

                try
                {
                    await PumpFramesAsync(
                            process.StandardInput.BaseStream,
                            width,
                            height,
                            frameRate,
                            durationSeconds,
                            cues,
                            progress,
                            cancellationToken)
                        .ConfigureAwait(false);

                    try
                    {
                        process.StandardInput.Close();
                    }
                    catch (Exception)
                    {
                        // ffmpeg may already be gone.
                    }

                    await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                    result.ExitCode = process.ExitCode;
                }
                catch (OperationCanceledException)
                {
                    result.WasCancelled = true;
                    Kill(process);
                    throw;
                }
                catch (Exception)
                {
                    Kill(process);
                    throw;
                }
                finally
                {
                    result.StandardOutput = output.ToString();
                    result.StandardError = errors.ToString();
                }
            }

            if (result.ExitCode != 0)
            {
                string details = string.IsNullOrWhiteSpace(result.StandardError)
                    ? "ffmpeg exited with code " + result.ExitCode.ToString(CultureInfo.InvariantCulture) + "."
                    : AudioTrackExtractor.FirstMeaningfulLines(result.StandardError);
                throw new InvalidOperationException("Burning the subtitles in failed: " + details);
            }

            progress?.Report(1.0);
            return result;
        }

        private static void Kill(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
            }
            catch (Exception)
            {
                // Already gone.
            }
        }

        /// <summary>
        /// Draws every frame and writes it to the pipe. Runs on its own STA thread because WPF
        /// visuals can only be created and rendered from an STA thread, and doing that on the UI
        /// thread would block the window for the whole export.
        /// </summary>
        private static Task PumpFramesAsync(
            Stream destination,
            int width,
            int height,
            double frameRate,
            double durationSeconds,
            IReadOnlyList<BurnInCue> cues,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            TaskCompletionSource<bool> completion =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            Thread thread = new Thread(delegate ()
            {
                try
                {
                    PumpFrames(destination, width, height, frameRate, durationSeconds, cues, progress, cancellationToken);
                    completion.TrySetResult(true);
                }
                catch (OperationCanceledException exception)
                {
                    completion.TrySetCanceled(exception.CancellationToken);
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            });

            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Name = "SubtitleForge burn-in";
            thread.Start();

            return completion.Task;
        }

        private static void PumpFrames(
            Stream destination,
            int width,
            int height,
            double frameRate,
            double durationSeconds,
            IReadOnlyList<BurnInCue> cues,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            double fps = frameRate > 1.0 && frameRate < 240.0 ? frameRate : 30.0;
            double seconds = durationSeconds;
            foreach (BurnInCue cue in cues)
            {
                if (cue.EndSeconds > seconds)
                {
                    seconds = cue.EndSeconds;
                }
            }

            long totalFrames = (long)Math.Ceiling(seconds * fps);
            if (totalFrames < 1)
            {
                totalFrames = 1;
            }

            List<Slot> slots = new List<Slot>();
            foreach (BurnInCue cue in cues)
            {
                SubtitleOverlay overlay = new SubtitleOverlay();
                overlay.Width = width;
                overlay.Height = height;
                overlay.TextStyle = cue.Style;
                overlay.Idle = cue.Idle;
                overlay.PopIn = cue.PopIn;
                overlay.CueStartSeconds = cue.StartSeconds;
                overlay.CueEndSeconds = cue.EndSeconds;
                overlay.PositionX = cue.PositionX;
                overlay.PositionY = cue.PositionY;
                overlay.Scale = cue.Scale;
                overlay.CueText = string.Empty;
                slots.Add(new Slot(cue, overlay));
            }

            RenderTargetBitmap bitmap = new RenderTargetBitmap(width, height, 96.0, 96.0, PixelFormats.Pbgra32);

            // One visual reused for every frame: opening a drawing context on it replaces the
            // previous frame's commands, so nothing has to be measured, arranged or laid out.
            DrawingVisual frameVisual = new DrawingVisual();
            int stride = width * 4;
            byte[] frame = new byte[stride * height];
            bool diagnosticsWritten = false;

            for (long index = 0; index < totalFrames; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                double time = index / fps;
                bool anythingVisible = false;

                foreach (Slot slot in slots)
                {
                    bool visible = time >= slot.Cue.StartSeconds && time < slot.Cue.EndSeconds;
                    slot.Visible = visible;
                    if (!visible)
                    {
                        if (slot.Element.CueText.Length != 0)
                        {
                            slot.Element.CueText = string.Empty;
                            slot.Element.Refresh();
                        }

                        continue;
                    }

                    if (slot.Element.CueText.Length == 0)
                    {
                        slot.Element.CueText = slot.Cue.Text ?? string.Empty;
                    }

                    slot.Element.PlayheadSeconds = time;
                    slot.Element.IdleSeconds = time;
                    slot.Element.Refresh();
                    anythingVisible = true;
                }

                if (anythingVisible)
                {
                    // Cleared and redrawn from scratch every frame: no dirty-region bookkeeping and
                    // no layout pass, so what the painter draws is exactly what lands in the pipe.
                    bitmap.Clear();
                    using (DrawingContext drawingContext = frameVisual.RenderOpen())
                    {
                        foreach (Slot slot in slots)
                        {
                            if (slot.Visible)
                            {
                                slot.Element.PaintFrame(drawingContext, width, height);
                            }
                        }
                    }

                    bitmap.Render(frameVisual);
                    bitmap.CopyPixels(frame, stride, 0);

                    if (!diagnosticsWritten)
                    {
                        diagnosticsWritten = true;
                        WriteDiagnostics(width, height, fps, slots.Count, cues.Count, frame, stride, bitmap);
                    }
                }
                else
                {
                    Array.Clear(frame, 0, frame.Length);
                }

                try
                {
                    destination.Write(frame, 0, frame.Length);
                }
                catch (IOException)
                {
                    // ffmpeg stops reading when the picture ends (a source shorter than the cue
                    // list, for example); the export itself is fine, so stop feeding it.
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (progress != null && index % 8 == 0)
                {
                    progress.Report((double)index / totalFrames);
                }
            }

            destination.Flush();
            progress?.Report(1.0);
        }

        /// <summary>
        /// Writes what the first painted frame actually contained, plus the frame itself as a PNG.
        /// A burn-in that comes out empty is almost always a size or an alpha problem, and neither
        /// can be told apart from the video alone.
        /// </summary>
        private static void WriteDiagnostics(
            int width,
            int height,
            double frameRate,
            int overlayCount,
            int cueCount,
            byte[] frame,
            int stride,
            BitmapSource bitmap)
        {
            try
            {
                int opaque = 0;
                int partial = 0;
                int minimumX = width;
                int minimumY = height;
                int maximumX = -1;
                int maximumY = -1;

                for (int y = 0; y < height; y++)
                {
                    int rowOffset = y * stride;
                    for (int x = 0; x < width; x++)
                    {
                        byte alpha = frame[rowOffset + (x * 4) + 3];
                        if (alpha == 0)
                        {
                            continue;
                        }

                        if (alpha == 255)
                        {
                            opaque++;
                        }
                        else
                        {
                            partial++;
                        }

                        if (x < minimumX)
                        {
                            minimumX = x;
                        }

                        if (y < minimumY)
                        {
                            minimumY = y;
                        }

                        if (x > maximumX)
                        {
                            maximumX = x;
                        }

                        if (y > maximumY)
                        {
                            maximumY = y;
                        }
                    }
                }

                StringBuilder report = new StringBuilder();
                report.AppendLine("SubtitleForge burn-in diagnostics");
                report.AppendLine("frame: " + width.ToString(CultureInfo.InvariantCulture)
                    + "x" + height.ToString(CultureInfo.InvariantCulture)
                    + " at " + frameRate.ToString("0.###", CultureInfo.InvariantCulture) + " fps");
                report.AppendLine("cues: " + cueCount.ToString(CultureInfo.InvariantCulture)
                    + ", overlays: " + overlayCount.ToString(CultureInfo.InvariantCulture));
                report.AppendLine("pixels with alpha: " + (opaque + partial).ToString(CultureInfo.InvariantCulture)
                    + " (opaque " + opaque.ToString(CultureInfo.InvariantCulture)
                    + ", partial " + partial.ToString(CultureInfo.InvariantCulture) + ")");
                report.AppendLine(maximumX < 0
                    ? "painted area: none, the frame came out fully transparent"
                    : "painted area: x " + minimumX.ToString(CultureInfo.InvariantCulture)
                        + ".." + maximumX.ToString(CultureInfo.InvariantCulture)
                        + ", y " + minimumY.ToString(CultureInfo.InvariantCulture)
                        + ".." + maximumY.ToString(CultureInfo.InvariantCulture));

                string? folder = DiagnosticFolder();
                if (folder == null)
                {
                    return;
                }

                File.WriteAllText(Path.Combine(folder, "burn-in-diagnostics.txt"), report.ToString());

                PngBitmapEncoder encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (FileStream stream = File.Create(Path.Combine(folder, "burn-in-first-frame.png")))
                {
                    encoder.Save(stream);
                }
            }
            catch (Exception)
            {
                // Diagnostics must never break an export.
            }
        }

        /// <summary>
        /// Records the exact ffmpeg invocation next to the frame dump, so a broken burn-in can be
        /// reproduced outside the app.
        /// </summary>
        private static void WriteCommand(string executablePath, IEnumerable<string> arguments, int width, int height)
        {
            try
            {
                string? folder = DiagnosticFolder();
                if (folder == null)
                {
                    return;
                }

                StringBuilder command = new StringBuilder();
                command.AppendLine("frame: " + width.ToString(CultureInfo.InvariantCulture)
                    + "x" + height.ToString(CultureInfo.InvariantCulture));
                command.Append(executablePath);
                foreach (string argument in arguments)
                {
                    command.Append(' ');
                    command.Append(argument.IndexOf(' ') >= 0 ? "\"" + argument + "\"" : argument);
                }

                File.WriteAllText(Path.Combine(folder, "burn-in-command.txt"), command.ToString());
            }
            catch (Exception)
            {
                // Diagnostics must never break an export.
            }
        }

        private static string? DiagnosticFolder()
        {
            try
            {
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SubtitleForge");
                Directory.CreateDirectory(path);
                return path;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private sealed class Slot
        {
            public Slot(BurnInCue cue, SubtitleOverlay element)
            {
                Cue = cue;
                Element = element;
            }

            public BurnInCue Cue { get; }

            /// <summary>Whether the cue covers the frame currently being drawn.</summary>
            public bool Visible { get; set; }

            public SubtitleOverlay Element { get; }
        }
    }
}
