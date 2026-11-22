using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Mini.Services
{
    /// <summary>
    /// Tries the main AI service and, if it fails (network down, server error), answers with an offline
    /// service so Mini still responds. The first fallback reply in a row tells the user what happened.
    /// </summary>
    public sealed class FallbackConversationService : IAIConversationService
    {
        private readonly IAIConversationService primary;
        private readonly IAIConversationService fallback;
        private bool announced;

        public FallbackConversationService(IAIConversationService primary, IAIConversationService fallback)
        {
            this.primary = primary;
            this.fallback = fallback;
        }

        public string Name { get { return primary.Name + " (+ offline fallback)"; } }

        public async Task<AIResponse> GetResponseAsync(IReadOnlyList<ConversationTurn> history, string userMessage,
            CancellationToken ct)
        {
            try
            {
                AIResponse response = await primary.GetResponseAsync(history, userMessage, ct);
                announced = false;
                return response;
            }
            catch (ServiceException e)
            {
                Debug.LogWarning("[Mini] AI service failed (" + e.Kind + "): " + e.Message + ". Using offline replies.");
                AIResponse offline = await fallback.GetResponseAsync(history, userMessage, ct);
                offline.FromFallback = true;
                if (!announced)
                {
                    offline.Text = "My cloud brain isn't answering right now, so I'll keep it simple. " + offline.Text;
                    announced = true;
                }
                return offline;
            }
        }
    }

    /// <summary>Uses the main TTS and falls back to an offline voice so Mini can always say something.</summary>
    public sealed class FallbackTextToSpeech : ITextToSpeechService
    {
        private readonly ITextToSpeechService primary;
        private readonly ITextToSpeechService fallback;

        public FallbackTextToSpeech(ITextToSpeechService primary, ITextToSpeechService fallback)
        {
            this.primary = primary;
            this.fallback = fallback;
        }

        public string Name { get { return primary.Name + " (+ offline fallback)"; } }

        public async Task<AudioClip> SynthesizeAsync(string text, CancellationToken ct)
        {
            try
            {
                return await primary.SynthesizeAsync(text, ct);
            }
            catch (ServiceException e)
            {
                Debug.LogWarning("[Mini] TTS failed (" + e.Kind + "): " + e.Message + ". Using offline voice.");
                return await fallback.SynthesizeAsync(text, ct);
            }
        }
    }
}
