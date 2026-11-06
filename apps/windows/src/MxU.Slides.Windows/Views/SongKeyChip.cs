using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MxU.Slides.Core.Editing;
using MxU.Slides.Core.Lyrics;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The song's key, beside its title: "Key: G", or "Key: G → A" when it's shown transposed. Its menu
/// transposes (every screen's chords), sets the key when the song has chords but no key yet, sets a
/// capo (the stage display shows the shapes to play), and opens the chord editor. A port of the Mac's
/// SongKeyChip (PresentGridView.swift); the capo is Windows only. Hidden for songs without chords or a key.
/// </summary>
public sealed class SongKeyChip : Button
{
    private MainViewModel? model;

    public SongKeyChip()
    {
        Padding = new Thickness(10, 3, 10, 3);
        Style = EditorKit.Res<Style>("GhostButton");
        ToolTip = "The song's key: transpose, capo, and chords";
        Click += (_, _) => OpenMenu();
        DataContextChanged += (_, _) =>
        {
            if (model is not null) model.PropertyChanged -= OnModelChanged;
            model = DataContext as MainViewModel;
            if (model is not null) model.PropertyChanged += OnModelChanged;
            Update();
        };
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.CurrentPresentation) or nameof(MainViewModel.SongKeyText) or nameof(MainViewModel.Capo)
            or nameof(MainViewModel.CurrentSubtitle) or "") Update();
    }

    private void Update()
    {
        var text = model?.SongKeyText ?? "";
        Content = text;
        Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OpenMenu()
    {
        if (model?.CurrentPresentation is not { } song) return;
        var menu = new ContextMenu { PlacementTarget = this, Placement = PlacementMode.Bottom };
        if (song.MusicKey is { } key)
        {
            var displayed = song.DisplayKey ?? key;
            menu.Items.Add(new MenuItem { Header = "Show the chords in", IsEnabled = false });
            foreach (var name in ChordMath.KeyChoices(key))
            {
                var original = string.Equals(name, key, StringComparison.OrdinalIgnoreCase);
                var choice = new MenuItem { Header = original ? $"{name} (original)" : name, IsCheckable = true, IsChecked = string.Equals(name, displayed, StringComparison.OrdinalIgnoreCase) };
                choice.Click += (_, _) => { model.Transpose(name); Update(); };
                menu.Items.Add(choice);
            }
        }
        else
        {
            var set = new MenuItem { Header = "Set the key the chords are written in" };
            foreach (var name in ChordMath.KeyChoices("C").Concat(ChordMath.KeyChoices("Am")))
            {
                var choice = new MenuItem { Header = name };
                choice.Click += (_, _) => { model.SetMusicKey(name); Update(); };
                set.Items.Add(choice);
            }
            menu.Items.Add(set);
        }
        menu.Items.Add(new Separator());
        var capo = new MenuItem { Header = "Capo (the stage display shows the shapes)" };
        for (var fret = 0; fret <= SongCapo.MaxFret; fret++)
        {
            var f = fret;
            var choice = new MenuItem { Header = f == 0 ? "None" : $"Fret {f}", IsCheckable = true, IsChecked = model.Capo == f };
            choice.Click += (_, _) => { model.Capo = f; Update(); };
            capo.Items.Add(choice);
        }
        menu.Items.Add(capo);
        menu.Items.Add(new Separator());
        var edit = new MenuItem { Header = "Edit chords…" };
        edit.Click += (_, _) => ChordEditorWindow.Open(Window.GetWindow(this)!, model, model.SelectedSlide?.Slide.Id);
        menu.Items.Add(edit);
        menu.IsOpen = true;
    }
}
