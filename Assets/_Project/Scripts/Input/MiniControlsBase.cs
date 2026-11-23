using UnityEngine;

namespace Mini.Controls
{
    /// <summary>
    /// The fallback controls the app and placement code read (voice is the main interface).
    /// <see cref="MiniControllerInput"/> implements it for Quest Touch controllers; a phone build uses
    /// a touch-screen implementation instead. Everything is polled once per frame.
    /// </summary>
    public abstract class MiniControlsBase : MonoBehaviour
    {
        /// <summary>Start placement, or confirm it if already placing.</summary>
        public abstract bool PlaceTogglePressed { get; }

        /// <summary>Confirm placement at the current preview position.</summary>
        public abstract bool ConfirmPressed { get; }

        /// <summary>"The pointer is touching the table now": use its height as the surface.</summary>
        public virtual bool CalibratePressed { get { return false; } }

        public abstract bool MicTogglePressed { get; }
        public abstract bool TalkPressed { get; }
        public abstract bool TalkReleased { get; }

        /// <summary>Stick/arrow input; y adjusts the manual surface height.</summary>
        public virtual Vector2 Adjust { get { return Vector2.zero; } }

        /// <summary>
        /// A world-space ray to aim placement with, when these controls supply one (e.g. a screen touch).
        /// Return false to let placement use its tracked pointer or the mouse.
        /// </summary>
        public virtual bool TryGetPointerRay(out Ray ray)
        {
            ray = default(Ray);
            return false;
        }

        /// <summary>Guidance shown while placing.</summary>
        public abstract string PlacementHint { get; }

        /// <summary>How to turn the microphone off, appended to the privacy notice.</summary>
        public abstract string MicOffHint { get; }

        /// <summary>Where to re-enable the microphone permission.</summary>
        public abstract string PermissionHint { get; }
    }
}
