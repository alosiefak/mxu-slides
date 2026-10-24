using System.Diagnostics;
using System.Runtime.InteropServices;
using MxU.Slides.Broadcast.Media.Interop;

namespace MxU.Slides.Broadcast.Media;

/// <summary>Which kind of encoder to use for streams.</summary>
public enum EncoderPreference
{
    /// <summary>A graphics-card encoder when there is one that works, otherwise Microsoft's software encoder.</summary>
    Automatic,

    /// <summary>Always the software encoder (slower, but identical on every PC, including CI servers).</summary>
    Software,
}

/// <summary>
/// Drives one Media Foundation transform (MFT) — an encoder — through its input/output
/// protocol. Software encoders are "synchronous": hand in a sample, then ask for output until
/// it says it needs more input. Hardware encoders are usually "asynchronous": they announce
/// with events when they want input and when output is ready; here those events are read
/// (without waiting) each time a sample goes in, so output always arrives on the caller's
/// thread, at most one frame late. Not thread-safe: one caller at a time.
/// </summary>
internal sealed class MftEncoder : IDisposable
{
    private readonly IMFTransform transform;
    private readonly IMFMediaEventGenerator? events;
    private readonly uint inputStream;
    private readonly uint outputStream;
    private readonly Queue<IMFSample> waiting = new();
    private MFT_OUTPUT_STREAM_INFO outputInfo;
    private int needInput;
    private bool streaming;
    private bool disposed;

    public MftEncoder(IMFTransform transform, string name, bool isHardware)
    {
        this.transform = transform;
        Name = name;
        IsHardware = isHardware;
        if (transform.GetAttributes(out var attributes) >= 0)
        {
            try
            {
                if (attributes.GetUINT32(MF.MF_TRANSFORM_ASYNC, out var async) >= 0 && async != 0)
                {
                    MF.Check(attributes.SetUINT32(MF.MF_TRANSFORM_ASYNC_UNLOCK, 1), "unlock async encoder");
                    attributes.SetUINT32(MF.MF_LOW_LATENCY, 1);
                    events = (IMFMediaEventGenerator)transform;
                }
            }
            finally
            {
                MediaFoundation.Release(attributes);
            }
        }
        var inputs = new uint[1];
        var outputs = new uint[1];
        if (transform.GetStreamIDs(1, inputs, 1, outputs) >= 0)
        {
            inputStream = inputs[0];
            outputStream = outputs[0];
        }
        Codec = transform as ICodecAPI;
    }

    public string Name { get; }

    public bool IsHardware { get; }

    public ICodecAPI? Codec { get; }

    public IMFTransform Transform => transform;

    public uint InputStream => inputStream;

    public uint OutputStream => outputStream;

    /// <summary>Sets an encoder property if the encoder has it; returns false when it doesn't.</summary>
    public bool TrySet(Guid property, ComVariantValue value)
    {
        if (Codec is null) return false;
        if (Codec.IsSupported(property) != MF.S_OK) return false;
        return Codec.SetValue(property, value) >= 0;
    }

    /// <summary>Call once the media types are set.</summary>
    public void BeginStreaming()
    {
        MF.Check(transform.GetOutputStreamInfo(outputStream, out outputInfo), "GetOutputStreamInfo");
        MF.Check(transform.ProcessMessage(MF.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0), "begin streaming");
        MF.Check(transform.ProcessMessage(MF.MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0), "start of stream");
        streaming = true;
    }

    /// <summary>Hands in one sample (taking ownership of it) and returns whatever output is ready.</summary>
    public List<IMFSample> Process(IMFSample sample)
    {
        var outputs = new List<IMFSample>();
        if (events is null)
        {
            var hr = transform.ProcessInput(inputStream, sample, 0);
            if (hr == MF.MF_E_NOTACCEPTING)
            {
                DrainOutput(outputs);
                hr = transform.ProcessInput(inputStream, sample, 0);
            }
            MediaFoundation.Release(sample);
            MF.Check(hr, $"{Name}: ProcessInput");
            DrainOutput(outputs);
        }
        else
        {
            waiting.Enqueue(sample);
            PumpEvents(outputs, wait: TimeSpan.Zero);
            // Give a busy encoder a moment to ask for more, but never hoard pictures: an
            // encoder that stops asking for input loses the oldest instead.
            var clock = Stopwatch.StartNew();
            while (waiting.Count > 2 && clock.Elapsed < TimeSpan.FromMilliseconds(250))
                PumpEvents(outputs, TimeSpan.FromMilliseconds(2));
            while (waiting.Count > 2)
            {
                MediaFoundation.Release(waiting.Dequeue());
                DroppedInputs++;
            }
        }
        return outputs;
    }

    /// <summary>Pictures an asynchronous encoder never asked for, so they were thrown away.</summary>
    public int DroppedInputs { get; private set; }

    /// <summary>Asks the encoder to finish the pictures it holds and returns them.</summary>
    public List<IMFSample> Drain()
    {
        var outputs = new List<IMFSample>();
        if (!streaming) return outputs;
        if (events is null)
        {
            transform.ProcessMessage(MF.MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0);
            if (transform.ProcessMessage(MF.MFT_MESSAGE_COMMAND_DRAIN, 0) >= 0) DrainOutput(outputs);
        }
        else
        {
            // Feed what is still waiting, then drain and collect until the encoder says it's done.
            var clock = Stopwatch.StartNew();
            while (waiting.Count > 0 && clock.Elapsed < TimeSpan.FromSeconds(1)) PumpEvents(outputs, TimeSpan.FromMilliseconds(5));
            transform.ProcessMessage(MF.MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0);
            if (transform.ProcessMessage(MF.MFT_MESSAGE_COMMAND_DRAIN, 0) >= 0)
            {
                clock.Restart();
                while (!PumpEvents(outputs, TimeSpan.FromMilliseconds(5)) && clock.Elapsed < TimeSpan.FromSeconds(2)) { }
            }
            // Pictures the encoder never asked for in time are lost; count them.
            DroppedInputs += waiting.Count;
            while (waiting.Count > 0) MediaFoundation.Release(waiting.Dequeue());
        }
        return outputs;
    }

