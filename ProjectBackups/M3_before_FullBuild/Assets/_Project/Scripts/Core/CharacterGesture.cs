namespace Mini.Core
{
    /// <summary>
    /// One-shot gestures a character visual can perform. Playing a gesture replaces any
    /// gesture currently running on the same body channel (head, arm or eyes).
    /// </summary>
    public enum CharacterGesture
    {
        LookLeft,
        LookRight,
        Blink,
        Nod,
        Wave
    }
}
