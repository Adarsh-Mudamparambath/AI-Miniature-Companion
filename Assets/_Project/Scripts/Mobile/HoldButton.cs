using UnityEngine;
using UnityEngine.EventSystems;

namespace Mini.Mobile
{
    /// <summary>Reports press and release of a UI element (used for hold-to-talk).</summary>
    public class HoldButton : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
    {
        public bool IsHeld { get; private set; }
        public bool PressedSinceLastRead { get; private set; }
        public bool ReleasedSinceLastRead { get; private set; }

        public void OnPointerDown(PointerEventData eventData)
        {
            IsHeld = true;
            PressedSinceLastRead = true;
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            IsHeld = false;
            ReleasedSinceLastRead = true;
        }

        public void ClearEvents()
        {
            PressedSinceLastRead = false;
            ReleasedSinceLastRead = false;
        }

        private void OnDisable()
        {
            if (IsHeld) ReleasedSinceLastRead = true;
            IsHeld = false;
        }
    }
}
