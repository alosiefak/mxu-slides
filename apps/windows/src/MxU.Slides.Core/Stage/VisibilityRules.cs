using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Stage;

/// <summary>
/// An object's show/hide conditions in render form (RenderEngine's ItemVisibility).
/// A render item can carry one so a whole scene can be filtered per frame.
/// </summary>
public sealed record ItemVisibility(VisibilityMatch Match, IReadOnlyList<ItemVisibility.Condition> Conditions)
{
    public sealed record Condition(ConditionKind Kind, RequiredState State);

    public abstract record ConditionKind
    {
        private ConditionKind() { }

        /// <summary>A named timer, or the primary timer when <paramref name="Id"/> is null.</summary>
        public sealed record Timer(string? Id) : ConditionKind;
        public sealed record VideoCountdown : ConditionKind;
        public sealed record AudioPlayback : ConditionKind;
        public sealed record LiveInput(string? Id) : ConditionKind;
        public sealed record Capture : ConditionKind;
        public sealed record ObjectText(string Id) : ConditionKind;

        /// <summary>A condition this build cannot judge; it never hides anything.</summary>
        public sealed record Unevaluable : ConditionKind;
    }

    public enum RequiredState { HasTimeRemaining, HasExpired, IsRunning, IsNotRunning, IsConnected, IsNotConnected, HasText, HasNoText }
}

/// <summary>
/// Show/hide conditions on stage display objects (timer running, video playing,
/// input live, sibling has text…). Port of SlideScene/VisibilityRules.swift;
/// filteredScene(...) belongs with the render model and can be built on
/// <see cref="IsShown(ItemVisibility?, ConfidenceInfo, DateTimeOffset, Func{string, bool?})"/>.
/// </summary>
public static class VisibilityRules
{
    public static ItemVisibility? ItemVisibilityFor(SlideObject o)
    {
        if (o.VisibilityConditions is not { Count: > 0 } conditions) return null;
        return new ItemVisibility(o.VisibilityMatch ?? VisibilityMatch.All, conditions.Select(ConditionFrom).ToList());
    }

    internal static ItemVisibility.Condition ConditionFrom(VisibilityCondition schema)
    {
        ItemVisibility.RequiredState? state = schema.State switch
        {
            VisibilityConditionState.HasTimeRemaining => ItemVisibility.RequiredState.HasTimeRemaining,
            VisibilityConditionState.HasExpired => ItemVisibility.RequiredState.HasExpired,
            VisibilityConditionState.IsRunning or VisibilityConditionState.IsActive => ItemVisibility.RequiredState.IsRunning,
            VisibilityConditionState.IsNotRunning or VisibilityConditionState.IsInactive => ItemVisibility.RequiredState.IsNotRunning,
            VisibilityConditionState.IsConnected => ItemVisibility.RequiredState.IsConnected,
            VisibilityConditionState.IsDisconnected => ItemVisibility.RequiredState.IsNotConnected,
            VisibilityConditionState.HasText => ItemVisibility.RequiredState.HasText,
            VisibilityConditionState.HasNoText => ItemVisibility.RequiredState.HasNoText,
            _ => null,
        };
        ItemVisibility.ConditionKind kind = schema.ConditionKind switch
        {
            VisibilityConditionKind.Timer => new ItemVisibility.ConditionKind.Timer(NonEmpty(schema.TimerId)),
            VisibilityConditionKind.VideoCountdown => new ItemVisibility.ConditionKind.VideoCountdown(),
            VisibilityConditionKind.ObjectText when NonEmpty(schema.ObjectId) is { } id => new ItemVisibility.ConditionKind.ObjectText(id),
            VisibilityConditionKind.AudioPlayback => new ItemVisibility.ConditionKind.AudioPlayback(),
            VisibilityConditionKind.LiveInput => new ItemVisibility.ConditionKind.LiveInput(NonEmpty(schema.LiveInputId)),
            VisibilityConditionKind.Capture => new ItemVisibility.ConditionKind.Capture(),
            _ => new ItemVisibility.ConditionKind.Unevaluable(),
        };
        return state is { } required
            ? new ItemVisibility.Condition(kind, required)
            : new ItemVisibility.Condition(new ItemVisibility.ConditionKind.Unevaluable(), ItemVisibility.RequiredState.IsRunning);
    }

    /// <summary>Whether a (resolved) layout object shows; object-text conditions read its resolved siblings.</summary>
    public static bool IsShown(SlideObject o, IReadOnlyList<SlideObject> siblings, ConfidenceInfo info, DateTimeOffset at) =>
        IsShown(ItemVisibilityFor(o), info, at, id =>
            siblings.FirstOrDefault(s => s.Id == id) is { } sibling ? !string.IsNullOrWhiteSpace(sibling.Text) : null);

