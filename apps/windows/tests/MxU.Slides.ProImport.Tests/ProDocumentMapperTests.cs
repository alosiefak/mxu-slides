using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;
using MxU.Slides.ProImport.Rtf;
using static MxU.Slides.ProImport.Tests.Proto;
using G = Pro.SerializationInterop.RVProtoData.Graphics.Types;
using RV = Pro.SerializationInterop.RVProtoData;
using SlideElement = Pro.SerializationInterop.RVProtoData.Slide.Types.Element;

namespace MxU.Slides.ProImport.Tests;

public class ProDocumentMapperTests
{
    private static SlideElement TextElement(string id, string rtfText, string fontName = "DrukWide-Medium", double size = 70) => Wrap(new G.Element
    {
        Uuid = Uuid(id),
        Name = "Lyric Line",
        Bounds = new() { Origin = new() { X = 88, Y = 944 }, Size = new() { Width = 1744, Height = 135 } },
        Rotation = 360,
        Opacity = 1,
        Text = new()
        {
            RtfData = RtfBytes("{\\rtf1\\ansi{\\fonttbl\\f0\\fnil\\fcharset0 Helvetica;}\\f0\\fs140 " + rtfText + "}"),
            VerticalAlignment = G.Text.Types.VerticalAlignment.Middle,
            Attributes = new()
            {
                Font = new() { Name = fontName, Size = size },
                TextSolidFill = Color(1, 1, 1),
                Kerning = 2,
                ParagraphStyle = new() { Alignment = G.Text.Types.Attributes.Types.Alignment.Center },
            },
        },
    });

    private static RV.Cue SlideCue(string id, IEnumerable<SlideElement> elements, string name = "", double width = 1920, double height = 1080)
    {
        var slide = new RV.Slide { Size = new() { Width = width, Height = height } };
        slide.Elements.Add(elements);
        return new RV.Cue
        {
            Uuid = Uuid(id),
            Name = name,
            Actions = { new RV.Action { Uuid = Uuid(Guid.NewGuid().ToString()), Slide = new() { Presentation = new() { BaseSlide = slide } } } },
        };
    }

    private static RV.Presentation.Types.CueGroup Group(string id, string name, string[] cueIds, RV.Color? color)
    {
        var cueGroup = new RV.Presentation.Types.CueGroup { Group = new() { Uuid = Uuid(id), Name = name } };
        if (color is not null) cueGroup.Group.Color = color;
        cueGroup.CueIdentifiers.Add(cueIds.Select(Uuid));
        return cueGroup;
    }

    private static RV.Presentation SongDocument() => new()
    {
        Uuid = Uuid("DOC-1"),
        Name = "Test Song",
        Cues =
        {
            SlideCue("CUE-V1", [TextElement("EL-1", "Line one")]),
            SlideCue("CUE-V2", [TextElement("EL-2", "Line two")]),
            SlideCue("CUE-C1", [TextElement("EL-3", "Chorus line")]),
        },
        CueGroups =
        {
            Group("GRP-V", "Verse 1", ["CUE-V1", "CUE-V2"], Color(0, 0.47f, 0.8f)),
            Group("GRP-C", "Chorus", ["CUE-C1"], Color(0.8f, 0, 0.31f)),
        },
        Arrangements = { new RV.Presentation.Types.Arrangement { Uuid = Uuid("ARR-1"), Name = "Sunday", GroupIdentifiers = { Uuid("GRP-V"), Uuid("GRP-C"), Uuid("GRP-V") } } },
        SelectedArrangement = Uuid("ARR-1"),
        Ccli = new() { SongNumber = 7106807, Author = "Test Author", Publisher = "Test Publisher", CopyrightYear = 2018 },
    };

    private static RV.Presentation SingleSlideDocument(params SlideElement[] elements)
    {
        var doc = SongDocument();
        doc.Cues.Clear();
        doc.Cues.Add(SlideCue("CUE-1", elements));
        doc.CueGroups.Clear();
        doc.Arrangements.Clear();
        return doc;
    }

    private static SlideObject FirstObject(RV.Presentation doc) => ProDocumentMapper.Map(doc, "fallback").Presentation.Slides[0].Objects[0];

    [Fact]
    public void NonWideDocumentKeepsItsCanvas()
    {
        var doc = SongDocument();
        doc.Cues.Clear();
        doc.Cues.Add(SlideCue("CUE-V1", [TextElement("EL-1", "Line one")], width: 1024, height: 768));
        doc.Cues.Add(SlideCue("CUE-V2", [TextElement("EL-2", "Line two")], width: 1024, height: 768));
        doc.Cues.Add(SlideCue("CUE-C1", [TextElement("EL-3", "Chorus")]));
        var mapped = ProDocumentMapper.Map(doc, "Song");
        Assert.Equal(1024, mapped.Presentation.CanvasWidth);
        Assert.Equal(768, mapped.Presentation.CanvasHeight);
        Assert.Equal(1744, mapped.Presentation.Slides[0].Objects[0].Width);
        Assert.Contains(mapped.Warnings, w => w.Contains("1 slide(s) are 1920×1080"));
    }

    [Fact]
    public void GroupHotKeysImportAsNormalizedLetters()
    {
        var doc = SongDocument();
        doc.CueGroups[0].Group.HotKey = new() { Code = RV.KeyCode.AnsiA };
        doc.CueGroups[1].Group.HotKey = new() { Code = RV.KeyCode.F1 };
        Assert.Equal(new Dictionary<string, string> { ["verse1"] = "a" }, ProDocumentMapper.Map(doc, "Song").GroupHotKeys);
        Assert.Empty(ProDocumentMapper.Map(SongDocument(), "Song").GroupHotKeys);
    }

    [Fact]
    public void UnnamedGroupsImportNoSectionUnlessArranged()
    {
        var doc = SongDocument();
        doc.CueGroups.Clear();
        doc.CueGroups.Add(Group("GRP-NONE", "", ["CUE-V1", "CUE-V2", "CUE-C1"], null));
        doc.Arrangements.Clear();
        var mapped = ProDocumentMapper.Map(doc, "fallback").Presentation;
        Assert.Null(mapped.Sections);
        Assert.All(mapped.Slides, s => Assert.Null(s.SectionId));
        Assert.Equal(3, mapped.Slides.Count);
        Assert.Equal("Line one", mapped.Slides[0].Objects[0].Text);

        var arranged = SongDocument();
        arranged.CueGroups.Clear();
        arranged.CueGroups.Add(Group("GRP-NONE", "", ["CUE-V1", "CUE-V2", "CUE-C1"], null));
        arranged.Arrangements.Clear();
        arranged.Arrangements.Add(new RV.Presentation.Types.Arrangement { Uuid = Uuid("ARR-2"), Name = "Sunday", GroupIdentifiers = { Uuid("GRP-NONE") } });
        Assert.Single(ProDocumentMapper.Map(arranged, "fallback").Presentation.Sections!);
    }

    private static G.Text.Types.Attributes.Types.CustomAttribute ChordAttribute(string symbol, int start, int end) =>
        new() { Range = new() { Start = start, End = end }, Chord = symbol };

    [Fact]
    public void ChordPlacementsMapRangesToLineAndColumn()
    {
        var graphics = new G.Element
        {
            Text = new()
            {
                Attributes = new()
                {
                    CustomAttributes =
                    {
                        ChordAttribute("G", 0, 5), ChordAttribute("C/E", 5, 9), ChordAttribute("Am7", 9, 14), ChordAttribute("D", 99, 99),
                    },
                },
            },
        };
        var placements = ProDocumentMapper.ChordPlacements(graphics, "Line one\nLine two");
        Assert.Equal(["G", "C/E", "Am7", "D"], placements.Select(p => p.Symbol));
        Assert.Equal([0, 0, 1, 1], placements.Select(p => p.Line));
        Assert.Equal([0, 5, 0, 8], placements.Select(p => p.Column));
    }

