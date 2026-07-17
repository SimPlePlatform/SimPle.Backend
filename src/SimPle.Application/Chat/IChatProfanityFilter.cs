namespace SimPle.Application.Chat;

/// <summary>Automated deny-list/regex profanity screen (docs/module-requirements/module-07-realtime-presence-
/// chat.md, "CUSTOM" security requirement) run on every outgoing chat body before persistence, in addition to
/// M12's existing manual report/triage pipeline. A match is a normal validation error
/// (<see cref="ChatErrors.ProfanityRejected"/>), never a security event — the message is simply not stored.</summary>
public interface IChatProfanityFilter
{
    /// <param name="normalizedBody">Already <see cref="ChatBodyNormalizer"/>-normalized text.</param>
    bool IsProfane(string normalizedBody);
}
