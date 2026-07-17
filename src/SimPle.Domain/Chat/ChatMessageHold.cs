using SimPle.Domain.Common;

namespace SimPle.Domain.Chat;

/// <summary>
/// A moderation hold against one <see cref="ChatMessage"/> row (docs/specs/module-07-realtime-presence-chat-spec.md,
/// "Data Model"). Created by Module 7, ships empty, written only by Module 12 through the
/// <c>IChatRepository.PlaceHoldAsync</c> seam this module builds but never calls.
///
/// <para>
/// A hold <strong>table</strong>, deliberately not a <c>HoldCount</c> column on <c>chat_messages</c>: a counter
/// column would make M12 write into an M7-owned aggregate — a boundary violation, and a field M7 could never
/// validate. This table lets M12 add its own evidence table keyed by <see cref="MessageId"/> with no schema break.
/// </para>
///
/// <para><see cref="ReasonCode"/> is M12's vocabulary; M7 never interprets it.</para>
/// </summary>
public class ChatMessageHold : Entity
{
    public Guid MessageId { get; private set; }
    public string ReasonCode { get; private set; } = default!;
    public DateTime PlacedAtUtc { get; private set; }

    /// <summary>Null = active hold. The partial index (<c>ix_chat_message_holds_active</c>) that the cleanup
    /// sweep's <c>NOT EXISTS</c> probe uses is built on exactly this predicate.</summary>
    public DateTime? ReleasedAtUtc { get; private set; }

    /// <summary>Set once M12's evidence copy is durable.</summary>
    public DateTime? AcknowledgedAtUtc { get; private set; }

    public bool IsActive => ReleasedAtUtc is null;

    private ChatMessageHold() { }

    public static ChatMessageHold Place(Guid messageId, string reasonCode, DateTime nowUtc)
    {
        var hold = new ChatMessageHold
        {
            MessageId = messageId,
            ReasonCode = reasonCode,
            PlacedAtUtc = nowUtc,
        };

        hold.CreatedAt = nowUtc;
        hold.UpdatedAt = nowUtc;

        return hold;
    }

    public void Release(DateTime nowUtc)
    {
        ReleasedAtUtc = nowUtc;
        UpdatedAt = nowUtc;
    }

    public void Acknowledge(DateTime nowUtc)
    {
        AcknowledgedAtUtc = nowUtc;
        UpdatedAt = nowUtc;
    }
}
