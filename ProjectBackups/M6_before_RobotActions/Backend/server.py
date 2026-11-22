"""
Mini backend server.

Keeps every provider API key off the headset. The Quest app talks only to this server:

    GET  /v1/health  -> which providers are active
    POST /v1/stt     -> body: audio/wav (16-bit PCM)           reply: {"text": "..."}
    POST /v1/chat    -> body: {"session_id", "messages":[...]}  reply: {"response","emotion","gesture","head_motion","eye_expression"}
    POST /v1/tts     -> body: {"text", "voice"}                 reply: audio/wav

Providers (set in .env or environment variables):
    MINI_CHAT_PROVIDER = anthropic | mock      (Claude via the official Anthropic SDK)
    MINI_STT_PROVIDER  = openai    | mock
    MINI_TTS_PROVIDER  = openai    | mock

Privacy: audio and text are processed in memory for the single request and are never written to disk
or logged. Only request sizes, timings and errors are logged.

Run:  python server.py            (needs `pip install -r requirements.txt` for the anthropic provider)
Test without any keys:  set all three providers to "mock".
"""

import io
import json
import math
import os
import struct
import sys
import threading
import time
import urllib.error
import urllib.request
import uuid
import wave
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

HERE = os.path.dirname(os.path.abspath(__file__))


