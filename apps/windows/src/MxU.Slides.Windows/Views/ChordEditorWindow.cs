using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MxU.Slides.Core.Editing;
using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// Chords over the words of a song's slides, as the Mac's slide editor places them on its canvas
/// (ChordEditing.swift): each line of the lyric with its chords above it.
/// <list type="bullet">
/// <item>Double-click over a letter to put a chord there, and type it.</item>
/// <item>Click a chord to pick it; drag it along or onto another line; Delete removes it; Enter or a
/// double-click retypes it (typing nothing removes it).</item>
/// <item>Or type the words with chords in brackets, <c>[G]Amazing [C]grace</c>, in the box below.</item>
/// <item>The song's key, the key it's shown in (Transpose) and a capo (the stage display shows the
/// shapes to play) are along the top.</item>
/// </list>
/// The chords shown are the ones stored, not transposed, as the Mac's editor shows them. Every change
/// is saved at once and undoes in the main window.
/// </summary>
public sealed class ChordEditorWindow : Window
{
    private static ChordEditorWindow? open;

    private readonly MainViewModel model;
    private readonly ChordLinesView lines;
    private readonly TextBlock where = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBox bracketed = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 70, FontFamily = new FontFamily("Consolas"), FontSize = 14 };
    private readonly ComboBox key = new() { Width = 70, Margin = new Thickness(4, 0, 12, 0), ToolTip = "The key the chords are written in" };
    private readonly ComboBox shown = new() { Width = 90, Margin = new Thickness(4, 0, 12, 0), ToolTip = "Transpose: the key the chords show in, on every screen" };
    private readonly ComboBox capo = new() { Width = 70, Margin = new Thickness(4, 0, 0, 0), ToolTip = "Capo: the stage display shows the shapes to play" };
    private int slideIndex;
    private bool loading;

    private ChordEditorWindow(Window owner, MainViewModel model)
    {
        this.model = model;
        EditorKit.Dress(this, owner, "Chords", 760, 560);
        lines = new ChordLinesView(this);

        var top = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        top.Children.Add(EditorKit.Muted("Key", 12, 0));
        top.Children.Add(key);
        top.Children.Add(EditorKit.Muted("Show in", 12, 0));
        top.Children.Add(shown);
        top.Children.Add(EditorKit.Muted("Capo", 12, 0));
        top.Children.Add(capo);
        key.SelectionChanged += (_, _) =>
        {
            if (loading) return;
            model.SetMusicKey(key.SelectedItem as string is { Length: > 0 } k && k != "None" ? k : null);
            LoadKeys();
        };
        shown.SelectionChanged += (_, _) =>
        {
            if (!loading && shown.SelectedItem is string k) model.Transpose(k.Replace(" (original)", ""));
        };
        capo.SelectionChanged += (_, _) =>
        {
            if (!loading && capo.SelectedIndex >= 0) model.Capo = capo.SelectedIndex;
        };

        var nav = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var previous = EditorKit.Button("◀ Slide", () => Show(slideIndex - 1), "The slide before");
        var next = EditorKit.Button("Slide ▶", () => Show(slideIndex + 1), "The slide after");
        previous.Margin = new Thickness(0, 0, 6, 0);
        DockPanel.SetDock(previous, Dock.Left);
        DockPanel.SetDock(next, Dock.Left);
        nav.Children.Add(previous);
        nav.Children.Add(next);
        nav.Children.Add(where);

        var hint = EditorKit.Muted("Double-click over a letter to add a chord. Drag a chord to move it; Delete removes it; Enter retypes it.", 11, 6);
        var apply = EditorKit.Button("Use these words and chords", () =>
        {
            if (CurrentSlide is { } slide) model.SetBracketedChords(slide.Id, bracketed.Text);
            Reload();
        }, "Takes the text below: words, with chords in [brackets]");
        apply.Margin = new Thickness(0, 6, 0, 0);
        apply.HorizontalAlignment = HorizontalAlignment.Left;

        var root = new DockPanel { Margin = new Thickness(14) };
        foreach (var (element, dock) in new (UIElement, Dock)[] { (top, Dock.Top), (nav, Dock.Top), (hint, Dock.Top), (apply, Dock.Bottom), (bracketed, Dock.Bottom) })
        {
            DockPanel.SetDock(element, dock);
            root.Children.Add(element);
        }
        root.Children.Add(new Border
        {
            Background = EditorKit.Res<Brush>("Raised"), CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 0, 8),
            Child = new ScrollViewer { Content = lines, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto },
        });
        Content = root;
        Closed += (_, _) => open = null;
    }

    /// <summary>Opens the chord editor on a slide of the open presentation (or brings it forward there).</summary>
    public static ChordEditorWindow Open(Window owner, MainViewModel model, string? slideId)
    {
        open ??= new ChordEditorWindow(owner, model);
        var slides = model.CurrentPresentation?.Slides ?? [];
        open.slideIndex = Math.Max(0, slides.FindIndex(s => s.Id == slideId));
        open.Reload();
        open.Show();
        open.Activate();
        return open;
    }

    internal Slide? CurrentSlide => model.CurrentPresentation?.Slides.ElementAtOrDefault(slideIndex);

    internal MainViewModel Model => model;

    internal ChordLinesView Lines => lines;

    private void Show(int index)
    {
        var count = model.CurrentPresentation?.Slides.Count ?? 0;
        if (count == 0) return;
        slideIndex = Math.Clamp(index, 0, count - 1);
        Reload();
    }

    /// <summary>Draws the slide's lines and chords again, and fills the boxes.</summary>
    internal void Reload()
    {
        var slide = CurrentSlide;
        var count = model.CurrentPresentation?.Slides.Count ?? 0;
        where.Text = slide is null ? "No slides" : $"Slide {slideIndex + 1} of {count}  ·  {model.CurrentPresentation?.Name}";
        bracketed.Text = slide is null ? "" : MainViewModel.BracketedChords(slide);
        lines.Load(slide is null ? null : MainViewModel.ChordTarget(slide));
        LoadKeys();
    }

    private void LoadKeys()
    {
        loading = true;
        try
        {
            var p = model.CurrentPresentation;
            key.ItemsSource = new[] { "None" }.Concat(ChordMath.KeyChoices("C")).Concat(ChordMath.KeyChoices("Am")).ToList();
            key.SelectedItem = p?.MusicKey ?? "None";
            if (p?.MusicKey is { } music)
            {
                var choices = ChordMath.KeyChoices(music).Select(k => string.Equals(k, music, StringComparison.OrdinalIgnoreCase) ? k + " (original)" : k).ToList();
                shown.ItemsSource = choices;
                shown.SelectedItem = choices.FirstOrDefault(c => c.StartsWith(p.DisplayKey ?? music, StringComparison.OrdinalIgnoreCase)
                    && (c.Length == (p.DisplayKey ?? music).Length || c.EndsWith("(original)", StringComparison.Ordinal)));
                shown.IsEnabled = true;
            }
            else
            {
                shown.ItemsSource = new[] { "Set the key first" };
                shown.SelectedIndex = 0;
                shown.IsEnabled = false;
            }
            capo.ItemsSource = Enumerable.Range(0, SongCapo.MaxFret + 1).Select(f => f == 0 ? "None" : f.ToString(CultureInfo.CurrentCulture)).ToList();
            capo.SelectedIndex = model.Capo;
        }
        finally
        {
            loading = false;
        }
    }

    /// <summary>A change from the lines view: save it and keep the chord picked.</summary>
    internal int? Apply(Func<SlideObject, ChordEditing.Edit> edit)
    {
        if (CurrentSlide is not { } slide) return null;
        var result = model.EditChords(slide.Id, edit);
        bracketed.Text = MainViewModel.BracketedChords(slide);
        return result?.Index;
    }
}

