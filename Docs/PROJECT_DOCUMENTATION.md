# AI Miniature Companion — Project Documentation

**Character:** Mini · **Target:** Meta Quest 2 · **Engine:** Unity 6000.3.11f1 (6.3 LTS), URP 17.3.0 · **Updated:** 14 November 2026

This document describes what is **actually implemented** in the project, how to run it, and what is still unverified.

---

## 0. Update — 15 November 2026: running on Quest 2 with free local AI

**Verified on the physical Quest 2:**
- the app installs and launches
- passthrough shows the room (grey)
- Mini is visible and stable
- **72/72 FPS** (app GPU about 6.2 ms)
- the microphone works
- Mini **understands what you say and answers correctly with a real voice**

**How to use it each time:**
1. Plug in the USB cable.
2. Double-click `Backend/start_mini_server.bat` and wait for "Models ready".
3. Open AI_Miniature_Companion from App Library → Unknown Sources.

**Free AI stack, all on the PC, no keys and no cost:**

| Job | Model | Measured on this PC |
|---|---|---|
| Hearing | Whisper `small.en` (faster-whisper, CPU int8) | ~2 s per sentence |
| Thinking | Llama 3.2 3B via Ollama 0.40.16 (GTX 1650) | ~2–3 s |
| Speaking | Piper `en_US-amy-medium` | ~0.15 s |

The Quest reaches the PC through **`adb reverse tcp:8787`** over the USB cable. The server only listens on `127.0.0.1`, so the Windows firewall isn't involved. (The PC's Wi-Fi is set to *Public*, which would block a wireless connection.)

**Project changes made for the Quest build** (backup of the earlier state: `ProjectBackups/M4_before_AndroidSwitch/`):
- **Active build target:** Android.
- **Android package name:** `com.minicompanion.mini`. The template's default name was already used by another app on the headset.
- **Android "Allow downloads over HTTP":** Always allowed. Needed so the Quest can talk to the local server.
- **`MiniSettings`:** Service Mode = Backend, Backend Url = `http://127.0.0.1:8787`, timeout 40 s.
- **Automatically added by the Unity/Meta build steps (Android only):**
  - Landscape Left orientation
  - static batching
  - preloaded `InputSystem_Actions` and `OculusRuntimeSettings`
- **Phase A settings still as approved:** Windows OpenXR off, Windows graphics API Automatic, `runInBackground` 0. Both Windows settings were briefly changed by a Meta "Fix" during the platform switch and **were restored**.

**Bugs found on the headset and fixed:**
1. **Build failed with "2 files found with path libopenxr_loader.so".**
   - Cause: Meta's build step only removes Unity's duplicate loader when **Android is selected** in Build Profiles.
   - Fix: select Android (`EditorUserBuildSettings.selectedBuildTargetGroup`) before building.
2. **The headset acted like the Unity Editor:**
   - the controller didn't move Mini
   - the camera was tilted
   - the passthrough log was skipped

   Cause: `XRSettings.isDeviceActive` is still false during `Start` on the headset. Fix: the Editor-only paths now also require `Application.isEditor`.
3. **No sound.** Android's app-operation `PLAY_AUDIO` was set to `ignore` for Mini after it was launched over adb while the headset was asleep. Fixed by resetting it (`adb shell appops set com.minicompanion.mini PLAY_AUDIO allow`). **Open Mini from the headset**, not from the PC while the headset sleeps.
4. **Controller buttons:** they're now read through both the Input System and Meta's `OVRInput`.
5. **Diagnostics:** the device log shows `[Mini] State →`, `Microphone →`, `Heard Ns`, `Speaking Ns` and `Button X`. No conversation text is ever logged.

**Not done yet:**
- Wireless (no-cable) connection. It needs the Wi-Fi set to Private or a firewall rule; that's your choice.
- The startup log label fix ("Backend AI (provider chosen on the server)") isn't in the installed APK yet. It's cosmetic only.
- Real-world checks still to make: placement on a table using the grip calibration, and comfort over a long session.

---

## 1. Status at a glance

