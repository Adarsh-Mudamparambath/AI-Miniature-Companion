# Mini backend (free, runs on your PC)

Mini's "brain" runs here, on your PC, using free open-source models. No accounts, no API keys, no cost.

| Job | Free model | Folder |
|---|---|---|
| Hear (speech to text) | Whisper `small.en` (faster-whisper) | `models/whisper` |
| Think (conversation) | Llama 3.2 3B via Ollama | managed by Ollama |
| Speak (text to speech) | Piper voice `en_US-amy-medium` | `models/piper` |

## Every time you want to use Mini
1. Plug the Quest into the PC with the USB cable.
2. Double-click **`start_mini_server.bat`** and wait for `Models ready`.
3. Put on the headset and open **AI_Miniature_Companion** from App Library → Unknown Sources.

Ollama starts with Windows automatically. If Mini says it can't reach its server, check the black server window is still open.

## Test without the headset
`python -P test_server.py` → `ALL BACKEND TESTS PASSED` (uses mock providers).

Audio and text are processed in memory only and never saved. Everything stays on your PC.