    [Fact]
    public void ChordsAndKeysMapThroughTheDocument()
    {
        var doc = SongDocument();
        var element = TextElement("EL-1", "Line one");
        element.Element_.Text.Attributes.CustomAttributes.Add([ChordAttribute("G", 0, 5), ChordAttribute("C", 5, 8)]);
        element.Element_.Text.ChordPro = new() { Enabled = true, Notation = G.Text.Types.ChordPro.Types.Notation.Numbers, Color = Color(1, 0.72f, 0.15f) };
        doc.Cues[0] = SlideCue("CUE-V1", [element]);
        doc.Music = new() { Original = new() { MusicKey = RV.MusicKeyScale.Types.MusicKey.G }, User = new() { MusicKey = RV.MusicKeyScale.Types.MusicKey.A } };

        var mapped = ProDocumentMapper.Map(doc, "X");
        Assert.Equal("G", mapped.Presentation.MusicKey);
        Assert.Equal("A", mapped.Presentation.DisplayKey);
        var lyric = mapped.Presentation.Slides[0].Objects[0];
        Assert.Equal(["G", "C"], lyric.Chords!.Select(c => c.Symbol));
        Assert.Equal([0, 5], lyric.Chords!.Select(c => c.Column));
        Assert.True(lyric.TextStyle?.ShowChords);
        Assert.Equal(ChordNotation.Numbers, lyric.TextStyle?.ChordNotation);
        Assert.NotNull(lyric.TextStyle?.ChordColorHex);
    }

    [Fact]
    public void ChordDataSurvivesWithDisplayOffAndKeysDedupe()
    {
        var doc = SongDocument();
        var element = TextElement("EL-1", "Line one");
        element.Element_.Text.Attributes.CustomAttributes.Add(ChordAttribute("Em", 0, 8));
        doc.Cues[0] = SlideCue("CUE-V1", [element]);
        doc.Music = new() { Original = new() { MusicKey = RV.MusicKeyScale.Types.MusicKey.DFlat }, User = new() { MusicKey = RV.MusicKeyScale.Types.MusicKey.DFlat } };

        var mapped = ProDocumentMapper.Map(doc, "X");
        Assert.Equal("Db", mapped.Presentation.MusicKey);
        Assert.Null(mapped.Presentation.DisplayKey);
        var lyric = mapped.Presentation.Slides[0].Objects[0];
        Assert.Equal(["Em"], lyric.Chords!.Select(c => c.Symbol));
        Assert.Null(lyric.TextStyle?.ShowChords);
    }

    [Fact]
    public void LegacyStringKeysBackFill()
    {
        var doc = SongDocument();
        doc.Music = new() { OriginalMusicKey = "F#m" };
        Assert.Equal("F#m", ProDocumentMapper.Map(doc, "X").Presentation.MusicKey);

        var junk = SongDocument();
        junk.MusicKey = "unknown";
        Assert.Null(ProDocumentMapper.Map(junk, "X").Presentation.MusicKey);
    }

    [Fact]
    public void GoToNextTimerMapsToAutoAdvance()
    {
        var doc = SongDocument();
        doc.Cues[0].CompletionTargetType = RV.Cue.Types.CompletionTargetType.Next;
        doc.Cues[0].CompletionActionType = RV.Cue.Types.CompletionActionType.AfterTime;
        doc.Cues[0].CompletionTime = 7;
        doc.Cues[1].CompletionTargetType = RV.Cue.Types.CompletionTargetType.Next;
        doc.Cues[1].CompletionActionType = RV.Cue.Types.CompletionActionType.AfterAction;
        doc.Cues[2].CompletionTargetType = RV.Cue.Types.CompletionTargetType.First;
        doc.Cues[2].CompletionActionType = RV.Cue.Types.CompletionActionType.AfterTime;
        doc.Cues[2].CompletionTime = 3;
        var slides = ProDocumentMapper.Map(doc, "fallback").Presentation.Slides;
        Assert.Equal(Json(new AutoAdvance { DelaySeconds = 7 }), Json(slides[0].AutoAdvance));
        Assert.Equal(Json(new AutoAdvance { DelaySeconds = 0, AfterPlayback = true }), Json(slides[1].AutoAdvance));
        Assert.Equal(Json(new AutoAdvance { DelaySeconds = 3, LoopToStart = true }), Json(slides[2].AutoAdvance));
    }

    [Fact]
    public void DocumentSlideShowMapsToPresentationAutoAdvance()
    {
        var doc = SongDocument();
        doc.SlideShowDuration = 5;
        var presentation = ProDocumentMapper.Map(doc, "fallback").Presentation;
        Assert.Equal(Json(new AutoAdvance { DelaySeconds = 5, LoopToStart = true }), Json(presentation.AutoAdvance));
        Assert.All(presentation.Slides, s => Assert.Null(s.AutoAdvance));
    }

    [Fact]
    public void TimedGoToNextOnVideoCueAnchorsAtVideoEnd()
    {
        var action = new RV.Action
        {
            Media = new()
            {
                Element = new() { Uuid = Uuid("MEDIA-BUMPER"), Url = new() { AbsoluteString = "file:///Users/test/bumper.mov" }, Video = new() },
                LayerType = RV.Action.Types.LayerType.Foreground,
            },
        };
        var doc = SongDocument();
        doc.Cues[0].Actions.Add(action);
        doc.Cues[0].CompletionTargetType = RV.Cue.Types.CompletionTargetType.Next;
        doc.Cues[0].CompletionActionType = RV.Cue.Types.CompletionActionType.AfterTime;
        doc.Cues[0].CompletionTime = 0;
        doc.Cues[1].CompletionTargetType = RV.Cue.Types.CompletionTargetType.Next;
        doc.Cues[1].CompletionActionType = RV.Cue.Types.CompletionActionType.AfterTime;
        doc.Cues[1].CompletionTime = 5;

        var slides = ProDocumentMapper.Map(doc, "fallback").Presentation.Slides;
        Assert.Equal(Json(new AutoAdvance { DelaySeconds = 0, AfterPlayback = true }), Json(slides[0].AutoAdvance));
        Assert.Equal(Json(new AutoAdvance { DelaySeconds = 5 }), Json(slides[1].AutoAdvance));
    }

    [Fact]
    public void CueActionsBecomeSlideActions()
    {
        var doc = SongDocument();
        var macroAction = new RV.Action { Macro = new() { Identification = new() { ParameterUuid = Uuid("MACRO-1") } } };
        var lookAction = new RV.Action { AudienceLook = new() { Identification = new() { ParameterUuid = Uuid("LOOK-1") } } };
        var timerAction = new RV.Action
        {
            Timer = new() { ActionType = RV.Action.Types.TimerType.Types.TimerAction.ActionStart, TimerIdentification = new() { ParameterUuid = Uuid("TIMER-1") } },
        };
        var transition = new RV.Action { Transition = new() };
        doc.Cues[0].Actions.Add([macroAction, lookAction, transition]);
        doc.Cues[1].Actions.Add(timerAction);

        var slides = ProDocumentMapper.Map(doc, "fallback", new Dictionary<string, string> { ["timer-1"] = "mxu-timer-9" }).Presentation.Slides;
        var first = slides[0].Actions!;
        Assert.Equal([SlideActionKind.FireCombo, SlideActionKind.SwitchOutputPreset], first.Select(a => a.Kind));
        Assert.Equal("macro-1", first[0].ComboId);
        Assert.Equal("look-1", first[1].PresetId);
        Assert.Contains("transition", slides[0].Notes);

        var second = slides[1].Actions!;
        Assert.Equal([SlideActionKind.TimerStart], second.Select(a => a.Kind));
        Assert.Equal("mxu-timer-9", second[0].TimerId);
        Assert.Null(slides[2].Actions);
    }

    [Fact]
    public void MidiCueActionsBecomeMIDIOutSlideActions()
    {
        var doc = SongDocument();
        var noteOn = new RV.Action.Types.CommunicationType
        {
            MidiCommand = new() { State = RV.Action.Types.CommunicationType.Types.MIDICommand.Types.State.On, Channel = 1, Note = 60, Intensity = 100 },
        };
        var noteOff = noteOn.Clone();
        noteOff.MidiCommand.State = RV.Action.Types.CommunicationType.Types.MIDICommand.Types.State.Off;
        var sony = new RV.Action.Types.CommunicationType { SonyBVSCommand = new() };
        doc.Cues[0].Actions.Add([new RV.Action { Communication = noteOn }, new RV.Action { Communication = sony }]);
        doc.Cues[1].Actions.Add(new RV.Action { Communication = noteOff });

        var slides = ProDocumentMapper.Map(doc, "fallback").Presentation.Slides;
        var first = slides[0].Actions!;
        Assert.Equal([SlideActionKind.MidiOut], first.Select(a => a.Kind));
        Assert.Equal(MIDIMessageKind.NoteOn, first[0].MidiKind);
        Assert.Equal(2, first[0].MidiChannel);
        Assert.Equal(60, first[0].MidiNumber);
        Assert.Equal(100, first[0].MidiValue);
        Assert.Contains("device control", slides[0].Notes);
        Assert.Equal(0, slides[1].Actions![0].MidiValue);
    }

