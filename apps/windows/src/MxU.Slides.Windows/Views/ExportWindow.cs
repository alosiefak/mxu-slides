using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using MxU.Slides.Core.Export;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// Export…: what to make of one presentation or several. The Mac puts these choices under its save
/// panel (ExportOptionsAccessory: Format, Columns, "Include background and foreground media");
/// Windows' save dialog can't hold them, so they come first, here, then the dialog asks where.
/// The last choices are remembered on this PC.
/// </summary>
internal sealed class ExportWindow : Window
{
    private readonly MainViewModel model;
    private readonly IReadOnlyList<Presentation> presentations;
    private readonly StackPanel formats = new();
    private readonly StackPanel pdfOptions = new() { Margin = new Thickness(24, 2, 0, 6) };
    private readonly StackPanel imageOptions = new() { Margin = new Thickness(24, 2, 0, 6), Orientation = Orientation.Horizontal };
    private readonly CheckBox includeMedia = new() { Content = "Include backgrounds and other media", Margin = new Thickness(0, 8, 0, 0) };
    private readonly ComboBox columns = new() { Width = 60, Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBlock note = EditorKit.Muted("", 11, 0);
    private PresentationExport.Format format;
    private PresentationExport.PdfKind pdfKind;
    private PresentationExport.ImageType imageType;

    private ExportWindow(Window owner, MainViewModel model, IReadOnlyList<Presentation> presentations)
    {
        this.model = model;
        this.presentations = presentations;
        EditorKit.Dress(this, owner, "Export", 460, double.NaN);
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        var settings = model.Settings;
        var available = Available();
        format = available.Contains(settings.ExportFormat) ? settings.ExportFormat : available[0];
        pdfKind = settings.ExportPdfKind;
        imageType = settings.ExportImageType;
        includeMedia.IsChecked = settings.ExportIncludesMedia;
        includeMedia.ToolTip = "Show the backgrounds, still pictures and videos each slide plays with, as a still frame. Off: only the slide's own words and shapes.";
        for (var n = PresentationExport.Sheet.MinColumns; n <= PresentationExport.Sheet.MaxColumns; n++) columns.Items.Add(n);
        columns.SelectedItem = Math.Clamp(settings.ExportColumns, PresentationExport.Sheet.MinColumns, PresentationExport.Sheet.MaxColumns);

        var body = new StackPanel { Margin = new Thickness(20, 16, 20, 0) };
        body.Children.Add(new TextBlock
        {
            Text = presentations.Count == 1 ? $"Export “{presentations[0].Name}”" : $"Export {presentations.Count} presentations",
            FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10), TextTrimming = TextTrimming.CharacterEllipsis,
        });
        foreach (var choice in available)
        {
            var radio = new RadioButton { Content = choice.Label(), GroupName = "format", IsChecked = choice == format, Margin = new Thickness(0, 2, 0, 2) };
            radio.Checked += (_, _) =>
            {
                format = choice;
                ShowChoices();
            };
            formats.Children.Add(radio);
            if (choice == PresentationExport.Format.Pdf) formats.Children.Add(pdfOptions);
            if (choice == PresentationExport.Format.Images) formats.Children.Add(imageOptions);
        }
        body.Children.Add(formats);

        foreach (var kind in PdfKindsAvailable())
        {
            var radio = new RadioButton { Content = kind.Label(), GroupName = "pdf", IsChecked = kind == pdfKind, Margin = new Thickness(0, 1, 0, 1) };
            radio.Checked += (_, _) =>
            {
                pdfKind = kind;
                ShowChoices();
            };
            pdfOptions.Children.Add(radio);
            if (kind == PresentationExport.PdfKind.SlideSheet)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24, 2, 0, 4) };
                row.Children.Add(new TextBlock { Text = "Slides across", VerticalAlignment = VerticalAlignment.Center });
                row.Children.Add(columns);
                pdfOptions.Children.Add(row);
            }
        }
        if (!PdfKindsAvailable().Contains(pdfKind)) pdfKind = PresentationExport.PdfKind.SlideSheet;
        foreach (var type in new[] { PresentationExport.ImageType.Png, PresentationExport.ImageType.Jpeg })
        {
            var radio = new RadioButton { Content = type == PresentationExport.ImageType.Png ? "PNG" : "JPEG", GroupName = "image", IsChecked = type == imageType, Margin = new Thickness(0, 1, 16, 1) };
            radio.Checked += (_, _) => imageType = type;
            imageOptions.Children.Add(radio);
        }
        body.Children.Add(includeMedia);
        body.Children.Add(note);

        var export = EditorKit.Button(presentations.Count == 1 ? "Export…" : "Choose a Folder…", () => _ = ExportAsync(), accent: true);
        export.IsDefault = true;
        var cancel = EditorKit.Button("Cancel", Close);
        cancel.IsCancel = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(20, 16, 20, 18) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(export);
        var root = new StackPanel();
        root.Children.Add(body);
        root.Children.Add(buttons);
        Content = root;
        ShowChoices();
    }

    public static void Open(Window owner, MainViewModel model, IReadOnlyList<Presentation> presentations)
    {
        var window = new ExportWindow(owner, model, presentations);
        Opened?.Invoke(window, EventArgs.Empty);
        window.ShowDialog();
    }

    /// <summary>Raised with each export window just before it shows (the self-test pictures it).</summary>
    internal static event EventHandler? Opened;

    /// <summary>One presentation: its own formats (the Mac's). Several: every format any of them has (text only for the songs).</summary>
    private IReadOnlyList<PresentationExport.Format> Available() =>
        [.. PresentationExport.AllFormats.Where(f => presentations.Any(p => PresentationExport.Formats(p).Contains(f)))];

    private IReadOnlyList<PresentationExport.PdfKind> PdfKindsAvailable() =>
        [.. Enum.GetValues<PresentationExport.PdfKind>().Where(k => presentations.Any(p => PresentationExport.PdfKinds(p).Contains(k)))];

    private void ShowChoices()
    {
        pdfOptions.Visibility = format == PresentationExport.Format.Pdf ? Visibility.Visible : Visibility.Collapsed;
        imageOptions.Visibility = format == PresentationExport.Format.Images ? Visibility.Visible : Visibility.Collapsed;
        var drawsSlides = format == PresentationExport.Format.Images || (format == PresentationExport.Format.Pdf && pdfKind is PresentationExport.PdfKind.SlideSheet or PresentationExport.PdfKind.SlidePages);
        includeMedia.Visibility = drawsSlides ? Visibility.Visible : Visibility.Collapsed;
        columns.IsEnabled = pdfKind == PresentationExport.PdfKind.SlideSheet;
        note.Text = format switch
        {
            PresentationExport.Format.SlidesFile => "One file with the slides, their themes, media and fonts: open it on a Mac or another PC.",
            PresentationExport.Format.Images => "One picture per slide, in a folder named after the presentation.",
            PresentationExport.Format.PlainText => "The words, section by section, without chords: for a bulletin or an email.",
            PresentationExport.Format.ChordPro => "Words and chords in ChordPro, which chord-chart apps open.",
            _ when pdfKind == PresentationExport.PdfKind.LyricSheet => "The words on paper, with the copyright and CCLI lines.",
            _ when pdfKind == PresentationExport.PdfKind.ChordChart => "The words with each chord above its letter, in the key the song is shown in.",
            _ => "Print it from the PDF reader, or send it to the team.",
        };
        if (presentations.Count > 1 && format is PresentationExport.Format.PlainText or PresentationExport.Format.ChordPro)
            note.Text += " Presentations that aren't songs are left out.";
        note.Margin = new Thickness(0, 8, 0, 0);
    }

    private ExportChoice Choice() => new(format, pdfKind, columns.SelectedItem is int n ? n : 3, imageType, includeMedia.IsChecked == true);

    private void Remember(ExportChoice choice)
    {
        var settings = model.Settings;
        settings.ExportFormat = choice.Format;
        settings.ExportPdfKind = choice.PdfKind;
        settings.ExportColumns = choice.Columns;
        settings.ExportImageType = choice.ImageType;
        settings.ExportIncludesMedia = choice.IncludeMedia;
        settings.Save();
    }

    private async Task ExportAsync()
    {
        var choice = Choice();
        string? destination;
        if (presentations.Count == 1 && format != PresentationExport.Format.Images)
        {
            var presentation = presentations[0];
            var extension = format.FileExtension()!;
            var dialog = new SaveFileDialog
            {
                Title = $"Export “{presentation.Name}”",
                FileName = PresentationExport.FileName(presentation, format),
                DefaultExt = "." + extension,
                Filter = $"{format.Label()}|*.{extension}",
                AddExtension = true,
            };
            destination = dialog.ShowDialog(this) == true ? dialog.FileName : null;
        }
        else
        {
            var dialog = new OpenFolderDialog { Title = presentations.Count == 1 ? "Choose where the slide pictures go" : "Choose a folder for the exports" };
            destination = dialog.ShowDialog(this) == true ? dialog.FolderName : null;
        }
        if (destination is null) return;
        Remember(choice);
        Close();
        try
        {
            if (presentations.Count > 1)
            {
                await model.ExportPresentationsAsync(presentations, choice, destination);
                return;
            }
            // Pictures go in a folder of their own, named after the presentation, inside the one chosen.
            if (format == PresentationExport.Format.Images)
            {
                var taken = Directory.Exists(destination) ? Directory.EnumerateFileSystemEntries(destination).Select(Path.GetFileName).OfType<string>() : [];
                destination = Path.Combine(destination, LibraryBatch.UniqueFileNames([PresentationExport.BaseName(presentations[0])], "", taken)[0]);
            }
            model.Notice = await model.ExportPresentationAsync(presentations[0], choice, destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            model.Problem = $"The export didn't finish: {ex.Message}";
        }
    }
}
