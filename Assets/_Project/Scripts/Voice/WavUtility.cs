using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace Mini.Voice
{
    /// <summary>Minimal WAV (RIFF) encoder/decoder: 16-bit PCM out, 16-bit PCM or 32-bit float in.</summary>
    public static class WavUtility
    {
        public static byte[] EncodePcm16(float[] samples, int sampleRate, int channels = 1)
        {
            if (samples == null) samples = new float[0];
            int dataBytes = samples.Length * 2;

            using (MemoryStream ms = new MemoryStream(44 + dataBytes))
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(Encoding.ASCII.GetBytes("RIFF"));
                w.Write(36 + dataBytes);
                w.Write(Encoding.ASCII.GetBytes("WAVE"));
                w.Write(Encoding.ASCII.GetBytes("fmt "));
                w.Write(16);
                w.Write((short)1);
                w.Write((short)channels);
                w.Write(sampleRate);
                w.Write(sampleRate * channels * 2);
                w.Write((short)(channels * 2));
                w.Write((short)16);
                w.Write(Encoding.ASCII.GetBytes("data"));
                w.Write(dataBytes);
                for (int i = 0; i < samples.Length; i++)
                {
                    float s = Mathf.Clamp(samples[i], -1f, 1f);
                    w.Write((short)Mathf.RoundToInt(s * 32767f));
                }
                return ms.ToArray();
            }
        }

        public static bool TryDecode(byte[] wav, out float[] samples, out int sampleRate, out int channels)
        {
            samples = null;
            sampleRate = 0;
            channels = 0;
            if (wav == null || wav.Length < 44) return false;
            if (Encoding.ASCII.GetString(wav, 0, 4) != "RIFF" || Encoding.ASCII.GetString(wav, 8, 4) != "WAVE") return false;

            int format = 0, bits = 0;
            int pos = 12;
            while (pos + 8 <= wav.Length)
            {
                string id = Encoding.ASCII.GetString(wav, pos, 4);
                int size = BitConverter.ToInt32(wav, pos + 4);
                int body = pos + 8;
                if (size < 0) return false;

                if (id == "fmt " && body + 16 <= wav.Length)
                {
                    format = BitConverter.ToInt16(wav, body);
                    channels = BitConverter.ToInt16(wav, body + 2);
                    sampleRate = BitConverter.ToInt32(wav, body + 4);
                    bits = BitConverter.ToInt16(wav, body + 14);
                    if (format == -2 && size >= 26) format = BitConverter.ToInt16(wav, body + 24); // WAVE_FORMAT_EXTENSIBLE
                }
                else if (id == "data")
                {
                    if (channels <= 0 || sampleRate <= 0) return false;
                    // Streaming encoders sometimes write 0 or 0xFFFFFFFF; use what is actually there.
                    int available = wav.Length - body;
                    int length = size <= 0 || size > available ? available : size;

                    if (format == 1 && bits == 16)
                    {
                        int count = length / 2;
                        samples = new float[count];
                        for (int i = 0; i < count; i++) samples[i] = BitConverter.ToInt16(wav, body + i * 2) / 32768f;
                        return true;
                    }
                    if (format == 3 && bits == 32)
                    {
                        int count = length / 4;
                        samples = new float[count];
                        for (int i = 0; i < count; i++) samples[i] = BitConverter.ToSingle(wav, body + i * 4);
                        return true;
                    }
                    return false;
                }

                pos = body + size + (size & 1);
            }
            return false;
        }

        /// <summary>Decode WAV bytes into a new AudioClip. Must run on the main thread. Caller owns the clip.</summary>
        public static AudioClip ToAudioClip(byte[] wav, string name)
        {
            float[] samples;
            int rate, channels;
            if (!TryDecode(wav, out samples, out rate, out channels) || samples.Length < channels) return null;

            AudioClip clip = AudioClip.Create(name, samples.Length / channels, channels, rate, false);
            clip.SetData(samples, 0);
            return clip;
        }
    }
}