| Area | Status | Verified how |
|---|---|---|
| Milestone 1 robot prototype | Done (extended, not replaced) | EditMode tests + `Mini_Prototype` Play Mode |
| Phase A XR config / Phase B Quest rendering | Done, unchanged | File hashes match backups |
| Phase C passthrough scene `Mini_MR` | Done | Files + Editor |
| Placement (manual surface, voice moves, reposition) | Done | Editor Play Mode (mouse) |
| Character states, emotions, gestures, look-at, sleep | Done | Editor Play Mode |
| Lip sync (audio-driven, interim) | Done | Editor Play Mode (mouth open on 83% of speaking frames) |
| Voice input (mic, permission, VAD, push-to-talk, mute) | Done | EditMode tests + PC microphone in Editor |
| Conversation loop, session memory, interruption, sleep/wake, errors | Done | Editor Play Mode |
| AI / STT / TTS through backend | Done | Editor ↔ local backend (mock providers) over real HTTP |
| Claude + OpenAI speech providers in backend | Written, **not run** | Needs your API keys |
| Final Blender model | Adapter ready (`AnimatorCharacterVisual`) | Model not available yet |
| Android build, Quest 2 passthrough, performance | **Not tested** | Needs the headset |

---

## 2. How it works (simple version)

1. Mini appears in your room (passthrough) and wakes up with a wave.
2. You place Mini by pointing the right controller at the floor or a table and pulling the trigger.
3. You just talk. A **voice activity detector** notices when you start and stop speaking.
4. Your speech goes to **speech-to-text** → text goes to the **AI** → the reply goes to **text-to-speech**.
5. Mini speaks, its mouth follows the audio, and it shows the emotion and gesture the AI suggested (after Unity checks them).
6. Mini goes back to idle: blinking, glancing around, looking at you. After 3 quiet minutes it falls asleep; talking wakes it.

You can talk over Mini to interrupt it. Simple commands like "come closer" are handled instantly by Unity without the AI.

---

## 3. Architecture

```
MiniCompanionApp (composition root / "GameManager")
 ├─ builds services from MiniSettings: Offline or Backend
 ├─ ConversationManager ── owns CompanionState, runs one turn at a time (cancellable)
 │    ├─ VoiceManager ── MicrophoneInput + VoiceActivityDetector
 │    ├─ ISpeechToTextService ─┐
 │    ├─ IAIConversationService├─ Offline: Scripted / RuleBased / ProceduralVoice
 │    ├─ ITextToSpeechService ─┘  Backend: BackendClient → Backend/server.py (+ offline fallbacks)
 │    ├─ VoiceCommandParser ── local commands (move, stay, sleep, stop, reset)
 │    ├─ CueValidator ── AI strings → whitelisted BehaviourCue
 │    └─ PlacementManager ── ISpatialEnvironmentProvider (ManualSurfaceProvider on Quest 2)
 ├─ CharacterManager ── CompanionState → body state + emotion + look target + idle life
 │    └─ MiniCharacterController ── ICharacterVisual (+ ICharacterFace)
 │          ├─ PrimitiveRobotVisual   (prototype, today)
 │          └─ AnimatorCharacterVisual (final Blender model, later)
 ├─ LipSyncController ── reads the voice AudioSource → ICharacterFace.SetSpeechMouth
 └─ CompanionHud ── status light, status text, subtitles, guidance panel
```

**Companion states:** Initialising → WakeUp → Idle ⇄ Listening → Processing → Speaking → Reacting → Idle; Idle → Sleeping; any failure → Error → Idle.

**Rules the code follows**
- Behaviour code talks to the robot only through `ICharacterVisual` / `ICharacterFace`. No object names are looked up.
- The AI never touches Unity objects. It returns strings like `"happy"` / `"wave"`; `CueValidator` maps them through a whitelist, and anything unknown becomes neutral.
- Every conversation turn has its own `CancellationToken`. Interrupting, a new turn, pausing the app or disabling the component cancels it, stops the audio and destroys the clip.
- No singletons. Systems are wired with serialized references in the scene, and the services are built in one place.

---

## 4. Folder structure

