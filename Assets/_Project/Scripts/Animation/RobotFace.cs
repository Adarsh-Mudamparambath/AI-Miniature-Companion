using UnityEngine;

// LED face for the robot.
// Setup (once):
//  1. Import FaceEmotions.png -> Texture Type: Default, Filter Mode: Point (no filter),
//     Compression: None, Wrap Mode: Clamp.
//  2. On the "FaceScreen" material of Robot.fbx (extract materials first):
//     Base Map = FaceEmotions, Emission ON, Emission Map = FaceEmotions, Emission Color = white (intensity ~1-2).
//  3. Add this component to the robot (same GameObject as the Animator is fine).
// Use: set Emotion in the Inspector, call SetEmotion(...) from code,
//      or keyframe "Emotion Index" (0-7) in any Animation clip.
[ExecuteAlways]
public class RobotFace : MonoBehaviour
{
    public enum Emotion { Neutral = 0, Happy = 1, Blink = 2, Sad = 3, Angry = 4, Surprised = 5, Love = 6, Sleepy = 7 }

    public Emotion emotion = Emotion.Happy;
    [Tooltip("Animatable: 0 Neutral, 1 Happy, 2 Blink, 3 Sad, 4 Angry, 5 Surprised, 6 Love, 7 Sleepy")]
    [Range(0, 7)] public float emotionIndex = 1;

    [Header("Auto blink (play mode)")]
    public bool autoBlink = true;
    public float blinkEvery = 3.5f;
    public float blinkLength = 0.12f;

    [Header("Renderer")]
    public Renderer targetRenderer;
    public string faceMaterialName = "FaceScreen";

    const int Frames = 8;
    MaterialPropertyBlock mpb;
    int matIndex = -1;
    float blinkTimer, blinkLeft;
    Emotion lastEnum;

    void OnEnable() { Setup(); lastEnum = emotion; }

    void OnValidate()
    {
        if (emotion != lastEnum) { emotionIndex = (int)emotion; lastEnum = emotion; }
        Setup(); Apply(Mathf.RoundToInt(emotionIndex));
    }

    void Setup()
    {
        if (!targetRenderer) targetRenderer = GetComponentInChildren<SkinnedMeshRenderer>();
        if (!targetRenderer) return;
        var mats = targetRenderer.sharedMaterials;
        matIndex = -1;
        for (int i = 0; i < mats.Length; i++)
            if (mats[i] && mats[i].name.Contains(faceMaterialName)) { matIndex = i; break; }
    }

    public void SetEmotion(Emotion e) { emotion = e; lastEnum = e; emotionIndex = (int)e; }
    public void SetEmotion(int index) { SetEmotion((Emotion)Mathf.Clamp(index, 0, Frames - 1)); }

    void LateUpdate()
    {
        int idx = Mathf.Clamp(Mathf.RoundToInt(emotionIndex), 0, Frames - 1);
        if (Application.isPlaying && autoBlink && (idx == 0 || idx == 1))
        {
            if (blinkLeft > 0f) { blinkLeft -= Time.deltaTime; idx = (int)Emotion.Blink; }
            else if ((blinkTimer += Time.deltaTime) >= blinkEvery) { blinkTimer = 0f; blinkLeft = blinkLength; }
        }
        Apply(idx);
    }

    void Apply(int idx)
    {
        if (!targetRenderer || matIndex < 0) return;
        if (mpb == null) mpb = new MaterialPropertyBlock();
        targetRenderer.GetPropertyBlock(mpb, matIndex);
        var st = new Vector4(1f / Frames, 1f, idx / (float)Frames, 0f);   // tiling x, y, offset x, y
        mpb.SetVector("_BaseMap_ST", st);   // URP
        mpb.SetVector("_MainTex_ST", st);   // Built-in
        targetRenderer.SetPropertyBlock(mpb, matIndex);
    }
}
