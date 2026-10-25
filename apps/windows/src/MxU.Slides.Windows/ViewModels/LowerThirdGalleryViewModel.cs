using CommunityToolkit.Mvvm.ComponentModel;
using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Core.Stage;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// The lower-thirds gallery window's state: which designs show (by category and look), the one
/// picked, a draft of it whose fields you fill in, and adding it to the library and putting it up.
/// <para>
/// Until it's added, the picked design is a draft that belongs to the window: typing into its
/// fields changes only the preview. "Add to library" saves a copy as an ordinary overlay (in the
/// pack you choose, filed under its category), and from then on the fields edit that overlay, the
/// same one the Overlays bin and the program show, so a typo fixed while it's up is fixed on screen.
/// </para>
/// The designs themselves are in Core (LowerThirdGallery); this only arranges them.
/// </summary>
public sealed partial class LowerThirdGalleryViewModel : ObservableObject
{
    public const string AllCategories = "All";
    public const string AllStyles = "All looks";
    public const string AnyTimer = "Whichever timer is running";

    private readonly MainViewModel main;

    public LowerThirdGalleryViewModel(MainViewModel main)
    {
        this.main = main;
        Pack = LowerThirdGallery.DefaultPack;
        Select(LowerThirdGallery.All[0]);
    }

    public MainViewModel Main => main;

    // MARK: Browsing

    public IReadOnlyList<string> Categories { get; } = [AllCategories, .. LowerThirdGallery.Categories];

    public IReadOnlyList<string> Styles { get; } = [AllStyles, "Clean", "Broadcast", "Glass", "Editorial"];

    [ObservableProperty]
    public partial string Category { get; set; } = AllCategories;

    [ObservableProperty]
    public partial string Style { get; set; } = AllStyles;

    partial void OnCategoryChanged(string value) => OnPropertyChanged(nameof(Visible));

    partial void OnStyleChanged(string value) => OnPropertyChanged(nameof(Visible));

    /// <summary>The designs the filters let through, in the gallery's order.</summary>
    public IReadOnlyList<LowerThirdTemplate> Visible =>
        [.. LowerThirdGallery.All.Where(t => (Category == AllCategories || t.Category == Category) && (Style == AllStyles || t.StyleName == Style))];

    // MARK: The design picked

    [ObservableProperty]
    public partial LowerThirdTemplate? Selected { get; private set; }

    /// <summary>What the fields edit: the draft, or once added, the overlay in the library.</summary>
    public Overlay? Working => Added ?? draft;

    private Overlay? draft;

    /// <summary>The library copy made from the picked design, once "Add to library" has been pressed.</summary>
    [ObservableProperty]
    public partial Overlay? Added { get; private set; }

    public IReadOnlyList<OverlayFieldRow> Fields { get; private set; } = [];

    /// <summary>A line about what happened last (added, up, filled in), for the window's status.</summary>
    [ObservableProperty]
    public partial string? Notice { get; set; }

    /// <summary>The picture needs drawing again (a field changed, another design was picked).</summary>
    public event Action? PreviewChanged;

    public void Select(LowerThirdTemplate template)
    {
        Selected = template;
        draft = template.Make();
        Added = null;
        Notice = null;
        RefreshFields();
        OnPropertyChanged(nameof(Working));
        OnPropertyChanged(nameof(IsCountdown));
        OnPropertyChanged(nameof(IsCredits));
        OnPropertyChanged(nameof(IsLive));
        OnPropertyChanged(nameof(TimerChoice));
        PreviewChanged?.Invoke();
    }

    private void RefreshFields()
    {
        if (Working is not { } overlay)
        {
            Fields = [];
        }
        else
        {
            var mattes = SlideSceneBuilder.MaskWiring(overlay.Objects).Mattes;
            Fields = [.. overlay.Objects.Where(o => o.ObjectKind == SlideObjectKind.Text && o.TextLink is null && !mattes.Contains(o.Id))
                .Select(o => new OverlayFieldRow(o, FieldChanged))];
        }
        OnPropertyChanged(nameof(Fields));
    }

