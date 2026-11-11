using System;
using System.IO;

namespace SubtitleForge.Services
{
    /// <summary>
    /// Reads and writes the 16 kHz mono 16-bit PCM WAV files used for recorded clips.
    /// Only what the app itself produces is supported on the write side; the reader also
    /// accepts stereo and other sample rates (downmix + linear resample) so an externally
    /// edited clip still loads.
    /// </summary>
    public static class WavFile
    {
        public const int SampleRate = 16000;

        /// <summary>
        /// Writes <paramref name="count"/> samples starting at <paramref name="start"/>.
        /// </summary>
        public static void Write(string path, short[] samples, int start, int count)
        {
            if (samples == null)
            {
                throw new ArgumentNullException(nameof(samples));
            }

            if (start < 0)
            {
                start = 0;
            }

            if (start > samples.Length)
            {
                start = samples.Length;
            }

            if (count < 0)
            {
                count = 0;
            }

            if (start + count > samples.Length)
            {
                count = samples.Length - start;
            }

            string? folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }

            int dataBytes = count * 2;

            using (FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                writer.Write(new char[] { 'R', 'I', 'F', 'F' });
                writer.Write(36 + dataBytes);
                writer.Write(new char[] { 'W', 'A', 'V', 'E' });
                writer.Write(new char[] { 'f', 'm', 't', ' ' });
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)1);
                writer.Write(SampleRate);
                writer.Write(SampleRate * 2);
                writer.Write((short)2);
                writer.Write((short)16);
                writer.Write(new char[] { 'd', 'a', 't', 'a' });
                writer.Write(dataBytes);

                for (int index = 0; index < count; index++)
                {
                    writer.Write(samples[start + index]);
                }
            }
        }

        /// <summary>
        /// Reads a PCM 16-bit WAV and returns 16 kHz mono samples.
        /// </summary>
        public static short[] Read(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length < 44)
            {
                throw new InvalidDataException("The clip is too short to be a WAV file.");
            }

            if (bytes[0] != 'R' || bytes[1] != 'I' || bytes[2] != 'F' || bytes[3] != 'F')
            {
                throw new InvalidDataException("The clip is not a RIFF/WAV file.");
            }

            int channels = 1;
            int sampleRate = SampleRate;
            int bitsPerSample = 16;
            int dataOffset = -1;
            int dataLength = 0;

            int position = 12;
            while (position + 8 <= bytes.Length)
            {
                string chunkId = System.Text.Encoding.ASCII.GetString(bytes, position, 4);
                int chunkSize = BitConverter.ToInt32(bytes, position + 4);
                int chunkStart = position + 8;

                if (chunkId == "fmt " && chunkStart + 16 <= bytes.Length)
                {
                    channels = BitConverter.ToInt16(bytes, chunkStart + 2);
                    sampleRate = BitConverter.ToInt32(bytes, chunkStart + 4);
                    bitsPerSample = BitConverter.ToInt16(bytes, chunkStart + 14);
                }
                else if (chunkId == "data")
                {
                    dataOffset = chunkStart;
                    dataLength = Math.Min(chunkSize, bytes.Length - chunkStart);
                    break;
                }

                if (chunkSize <= 0)
                {
                    break;
                }

                position = chunkStart + chunkSize + (chunkSize % 2);
            }

            if (dataOffset < 0 || dataLength <= 0)
            {
                throw new InvalidDataException("The clip has no audio data.");
            }

            if (bitsPerSample != 16)
            {
                throw new InvalidDataException("Only 16-bit PCM clips are supported.");
            }

            if (channels <= 0)
            {
                channels = 1;
            }

            int frameCount = dataLength / (2 * channels);
            short[] mono = new short[frameCount];

            for (int frame = 0; frame < frameCount; frame++)
            {
                int offset = dataOffset + (frame * channels * 2);
                mono[frame] = BitConverter.ToInt16(bytes, offset);
            }

            if (sampleRate == SampleRate)
            {
                return mono;
            }

            return Resample(mono, sampleRate, SampleRate);
        }

        private static short[] Resample(short[] source, int sourceRate, int targetRate)
        {
            if (sourceRate <= 0 || targetRate <= 0 || source.Length < 2)
            {
                return Array.Empty<short>();
            }

            double ratio = sourceRate / (double)targetRate;
            int outputLength = (int)(source.Length / ratio);
            if (outputLength <= 0)
            {
                return Array.Empty<short>();
            }

            short[] output = new short[outputLength];
            int lastIndex = source.Length - 2;

            for (int index = 0; index < outputLength; index++)
            {
                double position = index * ratio;
                int first = (int)position;
                if (first > lastIndex)
                {
                    first = lastIndex;
                }

                double fraction = position - first;
                double value = (source[first] * (1.0 - fraction)) + (source[first + 1] * fraction);
                if (value > short.MaxValue)
                {
                    value = short.MaxValue;
                }

                if (value < short.MinValue)
                {
                    value = short.MinValue;
                }

                output[index] = (short)value;
            }

            return output;
        }
    }
}
