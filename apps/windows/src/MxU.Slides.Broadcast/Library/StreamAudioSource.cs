using MxU.Slides.Core.Model;

namespace MxU.Slides.Broadcast.Library;

public enum StreamAudioSourceKind { Program, Mix, Input }

/// <summary>
/// Where a stream preset's sound comes from: the program audio, a named mix, or one input.
/// Applying a choice to a preset clears the fields of the other kinds; "include program"
/// only applies to mixes and inputs. <see cref="StorageValue"/> is the picker's tag ("",
/// "mix:id", "input:id"). Port of StreamAudioSource.swift.
/// </summary>
public readonly record struct StreamAudioSource(StreamAudioSourceKind Kind, string Id = "")
{
    public static StreamAudioSource Program => new(StreamAudioSourceKind.Program);

    public static StreamAudioSource Mix(string id) => new(StreamAudioSourceKind.Mix, id);

    public static StreamAudioSource Input(string id) => new(StreamAudioSourceKind.Input, id);

    public void ApplyTo(StreamRecordPreset preset, bool includingProgram)
    {
        switch (Kind)
        {
            case StreamAudioSourceKind.Program:
                preset.AudioMixId = null;
                preset.AudioInputId = null;
                preset.AudioInputUid = null;
                preset.AudioIncludesProgram = null;
                break;
            case StreamAudioSourceKind.Mix:
                preset.AudioMixId = Id;
                preset.AudioInputId = null;
                preset.AudioInputUid = null;
                preset.AudioIncludesProgram = includingProgram ? true : null;
                break;
            case StreamAudioSourceKind.Input:
                preset.AudioMixId = null;
                preset.AudioInputId = Id;
                preset.AudioInputUid = null;
                preset.AudioIncludesProgram = includingProgram ? true : null;
                break;
        }
    }

    public string StorageValue => Kind switch
    {
        StreamAudioSourceKind.Mix => $"mix:{Id}",
        StreamAudioSourceKind.Input => $"input:{Id}",
        _ => "",
    };

    public static StreamAudioSource FromStorageValue(string storageValue)
    {
        if (storageValue.StartsWith("mix:", StringComparison.Ordinal) && storageValue.Length > 4)
            return Mix(storageValue[4..]);
        if (storageValue.StartsWith("input:", StringComparison.Ordinal) && storageValue.Length > 6)
            return Input(storageValue[6..]);
        return Program;
    }

    /// <summary>The source a preset is set to (a mix wins over an input, as the Mac's audio feed resolves them).</summary>
    public static StreamAudioSource Of(StreamRecordPreset preset)
    {
        if (!string.IsNullOrEmpty(preset.AudioMixId)) return Mix(preset.AudioMixId);
        if (!string.IsNullOrEmpty(preset.AudioInputId)) return Input(preset.AudioInputId);
        return Program;
    }
}
