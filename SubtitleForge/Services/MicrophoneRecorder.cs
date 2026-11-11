using System;
using System.Collections.Generic;
using NAudio.Wave;

namespace SubtitleForge.Services
{
    public class MicrophoneSamplesEventArgs : EventArgs
    {
        public MicrophoneSamplesEventArgs(short[] samples)
        {
            Samples = samples;
        }

        public short[] Samples { get; }
    }

    /// <summary>
    /// Captures the default microphone and always delivers 16 kHz mono 16-bit PCM, matching
    /// the timeline sample format, so recorded audio lines up with the video waveform.
    /// Devices that refuse 16 kHz are opened at 44.1/48 kHz and linearly resampled here.
    /// </summary>
    public class MicrophoneRecorder : IDisposable
    {
        private static readonly int[] PreferredRates = { 16000, 48000, 44100, 32000 };

        private WaveInEvent? capture;
        private int captureRate;

        public bool IsRecording
        {
            get { return capture != null; }
        }

        /// <summary>
        /// Rate the device was actually opened at (16 kHz means no resampling happens).
        /// </summary>
        public int CaptureRate
        {
            get { return captureRate; }
        }

        public event EventHandler<MicrophoneSamplesEventArgs>? SamplesAvailable;

        /// <summary>
        /// Throws when there is no capture device or every supported format is rejected.
        /// </summary>
        public void Start()
        {
            if (capture != null)
            {
                return;
            }

            List<string> failures = new List<string>();

            foreach (int rate in PreferredRates)
            {
                WaveInEvent device = new WaveInEvent();
                device.WaveFormat = new WaveFormat(rate, 16, 1);
                device.BufferMilliseconds = 50;
                device.NumberOfBuffers = 3;
                device.DataAvailable += OnDataAvailable;

                try
                {
                    device.StartRecording();
                    capture = device;
                    captureRate = rate;
                    return;
                }
                catch (Exception exception)
                {
                    device.DataAvailable -= OnDataAvailable;
                    device.Dispose();
                    failures.Add(rate.ToString() + " Hz: " + exception.Message);
                }
            }

            throw new InvalidOperationException(
                "No usable microphone input format was found. Tried " + string.Join("; ", failures) + ".");
        }

        public void Stop()
        {
            WaveInEvent? device = capture;
            capture = null;
            captureRate = 0;

            if (device == null)
            {
                return;
            }

            device.DataAvailable -= OnDataAvailable;
            try
            {
                device.StopRecording();
            }
            catch (Exception)
            {
                // Device already gone.
            }

            device.Dispose();
        }

        public void Dispose()
        {
            Stop();
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs eventArgs)
        {
            int sampleCount = eventArgs.BytesRecorded / 2;
            if (sampleCount <= 1)
            {
                return;
            }

            short[] captured = new short[sampleCount];
            Buffer.BlockCopy(eventArgs.Buffer, 0, captured, 0, sampleCount * 2);

            short[] normalized = captureRate == 16000 ? captured : ResampleTo16k(captured, captureRate);
            if (normalized.Length == 0)
            {
                return;
            }

            EventHandler<MicrophoneSamplesEventArgs>? handler = SamplesAvailable;
            if (handler == null)
            {
                return;
            }

            // Called from the capture thread; the window marshals this onto the UI thread.
            handler(this, new MicrophoneSamplesEventArgs(normalized));
        }

        private static short[] ResampleTo16k(short[] source, int sourceRate)
        {
            if (sourceRate <= 0 || source.Length < 2)
            {
                return Array.Empty<short>();
            }

            double ratio = sourceRate / 16000.0;
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
