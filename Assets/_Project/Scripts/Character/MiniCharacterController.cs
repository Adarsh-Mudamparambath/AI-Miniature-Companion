using System;
using UnityEngine;
using Mini.Core;

namespace Mini.Character
{
    /// <summary>
    /// Behaviour layer for Mini. Owns the character state and forwards commands to whichever
    /// <see cref="ICharacterVisual"/> lives under <c>visualRoot</c>. It never references joints,
    /// meshes or hierarchy names, so the primitive model can be swapped for the final
    /// Blender character by replacing the child of <c>visualRoot</c>.
    /// </summary>
    [DisallowMultipleComponent]
    public class MiniCharacterController : MonoBehaviour
    {
        [Tooltip("Parent of the swappable visual model. The first child component implementing ICharacterVisual is used.")]
        [SerializeField] private Transform visualRoot;

        [SerializeField] private CharacterState initialState = CharacterState.Idle;

        private ICharacterVisual visual;

        public CharacterState State { get; private set; }
        public bool HasVisual { get { return visual != null; } }

        public event Action<CharacterState> StateChanged;

        private void Awake()
        {
            if (visualRoot == null)
            {
                visualRoot = transform;
            }

            ResolveVisual();
            State = initialState;
            if (visual != null)
            {
                visual.SetState(State);
            }
        }

        /// <summary>Find the visual under visualRoot again, e.g. after swapping the model.</summary>
        public void ResolveVisual()
        {
            // Prefer an active visual: a switched-off old model under the same root must not win.
            visual = visualRoot != null ? (visualRoot.GetComponentInChildren<ICharacterVisual>(false) ?? visualRoot.GetComponentInChildren<ICharacterVisual>(true)) : null;
            if (visual == null)
            {
                Debug.LogWarning("MiniCharacterController: no ICharacterVisual found under visualRoot.", this);
            }
        }

        /// <summary>Bind a visual directly (for example one created at runtime).</summary>
        public void SetVisual(ICharacterVisual newVisual)
        {
            visual = newVisual;
            if (visual != null)
            {
                visual.SetState(State);
            }
        }

        public void SetState(CharacterState state)
        {
            bool changed = state != State;
            State = state;

            if (visual != null)
            {
                visual.SetState(state);
            }

            if (changed && StateChanged != null)
            {
                StateChanged(state);
            }
        }

        public void PlayGesture(CharacterGesture gesture)
        {
            if (visual != null)
            {
                visual.PlayGesture(gesture);
            }
        }

        public void CancelGestures()
        {
            if (visual != null)
            {
                visual.CancelGestures();
            }
        }
    }
}
