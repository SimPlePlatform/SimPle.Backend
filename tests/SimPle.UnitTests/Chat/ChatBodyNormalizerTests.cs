using FluentAssertions;
using SimPle.Application.Chat;
using Xunit;

namespace SimPle.UnitTests.Chat;

/// <summary>Test matrix source: docs/specs/module-07-realtime-presence-chat-spec.md, "Test Matrix" —
/// "NFC normalization; CRLF/CR to LF; outer Unicode whitespace trimmed; LF allowed; other C0/C1 controls rejected;
/// 0 scalars rejected, 1 accepted, 1000 accepted, 1001 rejected; astral-plane scalars counted correctly."</summary>
public sealed class ChatBodyNormalizerTests
{
    [Fact]
    public void Normalize_CollapsesCrLfToLf()
    {
        var result = ChatBodyNormalizer.Normalize("line one\r\nline two");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("line one\nline two");
    }

    [Fact]
    public void Normalize_CollapsesLoneCrToLf()
    {
        var result = ChatBodyNormalizer.Normalize("line one\rline two");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("line one\nline two");
    }

    [Fact]
    public void Normalize_KeepsInternalLf()
    {
        var result = ChatBodyNormalizer.Normalize("line one\nline two");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("line one\nline two");
    }

    [Fact]
    public void Normalize_TrimsOuterUnicodeWhitespaceOnly()
    {
        // U+00A0 (NBSP) and U+2003 (EM SPACE) are Unicode whitespace but not ASCII space.
        var result = ChatBodyNormalizer.Normalize("   hello world \t\n");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("hello world");
    }

    [Fact]
    public void Normalize_AppliesNfcNormalization()
    {
        // "e" + combining acute accent (U+0065 U+0301) normalizes to precomposed U+00E9 ("é").
        var decomposed = "é";
        var result = ChatBodyNormalizer.Normalize(decomposed);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("é");
    }

    [Fact]
    public void Normalize_RejectsNull()
    {
        var result = ChatBodyNormalizer.Normalize(null);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be(ChatErrors.InvalidBody);
    }

    [Fact]
    public void Normalize_RejectsZeroScalarsAfterTrim()
    {
        var result = ChatBodyNormalizer.Normalize("   \t  ");

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be(ChatErrors.InvalidBody);
    }

    [Fact]
    public void Normalize_AcceptsSingleScalar()
    {
        var result = ChatBodyNormalizer.Normalize("a");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("a");
    }

    [Fact]
    public void Normalize_Accepts1000Scalars()
    {
        var body = new string('a', 1000);
        var result = ChatBodyNormalizer.Normalize(body);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveLength(1000);
    }

    [Fact]
    public void Normalize_Rejects1001Scalars()
    {
        var body = new string('a', 1001);
        var result = ChatBodyNormalizer.Normalize(body);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be(ChatErrors.InvalidBody);
    }

    [Fact]
    public void Normalize_CountsAstralPlaneScalarsCorrectly_NotUtf16Units()
    {
        // U+1F600 (grinning face emoji) is one Unicode scalar but two UTF-16 code units (a surrogate pair).
        // 999 'a' chars (999 scalars) + one astral emoji (1 scalar) = 1000 scalars, which must be accepted even
        // though the raw string.Length (UTF-16 code unit count) is 1001.
        var body = new string('a', 999) + "\U0001F600";
        body.Length.Should().Be(1001); // sanity: confirms the surrogate pair inflates UTF-16 length

        var result = ChatBodyNormalizer.Normalize(body);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(body);
    }

    [Fact]
    public void Normalize_RejectsAstralPlaneScalarOverflow()
    {
        // 1000 'a' chars (1000 scalars) + one astral emoji (1 more scalar) = 1001 scalars: must reject.
        var body = new string('a', 1000) + "\U0001F600";

        var result = ChatBodyNormalizer.Normalize(body);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be(ChatErrors.InvalidBody);
    }

    [Theory]
    [InlineData((char)0x00)] // NUL, C0
    [InlineData((char)0x01)] // C0
    [InlineData((char)0x1F)] // C0, last before LF's own 0x0A
    [InlineData((char)0x7F)] // DEL
    [InlineData((char)0x80)] // C1
    [InlineData((char)0x9F)] // C1, last C1 control
    public void Normalize_RejectsC0AndC1Controls(char control)
    {
        var body = $"hello{control}world";

        var result = ChatBodyNormalizer.Normalize(body);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be(ChatErrors.InvalidBody);
    }

    [Fact]
    public void Normalize_AllowsInternalLfControlCharacter()
    {
        var result = ChatBodyNormalizer.Normalize("hello\nworld");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("hello\nworld");
    }
}