    [Fact]
    public void UnmappableGoToNextShapesFlattenToNotes()
    {
        var doc = SongDocument();
        doc.Cues[0].CompletionTargetType = RV.Cue.Types.CompletionTargetType.Random;
        doc.Cues[0].CompletionActionType = RV.Cue.Types.CompletionActionType.AfterTime;
        doc.Cues[0].CompletionTime = 5;
        var slides = ProDocumentMapper.Map(doc, "fallback").Presentation.Slides;
        Assert.Null(slides[0].AutoAdvance);
        Assert.Contains("go to next timer", slides[0].Notes);
    }

    [Fact]
    public void MapsGroupsArrangementsAndCCLI()
    {
        var mapped = ProDocumentMapper.Map(SongDocument(), "fallback");
        var presentation = mapped.Presentation;
        Assert.Equal("Test Song", presentation.Name);
        Assert.Equal("doc-1", presentation.Id);
        Assert.Equal(3, presentation.Slides.Count);
        Assert.Null(presentation.CanvasWidth);

        var sections = presentation.Sections!;
        Assert.Equal(["Verse 1", "Chorus"], sections.Select(s => s.Name));
        Assert.Equal("#0078CCFF", sections[0].ColorHex);
        Assert.Equal(["grp-v", "grp-v", "grp-c"], presentation.Slides.Select(s => s.SectionId));

        var arrangement = presentation.Arrangements![0];
        Assert.Equal("Sunday", arrangement.Name);
        Assert.Equal(["grp-v", "grp-c", "grp-v"], arrangement.SectionIds);
        Assert.Equal(arrangement.Id, presentation.DefaultArrangementId);

        Assert.Equal(7106807, presentation.Ccli?.SongNumber);
        Assert.Equal("Test Author", presentation.Ccli?.Author);
        Assert.Equal(2018, presentation.Ccli?.CopyrightYear);
        Assert.Empty(mapped.Warnings);
    }

    [Fact]
    public void GroupWithoutColorMapsToNilColorHex()
    {
        var doc = SongDocument();
        var verse = doc.CueGroups[0];
        doc.CueGroups.Clear();
        doc.CueGroups.Add([verse, Group("GRP-B", "Bridge", ["CUE-C1"], null)]);
        var sections = ProDocumentMapper.Map(doc, "fallback").Presentation.Sections!;
        Assert.Equal(["#0078CCFF", null], sections.Select(s => s.ColorHex));
    }

    [Fact]
    public void MapsTextStyleFromProtobufAttributesNotRTF()
    {
        var obj = ProDocumentMapper.Map(SongDocument(), "fallback").Presentation.Slides[0].Objects[0];
        Assert.Equal(SlideObjectKind.Text, obj.ObjectKind);
        Assert.Equal("Line one", obj.Text);
        Assert.Equal(88, obj.X);
        Assert.Equal(944, obj.Y);
        Assert.Equal(1744, obj.Width);
        Assert.Null(obj.RotationDegrees);

        var style = obj.TextStyle!;
        Assert.Equal("DrukWide-Medium", style.FontName);
        Assert.Equal(70, style.FontSize);
        Assert.Equal("#FFFFFFFF", style.ColorHex);
        Assert.Equal(2, style.Tracking);
        Assert.Equal(TextHorizontalAlignment.Center, style.HorizontalAlignment);
        Assert.Equal(TextVerticalAlignment.Middle, style.VerticalAlignment);
    }

    [Fact]
    public void LinesOnlyFillImportsAsTextObjectWithLineFill()
    {
        var element = TextElement("EL-LF", "All hail");
        element.Element_.Fill = new() { Enable = true, Color = Color(0, 0, 0) };
        element.Element_.TextLineMask = new()
        {
            Enabled = true, MaskStyle = G.Text.Types.LineFillMask.Types.LineMaskStyle.LineWidth,
            WidthOffset = 8, VerticalOffset = -4, HorizontalOffset = -1,
        };
        var obj = FirstObject(SingleSlideDocument(element));
        Assert.Equal(SlideObjectKind.Text, obj.ObjectKind);
        var lineFill = obj.TextStyle!.LineFill!;
        Assert.Equal(FillKind.Solid, lineFill.Fill.FillKind);
        Assert.Equal("#000000FF", lineFill.Fill.ColorHex);
        Assert.Equal(TextLineFillWidthMode.LineWidth, lineFill.WidthMode);
        Assert.Null(lineFill.VerticalPadding);
        Assert.Equal(8, lineFill.HorizontalPadding);
        Assert.Equal(-4, lineFill.VerticalOffset);
        Assert.Equal(-1, lineFill.HorizontalOffset);
    }

    [Fact]
    public void LinesOnlyFillWithEmptyTextDropsElement()
    {
        var element = TextElement("EL-LF-EMPTY", " ");
        element.Element_.Fill = new() { Enable = true, Color = Color(0, 0, 0) };
        element.Element_.TextLineMask = new() { Enabled = true, MaskStyle = G.Text.Types.LineFillMask.Types.LineMaskStyle.LineWidth };
        Assert.Empty(ProDocumentMapper.Map(SingleSlideDocument(element), "fallback").Presentation.Slides[0].Objects);
    }

    [Fact]
    public void LinesOnlyFillWithEmptyTextKeepsEnabledStroke()
    {
        var element = TextElement("EL-LF-STROKE", "");
        element.Element_.Fill = new() { Enable = true, Color = Color(0, 0, 0) };
        element.Element_.Stroke = new() { Enable = true, Color = Color(1, 0, 0), Width = 3 };
        element.Element_.TextLineMask = new() { Enabled = true, MaskStyle = G.Text.Types.LineFillMask.Types.LineMaskStyle.LineWidth };
        var obj = FirstObject(SingleSlideDocument(element));
        Assert.Null(obj.Fill);
        Assert.Equal(3, obj.Stroke?.Width);
    }

    [Fact]
    public void DataLinkedElementSurvivesEmptyTextAndNoInk()
    {
        var element = TextElement("EL-VC", "");
        element.Element_.Fill = new() { Enable = false, Color = Color(0.13f, 0.59f, 0.95f) };
        element.DataLinks.Add(new SlideElement.Types.DataLink { VideoCountdown = new() });
        var obj = FirstObject(SingleSlideDocument(element));
        Assert.Equal(SlideObjectKind.Text, obj.ObjectKind);
        Assert.Equal(TextSourceKind.VideoCountdown, obj.TextLink?.Source);
        Assert.Equal(88, obj.X);
        Assert.False(obj.TextLink?.ShowsVideoName);
    }

    [Fact]
    public void TimerLinkedElementWithTextGainsLinkAndMappedTimerID()
    {
        var element = TextElement("EL-TM", "10:00");
        element.DataLinks.Add(new SlideElement.Types.DataLink { TimerText = new() { TimerUuid = Uuid("PRO-TIMER-1") } });
        var obj = ProDocumentMapper.Map(SingleSlideDocument(element), "fallback",
            new Dictionary<string, string> { ["pro-timer-1"] = "mxu-timer-1" }).Presentation.Slides[0].Objects[0];
        Assert.Equal(SlideObjectKind.Text, obj.ObjectKind);
        Assert.Equal("10:00", obj.Text);
        Assert.Equal(TextSourceKind.Timer, obj.TextLink?.Source);
        Assert.Equal("mxu-timer-1", obj.TextLink?.TimerId);
    }

    [Fact]
    public void TextMarginsImportAsInsets()
    {
        var element = TextElement("EL-M", "Inset line");
        element.Element_.Text.Margins = new() { Left = 20, Top = 10, Bottom = 5, Right = 0 };
        var style = FirstObject(SingleSlideDocument(element)).TextStyle!;
        Assert.Equal(20, style.InsetLeft);
        Assert.Equal(10, style.InsetTop);
        Assert.Equal(5, style.InsetBottom);
        Assert.Null(style.InsetRight);
    }

    [Fact]
    public void ParagraphIndentsImportFromProtobufStyle()
    {
        var element = TextElement("EL-I", "Indented line");
        var paragraph = element.Element_.Text.Attributes.ParagraphStyle;
        paragraph.FirstLineHeadIndent = 10;
        paragraph.HeadIndent = 60;
        paragraph.TailIndent = -40;
        paragraph.ParagraphSpacing = 24;
        var style = FirstObject(SingleSlideDocument(element)).TextStyle!;
        Assert.Equal(-50, style.FirstLineIndent);
        Assert.Equal(60, style.LeftIndent);
        Assert.Equal(40, style.RightIndent);
        Assert.Equal(24, style.ParagraphSpacing);
    }