/// <summary>
/// The lyric's lines, each with its chords above, in an even-width font so a chord's column is just
/// its x divided by a letter's width. Draws, and turns clicks, drags and keys into chord edits.
/// </summary>
internal sealed class ChordLinesView : FrameworkElement
{
    private const double Left = 16;
    private const double Top = 12;
    private const double ChordRow = 22;
    private const double LyricRow = 26;
    private static readonly Typeface Mono = new(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface MonoBold = new(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    private readonly ChordEditorWindow editor;
    private readonly TextBox typing = new() { Width = 80, FontFamily = new FontFamily("Consolas"), FontSize = 15, Visibility = Visibility.Collapsed };
    private readonly Canvas host = new();
    private SlideObject? box;
    private int? picked;
    private int? dragging;
    private (int Line, int Column)? typingAt;
    private int? retyping;
    private double letter = 9;

    public ChordLinesView(ChordEditorWindow editor)
    {
        this.editor = editor;
        Focusable = true;
        FocusVisualStyle = null;
        host.Children.Add(typing);
        AddVisualChild(host);
        typing.KeyDown += OnTypingKey;
        typing.LostKeyboardFocus += (_, _) => Commit();
    }

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => host;

    public void Load(SlideObject? lyric)
    {
        box = lyric;
        picked = null;
        typing.Visibility = Visibility.Collapsed;
        InvalidateMeasure();
        InvalidateVisual();
    }

    private string[] LinesOf => (box?.Text ?? "").Split('\n');

    private List<ChordPlacement> Chords => box?.Chords ?? [];

    protected override Size MeasureOverride(Size availableSize)
    {
        letter = new FormattedText("M", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Mono, 17, Brushes.White, 1).WidthIncludingTrailingWhitespace;
        var widest = LinesOf.Select(l => new StringInfo(l).LengthInTextElements).DefaultIfEmpty(0).Max();
        host.Measure(availableSize);
        return new Size(Left * 2 + (widest + 8) * letter, Top * 2 + LinesOf.Length * (ChordRow + LyricRow) + 10);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        host.Arrange(new Rect(finalSize));
        return finalSize;
    }

    private double LineTop(int line) => Top + line * (ChordRow + LyricRow);

    private (int Line, int Column) PositionAt(Point p)
    {
        var line = (int)Math.Floor((p.Y - Top) / (ChordRow + LyricRow));
        var column = (int)Math.Round((p.X - Left) / letter);
        return ChordEditing.Clamped(line, column, box?.Text ?? "");
    }

    private int? ChordAt(Point p)
    {
        for (var i = 0; i < Chords.Count; i++)
        {
            var c = Chords[i];
            var x = Left + c.Column * letter;
            var y = LineTop(c.Line);
            if (new Rect(x - 3, y, Math.Max(letter * 2, c.Symbol.Length * letter * 0.9) + 6, ChordRow).Contains(p)) return i;
        }
        return null;
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (box is null)
        {
            dc.DrawText(Text("This slide has no lyric text box.", Mono, 15, EditorKit.Res<Brush>("MutedText")), new Point(Left, Top));
            return;
        }
        var lines = LinesOf;
        var muted = EditorKit.Res<Brush>("MutedText");
        var accent = EditorKit.Res<Brush>("Accent");
        for (var i = 0; i < lines.Length; i++)
        {
            var top = LineTop(i);
            // A faint ruler under the chord row: where chords go.
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)), null, new Rect(Left - 4, top + 1, Math.Max(40, ActualWidth - Left * 2), ChordRow - 2));
            dc.DrawText(Text(lines[i].Length == 0 ? " " : lines[i], Mono, 17, EditorKit.Res<Brush>("Text")), new Point(Left, top + ChordRow));
        }
        for (var i = 0; i < Chords.Count; i++)
        {
            var c = Chords[i];
            var at = new Point(Left + c.Column * letter, LineTop(c.Line) + 2);
            var label = Text(c.Symbol, MonoBold, 15, i == picked ? Brushes.White : accent);
            if (i == picked) dc.DrawRoundedRectangle(accent, null, new Rect(at.X - 3, at.Y - 1, label.Width + 6, label.Height + 2), 3, 3);
            dc.DrawText(label, at);
            // A tick down to the letter it sits on.
            dc.DrawLine(new Pen(muted, 1), new Point(at.X + 1, LineTop(c.Line) + ChordRow - 2), new Point(at.X + 1, LineTop(c.Line) + ChordRow + 3));
        }
    }

    private static FormattedText Text(string text, Typeface face, double size, Brush brush) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, brush, 1);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (box is null) return;
        Focus();
        var p = e.GetPosition(this);
        var hit = ChordAt(p);
        if (e.ClickCount == 2)
        {
            if (hit is { } chord) BeginTyping(Chords[chord].Line, Chords[chord].Column, chord);
            else
            {
                var (line, column) = PositionAt(p);
                BeginTyping(line, column, null);
            }
            e.Handled = true;
            return;
        }
        picked = hit;
        dragging = hit;
        if (hit is not null) CaptureMouse();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = ChordAt(e.GetPosition(this)) is not null || dragging is not null ? Cursors.SizeAll : Cursors.IBeam;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        ReleaseMouseCapture();
        if (dragging is not { } index || box is null) return;
        dragging = null;
        var (line, column) = PositionAt(e.GetPosition(this));
        if (Chords[index].Line == line && Chords[index].Column == column) return;
        picked = editor.Apply(b => ChordEditing.Moving(index, line, column, b.Chords ?? [], b.Text));
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (picked is not { } index || box is null) return;
        switch (e.Key)
        {
            case Key.Delete or Key.Back:
                editor.Apply(b => new ChordEditing.Edit(ChordEditing.Removing(index, b.Chords ?? []), null));
                picked = null;
                break;
            case Key.Enter or Key.F2:
                BeginTyping(Chords[index].Line, Chords[index].Column, index);
                break;
            case Key.Left or Key.Right:
                var step = e.Key == Key.Left ? -1 : 1;
                picked = editor.Apply(b => ChordEditing.Moving(index, (b.Chords ?? [])[index].Line, (b.Chords ?? [])[index].Column + step, b.Chords ?? [], b.Text));
                break;
            default:
                return;
        }
        InvalidateVisual();
        e.Handled = true;
    }

    // MARK: Typing a chord

    private void BeginTyping(int line, int column, int? existing)
    {
        typingAt = (line, column);
        retyping = existing;
        typing.Text = existing is { } i ? Chords[i].Symbol : "";
        Canvas.SetLeft(typing, Left + column * letter - 3);
        Canvas.SetTop(typing, LineTop(line) - 2);
        typing.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() =>
        {
            typing.Focus();
            typing.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnTypingKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Commit();
        else if (e.Key == Key.Escape) Cancel();
        else return;
        e.Handled = true;
    }

    private void Cancel()
    {
        typingAt = null;
        retyping = null;
        typing.Visibility = Visibility.Collapsed;
        Focus();
    }

    private void Commit()
    {
        if (typingAt is not { } at || box is null) return;
        var symbol = typing.Text;
        var existing = retyping;
        Cancel();
        picked = existing is { } index
            ? editor.Apply(b => ChordEditing.Setting(symbol, index, b.Chords ?? []))
            : editor.Apply(b => ChordEditing.Adding(symbol, at.Line, at.Column, b.Chords ?? [], b.Text));
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>For the self-test: adds a chord as a double-click and typing would.</summary>
    internal void AddChordForTest(int line, int column, string symbol)
    {
        BeginTyping(line, column, null);
        typing.Text = symbol;
        Commit();
    }
}
