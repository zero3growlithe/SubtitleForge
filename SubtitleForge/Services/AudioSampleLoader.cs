using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SubtitleForge.Models;

namespace SubtitleForge.Services
{
    /// <summary>
    /// Decodes the first audio stream of a media file into 16 kHz mono samples for the
    /// timeline, streaming from ffmpeg so a long file is never held twice in memory.
    /// </summary>
    public static class AudioSampleLoader
    {
        public static async Task<short[]> LoadAsync(string ffmpegPath, string mediaPath, CancellationToken cancellationToken)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = ffmpegPath;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.RedirectStandardInput = true;

            string[] arguments =
            {
                "-nostdin",
                "-hide_banner",
                "-loglevel", "error",
                "-i", mediaPath,
                "-map", "0:a:0",
                "-vn",
                "-sn",
                "-dn",
                "-ac", "1",
                "-ar", TimelineTrack.SampleRate.ToString(CultureInfo.InvariantCulture),
                "-f", "s16le",
                "pipe:1"
            };

            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using (Process process = new Process())
            {
                process.StartInfo = startInfo;

                if (!process.Start())
                {
                    throw new InvalidOperationException("Could not start ffmpeg for audio decoding.");
                }

                Task<string> errorTask = process.StandardError.ReadToEndAsync();

                try
                {
                    process.StandardInput.Close();
                }
                catch (Exception)
                {
                    // Already gone.
                }

                List<short[]> chunks = new List<short[]>();
                long totalSamples = 0L;
                byte[] buffer = new byte[262144];
                byte[] leftover = Array.Empty<byte>();

                using (Stream output = process.StandardOutput.BaseStream)
                {
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int read = await output.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                        if (read <= 0)
                        {
                            break;
                        }

                        int available = leftover.Length + read;
                        byte[] combined;
                        if (leftover.Length > 0)
                        {
                            combined = new byte[available];
                            Array.Copy(leftover, combined, leftover.Length);
                            Array.Copy(buffer, 0, combined, leftover.Length, read);
                        }
                        else
                        {
                            combined = new byte[available];
                            Array.Copy(buffer, combined, read);
                        }

                        int usableBytes = available - (available % 2);
                        int sampleCount = usableBytes / 2;
                        if (sampleCount > 0)
                        {
                            short[] chunk = new short[sampleCount];
                            Buffer.BlockCopy(combined, 0, chunk, 0, usableBytes);
                            chunks.Add(chunk);
                            totalSamples += sampleCount;
                        }

                        leftover = usableBytes == available
                            ? Array.Empty<byte>()
                            : new[] { combined[available - 1] };
                    }
                }

                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                string errorText = await errorTask.ConfigureAwait(false);

                if (totalSamples == 0L)
                {
                    string details = string.IsNullOrWhiteSpace(errorText)
                        ? "the file has no decodable audio stream"
                        : AudioTrackExtractor.FirstMeaningfulLines(errorText);
                    throw new InvalidOperationException("Could not read the audio track: " + details);
                }

                if (totalSamples > int.MaxValue - 2)
                {
                    throw new InvalidOperationException("The audio track is longer than this build can hold in memory.");
                }

                short[] samples = new short[(int)totalSamples];
                int offset = 0;
                foreach (short[] chunk in chunks)
                {
                    Array.Copy(chunk, 0, samples, offset, chunk.Length);
                    offset += chunk.Length;
                }

                return samples;
            }
        }
    }
}