    [Fact]
    public void PerParagraphRTFIndentsBecomeLineOverrides()
    {
        var element = TextElement("EL-P", "ignored");
        element.Element_.Text.RtfData = RtfBytes("{\\rtf1\\ansi{\\fonttbl\\f0\\fnil\\fcharset0 Helvetica;}\\f0\\fs140 Line one\\par\\li1200\\fi-1200 Line two}");
        var obj = FirstObject(SingleSlideDocument(element));
        Assert.Equal("Line one\nLine two", obj.Text);
        var overrides = obj.TextStyle!.LineStyles!;
        var single = Assert.Single(overrides);
        Assert.Equal(1, single.LineIndex);
        Assert.Equal(60, single.LeftIndent);
        Assert.Equal(-60, single.FirstLineIndent);
        Assert.Null(single.RightIndent);
    }

    [Fact]
    public void NearFullBleedObjectsWarnWithoutResizing()
    {
        var shortOne = TextElement("EL-NB", "bg");
        shortOne.Element_.Bounds = new() { Origin = new() { X = 0, Y = 0 }, Size = new() { Width = 1919.9, Height = 1080 } };
        var rotated = TextElement("EL-NBR", "bg2");
        rotated.Element_.Bounds = new() { Origin = new() { X = 420, Y = -419.9 }, Size = new() { Width = 1080, Height = 1919.879 } };
        rotated.Element_.Rotation = 90;
        var exact = TextElement("EL-FB", "bg3");
        exact.Element_.Bounds = new() { Origin = new() { X = 0, Y = 0 }, Size = new() { Width = 1920, Height = 1080 } };

        var mapped = ProDocumentMapper.Map(SingleSlideDocument(shortOne, rotated, exact), "fallback");
        var bleedWarnings = mapped.Warnings.Where(w => w.Contains("misses the canvas edge")).ToList();
        Assert.Equal(2, bleedWarnings.Count);
        Assert.Contains(bleedWarnings, w => w.Contains("0.10"));
        Assert.Contains(bleedWarnings, w => w.Contains("0.06"));
        var objects = mapped.Presentation.Slides[0].Objects;
        Assert.Contains(objects, o => o.Width == 1919.9);
        Assert.Contains(objects, o => o.Height == 1919.879);
    }

    [Fact]
    public void BoxFillWithoutLineMaskStaysShapeHoldingText()
    {
        var element = TextElement("EL-BOX", "All hail");
        element.Element_.Fill = new() { Enable = true, Color = Color(0, 0, 0) };
        var obj = FirstObject(SingleSlideDocument(element));
        Assert.Equal(SlideObjectKind.Shape, obj.ObjectKind);
        Assert.Null(obj.TextStyle?.LineFill);
        Assert.Equal(FillKind.Solid, obj.Fill?.FillKind);
    }

    [Fact]
    public void SlideOrderFollowsGroupsNotStorageOrder()
    {
        var doc = SongDocument();
        doc.Cues.Clear();
        doc.Cues.Add(SlideCue("CUE-V1", [TextElement("EL-1", "One")]));
        doc.Cues.Add(SlideCue("CUE-C1", [TextElement("EL-3", "Chorus")]));
        doc.Cues.Add(SlideCue("CUE-V2", [TextElement("EL-2", "Two")]));
        var mapped = ProDocumentMapper.Map(doc, "fallback");
        Assert.Equal(["cue-v1", "cue-v2", "cue-c1"], mapped.Presentation.Slides.Select(s => s.Id));
        Assert.Equal(["grp-v", "grp-v", "grp-c"], mapped.Presentation.Slides.Select(s => s.SectionId));
        Assert.Equal(2, mapped.Presentation.Sections?.Count);
        Assert.Empty(mapped.Warnings);
    }

    [Fact]
    public void UngroupedCuesFollowInStorageOrderWithoutSections()
    {
        var doc = SongDocument();
        doc.Cues.Add(SlideCue("CUE-LOOSE", [TextElement("EL-9", "Loose")]));
        var mapped = ProDocumentMapper.Map(doc, "fallback");
        Assert.Equal("cue-loose", mapped.Presentation.Slides[^1].Id);
        Assert.Null(mapped.Presentation.Slides[^1].SectionId);
    }

    [Fact]
    public void CueMediaBecomesSlideBackgroundWithPlaceholder()
    {
        var media = new RV.Media
        {
            Uuid = Uuid("MEDIA-1"),
            Url = new() { AbsoluteString = "file:///Users/test/Media/Assets/loop%20one.mov", Local = new() { Path = "Media/Assets/loop one.mov" } },
            Video = new() { Transport = new() { PlaybackBehavior = RV.Media.Types.TransportProperties.Types.PlaybackBehavior.Loop } },
        };
        var doc = SongDocument();
        doc.Cues[0].Actions.Add(new RV.Action { Uuid = Uuid(Guid.NewGuid().ToString()), Media = new() { Element = media, LayerType = RV.Action.Types.LayerType.Background } });

        var mapped = ProDocumentMapper.Map(doc, "fallback");
        var background = mapped.Presentation.Slides[0].Background!;
        Assert.StartsWith(ProDocumentMapper.PlaceholderPrefix, background.MediaId);
        Assert.Equal(CueMediaLayer.LoopingVideos, background.Layer);
        Assert.True(background.Loops);

        var want = mapped.MediaWants[0];
        Assert.Equal(background.MediaId, want.PlaceholderId);
        Assert.Equal("/Users/test/Media/Assets/loop one.mov", want.AbsolutePath);
        Assert.Equal("Media/Assets/loop one.mov", want.RelativePath);
        Assert.Equal(MediaClassification.Background, want.Classification);
        Assert.True(want.Loops);
    }

    [Fact]
    public void PlayOnceBackgroundVideoStaysOnBackgroundMedia()
    {
        var media = new RV.Media
        {
            Uuid = Uuid("MEDIA-PO"),
            Url = new() { AbsoluteString = "file:///Users/test/Media/Assets/backdrop.mov" },
            Video = new() { Transport = new() { PlaybackBehavior = RV.Media.Types.TransportProperties.Types.PlaybackBehavior.Stop } },
        };
        var doc = SongDocument();
        doc.Cues[0].Actions.Add(new RV.Action { Uuid = Uuid(Guid.NewGuid().ToString()), Media = new() { Element = media, LayerType = RV.Action.Types.LayerType.Background } });

        var mapped = ProDocumentMapper.Map(doc, "fallback");
        var background = mapped.Presentation.Slides[0].Background!;
        Assert.Equal(CueMediaLayer.LoopingVideos, background.Layer);
        Assert.False(background.Loops);
        Assert.Equal(MediaClassification.Background, mapped.MediaWants[0].Classification);
        Assert.Null(mapped.MediaWants[0].Loops);
    }

    [Fact]
    public void ForegroundStillBecomesFireMediaActionNotBackground()
    {
        var media = new RV.Media { Uuid = Uuid("MEDIA-2"), Url = new() { AbsoluteString = "file:///Users/test/Media/Assets/announce.png" }, Image = new() };
        var doc = SongDocument();
        doc.Cues[0].Actions.Add(new RV.Action { Uuid = Uuid(Guid.NewGuid().ToString()), Media = new() { Element = media, LayerType = RV.Action.Types.LayerType.Foreground } });

        var mapped = ProDocumentMapper.Map(doc, "fallback");
        var slide = mapped.Presentation.Slides[0];
        Assert.Null(slide.Background);
        var placeholder = slide.Actions!.First(a => a.Kind == SlideActionKind.FireMedia).MediaId!;
        Assert.StartsWith(ProDocumentMapper.PlaceholderPrefix, placeholder);
        Assert.Equal(MediaClassification.Foreground, mapped.MediaWants[0].Classification);

        var resolved = ProDocumentMapper.ReplacingMediaIds(mapped.Presentation, new Dictionary<string, string> { [placeholder] = "real-id" });
        Assert.Equal("real-id", resolved.Slides[0].Actions!.First(a => a.Kind == SlideActionKind.FireMedia).MediaId);
    }

    [Fact]
    public void BackgroundStillBecomesFireMediaActionWithBackgroundClassification()
    {
        var media = new RV.Media { Uuid = Uuid("MEDIA-3"), Url = new() { AbsoluteString = "file:///Users/test/Media/Assets/backdrop.jpg" }, Image = new() };
        var doc = SongDocument();
        doc.Cues[0].Actions.Add(new RV.Action { Uuid = Uuid(Guid.NewGuid().ToString()), Media = new() { Element = media, LayerType = RV.Action.Types.LayerType.Background } });

        var mapped = ProDocumentMapper.Map(doc, "fallback");
        var slide = mapped.Presentation.Slides[0];
        Assert.Null(slide.Background);
        Assert.StartsWith(ProDocumentMapper.PlaceholderPrefix, slide.Actions!.First(a => a.Kind == SlideActionKind.FireMedia).MediaId);
        Assert.Equal(MediaClassification.Background, mapped.MediaWants[0].Classification);
    }

