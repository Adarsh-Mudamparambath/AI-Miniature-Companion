using UnityEngine;

namespace Mini.Core
{
    /// <summary>
    /// High-level companion states owned by the conversation flow. Each one is presented on the
    /// character by <c>CharacterManager</c>, which maps it to a looping <see cref="CharacterState"/>,
    /// an emotion and a look target.
    /// </summary>
    public enum CompanionState
    {
        Initialising,
        WakeUp,
        Idle,
        Listening,
        Processing,
        Speaking,
        Reacting,
        Sleeping,
        Error
    }

    /// <summary>Facial emotion that blends on top of the current body state.</summary>
    public enum CharacterEmotion
    {
        Neutral,
        Happy,
        Sad,
        Surprised,
        Confused,
        Curious,
        Thinking,
        Love,
        Angry
    }

    /// <summary>
    /// Mouth shape categories (nine, covering the eight-plus viseme requirement). The primitive
    /// robot approximates them with mouth width; a rigged Blender face can map each one to a blend shape.
    /// </summary>
    public enum Viseme
    {
        Rest,   // closed / neutral
        AA,     // "father", "cat"
        EE,     // "see", "say"
        IH,     // "sit", "bed"
        OH,     // "go", "law"
        OO,     // "you", "who"
        FV,     // "five", "very"
        MBP,    // "mom", "bob", "pop" (lips closed)
        LTD     // "late", "dog", "nine"
    }

    /// <summary>How much extra head motion Mini adds while speaking.</summary>
    public enum HeadMotion
    {
        None,
        Subtle,
        Expressive
    }

    /// <summary>
    /// Optional face controls. A character visual implements this next to <see cref="ICharacterVisual"/>
    /// when it has an expressive face. Behaviour code checks for it and degrades gracefully if absent.
    /// </summary>
    public interface ICharacterFace
    {
        /// <summary>Blend toward an emotion. Intensity 0..1.</summary>
        void SetEmotion(CharacterEmotion emotion, float intensity);

        /// <summary>Make the head and eyes follow a world-space point, or stop following with null.</summary>
        void SetLookTarget(Vector3? worldPosition);

        /// <summary>Drive the mouth from speech audio. Openness 0..1. Overrides the procedural speaking mouth.</summary>
        void SetSpeechMouth(float openness, Viseme viseme);

        /// <summary>Hand the mouth back to the procedural state animation.</summary>
        void ReleaseSpeechMouth();
    }
}
