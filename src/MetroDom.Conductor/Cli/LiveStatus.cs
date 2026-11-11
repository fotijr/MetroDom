using System.Globalization;
using Melanchall.DryWetMidi.Core;
using MetroDom.Conductor.Commands;
using MetroDom.Core;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace MetroDom.Conductor.Cli;

/// <summary>
/// Per-channel note activity, recorded from MIDI threads without locks or allocations
/// (timing-sensitive path, see docs/RELIABILITY.md) and read by the status view.
/// </summary>
internal sealed class ChannelActivity
{
    private readonly long[] _notes = new long[Orchestra.ChannelCount];
    private readonly long[] _lastNoteAt = new long[Orchestra.ChannelCount];

    public void Record(MidiEvent midiEvent)
    {
        if (midiEvent is NoteOnEvent note && note.Velocity > 0)
        {
            Interlocked.Increment(ref _notes[note.Channel]);
            Volatile.Write(ref _lastNoteAt[note.Channel], Environment.TickCount64);
        }
    }

    public long Notes(int channel) => Interlocked.Read(ref _notes[channel]);

    /// <summary>True if a note started on <paramref name="channel"/> within the last <paramref name="window"/>.</summary>
    public bool IsLit(int channel, TimeSpan window)
    {
        var at = Volatile.Read(ref _lastNoteAt[channel]);
        return at != 0 && Environment.TickCount64 - at < window.TotalMilliseconds;
    }
}

/// <summary>Recent warnings (reconnects, failed sends) shown in the status panel.</summary>
internal sealed class MessageLog
{
    private const int Capacity = 4;
    private readonly Queue<string> _messages = new();
    private readonly Lock _gate = new();

    public void Add(string message)
    {
        var stamped = $"{DateTime.Now:HH:mm:ss}  {message}";
        lock (_gate)
        {
            _messages.Enqueue(stamped);
            while (_messages.Count > Capacity)
            {
                _messages.Dequeue();
            }
        }
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
        {
            return _messages.ToArray();
        }
    }
}

/// <summary>
/// Renders the play/route status panel: what's playing where, progress, a 16-channel activity
/// strip (lit on note-on, M when muted), connection state, and recent warnings.
/// </summary>
internal static class LiveStatus
{
    private static readonly TimeSpan LitWindow = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan Refresh = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Shows <paramref name="render"/> until <paramref name="session"/> stops. In a terminal it
    /// redraws in place; when piped it just waits, so logs and scripts see plain lines.
    /// </summary>
    public static void Run(Session session, Func<IRenderable> render)
    {
        if (!Ui.IsInteractive)
        {
            session.WaitForStop();
            return;
        }

        session.UseLog(session.Messages.Add);
        try
        {
            AnsiConsole.Live(render())
                .AutoClear(false)
                .Overflow(VerticalOverflow.Ellipsis)
                .Start(ctx =>
                {
                    while (!session.WaitForStop(Refresh))
                    {
                        ctx.UpdateTarget(render());
                    }

                    ctx.UpdateTarget(render());
                });
        }
        finally
        {
            session.UseLog(Ui.Warn);
        }
    }

    public static IRenderable Panel(string title, Session session, IRenderable? progress)
    {
        var rows = new List<IRenderable>
        {
            new Markup($"[grey]Output[/]  {Markup.Escape(session.OutputName)}  {Connection(session.IsOutputConnected)}"),
        };
        if (progress is not null)
        {
            rows.Add(progress);
        }

        rows.Add(Channels(session));
        foreach (var message in session.Messages.Snapshot())
        {
            rows.Add(new Markup($"[yellow]{Markup.Escape(message)}[/]"));
        }

        rows.Add(new Markup(session.IsStopping
            ? "[red]Stopping…[/]"
            : "[grey]Ctrl+C to stop (sends panic)[/]"));

        return new Panel(new Rows(rows))
            .Header($" {title} ", Justify.Left)
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey);
    }

    public static IRenderable Progress(TimeSpan position, TimeSpan duration)
    {
        const int width = 40;
        var fraction = duration > TimeSpan.Zero ? Math.Clamp(position / duration, 0, 1) : 0;
        var filled = (int)Math.Round(fraction * width);
        return new Markup(string.Create(
            CultureInfo.InvariantCulture,
            $"[green]{new string('━', filled)}[/][grey]{new string('━', width - filled)}[/]  {position:m\\:ss} / {duration:m\\:ss}"));
    }

    private static string Connection(bool connected) =>
        connected ? "[green]● connected[/]" : "[yellow]○ waiting for device…[/]";

    private static Grid Channels(Session session)
    {
        var grid = new Grid();
        grid.AddColumn(new GridColumn().NoWrap().PadRight(1));
        for (var c = 0; c < Orchestra.ChannelCount; c++)
        {
            grid.AddColumn(new GridColumn().Centered().NoWrap().Width(3).Padding(0, 0));
        }

        var numbers = new List<IRenderable> { new Markup("[grey]ch[/]") };
        var lights = new List<IRenderable> { new Markup("[grey]  [/]") };
        var counts = new List<IRenderable> { new Markup("[grey]notes[/]") };
        for (var c = 0; c < Orchestra.ChannelCount; c++)
        {
            numbers.Add(new Markup($"[grey]{c + 1}[/]"));
            lights.Add(new Markup(session.Orchestra.IsMuted(c)
                ? "[red]M[/]"
                : session.Activity.IsLit(c, LitWindow) ? "[bold green]●[/]" : "[grey]·[/]"));
            counts.Add(new Markup($"[grey]{Compact(session.Activity.Notes(c))}[/]"));
        }

        grid.AddRow([.. numbers]);
        grid.AddRow([.. lights]);
        grid.AddRow([.. counts]);
        return grid;
    }

    private static string Compact(long n) => n switch
    {
        0 => "",
        < 1000 => n.ToString(CultureInfo.InvariantCulture),
        < 10_000 => string.Create(CultureInfo.InvariantCulture, $"{n / 1000.0:0.#}k"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{n / 1000}k"),
    };
}
