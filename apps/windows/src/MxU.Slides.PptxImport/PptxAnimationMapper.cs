using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.PptxImport;

/// <summary>Turns PowerPoint timing effects into MxU animation steps, picking the nearest MxU animation per preset.</summary>
internal static class PptxAnimationMapper
{
    public sealed record Result(List<string>? AnimationOrder, List<string> Warnings);

    public static Result Apply(IEnumerable<PptxAnimation> animationSteps, IReadOnlyDictionary<int, string> objectIds, List<SlideObject> objects)
    {
        var warnings = new List<string>();
        var order = new List<string>();
        foreach (var build in animationSteps)
        {
            if (!objectIds.TryGetValue(build.ShapeId, out var objectId)
                || objects.FirstOrDefault(o => o.Id == objectId) is not { } target)
                continue;
            if (Step(build, target, warnings) is not { } step) continue;
            target.AnimationSteps = [.. target.AnimationSteps ?? [], step];
            order.Add(step.Id);
        }
        return new Result(order.Count == 0 ? null : order, warnings);
    }

    public static AnimationStep? Step(PptxAnimation build, SlideObject target, List<string> warnings)
    {
        AnimationKind kind;
        switch (build.PresetClass)
        {
            case "entr": kind = AnimationKind.In; break;
            case "exit": kind = AnimationKind.Out; break;
            case "emph" or "path": kind = AnimationKind.Emphasis; break;
            default: return null;
        }
        var trigger = build.NodeType switch
        {
            "withEffect" => AnimationTrigger.WithPrevious,
            "afterEffect" => AnimationTrigger.AfterPrevious,
            _ => AnimationTrigger.OnClick,
        };
        var step = new AnimationStep
        {
            Id = LibraryStore.NewId(),
            Kind = kind,
            Animation = StepAnimation.Fade,
            Trigger = trigger,
            DurationSeconds = Math.Max((build.DurationMs ?? 500) / 1000, 0),
        };

        if (trigger != AnimationTrigger.AfterPrevious && build.DelayMs > 0) step.DelaySeconds = build.DelayMs / 1000;
        ApplyPreset(build, kind, step, warnings);
        if (build.ParagraphStart is { } start)
        {
            var ranges = ParagraphRanges(start, build.ParagraphEnd ?? start, target.Text);
            if (ranges.Count > 0) step.Ranges = ranges;
        }
        return step;
    }

    public static List<AnimationRange> ParagraphRanges(int first, int last, string text)
    {
        var lines = text.Split('\n');
        var ranges = new List<AnimationRange>();
        for (var line = first; line <= last; line++)
        {
            if (line < 0 || line >= lines.Length || lines[line].Length == 0) continue;
            ranges.Add(new AnimationRange { Line = line, Column = 0, Length = PptxDocumentMapper.CharacterCount(lines[line]) });
        }
        return ranges;
    }

    private static AnimationEdge? Edge(int? subtype)
    {
        if (subtype is not { } value) return null;
        if ((value & 8) != 0) return AnimationEdge.Left;
        if ((value & 2) != 0) return AnimationEdge.Right;
        if ((value & 4) != 0) return AnimationEdge.Bottom;
        if ((value & 1) != 0) return AnimationEdge.Top;
        return null;
    }

    private static void ApplyPreset(PptxAnimation build, AnimationKind kind, AnimationStep step, List<string> warnings)
    {
        void Note(string name, string nearest) =>
            warnings.AppendUnique($"animation \"{name}\" has no MxU equivalent; imported as {nearest}");
        var id = build.PresetId ?? 0;
        if (build.PresetClass == "path")
        {
            step.Animation = StepAnimation.Move;
            step.OffsetX = 0;
            step.OffsetY = -40;
            Note("motion path", "a nudge");
            return;
        }
        if (kind == AnimationKind.Emphasis)
        {
            switch (id)
            {
                case 6:
                    step.Animation = StepAnimation.Scale; step.Amount = 1.2;
                    break;
                case 1 or 3 or 7 or 14 or 16 or 17 or 18 or 19 or 20 or 21 or 22 or 25 or 26:
                    step.Animation = StepAnimation.Color;
                    break;
                case 9 or 24 or 31:
                    step.Animation = StepAnimation.Fade;
                    break;
                case 10 or 23 or 32 or 33 or 36:
                    step.Animation = StepAnimation.Pulse; step.Amount = 1.1;
                    break;
                case 8 or 28 or 30 or 35:
                    step.Animation = StepAnimation.Move; step.OffsetX = 0; step.OffsetY = -20;
                    Note($"emphasis {id}", "a nudge");
                    break;
                default:
                    step.Animation = StepAnimation.Pulse; step.Amount = 1.1;
                    Note($"emphasis {id}", "a pulse");
                    break;
            }
            return;
        }

        switch (id)
        {
            case 1:
                step.Animation = StepAnimation.Fade; step.DurationSeconds = 0;
                break;
            case 9 or 10:
                step.Animation = StepAnimation.Fade;
                break;
            case 2 or 7 or 12 or 26 or 27 or 30 or 32 or 37 or 38 or 41 or 42 or 47 or 48 or 52 or 54:
                step.Animation = StepAnimation.Move; step.Edge = Edge(build.PresetSubtype) ?? AnimationEdge.Bottom;
                step.WithFade = id != 2 && id != 7;
                break;
            case 3 or 4 or 5 or 6 or 8 or 13 or 14 or 16 or 18 or 20 or 21 or 22 or 28 or 39:
                step.Animation = StepAnimation.Wipe; step.Edge = Edge(build.PresetSubtype) ?? AnimationEdge.Left;
                step.SoftEdge = id == 22 ? 0 : 60;
                if (id is not (22 or 16 or 4 or 6)) Note($"entrance/exit {id}", "a soft wipe");
                break;
            case 23 or 31 or 53 or 55 or 50 or 17 or 51 or 40 or 56 or 58 or 33:
                step.Animation = StepAnimation.Scale; step.FromScale = 0.7; step.WithFade = true;
                if (id is not (23 or 31 or 53 or 55 or 50)) Note($"entrance/exit {id}", "a scale");
                break;
            case 19 or 45 or 43 or 49 or 35 or 15:
                step.Animation = StepAnimation.Scale; step.FromScale = 0.6; step.WithFade = true;
                Note($"entrance/exit {id}", "a scale");
                break;
            case 11 or 34:
                step.Animation = StepAnimation.Burn; step.Amount = 0.6;
                break;
            case 29:
                step.Animation = StepAnimation.Move; step.Edge = AnimationEdge.Bottom;
                step.DurationSeconds = Math.Max(step.DurationSeconds, 8);
                Note("Credits", "a slow rise (use Block Scroll for a real roll)");
                break;
            case 24:
                step.Animation = StepAnimation.Fade;
                Note("Random Effects", "a fade");
                break;
            default:
                step.Animation = StepAnimation.Fade;
                Note($"entrance/exit {id}", "a fade");
                break;
        }
    }
}
