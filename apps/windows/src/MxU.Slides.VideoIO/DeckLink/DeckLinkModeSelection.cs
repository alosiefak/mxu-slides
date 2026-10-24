namespace MxU.Slides.VideoIO.DeckLink;

/// <summary>The mode an output will open in, and the line the settings show for it ("1080p29.97 (Auto) · no reference").</summary>
public sealed record DeckLinkModeChoice(uint ModeId, string WireDetail);

/// <summary>
/// Picks the card mode for an output. A Format the operator chose wins. On Auto: the
/// output mode set in Desktop Video Setup if it is the program's size (and can key, when
/// keying), else the closest mode by <see cref="DeckLinkDisplayMode.BestMatch"/>, else a
/// message asking for a Format. Port of the mode half of DeckLinkHelper's startOutput.
/// </summary>
public static class DeckLinkModeSelection
{
    public static (DeckLinkModeChoice? Choice, string? Error) Choose(
        IReadOnlyList<DeckLinkDisplayMode> allModes, DeckLinkKeying keying, uint? configuredMode,
        int width, int height, int frameRateNumerator, int frameRateDenominator, long requestedModeId,
        Func<bool> referenceLocked)
    {
        uint chosen;
        var viaAuto = false;
        if (requestedModeId != 0)
        {
            chosen = unchecked((uint)requestedModeId);
        }
        else
        {
            if (allModes.Count == 0)
                return (null, "no playback modes — this connector may be mapped as an input in Desktop Video Setup");
            var modes = allModes.Where(mode => keying == DeckLinkKeying.Off || mode.SupportsKeying).ToList();
            if (configuredMode is { } configured && modes.FirstOrDefault(mode =>
                    mode.ModeId == configured && mode.Width == width && mode.Height == height) is { } cardMode)
                chosen = cardMode.ModeId;
            else if (frameRateDenominator != 0
                && DeckLinkDisplayMode.BestMatch(width, height, frameRateNumerator / frameRateDenominator, modes) is { } match)
                chosen = match.ModeId;
            else
                return (null, $"no SDI mode matches {width}×{height} at {frameRateNumerator}/{frameRateDenominator} fps — "
                    + "choose a Format to send this screen scaled");
            viaAuto = true;
        }

        var parts = new List<string>();
        if (allModes.FirstOrDefault(mode => mode.ModeId == chosen)?.Name is { } name) parts.Add(viaAuto ? $"{name} (Auto)" : name);
        if (keying == DeckLinkKeying.External && !referenceLocked()) parts.Add("no reference");
        return (new DeckLinkModeChoice(chosen, string.Join(" · ", parts)), null);
    }
}
