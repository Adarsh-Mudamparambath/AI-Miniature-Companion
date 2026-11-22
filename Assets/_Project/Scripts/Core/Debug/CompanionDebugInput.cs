#if UNITY_EDITOR
using UnityEngine;
using UnityEngine.InputSystem;
using Mini.Conversation;
using Mini.MR;

namespace Mini.Core.Debugging
{
    /// <summary>
    /// Editor-only test panel for the companion loop. Compiled out of player builds and placed on the
    /// EditorOnly-tagged debug object, so it never ships.
    ///
    /// Type a message and press Send (or Return) to talk to Mini without a microphone.
    /// Keys: T = next practice phrase, X = interrupt, Z = sleep, C = "come here".
    /// The Milestone 1 keys 1-9 (MiniDebugInput) still work.
    /// </summary>
    public class CompanionDebugInput : MonoBehaviour
    {
        [SerializeField] private ConversationManager conversation;
        [SerializeField] private PlacementManager placement;

        private static readonly string[] Phrases =
        {
            "Hello Mini!", "What is your name?", "Tell me a joke.", "Move closer.", "Go back.",
            "Move to the left.", "Stay here.", "How are you?", "Go to sleep."
        };

        private string typed = "";
        private int phrase;
        private bool typingFocused;

        private void Awake()
        {
            if (conversation == null) conversation = FindFirstObjectByType<ConversationManager>();
            if (placement == null) placement = FindFirstObjectByType<PlacementManager>();
        }

        private void Update()
        {
            Keyboard k = Keyboard.current;
            if (k == null || conversation == null || typingFocused) return;

            if (k.tKey.wasPressedThisFrame) conversation.SubmitText(Phrases[phrase++ % Phrases.Length]);
            if (k.xKey.wasPressedThisFrame) conversation.Interrupt();
            if (k.zKey.wasPressedThisFrame) conversation.GoToSleep();
            if (k.cKey.wasPressedThisFrame && placement != null) placement.ComeHere();
        }

        private void OnGUI()
        {
            if (conversation == null) return;

            GUILayout.BeginArea(new Rect(10, 10, 420, 160), GUI.skin.box);
            GUILayout.Label("Mini: " + conversation.State + (placement != null && placement.IsPlacing ? "  (placing)" : ""));
            GUILayout.BeginHorizontal();
            GUI.SetNextControlName("MiniType");
            typed = GUILayout.TextField(typed, 200, GUILayout.Width(320));
            bool send = GUILayout.Button("Send");
            GUILayout.EndHorizontal();
            typingFocused = GUI.GetNameOfFocusedControl() == "MiniType";

            Event e = Event.current;
            if (typingFocused && e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter))
            {
                send = true;
                e.Use();
            }
            if (send && typed.Trim().Length > 0)
            {
                conversation.SubmitText(typed);
                typed = "";
            }
            GUILayout.Label("T phrase · X interrupt · Z sleep · C come here · P place · M mic · Space hold-to-talk");
            if (!string.IsNullOrEmpty(conversation.LastMiniText)) GUILayout.Label("Mini said: " + conversation.LastMiniText);
            GUILayout.EndArea();
        }
    }
}
#endif