    [Fact]
    public void TimelineImportsOntoEveryPlayedOrder()
    {
        static RV.Presentation.Types.Timeline.Types.Cue TimelineCue(double at, string slide) => new() { TriggerTime = at, CueId = Uuid(slide) };
        static RV.Action MediaAction(string id, Action<RV.Media> type)
        {
            var media = new RV.Media { Uuid = Uuid(id), Url = new() { AbsoluteString = $"file:///Users/test/{id}" } };
            type(media);
            return new RV.Action { Media = new() { Element = media } };
        }
        var doc = SongDocument();
        var graphic = new RV.Presentation.Types.Timeline.Types.Cue { TriggerTime = 7, Action = MediaAction("GRAPHIC", m => m.Image = new()) };
        doc.Timeline = new()
        {
            CuesV2 = { TimelineCue(1, "CUE-V1"), TimelineCue(3, "CUE-V2"), TimelineCue(5, "CUE-C1"), graphic, TimelineCue(9, "CUE-V1"), TimelineCue(11, "CUE-GONE") },
            AudioAction = MediaAction("SONG", m => m.Audio = new()),
            TimecodeEnable = true,
            TimecodeOffset = 3600,
            Duration = 240,
        };

        var mapped = ProDocumentMapper.Map(doc, "Song");
        var timelines = mapped.Presentation.Timelines!;
        Assert.Equal([null, "arr-1"], timelines.Select(t => t.ArrangementId));
        var sunday = timelines[1];
        Assert.True(sunday.TimecodeEnabled && sunday.TimecodeStart == 3600 * 30 && sunday.Length == 240);
        var slides = sunday.Cues.Where(c => c.Lane == "slide").ToList();
        Assert.Equal(["cue-v1", "cue-v2", "cue-c1", "cue-v1"], slides.Select(c => c.SlideId));
        Assert.Equal(1, slides[^1].SectionOccurrence);
        Assert.Equal(0, timelines[0].Cues.Where(c => c.Lane == "slide").Last().SectionOccurrence);
        Assert.Equal(SlideActionKind.FireMedia, sunday.Cues.First(c => c.Lane == "actions").Actions?[0].Kind);
        var song = sunday.Cues.First(c => c.Lane == "audio");
        Assert.Equal(0, song.At);
        Assert.Contains(mapped.Warnings, w => w.Contains("1 cue(s) name slides that aren't in the presentation"));

        var resolved = ProDocumentMapper.ReplacingMediaIds(mapped.Presentation, new Dictionary<string, string> { [song.MediaId!] = "song-id" });
        var cues = resolved.Timelines![1].Cues;
        Assert.Equal("song-id", cues.First(c => c.Lane == "audio").MediaId);
        Assert.DoesNotContain(cues, c => c.Lane == "actions");
        Assert.Null(ProDocumentMapper.Map(SongDocument(), "Song").Presentation.Timelines);
    }

    [Fact]
    public void ReplacingMediaIDsRewritesAndStripsUnresolved()
    {
        var action = new RV.Action
        {
            Media = new()
            {
                Element = new() { Uuid = Uuid("MEDIA-1"), Url = new() { AbsoluteString = "file:///missing.mov" }, Video = new() },
                LayerType = RV.Action.Types.LayerType.Background,
            },
        };
        var doc = SongDocument();
        doc.Cues[0].Actions.Add(action);
        doc.Cues[1].Actions.Add(action);

        var mapped = ProDocumentMapper.Map(doc, "fallback");
        var placeholder = Assert.Single(mapped.MediaWants).PlaceholderId;
        var resolved = ProDocumentMapper.ReplacingMediaIds(mapped.Presentation, new Dictionary<string, string> { [placeholder] = "real-id" });
        Assert.Equal("real-id", resolved.Slides[0].Background?.MediaId);
        Assert.Equal("real-id", resolved.Slides[1].Background?.MediaId);
        Assert.Null(ProDocumentMapper.ReplacingMediaIds(mapped.Presentation, new Dictionary<string, string>()).Slides[0].Background);
    }

    [Fact]
    public void MediaElementBecomesMediaFilledShape()
    {
        var media = new RV.Media
        {
            Uuid = Uuid("MEDIA-2"), Url = new() { AbsoluteString = "file:///Users/test/logo.png" },
            Image = new() { Drawing = new() { ScaleBehavior = RV.Media.Types.ScaleBehavior.Fit } },
        };
        var graphics = new G.Element
        {
            Uuid = Uuid("EL-M"), Opacity = 1, Fill = new() { Media = media },
            Bounds = new() { Origin = new() { X = 100, Y = 100 }, Size = new() { Width = 400, Height = 300 } },
        };
        var obj = FirstObject(SingleSlideDocument(Wrap(graphics)));
        Assert.Equal(SlideObjectKind.Shape, obj.ObjectKind);
        Assert.Equal(FillKind.Media, obj.Fill?.FillKind);
        Assert.StartsWith(ProDocumentMapper.PlaceholderPrefix, obj.Fill?.MediaId);
        Assert.Equal(MediaScaleMode.Fit, obj.Fill?.MediaScaleMode);
        Assert.Equal(400, obj.Width);
    }

    [Fact]
    public void LiveVideoElementBecomesDirectDeviceFill()
    {
        var media = new RV.Media
        {
            LiveVideo = new()
            {
                LiveVideo = new() { VideoDevice = new() { Type = RV.Media.Types.VideoDevice.Types.Type.Av, UniqueId = "0xCAFE", Name = "Center Camera" } },
            },
        };
        var graphics = new G.Element { Uuid = Uuid("EL-LIVE"), Opacity = 1, Fill = new() { Media = media }, Bounds = new() { Size = new() { Width = 800, Height = 450 } } };
        var mapped = ProDocumentMapper.Map(SingleSlideDocument(Wrap(graphics)), "fallback");
        var obj = mapped.Presentation.Slides[0].Objects[0];
        Assert.Equal(SlideObjectKind.Shape, obj.ObjectKind);
        Assert.Equal(FillKind.Media, obj.Fill?.FillKind);
        Assert.Null(obj.Fill?.MediaId);
        Assert.Empty(mapped.MediaWants);
        Assert.Equal(CaptureSourceKind.Camera, obj.Fill?.CaptureSourceKind);
        Assert.Equal("0xCAFE", obj.Fill?.CaptureSourceId);
    }

    [Fact]
    public void RotationSignFlipsToClockwise()
    {
        var element = new G.Element
        {
            Uuid = Uuid("EL-ROT"), Bounds = new() { Size = new() { Width = 100, Height = 100 } }, Rotation = 7, Opacity = 1,
            Fill = new() { Color = Color(1, 0, 0), Enable = true },
        };
        Assert.Equal(-7, FirstObject(SingleSlideDocument(Wrap(element))).RotationDegrees);
    }

    [Fact]
    public void PowerPointOpacityScaleNormalizes()
    {
        var full = new G.Element
        {
            Uuid = Uuid("EL-O1"), Bounds = new() { Size = new() { Width = 100, Height = 100 } }, Opacity = 100,
            Fill = new() { Color = Color(1, 0, 0), Enable = true },
        };
        var half = full.Clone();
        half.Uuid = Uuid("EL-O2");
        half.Opacity = 50;
        var native = full.Clone();
        native.Uuid = Uuid("EL-O3");
        native.Opacity = 0.4;
        var objects = ProDocumentMapper.Map(SingleSlideDocument(Wrap(full), Wrap(half), Wrap(native)), "fallback").Presentation.Slides[0].Objects;
        var byId = objects.ToDictionary(o => o.Id);
        Assert.Null(byId["el-o1"].Opacity);
        Assert.Equal(0.5, byId["el-o2"].Opacity);
        Assert.Equal(0.4, byId["el-o3"].Opacity);
    }

    [Fact]
    public void InvisibleOnlyTextKeepsShapeAndStroke()
    {
        var element = TextElement("EL-Z", "\\uc0\\u8203 ");
        element.Element_.Stroke = new() { Enable = true, Width = 3, Color = Color(1, 1, 1) };
        Assert.Equal(((char)0x200B).ToString(), AttributedText.FromRtf(element.Element_.Text.RtfData.Span)?.Text);

        var obj = FirstObject(SingleSlideDocument(element));
        Assert.Equal(SlideObjectKind.Shape, obj.ObjectKind);
        Assert.Equal("", obj.Text);
        Assert.Equal("#FFFFFFFF", obj.Stroke?.ColorHex);
        Assert.Equal(3, obj.Stroke?.Width);
        Assert.Null(obj.Stroke?.DashKind);
    }