    /// <summary>Reads the async encoder's pending events; true once "drain complete" is seen.</summary>
    private bool PumpEvents(List<IMFSample> outputs, TimeSpan wait)
    {
        var drained = false;
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            var hr = events!.GetEvent(MF.MF_EVENT_FLAG_NO_WAIT, out var e);
            if (hr == MF.MF_E_NO_EVENTS_AVAILABLE)
            {
                if (deadline.Elapsed >= wait || drained) return drained;
                Thread.Sleep(1);
                continue;
            }
            MF.Check(hr, $"{Name}: GetEvent");
            try
            {
                e.GetEventType(out var type);
                e.GetStatus(out var status);
                MF.Check(status, $"{Name}: encoder event {type}");
                switch (type)
                {
                    case MF.METransformNeedInput:
                        needInput++;
                        break;
                    case MF.METransformHaveOutput:
                        ProcessOneOutput(outputs);
                        break;
                    case MF.METransformDrainComplete:
                        drained = true;
                        break;
                }
            }
            finally
            {
                MediaFoundation.Release(e);
            }
            while (needInput > 0 && waiting.Count > 0)
            {
                var next = waiting.Dequeue();
                var input = transform.ProcessInput(inputStream, next, 0);
                MediaFoundation.Release(next);
                MF.Check(input, $"{Name}: ProcessInput");
                needInput--;
            }
        }
    }

    private void DrainOutput(List<IMFSample> outputs)
    {
        while (ProcessOneOutput(outputs)) { }
    }

    /// <summary>Asks for one output sample; false when the encoder needs more input first.</summary>
    private bool ProcessOneOutput(List<IMFSample> outputs)
    {
        var providesSamples = (outputInfo.dwFlags & (MF.MFT_OUTPUT_STREAM_PROVIDES_SAMPLES | MF.MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES)) != 0;
        IMFSample? ours = null;
        var buffer = new MFT_OUTPUT_DATA_BUFFER { dwStreamID = outputStream };
        if (!providesSamples)
        {
            ours = MediaFoundation.CreateOutputSample((int)Math.Max(outputInfo.cbSize, 1024 * 1024u), (int)outputInfo.cbAlignment);
            buffer.pSample = Marshal.GetComInterfaceForObject<IMFSample, IMFSample>(ours);
        }
        int hr;
        try
        {
            hr = transform.ProcessOutput(0, 1, ref buffer, out _);
        }
        finally
        {
            if (buffer.pEvents != 0) Marshal.Release(buffer.pEvents);
        }

        if (hr == MF.MF_E_TRANSFORM_NEED_MORE_INPUT)
        {
            ReleaseOutput(ours, ref buffer);
            return false;
        }
        if (hr == MF.MF_E_TRANSFORM_STREAM_CHANGE)
        {
            ReleaseOutput(ours, ref buffer);
            // The encoder changed its output format (some hardware encoders do on the first
            // frame): accept the new one and carry on.
            MF.Check(transform.GetOutputAvailableType(outputStream, 0, out var type), $"{Name}: new output type");
            try { MF.Check(transform.SetOutputType(outputStream, type, 0), $"{Name}: set new output type"); }
            finally { MediaFoundation.Release(type); }
            MF.Check(transform.GetOutputStreamInfo(outputStream, out outputInfo), "GetOutputStreamInfo");
            return true;
        }
        if (hr < 0)
        {
            ReleaseOutput(ours, ref buffer);
            MF.Check(hr, $"{Name}: ProcessOutput");
        }

        if (ours is not null)
        {
            Marshal.Release(buffer.pSample);
            outputs.Add(ours);
        }
        else if (buffer.pSample != 0)
        {
            var provided = (IMFSample)Marshal.GetObjectForIUnknown(buffer.pSample);
            Marshal.Release(buffer.pSample);
            outputs.Add(provided);
        }
        return true;
    }

    private static void ReleaseOutput(IMFSample? ours, ref MFT_OUTPUT_DATA_BUFFER buffer)
    {
        if (ours is not null)
        {
            Marshal.Release(buffer.pSample);
            MediaFoundation.Release(ours);
        }
        else if (buffer.pSample != 0)
        {
            Marshal.Release(buffer.pSample);
        }
        buffer.pSample = 0;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        while (waiting.Count > 0) MediaFoundation.Release(waiting.Dequeue());
        try
        {
            if (streaming)
            {
                transform.ProcessMessage(MF.MFT_MESSAGE_COMMAND_FLUSH, 0);
                transform.ProcessMessage(MF.MFT_MESSAGE_NOTIFY_END_STREAMING, 0);
            }
        }
        catch (COMException)
        {
        }
        // Hardware encoders hold driver resources until shut down through their activator,
        // which the creator does; here we drop our reference.
        if (Marshal.IsComObject(transform)) Marshal.FinalReleaseComObject(transform);
    }
}
