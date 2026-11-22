using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using Mini.Voice;

namespace Mini.Services.Backend
{
    /// <summary>
    /// HTTP client for the Mini backend (see the Backend folder in the project root). The backend holds
    /// every provider API key; the app only knows the backend URL, so no secret ever ships in the APK.
    /// </summary>
    public sealed class BackendClient
    {
        private readonly string baseUrl;
        private readonly int timeoutSeconds;

        public string BaseUrl { get { return baseUrl; } }

        public BackendClient(string baseUrl, int timeoutSeconds)
        {
            this.baseUrl = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
            this.timeoutSeconds = Mathf.Clamp(timeoutSeconds, 3, 120);
        }

        public bool IsConfigured
        {
            get { return baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase); }
        }

        public Task<byte[]> PostJsonAsync(string path, string json, CancellationToken ct)
        {
            return SendAsync("POST", path, Encoding.UTF8.GetBytes(json ?? "{}"), "application/json", ct);
        }

        public async Task<byte[]> SendAsync(string method, string path, byte[] body, string contentType, CancellationToken ct)
        {
            if (!IsConfigured) throw new ServiceException(ServiceErrorKind.Unavailable, "Backend URL is not set.");
            ct.ThrowIfCancellationRequested();

            using (UnityWebRequest request = new UnityWebRequest(baseUrl + path, method))
            {
                if (body != null)
                {
                    request.uploadHandler = new UploadHandlerRaw(body);
                    request.SetRequestHeader("Content-Type", contentType);
                }
                request.downloadHandler = new DownloadHandlerBuffer();
                request.timeout = timeoutSeconds;

                UnityWebRequestAsyncOperation op = request.SendWebRequest();
                using (ct.Register(() => { if (!op.isDone) request.Abort(); }))
                {
                    while (!op.isDone) await Awaitable.NextFrameAsync();
                }
                ct.ThrowIfCancellationRequested();

                if (request.result == UnityWebRequest.Result.Success) return request.downloadHandler.data;
                throw Translate(request);
            }
        }

        private static ServiceException Translate(UnityWebRequest request)
        {
            long code = request.responseCode;
            string error = request.error ?? "unknown error";

            if (request.result == UnityWebRequest.Result.ConnectionError)
            {
                bool timeout = error.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0 ||
                               error.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0;
                return new ServiceException(timeout ? ServiceErrorKind.Timeout : ServiceErrorKind.Network, error);
            }
            if (code == 401 || code == 403) return new ServiceException(ServiceErrorKind.Unauthorized, error);
            if (code == 429) return new ServiceException(ServiceErrorKind.RateLimited, error);
            if (request.result == UnityWebRequest.Result.DataProcessingError)
                return new ServiceException(ServiceErrorKind.InvalidResponse, error);
            return new ServiceException(ServiceErrorKind.Unavailable, "HTTP " + code + ": " + error);
        }
    }

    // JSON shapes shared with Backend/server.py. Field names are the wire names.
    [Serializable] internal class ChatMessageDto { public string role; public string content; }
    [Serializable] internal class ChatRequestDto { public string session_id; public List<ChatMessageDto> messages = new List<ChatMessageDto>(); }
    [Serializable] internal class ChatResponseDto { public string response; public string emotion; public string gesture; public string head_motion; public string eye_expression; }
    [Serializable] internal class SttResponseDto { public string text; }
    [Serializable] internal class TtsRequestDto { public string text; public string voice; }

    public sealed class BackendSpeechToText : ISpeechToTextService
    {
        private readonly BackendClient client;

        public BackendSpeechToText(BackendClient client) { this.client = client; }

        public string Name { get { return "Backend STT"; } }

        public async Task<SpeechToTextResult> TranscribeAsync(float[] samples, int sampleRate, CancellationToken ct)
        {
            // Audio goes to the backend only for this request and is not stored by the app.
            byte[] wav = WavUtility.EncodePcm16(samples, sampleRate);
            byte[] reply = await client.SendAsync("POST", "/v1/stt", wav, "audio/wav", ct);
            SttResponseDto dto = Parse<SttResponseDto>(reply);
            return new SpeechToTextResult(dto.text);
        }

        internal static T Parse<T>(byte[] json) where T : class
        {
            try
            {
                T dto = JsonUtility.FromJson<T>(Encoding.UTF8.GetString(json ?? new byte[0]));
                if (dto == null) throw new ServiceException(ServiceErrorKind.InvalidResponse, "Empty response.");
                return dto;
            }
            catch (ArgumentException e)
            {
                throw new ServiceException(ServiceErrorKind.InvalidResponse, e.Message);
            }
        }
    }

    public sealed class BackendConversationService : IAIConversationService
    {
        private readonly BackendClient client;
        private readonly string sessionId;

        public BackendConversationService(BackendClient client, string sessionId)
        {
            this.client = client;
            this.sessionId = sessionId;
        }

        public string Name { get { return "Backend AI (provider chosen on the server)"; } }

        public async Task<AIResponse> GetResponseAsync(IReadOnlyList<ConversationTurn> history, string userMessage,
            CancellationToken ct)
        {
            ChatRequestDto request = new ChatRequestDto { session_id = sessionId };
            if (history != null)
            {
                foreach (ConversationTurn turn in history)
                {
                    request.messages.Add(new ChatMessageDto
                    {
                        role = turn.Role == ConversationRole.User ? "user" : "assistant",
                        content = turn.Text
                    });
                }
            }
            request.messages.Add(new ChatMessageDto { role = "user", content = userMessage });

            byte[] reply = await client.PostJsonAsync("/v1/chat", JsonUtility.ToJson(request), ct);
            ChatResponseDto dto = BackendSpeechToText.Parse<ChatResponseDto>(reply);
            if (string.IsNullOrWhiteSpace(dto.response))
                throw new ServiceException(ServiceErrorKind.InvalidResponse, "Backend returned no reply text.");

            return new AIResponse
            {
                Text = dto.response,
                Emotion = dto.emotion,
                Gesture = dto.gesture,
                HeadMotion = dto.head_motion,
                EyeExpression = dto.eye_expression
            };
        }
    }

    public sealed class BackendTextToSpeech : ITextToSpeechService
    {
        private readonly BackendClient client;
        private readonly string voice;

        public BackendTextToSpeech(BackendClient client, string voice)
        {
            this.client = client;
            this.voice = voice;
        }

        public string Name { get { return "Backend TTS"; } }

        public async Task<AudioClip> SynthesizeAsync(string text, CancellationToken ct)
        {
            string json = JsonUtility.ToJson(new TtsRequestDto { text = text, voice = voice });
            byte[] wav = await client.PostJsonAsync("/v1/tts", json, ct);
            AudioClip clip = WavUtility.ToAudioClip(wav, "MiniVoice_Backend");
            if (clip == null) throw new ServiceException(ServiceErrorKind.InvalidResponse, "TTS audio was not a supported WAV file.");
            return clip;
        }
    }
}