```
Assets/_Project/
  Art/Materials/          robot materials + M_Mini_StatusLight (new)
  Prefabs/Characters/     Mini_Robot_Prototype.prefab (unchanged)
  Scenes/                 Mini_Prototype.unity (unchanged), Mini_MR.unity (main app scene)
  Settings/               MiniSettings.asset (new), Rendering/Quest_* (unchanged)
  Scripts/                Mini.Runtime.asmdef (new assembly for all project code)
    Core/                 states, gestures, emotions, visemes, ICharacterVisual, ICharacterFace, BehaviourCue
    Core/Debug/           MiniDebugInput (keys 1-9), CompanionDebugInput (Editor test panel)
    Character/            MiniCharacterController, CharacterManager
    Animation/            PrimitiveRobotVisual, AnimatorCharacterVisual, LipSyncController
    MR/                   PassthroughStatusLogger, ISpatialEnvironmentProvider, ManualSurfaceProvider,
                          PlacementManager, EditorNoXRFallback
    Input/                MiniControllerInput (namespace Mini.Controls)
    Voice/                MicrophoneInput, VoiceActivityDetector, VoiceManager, WavUtility
    Services/             interfaces, fallbacks, Offline/, Backend/
    Conversation/         ConversationManager, history, VoiceCommandParser
    App/                  MiniCompanionApp, MiniSettings, CompanionHud
  Tests/EditMode/         Mini.Tests.EditMode (27 tests)
Backend/                  server.py, test_server.py, requirements.txt, .env.example
Docs/                     this file
ProjectBackups/M3_before_FullBuild/   backup + hashes taken before this work
```

---

## 5. Packages, XR and rendering

Unchanged from Phases A and B: Meta XR Core SDK 207.0.0, OpenXR 1.16.1, XR Management 4.5.4, Input System 1.19.0 and URP 17.3.0. **No packages were added.** The Windows OpenXR loader is still off, Windows uses PC_RPAsset, and Android defaults to the Quest quality level (Quest_RPAsset).

The only project-settings change in this stage was the **build scene list**: `Mini_MR` was added as scene 0 (enabled), and `SampleScene` stays listed but disabled.

Passthrough (Phase C): OVRCameraRig with an OVRPassthroughLayer (always an underlay in SDK 207), opacity 1, a transparent camera background, Floor Level tracking, Passthrough Support = Required, and the manifest `com.oculus.feature.PASSTHROUGH`. The Quest 2 passthrough image is **grayscale**.

---

## 6. Character and animation

### Prototype (`PrimitiveRobotVisual`)
It keeps all Milestone 1 behaviour and adds:
- **States:** `Sleeping` (head down, eyes closed, slow breathing). The value was added to the end of the enum, so existing serialized values didn't change.
- **Gestures:** ShakeHead, PointLeft, PointRight and Shrug, added to the end of the enum.
- **Emotions:** Happy (squint, wide mouth, bounce), Sad, Surprised, Confused (head tilt, one eye narrows), Curious and Thinking. They blend smoothly.
- **Look-at:** the head turns toward a target (clamped to ±40° yaw and ±22° pitch) and the eyes lead inside the face.
- **Speech mouth:** lip sync sets how open the mouth is and its shape (viseme). The old random speaking mouth is still used when no lip sync is connected, as in `Mini_Prototype`.

### Clip names (the 17 required animations)
| Required clip | Prototype today | Final model |
|---|---|---|
| Idle_Base, Listen_Idle, Think_Idle, Speak_Idle, Sleep | procedural states | Animator `State` 0–4 |
| Listen_Start, Think_Start, Wake_Up | smooth blend into the state | transitions |
| Wave, Nod, Shake_Head, Point_Left, Point_Right, Shrug | procedural gestures | triggers with the same names |
| Happy, Confused, Sad | emotions (face + posture) | blend shapes / additive layer |

### Final Blender model (`AnimatorCharacterVisual`)
1. Export the FBX (Generic rig, root motion off). Import it and set **Rig → Generic**.
2. Create an Animator Controller with:
   - an int parameter `State` (0 Idle, 1 Listening, 2 Thinking, 3 Speaking, 4 Sleeping)
   - triggers `LookLeft, LookRight, Nod, Wave, ShakeHead, PointLeft, PointRight, Shrug, CancelGestures`
3. Face blend shapes: `Smile, Sad, Surprise, Confused, Curious, Thinking, Blink` and the visemes `V_AA, V_EE, V_IH, V_OH, V_OO, V_FV, V_MBP, V_LTD`. The names can be changed in the Inspector.
4. Put the model under the Mini prefab's `VisualRoot`, add `AnimatorCharacterVisual`, assign Animator, face mesh, head bone and eye bones, and remove/disable the primitive geometry and `PrimitiveRobotVisual`.
5. Nothing else changes. `MiniCharacterController` finds whichever `ICharacterVisual` is under `VisualRoot`.

Budget: 15k–30k triangles (40k maximum), 1–3 materials, textures up to 1024.

