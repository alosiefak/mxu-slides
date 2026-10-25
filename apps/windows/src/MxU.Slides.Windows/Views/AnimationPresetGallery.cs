using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Rendering;
using MxU.Slides.Windows.ViewModels;
using SceneTransition = MxU.Slides.Windows.Rendering.SceneTransition;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The overlay editor's presets gallery: three shelves (Windows' kinetic text, the Mac's
/// recommended set, and the presets you saved), each a grid of tiles. Point at a tile and it plays
/// its preset on a small sample lower third (Core/Animation/PresetSamples.cs), so you can see the
/// motion before you use it; click to pick it (the line about it and the Apply / Add buttons sit
/// under the gallery, in the window's XAML); double-click to apply it straight away.
/// <para>
/// Built in code, like the lower-thirds gallery. It finds its <see cref="OverlayEditorViewModel"/>
/// through the DataContext it inherits from the window.
/// </para>
/// </summary>
public sealed class AnimationPresetGallery : UserControl
{
    // Two tiles to a row in the editor's right-hand column (about 310 pixels inside its scroll bar).
    private const double TileWidth = 136;
    private const double TileHeight = TileWidth * 9 / 16;

    private readonly StackPanel shelves = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
    private readonly WrapPanel tiles = new();
    private readonly DispatcherTimer loop;
    private readonly Dictionary<PresetChoice, (Border Tile, SceneView View, Overlay Sample)> tileFor = [];

    // The tiles playing, and when each one's current pass began.
    private readonly Dictionary<SceneView, (Overlay Sample, double StartedAt)> playing = [];

    private OverlayEditorViewModel? vm;
    private string shelf = PresetChoice.Kinetic;