    /// <summary>
    /// Conditions that cannot be judged (a deleted timer, an unpicked input) count as
    /// met for all/any and as unmet for none, so they never hide anything.
    /// </summary>
    public static bool IsShown(ItemVisibility? visibility, ConfidenceInfo info, DateTimeOffset at, Func<string, bool?> objectHasText)
    {
        if (visibility is null || visibility.Conditions.Count == 0) return true;
        var verdicts = visibility.Conditions.Select(c => Holds(c, info, at, objectHasText)).ToList();
        return visibility.Match switch
        {
            VisibilityMatch.Any => verdicts.Any(v => v ?? true),
            VisibilityMatch.None => !verdicts.Any(v => v == true),
            _ => verdicts.All(v => v ?? true),
        };
    }

    private static bool? Holds(ItemVisibility.Condition condition, ConfidenceInfo info, DateTimeOffset at, Func<string, bool?> objectHasText)
    {
        var state = condition.State;
        switch (condition.Kind)
        {
            case ItemVisibility.ConditionKind.Timer timer:
            {
                var snapshot = timer.Id is { } id
                    ? info.Timers.FirstOrDefault(t => t.Id == id)
                    : ConfidenceSceneBuilder.PrimaryTimer(info.Timers);
                if (snapshot is null) return null;
                return state switch
                {
                    ItemVisibility.RequiredState.IsRunning => snapshot.IsLive,
                    ItemVisibility.RequiredState.IsNotRunning => !snapshot.IsLive,
                    ItemVisibility.RequiredState.HasTimeRemaining => snapshot.Remaining(at) is { } remaining ? remaining > 0 : null,
                    ItemVisibility.RequiredState.HasExpired => snapshot.Remaining(at) is { } remaining ? remaining <= 0 : null,
                    _ => null,
                };
            }
            case ItemVisibility.ConditionKind.VideoCountdown:
                return Playback(info.VideoCountdown, state, at);
            case ItemVisibility.ConditionKind.AudioPlayback:
                return Playback(info.AudioCountdown, state, at);
            case ItemVisibility.ConditionKind.LiveInput input:
                if (input.Id is not { } inputId) return null;
                return state switch
                {
                    ItemVisibility.RequiredState.IsRunning => info.ActiveLiveInputIds.Contains(inputId),
                    ItemVisibility.RequiredState.IsNotRunning => !info.ActiveLiveInputIds.Contains(inputId),
                    ItemVisibility.RequiredState.IsConnected => info.ConnectedLiveInputIds.Contains(inputId),
                    ItemVisibility.RequiredState.IsNotConnected => !info.ConnectedLiveInputIds.Contains(inputId),
                    _ => null,
                };
            case ItemVisibility.ConditionKind.Capture:
                return state switch
                {
                    ItemVisibility.RequiredState.IsRunning => info.CaptureActive,
                    ItemVisibility.RequiredState.IsNotRunning => !info.CaptureActive,
                    _ => null,
                };
            case ItemVisibility.ConditionKind.ObjectText text:
                if (objectHasText(text.Id) is not { } hasText) return null;
                return state switch
                {
                    ItemVisibility.RequiredState.HasText => hasText,
                    ItemVisibility.RequiredState.HasNoText => !hasText,
                    _ => null,
                };
            default:
                return null;
        }
    }

    /// <summary>Video and audio playback: nothing playing is a definite state (not running, no time left), not an unknown.</summary>
    private static bool? Playback(VideoCountdown? countdown, ItemVisibility.RequiredState state, DateTimeOffset at)
    {
        if (countdown is null)
            return state switch
            {
                ItemVisibility.RequiredState.IsRunning or ItemVisibility.RequiredState.HasTimeRemaining => false,
                ItemVisibility.RequiredState.IsNotRunning => true,
                ItemVisibility.RequiredState.HasExpired => false,
                _ => null,
            };
        return state switch
        {
            ItemVisibility.RequiredState.IsRunning => countdown.IsPlaying,
            ItemVisibility.RequiredState.IsNotRunning => !countdown.IsPlaying,
            ItemVisibility.RequiredState.HasTimeRemaining => countdown.Remaining(at) > 0,
            ItemVisibility.RequiredState.HasExpired => countdown.Remaining(at) <= 0,
            _ => null,
        };
    }

    private static string? NonEmpty(string? text) => string.IsNullOrEmpty(text) ? null : text;
}
