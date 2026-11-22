namespace Mini.Core
{
    /// <summary>
    /// Persistent, looping character states. One-shot actions live in <see cref="CharacterGesture"/>.
    /// </summary>
    public enum CharacterState
    {
        Idle = 0,
        Listening = 1,
        Thinking = 2,
        Speaking = 3,
        Sleeping = 4
    }
}