    private static G.Path.Types.BezierPoint Corner(double x, double y)
    {
        var point = new G.Point { X = x, Y = y };
        return new() { Point = point, Q0 = point.Clone(), Q1 = point.Clone() };
    }

    private static void AssertRect(MediaSourceRect expected, MediaSourceRect? actual) =>
        Assert.Equal(Json(expected), Json(actual));

    [Fact]
    public void CropInsetsImportAsMediaSourceRect()
    {
        var media = new RV.Media
        {
            Uuid = Uuid("MEDIA-C"), Url = new() { AbsoluteString = "file:///Users/test/scan.png" },
            Image = new()
            {
                Drawing = new()
                {
                    ScaleBehavior = RV.Media.Types.ScaleBehavior.Stretch, NaturalSize = new() { Width = 1000, Height = 500 },
                    CropEnable = true, CropInsets = new() { Left = 100, Top = 50, Right = 400, Bottom = 200 },
                },
            },
        };
        var rectangle = new G.Element { Uuid = Uuid("EL-C1"), Bounds = new() { Size = new() { Width = 400, Height = 300 } }, Opacity = 1, Fill = new() { Media = media } };
        var freeform = new G.Element
        {
            Uuid = Uuid("EL-C2"), Bounds = new() { Size = new() { Width = 300, Height = 300 } }, Opacity = 1, Fill = new() { Media = media.Clone() },
            Path = new() { Closed = true, Shape = new() { Type = G.Path.Types.Shape.Types.Type.Custom }, Points = { Corner(0.5, 0), Corner(1, 1), Corner(0, 1) } },
        };
        var expected = new MediaSourceRect { X = 0.1, Y = 0.1, Width = 0.5, Height = 0.5 };
        var byId = ProDocumentMapper.Map(SingleSlideDocument(Wrap(rectangle), Wrap(freeform)), "fallback").Presentation.Slides[0].Objects.ToDictionary(o => o.Id);
        Assert.Equal(SlideObjectKind.Shape, byId["el-c1"].ObjectKind);
        AssertRect(expected, byId["el-c1"].Fill?.MediaSourceRect);
        Assert.Equal(SlideObjectKind.Shape, byId["el-c2"].ObjectKind);
        AssertRect(expected, byId["el-c2"].Fill?.MediaSourceRect);

        var uncropped = media.Clone();
        uncropped.Image = new() { Drawing = new() { ScaleBehavior = RV.Media.Types.ScaleBehavior.Stretch } };
        Assert.Null(ProDocumentMapper.MediaSourceRectOf(uncropped));

        var outset = media.Clone();
        outset.Image = new()
        {
            Drawing = new() { NaturalSize = new() { Width = 100, Height = 100 }, CropEnable = true, CropInsets = new() { Left = 10, Top = -20, Right = 40, Bottom = 30 } },
        };
        AssertRect(new MediaSourceRect { X = 0.1, Y = -0.2, Width = 0.5, Height = 0.9 }, ProDocumentMapper.MediaSourceRectOf(outset));
    }

    [Fact]
    public void RoundedRectangleAndBezierPathsMap()
    {
        var rounded = new G.Element
        {
            Uuid = Uuid("EL-R"), Bounds = new() { Size = new() { Width = 200, Height = 200 } }, Opacity = 1,
            Path = new() { Closed = true, Shape = new() { Type = G.Path.Types.Shape.Types.Type.Rectangle, RoundedRectangle = new() { Roundness = 24 } } },
            Fill = new() { Color = Color(1, 0, 0), Enable = true },
        };
        var custom = new G.Element
        {
            Uuid = Uuid("EL-P"), Bounds = new() { Size = new() { Width = 300, Height = 300 } }, Opacity = 1,
            Path = new() { Closed = true, Shape = new() { Type = G.Path.Types.Shape.Types.Type.Custom }, Points = { Corner(0.5, 0), Corner(1, 1), Corner(0, 1) } },
            Fill = new() { Color = Color(0, 0, 1), Enable = true },
        };
        var objects = ProDocumentMapper.Map(SingleSlideDocument(Wrap(rounded), Wrap(custom)), "fallback").Presentation.Slides[0].Objects;
        Assert.Equal(ShapeKind.RoundedRectangle, objects[1].ShapeKind);
        Assert.Equal(24, objects[1].CornerRadius);
        Assert.Equal(FillKind.Solid, objects[1].Fill?.FillKind);
        Assert.Equal("#FF0000FF", objects[1].Fill?.ColorHex);
        Assert.Equal(ShapeKind.Path, objects[0].ShapeKind);
        Assert.StartsWith("M 0.5000 0.0000", objects[0].PathData);
        Assert.EndsWith("Z", objects[0].PathData);
    }

    [Fact]
    public void ElementOrderReversesSoTextStaysAboveMedia()
    {
        var band = new G.Element
        {
            Uuid = Uuid("EL-BAND"), Bounds = new() { Size = new() { Width = 1920, Height = 1080 } }, Opacity = 1,
            Fill = new() { Media = new() { Uuid = Uuid("MEDIA-1"), Image = new() } },
        };
        var objects = ProDocumentMapper.Map(SingleSlideDocument(TextElement("EL-TEXT", "Chris Lindberg"), Wrap(band)), "fallback").Presentation.Slides[0].Objects;
        Assert.Equal(2, objects.Count);
        Assert.Equal(FillKind.Media, objects[0].Fill?.FillKind);
        Assert.Equal(SlideObjectKind.Text, objects[1].ObjectKind);
        Assert.Equal("Chris Lindberg", objects[1].Text);
    }

    [Fact]
    public void FlattenedActionsLandInSlideNotes()
    {
        var doc = SongDocument();
        doc.Cues[0].Actions.Add(new RV.Action { Uuid = Uuid(Guid.NewGuid().ToString()), Label = new() { Text = "Move In" }, Transition = new() });
        Assert.Contains("Move In", ProDocumentMapper.Map(doc, "fallback").Presentation.Slides[0].Notes);
    }

    [Fact]
    public void DecodeRoundTripThroughSerializedBytes()
    {
        var decoded = RV.Presentation.Parser.ParseFrom(Google.Protobuf.MessageExtensions.ToByteArray(SongDocument()));
        Assert.Equal("Test Song", decoded.Name);
        Assert.Equal(3, ProDocumentMapper.Map(decoded, "fallback").Presentation.Slides.Count);
    }

    private static RV.Transition RvTransition(string name, double duration) => new() { Duration = duration, Effect = new() { Name = name } };

    private static void AssertTransition(TransitionKind kind, double? duration, Transition? actual)
    {
        Assert.Equal(kind, actual?.TransitionKind);
        Assert.Equal(duration, actual?.DurationSeconds);
    }

    [Fact]
    public void SlideTransitionsImportWithDocFallback()
    {
        var doc = SongDocument();
        doc.Transition = RvTransition("Dissolve", 0.8);
        doc.Cues[1].Actions[0].Slide.Presentation.Transition = RvTransition("Move In", 1.2);
        var mapped = ProDocumentMapper.Map(doc, "fallback");
        var slides = mapped.Presentation.Slides;
        AssertTransition(TransitionKind.Dissolve, 0.8, slides[0].Transition);
        AssertTransition(TransitionKind.Dissolve, 1.2, slides[1].Transition);
        Assert.Contains(mapped.Warnings, w => w.Contains("Move In"));
        AssertTransition(TransitionKind.Dissolve, 0.8, slides[2].Transition);
    }

    [Fact]
    public void TransitionNameFamiliesMap()
    {
        var state = new ProDocumentMapper.MapState();
        AssertTransition(TransitionKind.FadeBlack, 1, ProDocumentMapper.Transition(RvTransition("Fade", 1), state));
        AssertTransition(TransitionKind.BlurDissolve, 2, ProDocumentMapper.Transition(RvTransition("Blur Dissolve", 2), state));
        AssertTransition(TransitionKind.Cut, null, ProDocumentMapper.Transition(RvTransition("None", 0.5), state));
        Assert.Null(ProDocumentMapper.Transition(new RV.Transition(), state));
    }

    [Fact]
    public void DocumentBackgroundImportsAsBackgroundFill()
    {
        var doc = SingleSlideDocument(TextElement("EL", "hi"));
        doc.Background = new() { IsEnabled = true, Color = Color(0, 0, 0, 1) };
        Assert.Equal(Json(new ObjectFill { FillKind = FillKind.Solid, ColorHex = "#000000FF" }), Json(ProDocumentMapper.Map(doc, "fallback").Presentation.BackgroundFill));
        doc.Background.IsEnabled = false;
        Assert.Null(ProDocumentMapper.Map(doc, "fallback").Presentation.BackgroundFill);
    }

