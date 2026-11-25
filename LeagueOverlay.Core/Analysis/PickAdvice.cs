namespace LeagueOverlay.Core.Analysis;

/// <summary>
/// Champion suggestions for the champion select screen: the mode, up to three picks (one in surprise mode), your role,
/// a note on what they're based on, and whether a pick can be hovered in the client for you.
/// </summary>
public sealed record PickAdvice(PickMode Mode, ValueList<PickSuggestion> Picks, string? Role, string Note, bool CanHover);
