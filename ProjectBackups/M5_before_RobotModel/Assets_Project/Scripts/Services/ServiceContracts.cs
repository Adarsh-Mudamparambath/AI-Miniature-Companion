using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Mini.Services
{
    public enum ConversationRole
    {
        User,
        Assistant
    }

    public struct ConversationTurn
    {
        public ConversationRole Role;
        public string Text;

        public ConversationTurn(ConversationRole role, string text)
        {
            Role = role;
            Text = text;
        }
    }

    /// <summary>
    /// What an AI service returns. The cue strings are untrusted suggestions; Unity validates them with
    /// Mini.Core.CueValidator before anything moves.
    /// </summary>
    public sealed class AIResponse
    {
        public string Text;
        public string Emotion;
        public string Gesture;
        public string HeadMotion;
        public string EyeExpression;

        /// <summary>True if an offline fallback produced this reply because the main service failed.</summary>
        public bool FromFallback;
    }

    public sealed class SpeechToTextResult
    {
        public string Text;

        public SpeechToTextResult(string text)
        {
            Text = text ?? string.Empty;
        }
    }

    public enum ServiceErrorKind
    {
        Network,
        Timeout,
        Unavailable,
        Unauthorized,
        RateLimited,
        InvalidResponse,
        Refused
    }

    public class ServiceException : Exception
    {
        public ServiceErrorKind Kind { get; private set; }

        public ServiceException(ServiceErrorKind kind, string message) : base(message)
        {
            Kind = kind;
        }
    }

    /// <summary>Turns recorded speech into text.</summary>
    public interface ISpeechToTextService
    {
        string Name { get; }
        Task<SpeechToTextResult> TranscribeAsync(float[] samples, int sampleRate, CancellationToken cancellationToken);
    }

    /// <summary>Produces Mini's reply. History holds earlier turns of this session, oldest first.</summary>
    public interface IAIConversationService
    {
        string Name { get; }
        Task<AIResponse> GetResponseAsync(IReadOnlyList<ConversationTurn> history, string userMessage,
            CancellationToken cancellationToken);
    }

    /// <summary>Turns text into audio. Returns a new AudioClip the caller destroys after playback.</summary>
    public interface ITextToSpeechService
    {
        string Name { get; }
        Task<AudioClip> SynthesizeAsync(string text, CancellationToken cancellationToken);
    }
}
