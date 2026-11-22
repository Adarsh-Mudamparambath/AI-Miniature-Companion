using System;
using UnityEngine;

namespace Mini.Voice
{
    /// <summary>
    /// Thin wrapper around Unity's Microphone API. Records into a looping clip and hands out new samples
    /// in fixed chunks without allocating every frame. Audio stays in memory only; nothing is written to disk.
    /// </summary>
    public sealed class MicrophoneInput
    {
        public const int ChunkSize = 160;

        private readonly float[] chunk = new float[ChunkSize];
        private string device;
        private AudioClip clip;
        private int readPosition;

        public int SampleRate { get; private set; }
        public bool IsRecording { get { return clip != null && Microphone.IsRecording(device); } }

        public static bool HasDevice { get { return Microphone.devices != null && Microphone.devices.Length > 0; } }

        public bool Start(string deviceName, int preferredRate, int loopSeconds = 10)
        {
            Stop();
            if (!HasDevice) return false;

            device = string.IsNullOrEmpty(deviceName) ? null : deviceName;
            int minRate, maxRate;
            Microphone.GetDeviceCaps(device, out minRate, out maxRate);
            int rate = preferredRate;
            if (maxRate > 0) rate = Mathf.Clamp(rate, Mathf.Max(minRate, 1), maxRate);

            clip = Microphone.Start(device, true, loopSeconds, rate);
            if (clip == null) return false;

            SampleRate = clip.frequency;
            readPosition = 0;
            return true;
        }

        public void Stop()
        {
            if (clip == null) return;
            if (Microphone.IsRecording(device)) Microphone.End(device);
            UnityEngine.Object.Destroy(clip);
            clip = null;
            readPosition = 0;
        }

        /// <summary>Deliver every complete new chunk recorded since the last call.</summary>
        public void Read(Action<float[], int> onChunk)
        {
            if (clip == null || onChunk == null) return;

            int writePosition = Microphone.GetPosition(device);
            if (writePosition < 0) return;

            int total = clip.samples;
            int available = writePosition - readPosition;
            if (available < 0) available += total;

            while (available >= ChunkSize)
            {
                if (readPosition + ChunkSize <= total)
                {
                    clip.GetData(chunk, readPosition);
                }
                else
                {
                    // Chunk wraps the end of the loop buffer: read the two halves separately.
                    int first = total - readPosition;
                    float[] head = new float[first];
                    float[] tail = new float[ChunkSize - first];
                    clip.GetData(head, readPosition);
                    clip.GetData(tail, 0);
                    Array.Copy(head, 0, chunk, 0, first);
                    Array.Copy(tail, 0, chunk, first, tail.Length);
                }

                onChunk(chunk, ChunkSize);
                readPosition = (readPosition + ChunkSize) % total;
                available -= ChunkSize;
            }
        }
    }
}