---

## 7. Placement (Quest 2 aware)

Quest 2 cannot detect tables automatically, so placement uses a **manual virtual surface**:

| Action | Controller | Editor |
|---|---|---|
| Move Mini (start/finish placement) | Right **A** | P |
| Place here | Right **trigger** | Left click / Enter |
| "This is my table height" (touch the table with the controller first) | Right **grip** | – |
| Raise/lower the surface | Right thumbstick up/down | Arrow keys |

On first launch, placement mode is on. Mini follows the pointer on the surface and always turns to face you.

**Voice:** "come here", "come closer", "go back", "move a little to the left/right", "stay here", "sit on this table" (starts placement), "go to sleep", "stop", "start over". Left and right mean as **you** see them. Commands only match short sentences (8 words or fewer), so normal talk isn't mistaken for a command.

Mini stays in place for the whole session (tracking space). Keeping the position after the app closes would need Meta Spatial Anchors, which aren't enabled. If you recenter the headset, Mini may move relative to the room.

`ISpatialEnvironmentProvider` lets a future provider (Meta scene planes on Quest 3, or anchors) replace the manual surface without touching any placement or character code.

---

## 8. Voice pipeline

Microphone (16 kHz, in-memory loop) → **VoiceActivityDetector** → utterance → STT → ConversationManager → AI → TTS → AudioSource → lip sync.

- **VAD:** detects speech by loudness, with a background-noise level that adapts. Speech must be about 3× louder than the background (and at least RMS 0.012) for 80 ms to start. It keeps 0.3 s of audio from just before you start, stops after 0.8 s of silence, discards anything under about 0.35 s, and cuts off at 15 s.
- **While Mini speaks:** the threshold is ×2.5 and noise learning is frozen, so Mini's own voice doesn't trigger it but a clear voice still interrupts.
- **Push-to-talk fallback:** hold the left trigger (Space in the Editor) for noisy rooms.
- **Mute:** right **B** (M in the Editor). The status light turns grey.
- **Permission:** asked at startup on Android. If you deny it, a panel explains how to enable it.
- **Privacy:** audio is never written to disk. It's dropped as soon as it has been turned into text. Nothing is uploaded until the detector hears speech. **While sleeping, Mini still listens locally to wake up.**

Settings are in `MiniSettings.asset → Voice Activity`.

---

## 9. AI conversation and speech output

### Two modes (`MiniSettings → Service Mode`)
| | Offline (default) | Backend |
|---|---|---|
| Speech-to-text | **Scripted demo**: each utterance is "heard" as the next practice phrase | Real (backend provider) |
| AI | Rule-based friendly replies | Claude (`claude-opus-5-5`) via backend |
| Voice | Procedural robot chirps (real audio, not words) | Real TTS via backend |
| Needs | nothing | backend running + API keys |

In Backend mode:
- If the **AI** fails, Mini answers with the offline brain and says so once.
- If **TTS** fails, the offline voice is used.
- If **STT** fails, Mini shows an error ("I can't reach my server. Check the Wi-Fi.") and returns to Idle.

### Structured replies
The backend asks Claude for JSON with structured outputs:
`{response, emotion, gesture, head_motion, eye_expression}`, with the enums fixed in the schema. Unity validates them again (`CueValidator`).

### Session memory
The last 16 messages are sent with each request. They're session-only and never saved. "Start over" clears them.

### Lip sync (interim, documented difference)
`LipSyncController` reads the **actual audio being played**: loudness sets how open the mouth is, and a sharpness measure (zero-crossing rate) picks a rough mouth shape. It is synchronised to the audio but it isn't phoneme-accurate. If a TTS provider later supplies viseme timings, feed them to `ICharacterFace.SetSpeechMouth` instead.

---

## 10. Backend server (keeps API keys off the headset)

Location: `Backend/`. It's plain Python 3 (standard library); only the Claude provider needs `pip install anthropic`.

| Endpoint | Body | Reply |
|---|---|---|
| `GET /v1/health` | – | providers in use |
| `POST /v1/stt` | `audio/wav` | `{"text": ...}` |
| `POST /v1/chat` | `{"session_id", "messages":[{role, content}]}` | `{response, emotion, gesture, head_motion, eye_expression}` |
| `POST /v1/tts` | `{"text", "voice"}` | `audio/wav` |

