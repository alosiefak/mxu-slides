using System.Threading.Channels;

namespace MxU.Slides.LocalApi;

/// <summary>A WebSocket channel. Each pushes the same shape as its REST snapshot.</summary>
public enum ApiTopic { Show, Timers, Audio, Transport, Outputs, Library, Scheduler, Timecode }

public static class ApiTopics
{
    public static IReadOnlyList<ApiTopic> All { get; } = Enum.GetValues<ApiTopic>();

    public static string Raw(this ApiTopic topic) => topic.ToString().ToLowerInvariant();

    public static ApiTopic? Parse(string raw) =>
        All.Where(t => t.Raw() == raw).Select(t => (ApiTopic?)t).FirstOrDefault();
}

/// <summary>A state push: <paramref name="Payload"/> is the topic's snapshot as JSON.</summary>
public sealed record ApiEvent(ApiTopic Topic, byte[] Payload);

/// <summary>
/// Fans events out to every connected socket. Each subscriber keeps only the newest 16
/// events, so a slow client never holds up the show. Thread-safe. Port of APIEventBus.
/// </summary>
public sealed class ApiEventBus
{
    private readonly Lock gate = new();
    private readonly List<Channel<ApiEvent>> subscribers = [];

    public ApiEventSubscription Subscribe()
    {
        var channel = Channel.CreateBounded<ApiEvent>(new BoundedChannelOptions(16)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        lock (gate) subscribers.Add(channel);
        return new ApiEventSubscription(channel.Reader, () =>
        {
            lock (gate) subscribers.Remove(channel);
            channel.Writer.TryComplete();
        });
    }

    public void Publish(ApiEvent e)
    {
        lock (gate)
            foreach (var subscriber in subscribers) subscriber.Writer.TryWrite(e);
    }

    public void CloseAll()
    {
        lock (gate)
        {
            foreach (var subscriber in subscribers) subscriber.Writer.TryComplete();
            subscribers.Clear();
        }
    }
}

public sealed class ApiEventSubscription(ChannelReader<ApiEvent> reader, Action unsubscribe) : IDisposable
{
    public ChannelReader<ApiEvent> Reader { get; } = reader;

    public void Dispose() => unsubscribe();
}
