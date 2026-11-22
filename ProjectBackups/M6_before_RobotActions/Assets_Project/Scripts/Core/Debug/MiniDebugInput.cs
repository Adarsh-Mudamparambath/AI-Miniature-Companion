#if UNITY_EDITOR
using UnityEngine;
using UnityEngine.InputSystem;
using Mini.Character;

namespace Mini.Core.Debugging
{
    /// <summary>
    /// Editor-only keyboard shortcuts for testing Mini. The whole class is compiled out of player
    /// builds, so it can never become part of the Quest 2 runtime controls.
    ///
    /// 1 Idle, 2 LookLeft, 3 LookRight, 4 Blink, 5 Nod, 6 Wave, 7 Listening, 8 Thinking, 9 Speaking.
    /// </summary>
    [DisallowMultipleComponent]
    public class MiniDebugInput : MonoBehaviour
    {
        [SerializeField] private MiniCharacterController character;

        private void Awake()
        {
            // A reference to a prefab asset (instead of the scene instance) must not be used.
            if (character == null || !character.gameObject.scene.IsValid())
            {
                character = FindFirstObjectByType<MiniCharacterController>();
            }
        }

        private void Update()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null || character == null) return;

            if (keyboard.digit1Key.wasPressedThisFrame) character.SetState(CharacterState.Idle);
            if (keyboard.digit2Key.wasPressedThisFrame) character.PlayGesture(CharacterGesture.LookLeft);
            if (keyboard.digit3Key.wasPressedThisFrame) character.PlayGesture(CharacterGesture.LookRight);
            if (keyboard.digit4Key.wasPressedThisFrame) character.PlayGesture(CharacterGesture.Blink);
            if (keyboard.digit5Key.wasPressedThisFrame) character.PlayGesture(CharacterGesture.Nod);
            if (keyboard.digit6Key.wasPressedThisFrame) character.PlayGesture(CharacterGesture.Wave);
            if (keyboard.digit7Key.wasPressedThisFrame) character.SetState(CharacterState.Listening);
            if (keyboard.digit8Key.wasPressedThisFrame) character.SetState(CharacterState.Thinking);
            if (keyboard.digit9Key.wasPressedThisFrame) character.SetState(CharacterState.Speaking);
        }
    }
}
#endif