**Environment variables** (`Backend/.env`, copied from `.env.example`):
- `ANTHROPIC_API_KEY`, `OPENAI_API_KEY`
- `MINI_CHAT_PROVIDER` (`anthropic`/`mock`), `MINI_STT_PROVIDER` and `MINI_TTS_PROVIDER` (`openai`/`mock`)
- Optional: `MINI_CLAUDE_MODEL`, `MINI_CLAUDE_EFFORT` (default `low` for fast replies), `MINI_OPENAI_TTS_VOICE`, `MINI_LANGUAGE`, `MINI_HOST`, `MINI_PORT` (8787)

**Protections built in:**
- Body size limits (4 MB audio, 64 KB JSON).
- 60 requests per minute per IP.
- History cleaned up: user/assistant roles only, alternating, ending with the user; anything else is dropped.
- Text length caps.
- Audio and text are never logged or stored.
- Claude refusals become a polite deflection. The server-side fallback (`fallbacks: "default"`) is enabled.

**Provider choice:** Claude handles the conversation. Speech uses OpenAI (`gpt-4o-mini-transcribe`, `gpt-4o-mini-tts`) only because Anthropic has no speech API. Each provider is a single function in `server.py`, so ElevenLabs, Azure or Google can be added the same way.

**Self-test (no keys needed):** `python Backend/test_server.py` → `ALL BACKEND TESTS PASSED`.

> This backend is for **home / development use on your local network** (plain HTTP, no user login). Before any public release, put it behind HTTPS with real authentication.

---

## 11. Security summary
- No API keys, tokens or passwords anywhere in Unity, `MiniSettings` or the APK. The app only knows the backend URL.
- AI output can only pick from whitelisted emotions and gestures. It cannot run code or address objects.
- The microphone is never recorded silently: the status light is cyan while listening, the guidance panel shows a privacy notice, and there's a mute button.
- Raw audio isn't kept, and conversation memory is session-only.

---

## 12. Running in the Unity Editor (Windows, no headset)

1. Open `Assets/_Project/Scenes/Mini_MR.unity` and press **Play**.
2. With no headset, the camera is raised to 1.2 m and tilted toward Mini. Hold the **right mouse button** to look around.
3. Placement mode is on: move the mouse over the floor and **left click** to place Mini.
4. Use the panel at the top left: type a message and press **Send**, or press **T** for practice phrases. **X** interrupts, **Z** sleeps, **C** = come here. Keys 1–9 still drive the Milestone 1 poses.
5. Talking into the PC microphone also works (in Offline mode it "hears" practice phrases).

The background is black or empty in the Editor. That's expected and is not passthrough.

---

## 13. Building and installing on Quest 2

> These steps change the active build target to Android. This was deliberately **not** done automatically: switching reimports assets and is the start of headset testing.

**One-time setup**
1. Install the Unity **Android Build Support** module (with OpenJDK and Android SDK & NDK) from Unity Hub → Installs → ⚙ → Add modules.
2. Enable **Developer Mode** for the headset in the Meta Horizon phone app, then connect the Quest with USB-C and choose **Allow USB debugging** inside the headset.

