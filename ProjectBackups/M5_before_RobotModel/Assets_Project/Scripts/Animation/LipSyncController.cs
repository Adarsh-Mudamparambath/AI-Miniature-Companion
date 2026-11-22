using UnityEngine;
using Mini.Core;

namespace Mini.Animation
{
    /// <summary>
    /// Interim audio-driven lip sync. While Mini's voice AudioSource plays, it reads the actual output
    /// samples every frame and turns them into mouth openness (loudness) and a rough mouth shape
    /// (zero-crossing rate: hissy sounds read as EE/FV, low buzzy sounds as OH/AA).
    ///
    /// This is synchronised to the real audio, but it is not phoneme-accurate. When a TTS provider can
    /// return viseme timings, feed them through <see cref="ICharacterFace.SetSpeechMouth"/> instead.
    /// </summary>
    [DisallowMultipleComponent]
    public class LipSyncController : MonoBehaviour
    {
        [SerializeField] private AudioSource voiceSource;
        [Tooltip("Object whose children contain the ICharacterFace (usually the Mini prefab instance).")]
        [SerializeField] private Transform characterRoot;

        [SerializeField] private float gain = 12f;
        [SerializeField] private float noiseGate = 0.01f;
        [SerializeField] private float attack = 30f;
        [SerializeField] private float release = 14f;

        private const int WindowSize = 256;
        private readonly float[] window = new float[WindowSize];
        private ICharacterFace face;
        private float openness;
        private bool driving;

        public AudioSource VoiceSource { get { return voiceSource; } }

        /// <summary>Current mouth openness 0..1 (also used by tests and the HUD).</summary>
        public float Openness { get { return openness; } }

        private void Awake()
        {
            ResolveFace();
        }

        public void ResolveFace()
        {
            face = characterRoot != null ? characterRoot.GetComponentInChildren<ICharacterFace>(true) : null;
        }

        private void Update()
        {
            if (voiceSource == null || face == null) return;

            if (!voiceSource.isPlaying)
            {
                if (driving) StopDriving();
                return;
            }

            voiceSource.GetOutputData(window, 0);

            float sum = 0f;
            int crossings = 0;
            for (int i = 0; i < WindowSize; i++)
            {
                float s = window[i];
                sum += s * s;
                if (i > 0 && (s >= 0f) != (window[i - 1] >= 0f)) crossings++;
            }

            float rms = Mathf.Sqrt(sum / WindowSize);
            float target = rms < noiseGate ? 0f : Mathf.Clamp01((rms - noiseGate) * gain);
            float rate = target > openness ? attack : release;
            openness = Mathf.Lerp(openness, target, 1f - Mathf.Exp(-rate * Time.deltaTime));

            float zcr = crossings / (float)WindowSize;
            face.SetSpeechMouth(openness, PickViseme(openness, zcr));
            driving = true;
        }

        private static Viseme PickViseme(float open, float zcr)
        {
            if (open < 0.05f) return Viseme.MBP;
            if (zcr > 0.30f) return open < 0.35f ? Viseme.FV : Viseme.EE;
            if (zcr > 0.18f) return Viseme.IH;
            if (open > 0.6f) return Viseme.AA;
            return zcr < 0.08f ? Viseme.OO : Viseme.OH;
        }

        private void StopDriving()
        {
            driving = false;
            openness = 0f;
            if (face != null) face.ReleaseSpeechMouth();
        }

        private void OnDisable()
        {
            if (driving) StopDriving();
        }
    }
}