    public AnimationPresetGallery()
    {
        var scroller = new ScrollViewer
        {
            Content = tiles, MaxHeight = 252, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(6, 6, 0, 0),
        };
        var stack = new StackPanel();
        stack.Children.Add(shelves);
        stack.Children.Add(new Border { Child = scroller, Background = EditorKit.Res<Brush>("Card"), CornerRadius = new CornerRadius(6) });
        Content = stack;
        loop = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => Loop(), Dispatcher);
        loop.Stop();
        Loaded += (_, _) => loop.Start();
        Unloaded += (_, _) =>
        {
            loop.Stop();
            playing.Clear();
        };
        DataContextChanged += (_, e) => Attach(e.NewValue as OverlayEditorViewModel);
    }

    /// <summary>The shelf showing: <see cref="PresetChoice.Kinetic"/>, <see cref="PresetChoice.Recommended"/> or <see cref="PresetChoice.Mine"/>.</summary>
    public string Shelf
    {
        get => shelf;
        set
        {
            if (shelf == value) return;
            shelf = value;
            Rebuild();
        }
    }

    private void Attach(OverlayEditorViewModel? model)
    {
        if (vm is not null) vm.PropertyChanged -= OnModelChanged;
        vm = model;
        if (vm is not null) vm.PropertyChanged += OnModelChanged;
        Rebuild();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OverlayEditorViewModel.PresetChoices)) Rebuild();
        else if (e.PropertyName == nameof(OverlayEditorViewModel.SelectedPreset))
        {
            // A preset picked on another shelf (one just saved lands on Mine) turns to that shelf.
            if (vm?.SelectedPreset is { } selected && selected.Group != shelf) Shelf = selected.Group;
            else MarkSelected();
        }
    }

    // MARK: Shelves and tiles

    private void Rebuild()
    {
        playing.Clear();
        tiles.Children.Clear();
        tileFor.Clear();
        shelves.Children.Clear();
        if (vm is null) return;
        var choices = vm.PresetChoices;
        foreach (var (name, label) in new[] { (PresetChoice.Kinetic, "Kinetic text"), (PresetChoice.Recommended, "Recommended"), (PresetChoice.Mine, "Mine") })
        {
            var button = new ToggleButton
            {
                Content = $"{label}  {choices.Count(c => c.Group == name)}", IsChecked = shelf == name, Margin = new Thickness(0, 0, 4, 0),
                Padding = new Thickness(9, 3, 9, 3), FontSize = 11,
                ToolTip = name switch
                {
                    PresetChoice.Kinetic => "Words and lines arriving in turn: built from steps the Mac plays too",
                    PresetChoice.Recommended => "The Mac's recommended presets",
                    _ => "Presets you saved (shared with the Mac through the library)",
                },
            };
            button.Click += (_, _) => Shelf = name;
            shelves.Children.Add(button);
        }
        var shown = choices.Where(c => c.Group == shelf).ToList();
        foreach (var choice in shown) tiles.Children.Add(Tile(choice));
        if (shown.Count == 0)
            tiles.Children.Add(new TextBlock
            {
                Text = "Nothing saved yet. Give an object the steps you like, then Save as preset…: it appears here (and on a Mac sharing the library).",
                TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = EditorKit.Res<Brush>("MutedText"), Margin = new Thickness(0, 0, 6, 8), MaxWidth = 300,
            });
        MarkSelected();
    }

    private Border Tile(PresetChoice choice)
    {
        var sample = OverlayEditorViewModel.Sample(choice);
        var view = new SceneView { PlaysVideo = false, Width = TileWidth, Height = TileHeight };
        view.Present(Still(sample), SceneTransition.Cut);
        var stack = new StackPanel();
        stack.Children.Add(new Border { Child = view, CornerRadius = new CornerRadius(4), ClipToBounds = true, Background = Brushes.Black });
        stack.Children.Add(new TextBlock
        {
            Text = choice.Name, FontSize = 11, Foreground = EditorKit.Res<Brush>("Text"), TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(2, 4, 2, 1), Width = TileWidth - 4,
        });
        var tile = new Border
        {
            Child = stack, Padding = new Thickness(2), Margin = new Thickness(0, 0, 6, 6), CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(2), Background = Brushes.Transparent, Cursor = Cursors.Hand,
            ToolTip = choice.Description.Length > 0 ? choice.Description : choice.Name,
        };
        tile.MouseEnter += (_, _) => Play(view, sample);
        tile.MouseLeave += (_, _) =>
        {
            playing.Remove(view);
            view.Present(Still(sample), SceneTransition.Cut);
        };
        tile.MouseLeftButtonDown += (_, e) =>
        {
            if (vm is null) return;
            vm.SelectedPreset = choice;
            if (e.ClickCount == 2) vm.ApplyPresetCommand.Execute("replace");
            e.Handled = true;
        };
        tileFor[choice] = (tile, view, sample);
        return tile;
    }

    private void MarkSelected()
    {
        foreach (var (choice, (tile, _, _)) in tileFor)
        {
            var picked = ReferenceEquals(choice, vm?.SelectedPreset);
            tile.BorderBrush = picked ? EditorKit.Res<Brush>("Accent") : Brushes.Transparent;
            if (picked) tile.BringIntoView();
        }
    }

    /// <summary>Plays one preset's tile as pointing at it does (for the self-test's pictures).</summary>
    public void Play(PresetChoice choice)
    {
        if (tileFor.TryGetValue(choice, out var entry)) Play(entry.View, entry.Sample);
    }

    // MARK: Pictures

    /// <summary>A plain, softly lit backdrop, the same under every tile.</summary>
    private static readonly RenderScene Backdrop = MakeBackdrop();

    private static RenderScene MakeBackdrop()
    {
        var scene = new RenderScene(SceneSize.Hd, new SceneColor(0.07, 0.07, 0.08));
        scene.AddItem(new RenderItem
        {
            Id = "preset-backdrop", Frame = new SceneRect(SceneSize.Hd),
            Content = new ItemContent.Shape(new ShapeStyle(SceneShapeKind.Rect, new SceneFill.LinearGradient(90,
                [new SceneGradientStop(new SceneColor(0.20, 0.21, 0.25), 0), new SceneGradientStop(new SceneColor(0.08, 0.08, 0.10), 1)]))),
        }, LayerKind.LoopingVideos);
        return scene;
    }

    /// <summary>The sample fully up (where a tile rests).</summary>
    private static AnimatedScene Still(Overlay sample)
    {
        var timeline = AnimationSequence.PreviewTimeline(sample.Objects, sample.AnimationOrder);
        return OverlayPreview.PlayThrough(sample, SceneSize.Hd, 0, Math.Max(0, timeline.DismissAt - 0.05), Backdrop);
    }

    private void Play(SceneView view, Overlay sample)
    {
        var now = ShowState.HostClock();
        playing[view] = (sample, now);
        view.Present(OverlayPreview.PlayThrough(sample, SceneSize.Hd, now, null, Backdrop), SceneTransition.Cut);
    }

    /// <summary>A playing tile starts again a moment after its play-through ends.</summary>
    private void Loop()
    {
        var now = ShowState.HostClock();
        foreach (var (view, (sample, startedAt)) in playing.ToList())
            if (now - startedAt > Math.Max(0.5, OverlayPreview.Duration(sample)) + 0.6) Play(view, sample);
    }
}