**Build**
1. **File → Build Profiles** → select **Android** → **Switch Platform** (wait for the reimport).
2. Check that `Mini_MR` is ticked in the scene list (it's already first).
3. Leave every XR, rendering and quality setting as it is. Do **not** run the Meta Project Setup Tool's Fix All.
4. Pick your Quest under **Run Device** → **Build And Run**. Save the APK as `Builds/MiniCompanion.apk`.

**Backend mode on Quest (optional)**
1. On the PC:
   - `cd Backend`
   - `pip install -r requirements.txt`
   - copy `.env.example` to `.env` and add your keys
   - `python server.py`
2. Find the PC's network address (`ipconfig` → IPv4, for example 192.168.1.50).
3. In Unity, open `Assets/_Project/Settings/MiniSettings.asset`:
   - set **Service Mode = Backend**
   - set **Backend Url** = `http://192.168.1.50:8787`
4. Plain-HTTP note: Android blocks `http://` by default. Either:
   - set **Project Settings → Player → Android → Other Settings → Allow downloads over HTTP = Always allowed** (a project setting change, needed only for a LAN backend), or
   - serve the backend over HTTPS.
5. Allow Python through the Windows Firewall when Windows asks.

**Read the headset log:** `adb logcat -s Unity | findstr "Mini"`
Look for:
- `[MiniPassthrough] Passthrough INITIALIZED.`
- `[Mini] Services → …`

---

## 14. Test results

| Test | Where | Result |
|---|---|---|
| 27 EditMode tests: voice detection (6), WAV (2), cue validation (3), voice commands (13), history (1), offline brain and voice (2) | Unity Test Runner | **27/27 passed** |
| Backend self-test: health, STT, bad audio rejected, chat history cleaned, bad chat rejected, TTS WAV | Python | **passed** |
| Startup: Initialising → WakeUp → Idle, mic Active on PC mic | Editor Play Mode | passed |
| Typed turn: Processing 0.4 s → Speaking 2.2 s → Reacting 1.2 s → Idle | Editor | passed |
| Lip sync during speech (mouth open on 196 of 236 frames) | Editor | passed |
| Voice command "Move closer" (0.60 m → 0.45 m) | Editor | passed |
| Interrupt mid-speech (0.5 of 4.0 s): audio stopped, clip released, no late state change | Editor | passed |
| Sleep → speech wakes → noise discarded → Idle | Editor | passed |
| Backend over real HTTP (WAV upload, JSON chat, WAV TTS decode) | Editor ↔ local mock backend | passed |
| AI unreachable → offline fallback with notice | Editor | passed |
| STT unreachable → Error state + message → Idle | Editor | passed |
| `Mini_Prototype` regression (PC quality, visual intact, no new errors) | Editor | passed |
| Protected files (prefab, Mini_Prototype, SampleScene, PC/Mobile/Quest rendering, XR settings) | SHA-256 vs backup | unchanged |

**Not yet tested (needs the Quest 2):**
- Android build
- Passthrough image
- Real-world scale and stability
- Controller bindings
- Quest microphone level and echo while Mini speaks
- Mic permission dialog
- Text readability in the headset
- 72 FPS performance
- Pause/resume when the headset is removed

**Not yet tested (needs your keys):** real Claude replies, real speech recognition, real TTS voice.

---

## 15. Known limitations
- **Offline mode can't understand speech.** It only demonstrates the loop with practice phrases.
- **Lip sync** follows loudness and rough sound shape, not exact phonemes.
- **Echo:** if the Quest speaker is loud, Mini might hear itself. Lower the volume or raise `bargeInMultiplier`.
- **Quest 2 limits:** no automatic table detection, so placement is manual. Real objects won't hide Mini (no occlusion), and passthrough is grayscale.
- **Placement isn't saved** between sessions (no spatial anchors).
- **Backend security:** it uses plain HTTP and is meant for a LAN only.
- **Text panels:** world-space text over passthrough may look slightly faded (alpha blending). Check readability on the headset.
- **No wake word:** while sleeping, Mini wakes on any speech.
- **Language:** English only for now (`MINI_LANGUAGE`, persona prompt).

## 16. Troubleshooting
| Problem | Fix |
|---|---|
| Play Mode frozen on frame 1 | Click inside the Game view (Unity pauses unfocused play when "Run In Background" is off, which is intentional for this project). |
| Mini never hears me | Check the status light: grey means muted (press B). Look at `VoiceManager` Threshold vs InputLevel in the Debug Inspector. Try hold-to-talk. |
| "I can't reach my server" | Is the backend running? Is the PC IP correct? Are the PC and Quest on the same Wi-Fi? Firewall? HTTP allowed (section 13)? |
| "rejected the request (check its API keys)" | Fix the keys in `Backend/.env` and restart the server. |
| Mini floats in the air | Placement mode (A), touch the real table with the controller, press grip. Or press thumbstick down. |
| Black background on Quest | Look in the log for `[MiniPassthrough]`. Check Passthrough Support = Required and the manifest feature (Phase C). |

## 17. Backups and change record
- Backup before this work: `ProjectBackups/M3_before_FullBuild/` (all of `Assets/_Project`, XR settings, Meta config, manifest, ProjectSettings, packages) plus `hashes_before.sha256`.
- Changed existing files:
  - `CharacterState.cs` (+Sleeping)
  - `CharacterGesture.cs` (+4 gestures)
  - `PrimitiveRobotVisual.cs` (additive extensions)
  - `Mini_MR.unity` (new objects: MiniApp, MiniVoice, debug panel)
  - `ProjectSettings/EditorBuildSettings.asset` (scene list)
- New: every other file listed in section 4.
