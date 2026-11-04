using CommunityToolkit.Mvvm.ComponentModel;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Stage;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// What a stage layout's text box shows: words you type, or something the app fills in (the live
/// slide, a timer, the clock…), and that source's settings: which timer, the clock's and the timer's
/// format, chords, how many lines of the next slide, which video's countdown. Port of the "Text"
/// section of the Mac's SlideObjectInspector.swift (textContentSection, linkSourceBinding, the clock
/// and timer format presets), for the stage layout editor.
/// </summary>
public sealed partial class StageLinkViewModel(SlideObject o, Action changed, Func<IReadOnlyList<Choice<string?>>> timers) : ObservableObject
{
    public SlideObject Object => o;

    public bool IsText => o.ObjectKind == SlideObjectKind.Text;

    private TextLink? Link => o.TextLink;

    private void Write(Action change)
    {
        change();
        OnPropertyChanged(string.Empty);
        changed();
    }

    /// <summary>"Static text" (null), then every source a box can read; "Slide notes" is Windows' name for the current slide's "Notes".</summary>
    public IReadOnlyList<Choice<string?>> Sources
    {
        get
        {
            var list = new List<Choice<string?>> { new(null, "Static text (what you type)"), new(NotesKey, "Slide notes") };
            list.AddRange(LinkedText.AuthoringSources(Link?.Source).Select(s => new Choice<string?>(TextSourceKindJsonConverter.ToRaw(s), StageLayoutEditing.SourceName(s))));
            return list;
        }
    }

    private const string NotesKey = "windows:notes";

    private bool IsNotes => Link is { Source: TextSourceKind.CurrentSlide, SourceObjectName: { } name } && string.Equals(name, StageLayoutEditing.NotesObjectName, StringComparison.OrdinalIgnoreCase);

    public Choice<string?>? Source
    {
        get
        {
            var key = Link is null ? null : IsNotes ? NotesKey : TextSourceKindJsonConverter.ToRaw(Link.Source);
            return Sources.FirstOrDefault(c => c.Value == key);
        }
        set
        {
            if (value is null || value.Value == Source?.Value) return;
            Write(() =>
            {
                if (value.Value is null) o.TextLink = null;
                else if (value.Value == NotesKey) o.TextLink = new TextLink { Source = TextSourceKind.CurrentSlide, SourceObjectName = StageLayoutEditing.NotesObjectName };
                else if (TextSourceKindJsonConverter.TryParse(value.Value, out var kind)) o.TextLink = new TextLink { Source = kind };
            });
        }
    }

    public bool IsStatic => Link is null;

    public string Text
    {
        get => o.Text;
        set
        {
            var normalized = value.Replace("\r\n", "\n");
            if (normalized == o.Text) return;
            o.Text = normalized;
            OnPropertyChanged();
            changed();
        }
    }

    // MARK: A timer

    public bool IsTimer => Link?.Source == TextSourceKind.Timer;

    /// <summary>"The first running timer" (null), the Timers panel's timers, then the service-tracking timers.</summary>
    public IReadOnlyList<Choice<string?>> Timers => [new(null, "The first running timer"), .. timers()];

    public Choice<string?>? Timer
    {
        get => Timers.FirstOrDefault(t => t.Value == (string.IsNullOrEmpty(Link?.TimerId) ? null : Link!.TimerId)) ?? new Choice<string?>(Link?.TimerId, "A timer that's gone");
        set
        {
            if (value is null || Link is not { } link || value.Value == link.TimerId) return;
            Write(() => link.TimerId = value.Value);
        }
    }

    public bool UsesTimerFormat => Link?.Source is TextSourceKind.Timer or TextSourceKind.VideoCountdown;

    public IReadOnlyList<Choice<TimerTextFormat>> TimerFormats { get; } = [.. StageLayoutEditing.TimerFormats.Select(f => new Choice<TimerTextFormat>(f.Format, f.Label))];

    public Choice<TimerTextFormat>? TimerFormat
    {
        get => TimerFormats.FirstOrDefault(f => f.Value == (Link?.TimerFormat ?? TimerTextFormat.Digits));
        set
        {
            if (value is null || Link is not { } link) return;
            Write(() =>
            {
                link.TimerFormat = value.Value == TimerTextFormat.Digits ? null : value.Value;
                link.TimerPattern = null;
            });
        }
    }

    /// <summary>The timer turns amber, then red, as it runs out (on unless turned off).</summary>
    public bool UsesWarningColor
    {
        get => Link?.UsesWarningColor != false;
        set
        {
            if (Link is not { } link) return;
            Write(() => link.UsesWarningColor = value ? null : false);
        }
    }

    // MARK: The clock

    public bool IsClock => Link?.Source == TextSourceKind.Clock;

    public IReadOnlyList<Choice<string>> ClockFormats { get; } = [.. StageLayoutEditing.ClockFormats.Select(f => new Choice<string>(f.Format, f.Label))];

    public Choice<string>? ClockFormat
    {
        get => ClockFormats.FirstOrDefault(f => f.Value == (Link?.ClockFormat ?? "")) ?? new Choice<string>(Link?.ClockFormat ?? "", Link?.ClockFormat ?? "");
        set
        {
            if (value is null || Link is not { } link) return;
            Write(() => link.ClockFormat = value.Value.Length == 0 ? null : value.Value);
        }
    }

    // MARK: Slides and video

    public bool IsSlide => Link?.Source is TextSourceKind.CurrentSlide or TextSourceKind.NextSlide or TextSourceKind.LastSlide && !IsNotes;

    /// <summary>Chords above the words, for the band.</summary>
    public bool ShowChords
    {
        get => o.TextStyle?.ShowChords == true;
        set => Write(() =>
        {
            o.TextStyle ??= new TextStyle();
            o.TextStyle.ShowChords = value ? true : null;
        });
    }

    /// <summary>How many lines of the slide to show (0: all of them).</summary>
    public string MaxLines
    {
        get => (Link?.MaxLines ?? 0).ToString(System.Globalization.CultureInfo.CurrentCulture);
        set
        {
            if (Link is not { } link || !int.TryParse(value, out var lines)) return;
            Write(() => link.MaxLines = lines <= 0 ? null : Math.Min(lines, 20));
        }
    }

    public bool IsVideo => Link?.Source == TextSourceKind.VideoCountdown;

    public IReadOnlyList<Choice<string?>> VideoLayers { get; } = [.. StageLayoutEditing.VideoLayers.Select(v => new Choice<string?>(v.Layer, v.Label))];

    public Choice<string?>? VideoLayer
    {
        get => VideoLayers.FirstOrDefault(v => v.Value == Link?.VideoLayer) ?? VideoLayers[0];
        set
        {
            if (value is null || Link is not { } link) return;
            Write(() => link.VideoLayer = value.Value);
        }
    }

    /// <summary>The video's name before its time left ("Announcements 2:14").</summary>
    public bool ShowsVideoName
    {
        get => Link?.ShowsVideoName != false;
        set
        {
            if (Link is not { } link) return;
            Write(() => link.ShowsVideoName = value ? null : false);
        }
    }

    /// <summary>What the box reads with the editor's sample data ("Reads 12:33").</summary>
    public string SampleLine => Link is { } l ? $"Reads: {LinkedText.SampleText(l).Replace('\n', ' ')}" : "";

    public void Refresh() => OnPropertyChanged(string.Empty);
}
