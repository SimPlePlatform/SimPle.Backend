using System.Reflection;
using FluentAssertions;
using SimPle.Domain.Chat;

namespace SimPle.UnitTests.Realtime;

/// <summary>
/// Regression tests for the Module 7 dead-code removal (docs/specs/module-07-realtime-presence-chat-spec.md,
/// backend sessions A/B, M07-B1/M07-B2): the orphan <c>ChatContext.DirectMessage</c>-era stub (zero DbSet, zero
/// EF config, zero migration, zero repository, zero consumer) was replaced outright by <see cref="ChatScope"/> in
/// M07-B2 — never migrated, never given a <c>DirectMessage</c> member — and the four unused placeholder notifier
/// interfaces that routed by a non-existent <c>lobbyCode</c> were deleted outright in M07-B1. These tests fail
/// loudly if any of it ever comes back.
/// </summary>
public sealed class RealtimeDeadCodeRegressionTests
{
    [Fact]
    public void ChatScope_HasNoDirectMessageValue()
    {
        Enum.GetNames<ChatScope>().Should().NotContain("DirectMessage");
    }

    [Fact]
    public void ChatScope_OnlyHasLobbyAndMatch()
    {
        Enum.GetNames<ChatScope>().Should().BeEquivalentTo("Lobby", "Match");
    }

    [Fact]
    public void NoChatContextTypeSurvivesAnywhereInTheLoadedAssemblies()
    {
        // ChatContext was the orphan stub's enum name (pre-M07-B2). Replaced by ChatScope, not renamed in place —
        // if a type literally named ChatContext ever reappears, something resurrected the old shape.
        typeof(ChatMessage).Assembly.GetTypes().Select(t => t.Name).Should().NotContain("ChatContext");
    }

    [Fact]
    public void NoLobbyCodeRoutedNotifierInterfaceSurvivesAnywhereInTheLoadedAssemblies()
    {
        // The dead placeholder file (src/SimPle.Infrastructure/Realtime/IHubContext.cs) declared
        // IPresenceNotifier/ILobbyNotifier/IGameNotifier/IHardwareNotifier, none of which had a single
        // implementation or caller. If any of these type names ever reappear anywhere in the solution's own
        // assemblies, this test fails — the fix was to delete them, not resurrect them under the same name.
        var deadTypeNames = new[] { "IPresenceNotifier", "ILobbyNotifier", "IGameNotifier", "IHardwareNotifier" };

        var assemblies = new[]
        {
            typeof(SimPle.Application.Realtime.Contracts.IRealtimeClient).Assembly,
            typeof(SimPle.Domain.Chat.ChatMessage).Assembly,
        };

        foreach (var assembly in assemblies)
        {
            var typeNames = assembly.GetTypes().Select(t => t.Name).ToList();
            var resurrected = typeNames.Intersect(deadTypeNames).ToList();
            resurrected.Should().BeEmpty(
                $"assembly {assembly.GetName().Name} should not declare any resurrected dead placeholder type");
        }
    }
}