def load_dotenv(path):
    """Tiny .env reader (KEY=VALUE lines). Real environment variables win."""
    if not os.path.exists(path):
        return
    with open(path, "r", encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line or line.startswith("#") or "=" not in line:
                continue
            key, value = line.split("=", 1)
            key, value = key.strip(), value.strip().strip('"').strip("'")
            os.environ.setdefault(key, value)


load_dotenv(os.path.join(HERE, ".env"))

HOST = os.environ.get("MINI_HOST", "0.0.0.0")
PORT = int(os.environ.get("MINI_PORT", "8787"))
# Defaults are the free local providers (no API keys, no cost).
CHAT_PROVIDER = os.environ.get("MINI_CHAT_PROVIDER", "ollama").lower()
STT_PROVIDER = os.environ.get("MINI_STT_PROVIDER", "whisper").lower()
TTS_PROVIDER = os.environ.get("MINI_TTS_PROVIDER", "piper").lower()
CLAUDE_MODEL = os.environ.get("MINI_CLAUDE_MODEL", "claude-opus-5-5")
CLAUDE_EFFORT = os.environ.get("MINI_CLAUDE_EFFORT", "low")  # chat replies: low effort keeps latency down
OPENAI_STT_MODEL = os.environ.get("MINI_OPENAI_STT_MODEL", "gpt-4o-mini-transcribe")
OPENAI_TTS_MODEL = os.environ.get("MINI_OPENAI_TTS_MODEL", "gpt-4o-mini-tts")
OPENAI_TTS_VOICE = os.environ.get("MINI_OPENAI_TTS_VOICE", "alloy")
LANGUAGE = os.environ.get("MINI_LANGUAGE", "en")

MAX_AUDIO_BYTES = 4 * 1024 * 1024       # ~2 minutes of 16 kHz PCM16
MAX_JSON_BYTES = 64 * 1024
MAX_MESSAGES = 40
MAX_MESSAGE_CHARS = 2000
MAX_TTS_CHARS = 600
RATE_LIMIT_PER_MINUTE = 60

EMOTIONS = ["neutral", "happy", "sad", "surprised", "confused", "curious", "thinking"]
GESTURES = ["none", "nod", "wave", "shake_head", "shrug", "point_left", "point_right", "look_left", "look_right"]
HEAD_MOTIONS = ["none", "subtle", "expressive"]

PERSONA = """You are Mini, a 28 cm tall friendly robot companion who stands on the user's table in mixed reality.
Personality: friendly, curious, playful, attentive, kind. You are a small robot, not a human, and you are honest about what you can and cannot do.
Your words are spoken aloud by a text-to-speech voice, so:
- Reply in one to three short sentences (under 60 words) unless the user clearly asks for more.
- Plain conversational speech only: no lists, markdown, emoji, URLs or stage directions.
- Ask a short follow-up question now and then to keep the conversation going.
You cannot see the room, browse the internet, or remember anything after this session ends. If asked, say so kindly.
Movement commands (come here, move closer, stay) are handled by your body automatically; if the user asks you to move, just respond briefly and warmly.
Choose an emotion and at most one gesture that match your reply. Use "none" for gesture most of the time; gestures are for moments that call for them (greeting -> wave, agreeing -> nod, not knowing -> shrug)."""

RESPONSE_SCHEMA = {
    "type": "object",
    "properties": {
        "response": {"type": "string", "description": "What Mini says aloud."},
        "emotion": {"type": "string", "enum": EMOTIONS},
        "gesture": {"type": "string", "enum": GESTURES},
        "head_motion": {"type": "string", "enum": HEAD_MOTIONS},
        "eye_expression": {"type": "string", "enum": EMOTIONS},
    },
    "required": ["response", "emotion", "gesture", "head_motion", "eye_expression"],
    "additionalProperties": False,
}


class ProviderError(Exception):
    def __init__(self, status, message):
        super().__init__(message)
        self.status = status


# ---------------------------------------------------------------- chat (Claude)

_anthropic_client = None
_anthropic_lock = threading.Lock()


def _get_anthropic():
    global _anthropic_client
    with _anthropic_lock:
        if _anthropic_client is None:
            import anthropic  # imported lazily so mock mode needs no packages

            _anthropic_client = anthropic.Anthropic()  # reads ANTHROPIC_API_KEY or an `ant auth login` profile
        return _anthropic_client


def clean_messages(raw):
    """Validate the client's history: user/assistant only, text only, alternating, starting with user."""
    if not isinstance(raw, list) or not raw:
        raise ProviderError(400, "messages must be a non-empty list")
    cleaned = []
    for item in raw[-MAX_MESSAGES:]:
        if not isinstance(item, dict):
            continue
        role = item.get("role")
        content = item.get("content")
        if role not in ("user", "assistant") or not isinstance(content, str):
            continue
        content = content.strip()[:MAX_MESSAGE_CHARS]
        if not content:
            continue
        if cleaned and cleaned[-1]["role"] == role:
            cleaned[-1]["content"] += "\n" + content
        else:
            cleaned.append({"role": role, "content": content})
    while cleaned and cleaned[0]["role"] != "user":
        cleaned.pop(0)
    if not cleaned or cleaned[-1]["role"] != "user":
        raise ProviderError(400, "the last message must be from the user")
    return cleaned


def chat_anthropic(messages):
    import anthropic

    client = _get_anthropic()
    try:
        response = client.beta.messages.create(
            model=CLAUDE_MODEL,
            max_tokens=4096,
            system=PERSONA,
            messages=messages,
            output_config={
                "effort": CLAUDE_EFFORT,
                "format": {"type": "json_schema", "schema": RESPONSE_SCHEMA},
            },
            # If a safety classifier declines, the API retries on a suitable model inside the same call.
            betas=["server-side-fallback-2026-07-01"],
            fallbacks="default",
        )
    except anthropic.AuthenticationError as e:
        raise ProviderError(401, "Anthropic rejected the API key") from e
    except anthropic.PermissionDeniedError as e:
        raise ProviderError(403, "Anthropic permission denied") from e
    except anthropic.RateLimitError as e:
        raise ProviderError(429, "Anthropic rate limit") from e
    except anthropic.BadRequestError as e:
        raise ProviderError(502, "Anthropic rejected the request: %s" % e.message) from e
    except anthropic.APIStatusError as e:
        raise ProviderError(503, "Anthropic error %s" % e.status_code) from e
    except anthropic.APIConnectionError as e:
        raise ProviderError(503, "Cannot reach Anthropic") from e

    if response.stop_reason == "refusal":
        return {"response": "Hmm, I'd rather not talk about that. Can we chat about something else?",
                "emotion": "confused", "gesture": "shake_head", "head_motion": "subtle", "eye_expression": "neutral"}

    text = next((b.text for b in response.content if b.type == "text"), "")
    try:
        data = json.loads(text)
    except ValueError:
        # max_tokens or an unexpected shape: speak whatever text there is.
        data = {"response": text or "Sorry, I lost my train of thought.", "emotion": "confused"}
    return normalize_chat(data)


def chat_mock(messages):
    last = messages[-1]["content"]
    return normalize_chat({"response": "Beep boop! You said: %s" % last[:120], "emotion": "happy", "gesture": "nod",
                           "head_motion": "subtle", "eye_expression": "happy"})


def normalize_chat(data):
    def pick(key, allowed, default):
        value = str(data.get(key, default)).lower()
        return value if value in allowed else default

    reply = str(data.get("response", "")).strip()
    gesture = pick("gesture", GESTURES, "none")
    return {
        "response": reply or "Hmm.",
        "emotion": pick("emotion", EMOTIONS, "neutral"),
        "gesture": "" if gesture == "none" else gesture,
        "head_motion": pick("head_motion", HEAD_MOTIONS, "subtle"),
        "eye_expression": pick("eye_expression", EMOTIONS, "neutral"),
    }


# ---------------------------------------------------------------- speech (OpenAI REST, replaceable)

def _openai_key():
    key = os.environ.get("OPENAI_API_KEY", "")
    if not key:
        raise ProviderError(503, "OPENAI_API_KEY is not set on the server")
    return key


def _http(url, body, headers, timeout=30):
    request = urllib.request.Request(url, data=body, headers=headers, method="POST")
    try:
        with urllib.request.urlopen(request, timeout=timeout) as reply:
            return reply.read()
    except urllib.error.HTTPError as e:
        status = 401 if e.code in (401, 403) else 429 if e.code == 429 else 503
        raise ProviderError(status, "speech provider HTTP %s" % e.code) from e
    except (urllib.error.URLError, TimeoutError) as e:
        raise ProviderError(503, "cannot reach speech provider") from e


def stt_openai(wav_bytes):
    boundary = uuid.uuid4().hex
    parts = []

    def field(name, value):
        parts.append(("--%s\r\nContent-Disposition: form-data; name=\"%s\"\r\n\r\n%s\r\n" % (boundary, name, value)).encode())

    field("model", OPENAI_STT_MODEL)
    field("language", LANGUAGE)
    field("response_format", "json")
    parts.append(("--%s\r\nContent-Disposition: form-data; name=\"file\"; filename=\"speech.wav\"\r\n"
                  "Content-Type: audio/wav\r\n\r\n" % boundary).encode() + wav_bytes + b"\r\n")
    parts.append(("--%s--\r\n" % boundary).encode())
    body = b"".join(parts)
    raw = _http("https://api.openai.com/v1/audio/transcriptions", body,
                {"Authorization": "Bearer " + _openai_key(), "Content-Type": "multipart/form-data; boundary=" + boundary})
    return json.loads(raw.decode("utf-8")).get("text", "").strip()


def stt_mock(wav_bytes):
    return "Hello Mini!"


def tts_openai(text, voice):
    body = json.dumps({
        "model": OPENAI_TTS_MODEL,
        "voice": voice if voice and voice != "default" else OPENAI_TTS_VOICE,
        "input": text,
        "response_format": "wav",
        "instructions": "Speak as a small, cheerful, friendly robot. Warm, bright and playful, at a relaxed pace.",
    }).encode("utf-8")
    return _http("https://api.openai.com/v1/audio/speech", body,
                 {"Authorization": "Bearer " + _openai_key(), "Content-Type": "application/json"}, timeout=60)


def tts_mock(text, voice):
    """A short beep melody, one note per word, as a valid 16-bit PCM WAV."""
    rate = 22050
    frames = bytearray()
    for i, _ in enumerate(text.split()[:40]):
        freq = 440 + (i % 5) * 60
        for n in range(int(rate * 0.16)):
            env = min(1.0, n / 300.0) * min(1.0, (rate * 0.16 - n) / 600.0)
            frames += struct.pack("<h", int(9000 * env * math.sin(2 * math.pi * freq * n / rate)))
        frames += b"\x00\x00" * int(rate * 0.05)
    buffer = io.BytesIO()
    with wave.open(buffer, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(rate)
        w.writeframes(bytes(frames))
    return buffer.getvalue()


# ---------------------------------------------------------------- free, local providers (no keys, no cost)
# Whisper (faster-whisper) for speech-to-text, Ollama for the conversation, Piper for the voice.
# Everything runs on this PC; nothing leaves the home network.

WHISPER_MODEL = os.environ.get("MINI_WHISPER_MODEL", "small.en")
WHISPER_DIR = os.path.join(HERE, "models", "whisper")
OLLAMA_URL = os.environ.get("MINI_OLLAMA_URL", "http://127.0.0.1:11434")
OLLAMA_MODEL = os.environ.get("MINI_OLLAMA_MODEL", "llama3.2:3b")
PIPER_VOICE = os.environ.get("MINI_PIPER_VOICE", os.path.join(HERE, "models", "piper", "en_US-amy-medium.onnx"))

_whisper = None
_whisper_lock = threading.Lock()
_piper = None
_piper_lock = threading.Lock()


def _get_whisper():
    global _whisper
    with _whisper_lock:
        if _whisper is None:
            from faster_whisper import WhisperModel
            _whisper = WhisperModel(WHISPER_MODEL, device="cpu", compute_type="int8", download_root=WHISPER_DIR)
        return _whisper


def _wav_to_float16k(wav_bytes):
    """Read a PCM16 WAV into mono float32 samples at 16 kHz (what Whisper expects), using only the
    standard library and numpy, so no audio-codec package is needed."""
    import numpy as np
    with wave.open(io.BytesIO(wav_bytes), "rb") as w:
        if w.getsampwidth() != 2:
            raise ProviderError(400, "expected 16-bit PCM WAV")
        channels, rate = w.getnchannels(), w.getframerate()
        data = np.frombuffer(w.readframes(w.getnframes()), dtype="<i2").astype(np.float32) / 32768.0
    if channels > 1:
        data = data.reshape(-1, channels).mean(axis=1)
    if rate != 16000 and len(data) > 1:
        target = int(len(data) * 16000 / rate)
        data = np.interp(np.linspace(0, len(data) - 1, target), np.arange(len(data)), data).astype(np.float32)
    return data


def stt_whisper(wav_bytes):
    model = _get_whisper()
    audio = _wav_to_float16k(wav_bytes)
    with _whisper_lock:  # one transcription at a time keeps CPU use predictable
        segments, _ = model.transcribe(audio, language=LANGUAGE, beam_size=1,
                                       initial_prompt="Mini is a small friendly robot.",
                                       vad_filter=True, condition_on_previous_text=False)
        text = " ".join(s.text.strip() for s in segments).strip()
    return text


def chat_ollama(messages):
    body = json.dumps({
        "model": OLLAMA_MODEL,
        "messages": [{"role": "system", "content": PERSONA + "\nAnswer ONLY with the JSON object described by the schema."}] + messages,
        "format": RESPONSE_SCHEMA,
        "stream": False,
        "keep_alive": "30m",
        "options": {"temperature": 0.6, "num_predict": 200},
    }).encode("utf-8")
    request = urllib.request.Request(OLLAMA_URL + "/api/chat", data=body,
                                     headers={"Content-Type": "application/json"}, method="POST")
    try:
        with urllib.request.urlopen(request, timeout=90) as reply:
            data = json.loads(reply.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        raise ProviderError(503, "Ollama HTTP %s (is the model '%s' pulled?)" % (e.code, OLLAMA_MODEL)) from e
    except (urllib.error.URLError, TimeoutError) as e:
        raise ProviderError(503, "cannot reach Ollama at %s (is Ollama running?)" % OLLAMA_URL) from e

    content = data.get("message", {}).get("content", "")
    try:
        parsed = json.loads(content)
    except ValueError:
        parsed = {"response": content.strip() or "Hmm, let me think about that.", "emotion": "curious"}
    return normalize_chat(parsed)


def _get_piper():
    global _piper
    with _piper_lock:
        if _piper is None:
            from piper import PiperVoice
            if not os.path.exists(PIPER_VOICE):
                raise ProviderError(503, "Piper voice file not found: %s" % PIPER_VOICE)
            _piper = PiperVoice.load(PIPER_VOICE)
        return _piper


def tts_piper(text, voice):
    from piper import SynthesisConfig
    model = _get_piper()
    buffer = io.BytesIO()
    with _piper_lock:
        with wave.open(buffer, "wb") as wav:
            # Slightly quicker than default speech suits a small, lively robot.
            model.synthesize_wav(text, wav, syn_config=SynthesisConfig(length_scale=0.92))
    return buffer.getvalue()


CHAT = {"anthropic": chat_anthropic, "ollama": chat_ollama, "mock": chat_mock}
STT = {"openai": stt_openai, "whisper": stt_whisper, "mock": stt_mock}
TTS = {"openai": tts_openai, "piper": tts_piper, "mock": tts_mock}


# ---------------------------------------------------------------- HTTP

_rate = {}
_rate_lock = threading.Lock()


def allow(ip):
    now = time.time()
    with _rate_lock:
        stamps = [t for t in _rate.get(ip, []) if now - t < 60]
        if len(stamps) >= RATE_LIMIT_PER_MINUTE:
            _rate[ip] = stamps
            return False
        stamps.append(now)
        _rate[ip] = stamps
        return True


class Handler(BaseHTTPRequestHandler):
    server_version = "MiniBackend/1.0"

    def log_message(self, fmt, *args):  # never log bodies; keep the default access log short
        sys.stderr.write("[mini] %s %s\n" % (self.address_string(), fmt % args))

    def _send(self, status, body, content_type="application/json"):
        if isinstance(body, (dict, list)):
            body = json.dumps(body).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)

    def _read_body(self, limit):
        length = int(self.headers.get("Content-Length", "0") or 0)
        if length <= 0:
            raise ProviderError(400, "empty body")
        if length > limit:
            raise ProviderError(413, "body too large")
        return self.rfile.read(length)

    def do_GET(self):
        if self.path == "/v1/health":
            self._send(200, {"ok": True, "chat": CHAT_PROVIDER, "stt": STT_PROVIDER, "tts": TTS_PROVIDER,
                             "model": CLAUDE_MODEL if CHAT_PROVIDER == "anthropic" else OLLAMA_MODEL if CHAT_PROVIDER == "ollama" else None})
        else:
            self._send(404, {"error": "not found"})

    def do_POST(self):
        if not allow(self.client_address[0]):
            self._send(429, {"error": "slow down"})
            return
        started = time.time()
        try:
            if self.path == "/v1/stt":
                audio = self._read_body(MAX_AUDIO_BYTES)
                if audio[:4] != b"RIFF":
                    raise ProviderError(400, "expected a WAV file")
                text = STT[STT_PROVIDER](audio)
                del audio  # not kept
                self._send(200, {"text": text})
            elif self.path == "/v1/chat":
                payload = json.loads(self._read_body(MAX_JSON_BYTES).decode("utf-8"))
                messages = clean_messages(payload.get("messages"))
                self._send(200, CHAT[CHAT_PROVIDER](messages))
            elif self.path == "/v1/tts":
                payload = json.loads(self._read_body(MAX_JSON_BYTES).decode("utf-8"))
                text = str(payload.get("text", "")).strip()[:MAX_TTS_CHARS]
                if not text:
                    raise ProviderError(400, "text is empty")
                self._send(200, TTS[TTS_PROVIDER](text, str(payload.get("voice", "default"))), "audio/wav")
            else:
                self._send(404, {"error": "not found"})
                return
            sys.stderr.write("[mini] %s ok in %.2fs\n" % (self.path, time.time() - started))
        except ProviderError as e:
            sys.stderr.write("[mini] %s failed: %s\n" % (self.path, e))
            self._send(e.status, {"error": str(e)})
        except (ValueError, KeyError) as e:
            self._send(400, {"error": "bad request"})
        except Exception as e:  # keep the server alive; details stay in the server log
            sys.stderr.write("[mini] %s crashed: %r\n" % (self.path, e))
            self._send(500, {"error": "internal error"})


def main():
    for name, table, value in (("MINI_CHAT_PROVIDER", CHAT, CHAT_PROVIDER), ("MINI_STT_PROVIDER", STT, STT_PROVIDER),
                               ("MINI_TTS_PROVIDER", TTS, TTS_PROVIDER)):
        if value not in table:
            sys.exit("%s must be one of %s (got %r)" % (name, ", ".join(table), value))
    # Load the local models now so Mini's first answer is not slow.
    started = time.time()
    try:
        if STT_PROVIDER == "whisper":
            _get_whisper()
        if TTS_PROVIDER == "piper":
            _get_piper()
        if CHAT_PROVIDER == "ollama":
            chat_ollama([{"role": "user", "content": "Say hi in three words."}])
        print("Models ready in %.1fs" % (time.time() - started))
    except ProviderError as e:
        print("WARNING: %s" % e)
    server = ThreadingHTTPServer((HOST, PORT), Handler)
    print("Mini backend on http://%s:%d  chat=%s stt=%s tts=%s" % (HOST, PORT, CHAT_PROVIDER, STT_PROVIDER, TTS_PROVIDER))
    print("Set MiniSettings > Backend Url in Unity to http://<this PC's LAN IP>:%d" % PORT)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
