using System.Globalization;
using MxU.Slides.Core.Editing;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Rendering;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// The inspector's text tools (Phase 7): styling part of the words, and the Mac's text-fit
/// options. Select words in the inspector's text box, then Bold, Italic, Underline, a colour or a
/// size applies to just those words, as a run (Core/Editing/TextStyleRuns.cs), the way the Mac's
/// canvas text editor does (SlideEditorModel.applyStyleToSelection, toggleSelectionBold). With
/// nothing selected, the same buttons style the whole box.
/// </summary>
public sealed partial class ObjectInspectorViewModel
{
    // MARK: Styling the selected words

    /// <summary>The style under the start of a selection (UTF-16 offsets, as the text box reports them).</summary>
    public TextStyleRun? RunAt(int utf16Start, int utf16Length)
    {
        var range = TextStyleRuns.CharacterRange(utf16Start, utf16Length, o.Text);
        return TextStyleRuns.EffectiveRun(range.Start, o.StyleRuns ?? [], o.Text);
    }

    /// <summary>Changes the style of the selected words. An empty selection styles every word of the box.</summary>
    public void StyleSelection(int utf16Start, int utf16Length, Action<TextStyleRun> mutate)
    {
        var range = utf16Length > 0
            ? TextStyleRuns.CharacterRange(utf16Start, utf16Length, o.Text)
            : new TextStyleRuns.Range(0, TextStyleRuns.CharacterRange(0, o.Text.Length, o.Text).End);
        if (range.IsEmpty) return;
        Set(() =>
        {
            var runs = TextStyleRuns.Applying(mutate, o.StyleRuns ?? [], o.Text, range);
            o.StyleRuns = runs.Count > 0 ? runs : null;
        });
    }

    /// <summary>The font the selected words are drawn in: their run's, else the box's own (theme included).</summary>
    private string FontAt(int utf16Start, int utf16Length) => RunAt(utf16Start, utf16Length)?.FontName ?? Effective.FontName;

    public bool IsBoldAt(int utf16Start, int utf16Length) => FontMap.IsBold(FontAt(utf16Start, utf16Length));

    public bool IsItalicAt(int utf16Start, int utf16Length) => FontMap.IsItalic(FontAt(utf16Start, utf16Length));

    /// <summary>Bold on or off for the selected words: the same family's bold face, as the Mac's font manager converts it.</summary>
    public void ToggleBold(int utf16Start, int utf16Length)
    {
        var font = FontAt(utf16Start, utf16Length);
        var face = FontMap.WithTraits(font, !FontMap.IsBold(font), FontMap.IsItalic(font));
        StyleSelection(utf16Start, utf16Length, r => r.FontName = face);
    }

    public void ToggleItalic(int utf16Start, int utf16Length)
    {
        var font = FontAt(utf16Start, utf16Length);
        var face = FontMap.WithTraits(font, FontMap.IsBold(font), !FontMap.IsItalic(font));
        StyleSelection(utf16Start, utf16Length, r => r.FontName = face);
    }

    public void ToggleUnderline(int utf16Start, int utf16Length)
    {
        var on = RunAt(utf16Start, utf16Length)?.Underline == true;
        StyleSelection(utf16Start, utf16Length, r => r.Underline = on ? null : true);
    }

    public void ToggleStrikethrough(int utf16Start, int utf16Length)
    {
        var on = RunAt(utf16Start, utf16Length)?.Strikethrough == true;
        StyleSelection(utf16Start, utf16Length, r => r.Strikethrough = on ? null : true);
    }

    /// <summary>
    /// All Caps on or off for the selected words (MxU's update: "All Caps applies to the selected text,
    /// not only the whole box"). What's "on" reads the placeholder's case too, as the Mac's Selection
    /// strip does: turning it off in a box whose theme writes capitals stores "none", not nothing.
    /// </summary>
    public void ToggleCaps(int utf16Start, int utf16Length)
    {
        var baseCaps = Effective.Transform == SceneTextTransform.Uppercase;
        var on = IsCapsAt(utf16Start, utf16Length);
        StyleSelection(utf16Start, utf16Length, r => r.TextTransform = !on ? TextTransform.Uppercase : baseCaps ? TextTransform.None : null);
    }

    public bool IsCapsAt(int utf16Start, int utf16Length) =>
        (RunAt(utf16Start, utf16Length)?.TextTransform ?? (Effective.Transform == SceneTextTransform.Uppercase ? TextTransform.Uppercase : TextTransform.None))
            == TextTransform.Uppercase;

    /// <summary>Takes the selected words' own styling away (they take the box's again).</summary>
    public void ClearSelectionStyle(int utf16Start, int utf16Length) => StyleSelection(utf16Start, utf16Length, r =>
    {
        r.Underline = r.Strikethrough = null;
        r.FontName = r.ColorHex = r.HighlightColorHex = null;
        r.FontSize = r.Tracking = null;
        r.TextTransform = null;
    });

    /// <summary>The box has styled words (to show a "has styling" hint and the Clear button).</summary>
    public bool HasStyledWords => o.StyleRuns is { Count: > 0 };

    // MARK: Text fit (the Mac's typography toggles)

    /// <summary>Spreads a wrapped line's words evenly over its rows: never one lone word on the last row.</summary>
    public bool BalancedWrap
    {
        get => Effective.BalancedWrap;
        set => Set(() => Own.BalancedWrap = value ? true : null);
    }

    /// <summary>Text that still doesn't fit after shrinking turns pages on each click instead of clipping.</summary>
    public bool PageOnClick
    {
        get => o.TextStyle?.PageOnClick ?? Inherited?.PageOnClick ?? false;
        set => Set(() => Own.PageOnClick = value ? true : null);
    }

    /// <summary>Shrink further so no line breaks mid-phrase (only while shrinking to fit).</summary>
    public bool KeepLinesWhole
    {
        get => Effective.KeepLinesWhole;
        set => Set(() => Own.KeepLinesWhole = value ? true : null);
    }

    /// <summary>The smallest size shrinking to fit may go to.</summary>
    public string MinFontSize
    {
        get => Math.Round(Effective.MinFontSize, 1).ToString(CultureInfo.CurrentCulture);
        set
        {
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out var size) || size < 1) return;
            Set(() => Own.MinFontSize = Math.Min(500, size));
        }
    }
}
