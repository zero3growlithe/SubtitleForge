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

            Grid host = new Grid();
            host.Width = width;
            host.Height = height;

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
                host.Children.Add(overlay);
                slots.Add(new Slot(cue, overlay));
            }

            host.Measure(new Size(width, height));
            host.Arrange(new Rect(0.0, 0.0, width, height));

            RenderTargetBitmap bitmap = new RenderTargetBitmap(width, height, 96.0, 96.0, PixelFormats.Pbgra32);
            int stride = width * 4;
            byte[] frame = new byte[stride * height];

            for (long index = 0; index < totalFrames; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                double time = index / fps;
                bool anythingVisible = false;

                foreach (Slot slot in slots)
                {
                    bool visible = time >= slot.Cue.StartSeconds && time < slot.Cue.EndSeconds;
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
                    // Clearing first makes the render pass redraw every dirty glyph instead of
                    // keeping the previous frame's commands.
                    bitmap.Clear();
                    bitmap.Render(host);
                    bitmap.CopyPixels(frame, stride, 0);
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

        private sealed class Slot
        {
            public Slot(BurnInCue cue, SubtitleOverlay element)
            {
                Cue = cue;
                Element = element;
            }

            public BurnInCue Cue { get; }

            public SubtitleOverlay Element { get; }
        }
    }
}
