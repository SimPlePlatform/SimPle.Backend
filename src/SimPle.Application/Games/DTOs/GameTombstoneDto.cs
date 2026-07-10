namespace SimPle.Application.Games.DTOs;

/// <summary>
/// Minimal shape returned for a Retired game detail read (410). Carries only enough for the client to render
/// an honest "this game is retired" state — no summary, no tags, no capabilities, no art.
/// </summary>
public sealed record GameTombstoneDto(string Slug, string Name, string Lifecycle, string ReasonCode);