    [Fact]
    public void UnderlineRunsImportAsStyleRuns()
    {
        var element = TextElement("EL-U", "");
        element.Element_.Text.RtfData = RtfBytes("{\\rtf1\\ansi{\\fonttbl\\f0\\fnil\\fcharset0 Helvetica;}\\f0\\fs140 Gracious {\\ul words} heal\\par softly}");
        var obj = FirstObject(SingleSlideDocument(element));
        var run = Assert.Single(obj.StyleRuns!);
        Assert.Equal(0, run.Line);
        Assert.Equal(9, run.Column);
        Assert.Equal(5, run.Length);
        Assert.True(run.Underline);
        Assert.Null(run.Strikethrough);
        Assert.Null(obj.TextStyle?.Underline);
    }

    [Fact]
    public void MidLineFontSizeAndColorImportAsStyleRuns()
    {
        var element = TextElement("EL-M", "");
        element.Element_.Text.RtfData = RtfBytes(
            "{\\rtf1\\ansi{\\fonttbl\\f0\\fnil\\fcharset0 Helvetica;\\f1\\fnil\\fcharset0 Helvetica-Bold;}"
            + "{\\colortbl;\\red255\\green0\\blue0;}"
            + "\\f0\\fs140 Amazing {\\f1\\fs200 grace} how {\\cf1 sweet}}");
        var runs = FirstObject(SingleSlideDocument(element)).StyleRuns!;
        var bold = runs.First(r => r.FontName is not null);
        Assert.Equal(0, bold.Line);
        Assert.Equal(8, bold.Column);
        Assert.Equal(5, bold.Length);
        Assert.Equal("Helvetica-Bold", bold.FontName);
        Assert.Equal(100, bold.FontSize);
        var colored = runs.First(r => r.ColorHex is not null);
        Assert.Equal(18, colored.Column);
        Assert.Equal(5, colored.Length);
        Assert.Equal("#FF2600FF", colored.ColorHex);
        Assert.All(runs, r => Assert.True(r.Column > 0));
    }

    [Fact]
    public void InvisibleSpacerRunsEmitNoStyleRuns()
    {
        var element = TextElement("EL-Z", "");
        element.Element_.Text.RtfData = RtfBytes(
            "{\\rtf1\\ansi{\\fonttbl\\f0\\fnil\\fcharset0 Helvetica;\\f1\\fnil\\fcharset0 Helvetica-Bold;}"
            + "\\f0\\fs140 Amazing{\\f1 \\u8203 ?}grace}");
        var obj = FirstObject(SingleSlideDocument(element));
        Assert.All(obj.StyleRuns ?? [], r => Assert.Null(r.FontName));
    }

    [Fact]
    public void ProtobufUnderlineBecomesBaseStyle()
    {
        var element = TextElement("EL-BU", "All of it");
        element.Element_.Text.Attributes.UnderlineStyle = new() { Style = G.Text.Types.Attributes.Types.Underline.Types.Style.Single };
        Assert.True(FirstObject(SingleSlideDocument(element)).TextStyle?.Underline);
    }

    [Fact]
    public void PerLineFontNamesCarryWithRTFGuard()
    {
        var element = TextElement("EL-F", "");
        element.Element_.Text.RtfData = RtfBytes(
            "{\\rtf1\\ansi{\\fonttbl\\f0\\fnil\\fcharset0 Helvetica;\\f1\\fnil\\fcharset0 Helvetica-Bold;}\\f1\\fs140 TITLE LINE\\par\\f0 body line}");
        var lines = FirstObject(SingleSlideDocument(element)).TextStyle!.LineStyles!;
        Assert.Equal("Helvetica-Bold", lines.First(l => l.LineIndex == 0).FontName);
    }

    private static SlideElement MediaElement(string id, Action<RV.Media.Types.DrawingProperties> configure)
    {
        var drawing = new RV.Media.Types.DrawingProperties { ScaleBehavior = RV.Media.Types.ScaleBehavior.Fill };
        configure(drawing);
        return Wrap(new G.Element
        {
            Uuid = Uuid(id), Name = "Picture", Opacity = 1,
            Bounds = new() { Size = new() { Width = 1920, Height = 1080 } },
            Fill = new()
            {
                Media = new() { Uuid = Uuid($"MEDIA-{id}"), Url = new() { AbsoluteString = "file:///Users/test/picture.jpg" }, Image = new() { Drawing = drawing } },
            },
        });
    }

    [Fact]
    public void MediaFlipsImportOntoTheObject()
    {
        var obj = FirstObject(SingleSlideDocument(MediaElement("EL-FL", d =>
        {
            d.FlippedHorizontally = true;
            d.FlippedVertically = true;
        })));
        Assert.Equal(SlideObjectKind.Shape, obj.ObjectKind);
        Assert.Equal(FillKind.Media, obj.Fill?.FillKind);
        Assert.True(obj.FlipHorizontal);
        Assert.True(obj.FlipVertical);
    }

    [Fact]
    public void CustomImageBoundsImportAsSourceRect()
    {
        var obj = FirstObject(SingleSlideDocument(MediaElement("EL-PZ", d =>
        {
            d.ScaleBehavior = RV.Media.Types.ScaleBehavior.Custom;
            d.CustomImageBounds = new() { Origin = new() { X = 0, Y = -183.6 }, Size = new() { Width = 1920, Height = 1447.2 } };
        })));
        var rect = obj.Fill!.MediaSourceRect!;
        Assert.True(Math.Abs(rect.X) < 0.0001);
        Assert.True(Math.Abs(rect.Y - 183.6 / 1447.2) < 0.0001);
        Assert.True(Math.Abs(rect.Width - 1) < 0.0001);
        Assert.True(Math.Abs(rect.Height - 1080 / 1447.2) < 0.0001);
        Assert.Equal(MediaScaleMode.Stretch, obj.Fill.MediaScaleMode);
    }

    [Fact]
    public void MediaEffectsMapAdjustColorAndAlphaWarnOthers()
    {
        static RV.Effect Effect(string name, params (string Name, double Value)[] variables)
        {
            var effect = new RV.Effect { Enabled = true, Name = name };
            effect.Variables.Add(variables.Select(v => new RV.Effect.Types.EffectVariable { Name = v.Name, Double = new() { Value = v.Value } }));
            return effect;
        }
        var mapped = ProDocumentMapper.Map(SingleSlideDocument(MediaElement("EL-FX", d => d.Effects.Add(
        [
            Effect("Adjust Color", ("hue", 0), ("saturation", 1.4), ("brightness", 0.1), ("contrast", 1.3)),
            Effect("Adjust Alpha", ("amount", 0.5)),
            Effect("Tile", ("cells", 15)),
        ]))), "fallback");
        var obj = mapped.Presentation.Slides[0].Objects[0];
        var effect = Assert.Single(obj.Effects!);
        Assert.Equal(EffectKind.ColorAdjust, effect.EffectKind);
        Assert.Equal(1.4, effect.Saturation);
        Assert.True(Math.Abs((effect.Brightness ?? 0) - 0.1) < 0.0001);
        Assert.True(Math.Abs((effect.Contrast ?? 0) - 0.3) < 0.0001);
        Assert.True(Math.Abs((obj.Opacity ?? 1) - 0.5) < 0.0001);
        Assert.Contains(mapped.Warnings, w => w.Contains("Tile"));
    }

    [Fact]
    public void VideoTransportRidesTheWant()
    {
        var media = new RV.Media
        {
            Uuid = Uuid("MEDIA-TRIM"), Url = new() { AbsoluteString = "file:///Users/test/bumper.mov" },
            Video = new() { Transport = new() { InPoint = 2.5, OutPoint = 40, ShouldFadeIn = true, FadeInDuration = 1.5 } },
        };
        var doc = SongDocument();
        doc.Cues[0].Actions.Add(new RV.Action { Media = new() { Element = media, LayerType = RV.Action.Types.LayerType.Foreground } });
        var want = ProDocumentMapper.Map(doc, "fallback").MediaWants.First(w => w.AbsolutePath?.Contains("bumper") == true);
        Assert.Equal(2.5, want.InPoint);
        Assert.Equal(40, want.OutPoint);
        Assert.Null(want.PlayRate);
        Assert.Equal(1.5, want.FadeSeconds);
    }

