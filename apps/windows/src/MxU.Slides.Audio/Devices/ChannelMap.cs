namespace MxU.Slides.Audio;

/// <summary>
/// Which device channel each of a bus's channels lands on. A bus is stereo (or mono);
/// a 16-channel interface has 16 outputs, and the operator picks the pair (or the one
/// channel) it plays on. Port of the channel-map maths in AudioEngine.applyChannelMap.
/// </summary>
public static class ChannelMap
{
    /// <summary>
    /// For each device channel, the bus channel it plays (or -1 for silence). An offset
    /// that doesn't fit the device falls back to the first pair, like the Mac.
    /// </summary>
    public static int[] Make(int deviceChannels, int channelOffset, bool mono)
    {
        var channels = Math.Max(2, deviceChannels);
        var offset = channelOffset >= 0 && channelOffset + (mono ? 0 : 1) < channels ? channelOffset : 0;
        var map = new int[channels];
        Array.Fill(map, -1);
        map[offset] = 0;
        if (!mono) map[offset + 1] = 1;
        return map;
    }

    /// <summary>Every device channel plays the source channel of the same number.</summary>
    public static int[] Identity(int channels) => Enumerable.Range(0, Math.Max(1, channels)).ToArray();
}
