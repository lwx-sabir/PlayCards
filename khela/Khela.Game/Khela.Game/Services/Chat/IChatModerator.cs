using System.Text.RegularExpressions;

namespace Khela.Game.Services.Chat
{
    public enum ModerationOutcome { Approved = 0, Masked = 1, Rejected = 2 }

    public readonly record struct ModerationResult(ModerationOutcome Outcome, string Text);

    /// <summary>
    /// Pre-broadcast chat moderation. Async by design so the future AI moderator drops in with no caller
    /// change — swap the DI registration for an AI-backed impl.
    /// </summary>
    public interface IChatModerator
    {
        Task<ModerationResult> ModerateAsync(string body);
    }

    /// <summary>
    /// v1 synchronous blocklist moderator (the seam/dummy for the AI later). Trims + caps length, masks
    /// blocklisted words, rejects empty. Stateless → registered as a singleton.
    /// </summary>
    public sealed class BasicChatModerator : IChatModerator
    {
        private const int MaxLength = 1000;

        // Minimal starter blocklist; the real filter is the AI moderator that replaces this impl.
        private static readonly string[] Blocklist = { "fuck", "shit", "bitch", "asshole", "cunt" };

        public Task<ModerationResult> ModerateAsync(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
                return Task.FromResult(new ModerationResult(ModerationOutcome.Rejected, string.Empty));

            var text = body.Trim();
            if (text.Length > MaxLength) text = text.Substring(0, MaxLength);

            var masked = text;
            foreach (var word in Blocklist)
                masked = Regex.Replace(masked, Regex.Escape(word), new string('*', word.Length), RegexOptions.IgnoreCase);

            var outcome = masked == text ? ModerationOutcome.Approved : ModerationOutcome.Masked;
            return Task.FromResult(new ModerationResult(outcome, masked));
        }
    }
}