    [Fact]
    public void AudioCueBecomesFireAudioAction()
    {
        var media = new RV.Media
        {
            Uuid = Uuid("MEDIA-SFX"), Url = new() { AbsoluteString = "file:///Users/test/stinger.wav" },
            Audio = new() { Transport = new() { PlaybackBehavior = RV.Media.Types.TransportProperties.Types.PlaybackBehavior.Loop } },
        };
        var doc = SongDocument();
        doc.Cues[0].Actions.Add(new RV.Action { Media = new() { Element = media } });
        var mapped = ProDocumentMapper.Map(doc, "fallback");
        var slide = mapped.Presentation.Slides[0];
        Assert.Null(slide.Background);
        var action = Assert.Single(slide.Actions!);
        Assert.Equal(SlideActionKind.FireAudio, action.Kind);
        Assert.True(action.AudioRepeat);
        var placeholder = action.AudioItemId!;
        Assert.StartsWith(ProDocumentMapper.PlaceholderPrefix, placeholder);

        var resolved = ProDocumentMapper.ReplacingMediaIds(mapped.Presentation, new Dictionary<string, string> { [placeholder] = "audio-77" });
        Assert.Equal("audio-77", resolved.Slides[0].Actions?[0].AudioItemId);
        Assert.Null(ProDocumentMapper.ReplacingMediaIds(mapped.Presentation, new Dictionary<string, string>()).Slides[0].Actions);
    }

    private static RV.Transition Transition(string name, double duration) => new() { Duration = duration, Effect = new() { Name = name } };

    [Fact]
    public void ElementBuildsImportAsSteps()
    {
        var title = TextElement("EL-T", "Title");
        title.BuildIn = new() { Uuid = Uuid("B-T-IN"), Start = SlideElement.Types.Build.Types.Start.WithSlide, Transition = Transition("Fly In From Left", 0.8) };
        title.BuildOut = new() { Uuid = Uuid("B-T-OUT"), Start = SlideElement.Types.Build.Types.Start.OnClick, Transition = Transition("Zoom Out", 0.4) };

        var points = TextElement("EL-P", "One\\\nTwo\\\nThree");
        points.BuildIn = new() { Uuid = Uuid("B-P-IN"), Transition = Transition("Dissolve", 0.5) };
        points.RevealType = SlideElement.Types.TextRevealType.Underline;
        points.ChildBuilds.Add(Enumerable.Range(0, 3).Select(index => new SlideElement.Types.ChildBuild
        {
            Uuid = Uuid($"B-P-{index}"), Index = (uint)index,
            Start = index == 0 ? SlideElement.Types.Build.Types.Start.OnClick : SlideElement.Types.Build.Types.Start.AfterPrevious,
            DelayTime = index == 0 ? 0 : 0.25,
        }));
        var odd = TextElement("EL-O", "Odd");
        odd.BuildIn = new() { Uuid = Uuid("B-O-IN"), Transition = Transition("Origami Fold", 1) };

        var cue = SlideCue("CUE-B", [title, points, odd]);
        cue.Actions[0].Slide.Presentation.BaseSlide.ElementBuildOrder.Add([Uuid("EL-P"), Uuid("EL-T")]);
        var doc = new RV.Presentation { Uuid = Uuid("DOC-B"), Name = "Builds", Cues = { cue } };

        var mapped = ProDocumentMapper.Map(doc, "fallback");
        var slide = mapped.Presentation.Slides[0];
        var byId = slide.Objects.ToDictionary(o => o.Id);

        var t = byId["el-t"].AnimationSteps!;
        Assert.Equal([AnimationKind.In, AnimationKind.Out], t.Select(s => s.Kind));
        Assert.True(t[0].Animation == StepAnimation.Move && t[0].Edge == AnimationEdge.Left && t[0].Trigger == AnimationTrigger.WithPrevious && t[0].DurationSeconds == 0.8);
        Assert.True(t[1].Animation == StepAnimation.Scale && t[1].Trigger == AnimationTrigger.OnClick && t[1].Id == "b-t-out");

        var p = byId["el-p"].AnimationSteps!;
        Assert.Equal(3, p.Count);
        Assert.Equal([0, 1, 2], p.Select(s => s.Ranges?[0].Line));
        Assert.True(p[1].Trigger == AnimationTrigger.AfterPrevious && p[1].DelaySeconds == 0.25 && p[0].Trigger == AnimationTrigger.OnClick);
        Assert.All(p, s => Assert.True(s.Animation == StepAnimation.Fade && s.PlaceholderUnderline == true));

        var o = byId["el-o"].AnimationSteps!;
        Assert.Equal(StepAnimation.Fade, o[0].Animation);
        Assert.Contains("Origami Fold", slide.Notes);
        Assert.Contains(mapped.Warnings, w => w.Contains("Origami Fold"));

        var noisy = TextElement("EL-N", "Noise");
        noisy.BuildIn = new() { Uuid = Uuid("B-N-IN"), Transition = Transition("Dissolve", 0.6000000238418579), DelayTime = 2.7755575615628914e-17 };
        var noisyDoc = new RV.Presentation { Uuid = Uuid("DOC-N"), Name = "N", Cues = { SlideCue("CUE-N", [noisy]) } };
        var noisyStep = ProDocumentMapper.Map(noisyDoc, "N").Presentation.Slides[0].Objects[0].AnimationSteps![0];
        Assert.Equal(0.6, noisyStep.DurationSeconds);
        Assert.Null(noisyStep.DelaySeconds);

        Assert.Equal(["b-p-0", "b-p-1", "b-p-2", "b-t-in", "b-t-out", "b-o-in"], slide.AnimationOrder);
        Assert.All(slide.Objects, obj => Assert.All(obj.AnimationSteps ?? [], s => Assert.NotEqual(AnimationTrigger.OnDismiss, s.Trigger)));
    }

    [Fact]
    public void BuildUuidOrderAndRevealOutsMatchPro()
    {
        static SlideElement Element(string id, string text, string inId, string outId, SlideElement.Types.Build.Types.Start inStart)
        {
            var e = TextElement(id, text);
            var moveIn = Transition("Move In", 0.6);
            moveIn.Effect.Variables.Add(new RV.Effect.Types.EffectVariable
            {
                Name = "Direction", Direction = new() { Direction = RV.Effect.Types.EffectVariable.Types.EffectDirection.Types.EffectDirection.Right },
            });
            e.BuildIn = new() { Uuid = Uuid(inId), Start = inStart, Transition = moveIn };
            e.BuildOut = new()
            {
                Uuid = Uuid(outId),
                Start = outId == "B-T-OUT" ? SlideElement.Types.Build.Types.Start.OnClick : SlideElement.Types.Build.Types.Start.WithPrevious,
                Transition = Transition("Reveal", 0.6),
            };
            return e;
        }
        var plate = Element("EL-PL", "Plate", "B-PL-IN", "B-PL-OUT", SlideElement.Types.Build.Types.Start.WithSlide);
        var title = Element("EL-T", "Title", "B-T-IN", "B-T-OUT", SlideElement.Types.Build.Types.Start.WithPrevious);
        var name = Element("EL-N", "Name", "B-N-IN", "B-N-OUT", SlideElement.Types.Build.Types.Start.WithPrevious);
        var cue = SlideCue("CUE-R", [plate, title, name]);
        cue.Actions[0].Slide.Presentation.BaseSlide.ElementBuildOrder.Add(
            new[] { "B-PL-IN", "B-T-IN", "B-N-IN", "B-T-OUT", "B-N-OUT", "B-PL-OUT" }.Select(Uuid));
        var doc = new RV.Presentation { Uuid = Uuid("DOC-R"), Name = "R", Cues = { cue } };

        var slide = ProDocumentMapper.Map(doc, "R").Presentation.Slides[0];
        Assert.Equal(["b-pl-in", "b-t-in", "b-n-in", "b-t-out", "b-n-out", "b-pl-out"], slide.AnimationOrder);
        var byId = slide.Objects.SelectMany(o => o.AnimationSteps ?? []).ToDictionary(s => s.Id);
        Assert.Equal(AnimationTrigger.OnClick, byId["b-t-out"].Trigger);
        Assert.True(byId["b-n-out"].Trigger == AnimationTrigger.WithPrevious && byId["b-pl-out"].Trigger == AnimationTrigger.WithPrevious);
        foreach (var id in new[] { "b-pl-in", "b-t-in", "b-n-in" })
            Assert.True(byId[id].Edge is null && byId[id].OffsetX == 1832, id);
        foreach (var id in new[] { "b-t-out", "b-n-out", "b-pl-out" })
        {
            Assert.Equal(StepAnimation.Move, byId[id].Animation);
            Assert.True(byId[id].Edge is null && byId[id].OffsetX == 1832, id);
        }
    }
}
