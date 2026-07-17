using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace SimPle.Application.Chat;

/// <summary>Default <see cref="IChatProfanityFilter"/>: whole-word, case-insensitive matching against
/// <see cref="ProfanityOptions.Terms"/>. Deliberately simple and dependency-free — this is a first-pass automated
/// screen, not a replacement for M12's manual moderation pipeline.</summary>
public sealed class ChatProfanityFilter : IChatProfanityFilter
{
    private readonly IReadOnlyList<Regex> _patterns;

    public ChatProfanityFilter(IOptions<ProfanityOptions> options)
    {
        _patterns = options.Value.Terms
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Select(term => new Regex(
                $@"\b{Regex.Escape(term.Trim())}\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled))
            .ToList();
    }

    public bool IsProfane(string normalizedBody) =>
        _patterns.Count > 0 && _patterns.Any(pattern => pattern.IsMatch(normalizedBody));
}
