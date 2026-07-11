using System.Text.Encodings.Web;
using System.Text.Json;
using SimPle.Domain.GameHost;

namespace SimPle.Application.GameHost.Serialization;

/// <summary>
/// Thrown when a payload fails the codec's fail-closed checks. Never carries the offending bytes or the raw
/// exception text — only a stable <see cref="EngineErrorCode"/> the caller maps to a client-safe rejection.
/// </summary>
public sealed class GameHostSerializationException(EngineErrorCode code, string message) : Exception(message)
{
    public EngineErrorCode Code { get; } = code;
}

/// <summary>
/// The one pinned <see cref="JsonSerializerOptions"/> instance every state/command/view/event payload in the
/// game-host tree is serialized and deserialized through. "Pinned" is the point: naming, number, and enum
/// handling are fixed here so a resolver misconfiguration cannot silently change envelope bytes and invalidate
/// a stored golden vector (risk #3 in the spec).
/// <para>
/// <b>No CLR type-name polymorphism is ever used.</b> Every call site deserializes into a concrete,
/// caller-selected .NET type (<c>Deserialize&lt;T&gt;</c> with <typeparamref name="object"/> never used as
/// <c>T</c>). A payload's own <c>TCommand</c> union, if a game declares one, is resolved only through
/// compile-time <see cref="System.Text.Json.Serialization.JsonDerivedTypeAttribute"/> string discriminators
/// the game author writes on their own sealed hierarchy — never through a CLR-qualified <c>$type</c> or a
/// reflection-based arbitrary activation. Combined with <see cref="JsonSerializerOptions.UnmappedMemberHandling"/>
/// set to <see cref="System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow"/>, a payload that
/// smuggles a CLR-qualified <c>$type</c>/<c>$id</c> gadget at a non-polymorphic type is rejected as an unmapped
/// member rather than ever reaching a deserializer that would honor it.
/// </para>
/// <para>
/// <b><c>AllowOutOfOrderMetadataProperties</c> is not set anywhere in this codec</b> because the property does
/// not exist on the .NET 8 <see cref="JsonSerializerOptions"/> surface the project targets — it was added in
/// .NET 9. .NET 8's polymorphic deserializer already requires the type-discriminator property to appear first
/// in a polymorphic object and throws on an out-of-order discriminator, which is exactly the "disabled"
/// (strict, in-order-only) behavior the spec mandates. There is deliberately no new package dependency added
/// solely to re-express a value that is already the platform default, matching the minimalism the D3 benchmark
/// deviation already established for this module. The serializer-hardening test suite exercises this with an
/// out-of-order-discriminator payload asserting the expected failure.
/// </para>
/// </summary>
public static class GameHostJsonContext
{
    /// <summary>
    /// The pinned options. Reflection-based (not source-generated) because game definitions are trusted,
    /// compiled-in code registering their own polymorphic hierarchies with ordinary attributes; the safety
    /// property comes from the fixed options below plus never deserializing into <c>object</c>, not from the
    /// resolver strategy.
    /// </summary>
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict,
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
            WriteIndented = false,
            // Reject the exact escape-widening surface that lets a naive template smuggle control characters;
            // game payloads are opaque data, never HTML/JS, so the strictest built-in encoder is correct here.
            Encoder = JavaScriptEncoder.Default,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
        };
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    /// <summary>
    /// Serializes <paramref name="value"/> under the pinned options. Never throws for a well-formed in-process
    /// value; a serialization failure here is a definition bug, not untrusted input, so it is allowed to
    /// propagate as an ordinary exception for <see cref="Services.GameHostInvoker"/> to map to
    /// <see cref="EngineErrorCode.PluginFailure"/>.
    /// </summary>
    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    /// <summary>
    /// Deserializes untrusted bytes into the caller-selected concrete type <typeparamref name="T"/>. Every
    /// failure mode — malformed JSON, an unknown/renamed/cross-definition discriminator, an unmapped member, a
    /// trailing second value — surfaces as <see cref="GameHostSerializationException"/> with a stable
    /// <see cref="EngineErrorCode"/> rather than an unmapped <see cref="JsonException"/>, so a caller never
    /// needs to catch <see cref="JsonException"/> directly and risk missing a new failure shape.
    /// </summary>
    public static T Deserialize<T>(ReadOnlySpan<byte> utf8Json, EngineErrorCode onFailure)
    {
        try
        {
            var result = JsonSerializer.Deserialize<T>(utf8Json, Options);
            if (result is null)
            {
                throw new GameHostSerializationException(onFailure, "Deserialized value was null.");
            }

            return result;
        }
        catch (JsonException)
        {
            throw new GameHostSerializationException(onFailure, "Payload failed fail-closed deserialization.");
        }
        catch (NotSupportedException)
        {
            // The polymorphic resolver throws NotSupportedException (not JsonException) for an undeclared
            // derived type under UnknownDerivedTypeHandling.FailSerialization — both are "the payload's
            // claimed shape is not one this type accepts" and must map to the same typed rejection.
            throw new GameHostSerializationException(onFailure, "Payload declared an unrecognized derived type.");
        }
    }
}
