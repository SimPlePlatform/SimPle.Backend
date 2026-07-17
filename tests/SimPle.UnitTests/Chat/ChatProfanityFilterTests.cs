using FluentAssertions;
using Microsoft.Extensions.Options;
using SimPle.Application.Chat;
using Xunit;

namespace SimPle.UnitTests.Chat;

/// <summary>Test matrix source: docs/specs/module-07-realtime-presence-chat-spec.md, "Test Matrix" — "Profanity
/// filter: deny-list match rejects and does not persist; versioned config is read server-side only."</summary>
public sealed class ChatProfanityFilterTests
{
    private static ChatProfanityFilter CreateFilter(params string[] terms) =>
        new(Options.Create(new ProfanityOptions { Terms = terms }));

    [Fact]
    public void IsProfane_MatchesConfiguredTerm_CaseInsensitive()
    {
        var filter = CreateFilter("badword");

        filter.IsProfane("this is a BadWord in a sentence").Should().BeTrue();
    }

    [Fact]
    public void IsProfane_DoesNotMatchSubstringOfAnotherWord()
    {
        var filter = CreateFilter("ass");

        // "class" contains "ass" as a substring but not as a whole word.
        filter.IsProfane("this is my class assignment").Should().BeFalse();
    }

    [Fact]
    public void IsProfane_MatchesWholeWordWithPunctuationBoundary()
    {
        var filter = CreateFilter("badword");

        filter.IsProfane("badword!").Should().BeTrue();
    }

    [Fact]
    public void IsProfane_ReturnsFalseWhenNoTermsConfigured()
    {
        var filter = CreateFilter();

        filter.IsProfane("anything at all").Should().BeFalse();
    }

    [Fact]
    public void IsProfane_ReturnsFalseForCleanMessage()
    {
        var filter = CreateFilter("badword", "worseword");

        filter.IsProfane("hello, how are you today?").Should().BeFalse();
    }

    [Fact]
    public void IsProfane_IgnoresBlankConfiguredTerms()
    {
        var filter = CreateFilter("", "   ", "badword");

        filter.IsProfane("this has a badword in it").Should().BeTrue();
        filter.IsProfane("this is clean").Should().BeFalse();
    }
}
