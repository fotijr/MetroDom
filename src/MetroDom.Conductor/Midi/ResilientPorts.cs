using Melanchall.DryWetMidi.Core;
using MetroDom.Core;

namespace MetroDom.Conductor.Midi;

/// <summary>
/// Keeps one OS port open across unplug/replug. Every <see cref="PollInterval"/> it checks the
/// OS port list for the port's stable name: gone → close it and go silent; back → reopen.
/// Polling is needed because some OSes don't fail sends to a vanished port (ALSA drops them).
/// </summary>
/// <remarks>
/// Ports are matched by name, so two identical devices with the same name can't be told apart
/// (the first one listed wins).
/// </remarks>
internal abstract class ResilientPort<TPort> : IDisposable
    where TPort : class, IDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly Func<IReadOnlyList<MidiPortInfo>> _list;
    private readonly Func<MidiPortInfo, TPort> _open;
    private readonly Action<string> _log;
    private readonly ITimer _timer;
    private int _polling;
    private bool _disposed;

    protected ResilientPort(
        MidiPortInfo port,
        Func<IReadOnlyList<MidiPortInfo>> list,
        Func<MidiPortInfo, TPort> open,
        Action<string> log,
        TimeProvider? time,
        TimeSpan? pollInterval)
    {
        Name = port.Name;
        _list = list;
        _open = open;
        _log = log;
        Current = open(port);
        var interval = pollInterval ?? PollInterval;
        _timer = (time ?? TimeProvider.System).CreateTimer(_ => Poll(), null, interval, interval);
    }

    public string Name { get; }

    public bool IsConnected
    {
        get
        {
            lock (Gate)
            {
                return Current is not null;
            }
        }
    }

    protected Lock Gate { get; } = new();

    /// <summary>The open port, or null while unplugged. Guarded by <see cref="Gate"/>.</summary>
    protected TPort? Current { get; private set; }

    /// <summary>Checks whether the device left or came back. Runs on the timer; tests call it.</summary>
    internal void Poll()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1)
        {
            return;
        }

        try
        {
            IReadOnlyList<MidiPortInfo> ports;
            try
            {
                ports = _list();
            }
#pragma warning disable CA1031 // A failed listing must not change state or kill the timer.
            catch (Exception)
#pragma warning restore CA1031
            {
                return;
            }

            var match = ports.FirstOrDefault(p => p.Name == Name);
            lock (Gate)
            {
                if (_disposed)
                {
                    return;
                }

                if (Current is not null && match is null)
                {
                    Lose("unplugged");
                }
                else if (Current is null && match is not null)
                {
                    Reconnect(match);
                }
            }
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        lock (Gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Current?.Dispose();
            Current = null;
        }
    }

    /// <summary>Called under <see cref="Gate"/> right after reopening.</summary>
    protected abstract void OnReconnected(TPort port);

    /// <summary>Closes the port and goes silent. Call under <see cref="Gate"/>.</summary>
    protected void Lose(string reason)
    {
        var port = Current;
        Current = null;
        try
        {
            port?.Dispose();
        }
#pragma warning disable CA1031 // The device is already gone; closing it may fail too.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        _log($"MIDI {Name}: disconnected ({reason}). Silent until it comes back.");
    }

    private void Reconnect(MidiPortInfo match)
    {
        try
        {
            var port = _open(match);
            Current = port;
            OnReconnected(port);
            _log($"MIDI {Name}: reconnected.");
        }
#pragma warning disable CA1031 // Device may still be initializing; try again next poll.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Current?.Dispose();
            Current = null;
            _log($"MIDI {Name}: found but couldn't reopen ({ex.Message}). Retrying.");
        }
    }
}

/// <summary>
/// An output that survives unplugs. While the device is gone, events are dropped (fail silent:
/// the instrument's own watchdog has already released its actuators). On return it gets a
/// panic first, so it starts from silence, then normal events resume.
/// </summary>
internal sealed class ResilientOutput : ResilientPort<IMidiOutputPort>, IMidiOutputPort
{
    public ResilientOutput(
        IMidiBackend backend,
        MidiPortInfo port,
        Action<string> log,
        TimeProvider? time = null,
        TimeSpan? pollInterval = null)
        : base(port, backend.Outputs, backend.OpenOutput, log, time, pollInterval)
    {
    }

    public void Send(MidiEvent midiEvent)
    {
        lock (Gate)
        {
            if (Current is null)
            {
                return;
            }

            try
            {
                Current.Send(midiEvent);
            }
#pragma warning disable CA1031 // A dead device goes silent; it must not stop the band.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Lose(ex.Message);
            }
        }
    }

    protected override void OnReconnected(IMidiOutputPort port) => Orchestra.SendPanic(port);
}

/// <summary>An input (controller) that survives unplugs and resumes listening when it returns.</summary>
internal sealed class ResilientInput : ResilientPort<IMidiInputPort>, IMidiInputPort
{
    private bool _started;

    public ResilientInput(
        IMidiBackend backend,
        MidiPortInfo port,
        Action<string> log,
        TimeProvider? time = null,
        TimeSpan? pollInterval = null)
        : base(port, backend.Inputs, backend.OpenInput, log, time, pollInterval)
    {
        Current!.EventReceived += Forward;
    }

    public event Action<MidiEvent>? EventReceived;

    public void Start()
    {
        lock (Gate)
        {
            _started = true;
            Current?.Start();
        }
    }

    public void Stop()
    {
        lock (Gate)
        {
            _started = false;
            Current?.Stop();
        }
    }

    protected override void OnReconnected(IMidiInputPort port)
    {
        port.EventReceived += Forward;
        if (_started)
        {
            port.Start();
        }
    }

    private void Forward(MidiEvent midiEvent) => EventReceived?.Invoke(midiEvent);
}
