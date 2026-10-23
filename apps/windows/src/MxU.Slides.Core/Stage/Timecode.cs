using System.Globalization;

namespace MxU.Slides.Core.Stage;

// SMPTE timecode labels for the stage display's timecode source. Port of
// TimecodeRate and TimecodeLabel in PresenterCore/Timeline/LTCDecoder.swift
// (the decoder itself, and label(startFrames:), are not ported here).

public enum TimecodeRate { Fps24, Fps25, Fps2997, Fps2997DropFrame, Fps30 }

public static class TimecodeRates
{
    public static readonly IReadOnlyList<TimecodeRate> All = Enum.GetValues<TimecodeRate>();

    public static string Raw(this TimecodeRate rate) => rate switch
    {
        TimecodeRate.Fps24 => "fps24",
        TimecodeRate.Fps25 => "fps25",
        TimecodeRate.Fps2997 => "fps2997",
        TimecodeRate.Fps2997DropFrame => "fps2997DropFrame",
        _ => "fps30",
    };

    public static TimecodeRate? Parse(string? raw) => All.Cast<TimecodeRate?>().FirstOrDefault(r => r!.Value.Raw() == raw);

    public static int Nominal(this TimecodeRate rate) => rate switch
    {
        TimecodeRate.Fps24 => 24,
        TimecodeRate.Fps25 => 25,
        _ => 30,
    };

    public static double Actual(this TimecodeRate rate) =>
        rate is TimecodeRate.Fps2997 or TimecodeRate.Fps2997DropFrame ? 30000.0 / 1001.0 : rate.Nominal();

    public static string Title(this TimecodeRate rate) => rate switch
    {
        TimecodeRate.Fps24 => "24 fps",
        TimecodeRate.Fps25 => "25 fps",
        TimecodeRate.Fps2997 => "29.97 fps",
        TimecodeRate.Fps2997DropFrame => "29.97 fps drop-frame",
        _ => "30 fps",
    };

    public static int FrameNumber(this TimecodeRate rate, TimecodeLabel label)
    {
        var nominal = rate.Nominal();
        var labelled = ((label.Hours * 60 + label.Minutes) * 60 + label.Seconds) * nominal + label.Frames;
        if (rate != TimecodeRate.Fps2997DropFrame) return labelled;
        var minutes = label.Hours * 60 + label.Minutes;
        return labelled - 2 * (minutes - minutes / 10);
    }

    public static TimecodeLabel LabelForFrame(this TimecodeRate rate, int frameNumber)
    {
        var n = Math.Max(frameNumber, 0);
        if (rate == TimecodeRate.Fps2997DropFrame)
        {
            int tens = n / 17_982, rest = n % 17_982;
            n += 18 * tens + (rest < 2 ? 0 : 2 * ((rest - 2) / 1_798));
        }
        var nominal = rate.Nominal();
        var seconds = n / nominal;
        return new TimecodeLabel(seconds / 3600 % 24, seconds / 60 % 60, seconds % 60, n % nominal);
    }

    public static TimecodeLabel LabelForSeconds(this TimecodeRate rate, double seconds) =>
        rate.LabelForFrame((int)Math.Floor(seconds * rate.Actual() + 1e-6));

    public static double Seconds(this TimecodeRate rate, TimecodeLabel label) => rate.FrameNumber(label) / rate.Actual();
}

public readonly record struct TimecodeLabel(int Hours, int Minutes, int Seconds, int Frames)
{
    public string Text(bool dropFrame = false) =>
        string.Create(CultureInfo.InvariantCulture, $"{Hours:00}:{Minutes:00}:{Seconds:00}{(dropFrame ? ";" : ":")}{Frames:00}");
}

/// <summary>Incoming timecode (LTC/MTC) as last read, counted forward from <see cref="AnchoredAt"/> while running.</summary>
public sealed record TimecodeDisplay(double Seconds, TimecodeRate Rate, DateTimeOffset AnchoredAt, bool IsRunning, bool Reverse = false)
{
    public double SecondsAt(DateTimeOffset at) =>
        IsRunning ? Seconds + (at - AnchoredAt).TotalSeconds * (Reverse ? -1 : 1) : Seconds;

    public string Text(DateTimeOffset at) =>
        Rate.LabelForSeconds(SecondsAt(at)).Text(dropFrame: Rate == TimecodeRate.Fps2997DropFrame);
}