    private void FieldChanged()
    {
        if (Working is not { } overlay) return;
        if (Added is { } added) main.OverlayChanged(added); // saves it, and updates the program if it's up (Reflow included)
        else KineticText.Reflow(overlay);
        PreviewChanged?.Invoke();
    }

    /// <summary>The value of one field (by its name), for the self-test and the window.</summary>
    public void SetField(string name, string text)
    {
        if (Fields.FirstOrDefault(f => f.Label == name) is { } field) field.Text = text;
    }

    // MARK: Song credits

    public bool IsCredits => Selected?.Category == "Song Credits";

    /// <summary>The song on the screen (or open), whose CCLI details the credits can take.</summary>
    private Presentation? Song => main.Show.Live?.Presentation is { PresentationKind: PresentationKind.Song } live ? live
        : main.CurrentPresentation is { PresentationKind: PresentationKind.Song } open ? open : null;

    public bool FillFromSong()
    {
        if (Working is not { } overlay || Song is not { } song)
        {
            Notice = "Open or put up a song first: its title, writers and CCLI number fill in.";
            return false;
        }
        var changed = LowerThirdGallery.FillCredits(overlay, song);
        RefreshFields();
        if (changed) FieldChanged();
        Notice = changed ? $"Filled in from “{song.Name}”." : $"“{song.Name}” has nothing new to fill in.";
        return changed;
    }

    // MARK: Countdowns

    public bool IsCountdown => Working?.Objects.Any(o => o.TextLink is { Source: TextSourceKind.Timer }) == true;

    /// <summary>The timers the digits can follow: whichever is running, or one by name.</summary>
    public IReadOnlyList<string> TimerChoices =>
        [AnyTimer, .. main.Timers.Timers.Where(t => !TimerController.IsPermanent(t.Id)).Select(t => t.Name)];

    public string TimerChoice
    {
        get
        {
            var id = Working?.Objects.FirstOrDefault(o => o.TextLink is { Source: TextSourceKind.Timer })?.TextLink?.TimerId;
            return id is null ? AnyTimer : main.Timers.Snapshot(id)?.Name ?? AnyTimer;
        }
        set
        {
            if (Working is not { } overlay) return;
            var timer = main.Timers.Timers.FirstOrDefault(t => !TimerController.IsPermanent(t.Id) && t.Name == value);
            LowerThirdGallery.UseTimer(overlay, timer?.Id);
            OnPropertyChanged();
            FieldChanged();
        }
    }

    /// <summary>Makes a countdown timer of so many minutes, starts it, and points the digits at it.</summary>
    public void StartCountdown(int minutes)
    {
        if (Working is not { } overlay) return;
        var id = main.Timers.AddCountdown($"Countdown {minutes}:00", minutes * 60);
        main.Timers.Start(id);
        LowerThirdGallery.UseTimer(overlay, id);
        OnPropertyChanged(nameof(TimerChoices));
        OnPropertyChanged(nameof(TimerChoice));
        FieldChanged();
        Notice = $"A {minutes}-minute countdown is running (Timers panel), and the digits follow it.";
    }

    // MARK: Into the library, and on screen

    /// <summary>The pack the design goes into (an existing one, or a new name).</summary>
    [ObservableProperty]
    public partial string Pack { get; set; }

    partial void OnPackChanged(string value) => OnPropertyChanged(nameof(Packs));

