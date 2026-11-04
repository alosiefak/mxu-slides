using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The Auto Advance sheet: turn it on, count from the slide (or media) firing or from its video
/// ending, set the delay, loop back to the first slide, and read what will happen. For slides it can
/// reach the slides chosen, every slide of their groups, or every slide (the Mac's bulk menu, since
/// the Windows slide grid picks one slide at a time). A presentation's Slide Show is the same sheet
/// for the whole deck. Every change is saved straight away, as on the Mac.
/// <para>Port of the Mac's Sources/AutoAdvanceControls.swift (AutoAdvanceSheet, SecondsRow).</para>
/// </summary>
public sealed class AutoAdvanceWindow : Window
{
    private readonly Func<AutoAdvance?> read;
    private readonly Action<AutoAdvance?> write;
    private readonly bool mediaScope;
    private readonly bool showsCountFrom;
    private readonly StackPanel controls = new();
    private readonly CheckBox enabled = new() { Content = "Auto Advance", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };

    private AutoAdvanceWindow(Window? owner, string title, Func<AutoAdvance?> read, Action<AutoAdvance?> write, bool mediaScope, bool showsCountFrom,
        string? note, UIElement? extra)
    {
        this.read = read;
        this.write = write;
        this.mediaScope = mediaScope;
        this.showsCountFrom = showsCountFrom;
        EditorKit.Dress(this, owner, title, 440, double.NaN);
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;

        enabled.Checked += (_, _) => Change(AutoAdvanceEditing.Enabled(read(), true));
        enabled.Unchecked += (_, _) => Change(AutoAdvanceEditing.Enabled(read(), false));
        var done = EditorKit.Button("Done", Close, accent: true);
        done.IsDefault = true;
        done.MinWidth = 90;
        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        DockPanel.SetDock(done, Dock.Right);
        footer.Children.Add(done);
        footer.Children.Add(new TextBlock());

        var body = new StackPanel { Margin = new Thickness(18, 16, 18, 14) };
        body.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap });
        if (extra is not null) body.Children.Add(extra);
        body.Children.Add(enabled);
        body.Children.Add(controls);
        if (note is not null) body.Children.Add(EditorKit.Muted(note, 11, 0));
        body.Children.Add(footer);
        Content = body;
        Refresh();
    }

    private bool refreshing;

    private void Change(AutoAdvance? next)
    {
        if (refreshing) return;
        write(next);
        Refresh();
    }

    /// <summary>Draws the controls for the advance as it is now.</summary>
    private void Refresh()
    {
        refreshing = true;
        var current = read();
        enabled.IsChecked = current is not null;
        controls.Children.Clear();
        if (current is not null)
        {
            if (!mediaScope || showsCountFrom)
            {
                var from = new ComboBox { Width = 180, HorizontalAlignment = HorizontalAlignment.Left };
                from.Items.Add(mediaScope ? "This fire" : "This slide firing");
                from.Items.Add("Video end");
                from.SelectedIndex = current.AfterPlayback == true ? 1 : 0;
                from.SelectionChanged += (_, _) => Change(AutoAdvanceEditing.CountingFromVideoEnd(read(), from.SelectedIndex == 1));
                controls.Children.Add(EditorKit.Row("Count from", from, 96));
            }
            controls.Children.Add(EditorKit.Row("Delay", DelayRow(current), 96));
            if (!mediaScope)
            {
                var loop = new CheckBox { Content = "Loop to first slide", IsChecked = current.LoopToStart == true, Margin = new Thickness(96, 2, 0, 8) };
                loop.Click += (_, _) => Change(AutoAdvanceEditing.Looping(read(), loop.IsChecked == true));
                controls.Children.Add(loop);
            }
            controls.Children.Add(EditorKit.Muted(AutoAdvanceEditing.Caption(current, mediaScope), 11, 8));
        }
        refreshing = false;
    }

    private StackPanel DelayRow(AutoAdvance current)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        TextBox? box = null;
        box = EditorKit.CommitBox(Format(current.DelaySeconds), typed =>
        {
            if (!double.TryParse(typed.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var seconds)
                && !double.TryParse(typed.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds))
                return Format(read()?.DelaySeconds ?? 0);
            var next = AutoAdvanceEditing.WithDelay(read(), seconds);
            write(next);
            return Format(next?.DelaySeconds ?? 0);
        }, width: 64);
        box.TextAlignment = TextAlignment.Right;
        row.Children.Add(box);
        row.Children.Add(new TextBlock { Text = "seconds", Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = EditorKit.Res<Brush>("MutedText") });
        void Nudge(int by)
        {
            var next = AutoAdvanceEditing.WithDelay(read(), (read()?.DelaySeconds ?? 0) + by);
            write(next);
            box.Text = Format(next?.DelaySeconds ?? 0);
        }
        row.Children.Add(EditorKit.Ghost("−", "A second less", () => Nudge(-1), 13));
        row.Children.Add(EditorKit.Ghost("+", "A second more", () => Nudge(+1), 13));
        return row;
    }

    private static string Format(double seconds) => Math.Round(seconds, 1).ToString("0.#", CultureInfo.CurrentCulture);

    // MARK: Openers

    /// <summary>
    /// A slide's Auto Advance (or several slides'). The chosen slides start the sheet; "Apply to"
    /// widens it to their groups or the whole presentation. Saved through the main view model.
    /// </summary>
    public static AutoAdvanceWindow? ForSlides(Window? owner, MainViewModel model, Presentation presentation, IReadOnlyCollection<string> slideIds)
    {
        var chosen = presentation.Slides.Where(s => slideIds.Contains(s.Id)).Select(s => s.Id).ToList();
        if (chosen.Count == 0) return null;
        var reach = AutoAdvanceEditing.Reach.Chosen;
        List<string> Targets() => AutoAdvanceEditing.Reached(presentation, chosen, reach);
        var name = chosen.Count == 1 ? SlideName(presentation, chosen[0]) : $"{chosen.Count} Slides";

        var applyTo = new ComboBox { MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Left };
        var groups = AutoAdvanceEditing.Reached(presentation, chosen, AutoAdvanceEditing.Reach.Groups).Count;
        applyTo.Items.Add(chosen.Count == 1 ? "This slide" : $"These {chosen.Count} slides");
        applyTo.Items.Add(chosen.Count == 1 ? $"Its group ({groups} slide{(groups == 1 ? "" : "s")})" : $"Their groups ({groups} slides)");
        applyTo.Items.Add($"Every slide ({presentation.Slides.Count})");
        applyTo.SelectedIndex = 0;
        // The sheet shows the first of the slides' advances that is on (mixed slides then all take what's set).
        AutoAdvance? Read() => AutoAdvanceEditing.OnSlides(presentation, Targets()).FirstOrDefault(a => a is not null);
        var window = new AutoAdvanceWindow(owner, $"Auto Advance: {name}", Read,
            advance => model.SaveSlidesAutoAdvance(presentation, Targets(), advance),
            mediaScope: false, showsCountFrom: true, note: null, extra: EditorKit.Row("Apply to", applyTo, 96));
        applyTo.SelectionChanged += (_, _) =>
        {
            reach = (AutoAdvanceEditing.Reach)applyTo.SelectedIndex;
            // Widening applies what the sheet shows to the new slides at once (that's why it was widened).
            if (window.read() is { } shown) model.SaveSlidesAutoAdvance(presentation, Targets(), shown);
            window.Refresh();
        };
        window.Reach = () => reach;
        window.ApplyTo = applyTo;
        return window;
    }

    /// <summary>Which slides the sheet reaches now (the self-test sets the picker and reads this).</summary>
    internal Func<AutoAdvanceEditing.Reach>? Reach { get; private set; }

    /// <summary>The slides sheet's "Apply to" picker (the self-test sets it).</summary>
    internal ComboBox? ApplyTo { get; private set; }

    /// <summary>A presentation's Slide Show: every slide moves on by itself (slides with their own Auto Advance keep theirs).</summary>
    public static AutoAdvanceWindow ForPresentation(Window? owner, MainViewModel model, Presentation presentation) =>
        new(owner, string.IsNullOrEmpty(presentation.Name) ? "Slide Show" : $"Slide Show: {presentation.Name}",
            () => presentation.AutoAdvance, advance => model.SavePresentationSlideShow(presentation, advance),
            mediaScope: false, showsCountFrom: true, note: AutoAdvanceEditing.SlideShowNote, extra: null);

    /// <summary>A media item's Auto Advance: when it's fired from a service, the next item follows.</summary>
    public static AutoAdvanceWindow? ForMedia(Window? owner, MainViewModel model, MediaItem item)
    {
        if (model.Library.Get<MediaItem>(item.Id) is not { } stored) return null;
        return new AutoAdvanceWindow(owner, $"Auto Advance: {stored.Name}", () => stored.AutoAdvance, advance => model.SaveMediaAutoAdvance(stored, advance),
            mediaScope: true, showsCountFrom: stored.MediaKind == MediaKind.Video, note: null, extra: null);
    }

    private static string SlideName(Presentation presentation, string slideId)
    {
        var index = presentation.Slides.FindIndex(s => s.Id == slideId);
        var slide = presentation.Slides[index];
        return slide.Name.Length > 0 ? slide.Name : $"Slide {index + 1}";
    }

    /// <summary>For the self-test: the sheet's own controls, to drive them like a person would.</summary>
    internal CheckBox EnabledBox => enabled;

    internal StackPanel Controls => controls;
}
