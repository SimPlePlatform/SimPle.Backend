using SimPle.Domain.Common;

namespace SimPle.Domain.Chat;

/// <summary>
/// Chat scope. Deliberately no <c>DirectMessage</c> member — direct messages are an explicit Module 7 non-goal
/// (docs/specs/module-07-realtime-presence-chat-spec.md, "Non-Goals"). Do not add one back.
/// </summary>
public enum ChatScope
{
    Lobby = 0,
    Match = 1,
}

/// <summary>
/// A persisted lobby/match chat message (docs/specs/module-07-realtime-presence-chat-spec.md, "Data Model").
/// Replaces the M07-B1-era orphan stub outright (no DbSet, no EF config, no migration, no consumer ever existed
/// for it) rather than migrating it.
///
/// <para>
/// <strong>Author deletion does not clear <see cref="Body"/>.</strong> The spec states this explicitly: "a
/// deleted body persists on disk for up to 30 days, by design" so M12's evidence copy can still run after an
/// author deletes. The <em>read path</em> (<c>ChatService</c>/DTO projection) is what returns
/// <c>body: null, deleted: true</c> — the body itself never leaves the server again once deleted. Never project
/// <see cref="Body"/> directly for a deleted row.
/// </para>
///
/// <para>
/// <see cref="Entity.CreatedAt"/> is overwritten from the caller's injected <c>TimeProvider</c> at construction
/// (Risk #7) — <c>Entity</c>'s own default is a raw <see cref="DateTime.UtcNow"/>, which would silently defeat the
/// fake-clock retention/hold tests.
/// </para>
/// </summary>
public class ChatMessage : Entity
{
    /// <summary>Chat is retained 30 days. There is no separate M7 evidence-retention policy — M12 owns its own
    /// two-year moderation-evidence domain in its own table via the <see cref="Chat.ChatMessageHold"/> seam.</summary>
    public static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(30);

    public ChatScope Scope { get; private set; }
    public Guid ScopeId { get; private set; }
    public Guid SenderId { get; private set; }

    /// <summary>NFC-normalized, LF-only, 1-1000 Unicode scalars. Normalized by <c>ChatBodyNormalizer</c> BEFORE
    /// this is constructed — this type does not re-validate it.</summary>
    public string Body { get; private set; } = default!;

    public int SchemaVersion { get; private set; } = 1;

    /// <summary>Idempotency key supplied by the client. Unique with <see cref="SenderId"/>
    /// (<c>ux_chat_messages_sender_command</c>) — a duplicate send catches <c>23505</c> and returns the original.</summary>
    public Guid ClientCommandId { get; private set; }

    public DateTime? DeletedAtUtc { get; private set; }
    public Guid? DeletedByUserId { get; private set; }

    /// <summary><see cref="Entity.CreatedAt"/> + <see cref="RetentionPeriod"/>, fixed at creation. The cleanup
    /// sweep's scan key (<c>ix_chat_messages_retain</c>).</summary>
    public DateTime RetainUntilUtc { get; private set; }

    public bool IsDeleted => DeletedAtUtc is not null;

    private ChatMessage() { }

    public static ChatMessage Create(
        ChatScope scope, Guid scopeId, Guid senderId, string normalizedBody, Guid clientCommandId, DateTime nowUtc)
    {
        var message = new ChatMessage
        {
            Scope = scope,
            ScopeId = scopeId,
            SenderId = senderId,
            Body = normalizedBody,
            SchemaVersion = 1,
            ClientCommandId = clientCommandId,
            RetainUntilUtc = nowUtc + RetentionPeriod,
        };

        // Overwrite Entity's DateTime.UtcNow default with the injected clock (Risk #7) — accessible here because
        // CreatedAt/UpdatedAt are `protected set` and this factory runs inside the derived type.
        message.CreatedAt = nowUtc;
        message.UpdatedAt = nowUtc;

        return message;
    }

    /// <summary>Tombstones the message. Idempotent — a retried delete finds <see cref="IsDeleted"/> already true
    /// and does nothing further, matching a chat command's UUID-idempotency convention elsewhere in this module.
    /// The id is never reused and the body is never cleared (see class doc).</summary>
    public void Delete(Guid deletedByUserId, DateTime nowUtc)
    {
        if (IsDeleted) return;

        DeletedAtUtc = nowUtc;
        DeletedByUserId = deletedByUserId;
        UpdatedAt = nowUtc;
    }
}
