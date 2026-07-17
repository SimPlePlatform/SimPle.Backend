using System.Text;
using SimPle.Shared.Common;

namespace SimPle.Application.Chat;

/// <summary>
/// Normalizes and validates a raw chat body before it is ever stored (docs/specs/module-07-realtime-presence-chat-
/// spec.md, "Test Matrix": "NFC normalization; CRLF/CR to LF; outer Unicode whitespace trimmed; LF allowed; other
/// C0/C1 controls rejected; 0 scalars rejected, 1 accepted, 1000 accepted, 1001 rejected; astral-plane scalars
/// counted correctly.").
///
/// <para>Counts <em>Unicode scalar values</em> (<see cref="Rune"/>), never UTF-16 code units — a single astral
/// character (e.g. an emoji outside the BMP) is one scalar even though it is two <c>char</c>s.</para>
/// </summary>
public static class ChatBodyNormalizer
{
    public const int MinScalars = 1;
    public const int MaxScalars = 1000;

    public static Result<string> Normalize(string? rawBody)
    {
        if (rawBody is null) return Fail();

        // CRLF/CR -> LF first, so a lone CR or a CRLF pair both collapse to the one allowed control character.
        var unified = rawBody.Replace("\r\n", "\n").Replace("\r", "\n");

        // NFC normalization. Combining-mark sequences and their precomposed equivalents must count and compare
        // identically; skipping this would let visually-identical bodies evade the profanity deny-list.
        var normalizedForm = unified.Normalize(NormalizationForm.FormC);

        // Outer Unicode whitespace trim only — String.Trim() uses Char.IsWhiteSpace, which is Unicode-aware, and
        // only removes from the ends, so an internal LF (a deliberate multi-line message) survives untouched.
        var trimmed = normalizedForm.Trim();

        var scalarCount = 0;
        foreach (var rune in trimmed.EnumerateRunes())
        {
            if (IsRejectedControl(rune.Value)) return Fail();
            scalarCount++;
        }

        if (scalarCount is < MinScalars or > MaxScalars) return Fail();

        return Result<string>.Ok(trimmed);
    }

    /// <summary>LF (U+000A) is the one allowed control character (it is how a multi-line body is represented post
    /// CRLF/CR collapse). Every other C0 control (U+0000-U+001F) and every C1 control plus DEL (U+007F-U+009F) is
    /// rejected.</summary>
    private static bool IsRejectedControl(int scalarValue) =>
        (scalarValue <= 0x1F && scalarValue != 0x0A) || (scalarValue is >= 0x7F and <= 0x9F);

    private static Result<string> Fail() => Result<string>.Fail(ChatErrors.InvalidBody, "Message body is invalid.");
}
