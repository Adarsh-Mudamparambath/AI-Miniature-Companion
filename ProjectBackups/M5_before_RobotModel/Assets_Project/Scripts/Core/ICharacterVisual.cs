namespace Mini.Core
{
    /// <summary>
    /// Contract between the character behaviour layer and whatever model presents it.
    /// The primitive prototype and the future Blender character each provide their own
    /// implementation. Behaviour code must only talk to the visual through this interface.
    /// </summary>
    public interface ICharacterVisual
    {
        /// <summary>Switch the looping state (idle, listening, thinking, speaking).</summary>
        void SetState(CharacterState state);

        /// <summary>Start a one-shot gesture. Replaces a running gesture on the same channel.</summary>
        void PlayGesture(CharacterGesture gesture);

        /// <summary>Stop all running gestures and return to the current state pose.</summary>
        void CancelGestures();
    }
}