    /// <summary>The packs to choose from: the library's, the gallery's own, and a new one just named.</summary>
    public IReadOnlyList<string> Packs =>
        [.. OverlayLibrary.Packs(main.Library.All<Overlay>()).Where(p => p != OverlayLibrary.NoPack).Append(LowerThirdGallery.DefaultPack).Append(Pack)
            .Distinct().Order(StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>Chooses a new pack by name. False (and nothing changes) when the name can't be a pack's.</summary>
    public bool NewPack(string name)
    {
        if (OverlayLibrary.ValidPackName(name) is not { } valid)
        {
            Notice = $"A pack needs a name without \"/\", and can't be called \"{OverlayLibrary.NoPack}\".";
            return false;
        }
        Pack = valid;
        return true;
    }

    /// <summary>
    /// Saves the design (as filled in) to the library: a new overlay in the pack, under its
    /// category, with the template's name. Pressing it again adds another copy.
    /// </summary>
    public Overlay? AddToLibrary()
    {
        if (Selected is not { } template || Working is not { } overlay) return null;
        var copy = MxuJson.Clone(overlay);
        var instance = LowerThirdGallery.Instantiate(template, Pack);
        copy.Id = instance.Id;
        copy.Folder = instance.Folder;
        main.SaveOverlayNow(copy);
        main.SelectedOverlayPack = OverlayLibrary.PackOf(copy);
        main.RefreshOverlays();
        Added = main.Library.Get<Overlay>(copy.Id) ?? copy;
        RefreshFields();
        OnPropertyChanged(nameof(Working));
        OnPropertyChanged(nameof(Packs));
        Notice = $"Added to {copy.Folder?.Replace("/", " › ")}. It's in the Overlays bin; the fields here now edit it.";
        return Added;
    }

    public bool IsLive => Added is { } added && main.IsOverlayLive(added.Id);

    /// <summary>Puts the design up on the program (adding it to the library first, if it isn't yet).</summary>
    public void GoLive()
    {
        if (Added is null && AddToLibrary() is null) return;
        main.SetOverlayLive(Added!, up: true);
        OnPropertyChanged(nameof(IsLive));
        Notice = $"“{Added!.Name}” is up. Take it down here, in the Overlays bin, or with F4.";
    }

    public void TakeDown()
    {
        if (Added is not { } added) return;
        main.SetOverlayLive(added, up: false);
        OnPropertyChanged(nameof(IsLive));
        Notice = $"“{added.Name}” is coming down.";
    }

    /// <summary>Re-reads whether the added overlay is up (it may be taken down elsewhere).</summary>
    public void RefreshLive() => OnPropertyChanged(nameof(IsLive));

    // MARK: Pictures

    /// <summary>
    /// What the previews draw behind a design: 0 a stand-in picture (a camera shot of the stage
    /// for lower thirds, a worship background for full-screen designs), 1 the live program.
    /// </summary>
    [ObservableProperty]
    public partial int Backdrop { get; set; }

    public static readonly string[] Backdrops = ["Over a stand-in picture", "Over the live program"];

    partial void OnBackdropChanged(int value) => PreviewChanged?.Invoke();

    /// <summary>
    /// True when the window paints a stand-in picture (Views/StandInPictures.cs) behind the previews.
    /// Their scenes are then see-through, so the picture shows where the design doesn't cover it.
    /// </summary>
    public bool StandInBehind => Backdrop == 0;

    /// <summary>A design that fills the screen (a title over the walk-in loop) rather than sitting in a third: it has a full-screen layer.</summary>
    public static bool FillsTheScreen(Overlay overlay) => overlay.Objects.Any(o => o.Width >= 1900 && o.Height >= 1000);

    public RenderScene BackdropScene(Overlay overlay) =>
        StandInBehind ? new RenderScene(SceneSize.Hd, SceneColor.Clear) : OverlayPreview.Without(main.Show.Scene(), overlay);

    /// <summary>The design playing through (going up, a hold, coming down) from <paramref name="startedAt"/>; with <paramref name="time"/>, frozen there.</summary>
    public AnimatedScene Picture(Overlay overlay, double startedAt, double? time = null) =>
        OverlayPreview.PlayThrough(main.WithLiveText(overlay), SceneSize.Hd, startedAt, time, BackdropScene(overlay));

    /// <summary>A design at rest, fully up (for the gallery's tiles).</summary>
    public AnimatedScene Still(Overlay overlay)
    {
        var timeline = AnimationSequence.PreviewTimeline(overlay.Objects, overlay.AnimationOrder);
        return Picture(overlay, 0, Math.Max(0, timeline.DismissAt - 0.05));
    }

    /// <summary>How long one play-through lasts, in seconds.</summary>
    public static double Duration(Overlay overlay) => Math.Max(0.5, OverlayPreview.Duration(overlay));
}
