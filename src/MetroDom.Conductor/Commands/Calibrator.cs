using System.ComponentModel;
using System.Globalization;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using MetroDom.Conductor.Cli;
using MetroDom.Conductor.Midi;
using MetroDom.Core;
using MetroDom.Core.Arranging;
using MetroDom.Core.Calibration;
using MetroDom.Core.Timing;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MetroDom.Conductor.Commands;

/// <summary>
/// Where onsets come from (ADR 0004): a MIDI input that sends a Note On whenever it hears an
/// instrument sound, such as a piezo on a drum trigger module, a mic into a trigger-to-MIDI box,
/// or <c>metrodom virtual --onsets</c>. MIDI stays the contract (ADR 0001).
/// </summary>
internal sealed record OnsetSource(string Port, TapTrigger Trigger, TimeSpan Latency);

/// <summary>Listens to the onset input and hands each onset to the probe being run.</summary>
internal sealed class OnsetListener : IDisposable
{
    private readonly IMidiInputPort _input;
    private readonly IClock _clock;
    private readonly TapTrigger _trigger;
    private volatile LatencyProbe? _probe;

    public OnsetListener(IMidiInputPort input, TapTrigger trigger, IClock clock)
    {
        _input = input;
        _trigger = trigger;
        _clock = clock;
        _input.EventReceived += OnEvent;
        _input.Start();
    }

    public string Name => _input.Name;

    /// <summary>The run onsets go to; null between runs (onsets then are ignored).</summary>
    public LatencyProbe? Probe
    {
        get => _probe;
        set => _probe = value;
    }

    private void OnEvent(MidiEvent e)
    {
        var at = _clock.Now; // first: the time it arrived, before anything else runs
        if (_probe is { } probe && e is NoteOnEvent noteOn && noteOn.Velocity > 0 && _trigger.IsTap(e))
        {
            probe.Onset(at);
        }
    }

    public void Dispose()
    {
        _input.EventReceived -= OnEvent;
        _input.Dispose();
    }
}

/// <summary>Runs latency probes against real outputs, in real time.</summary>
internal static class Calibrator
{
    /// <summary>
    /// Strikes <paramref name="notes"/> on <paramref name="output"/> (through its safety governor,
    /// on <paramref name="channel"/>) and times each onset. Stops early, with what it has, when
    /// <paramref name="stop"/> says so (it waits on it, so Ctrl+C is answered at once).
    /// </summary>
    public static CalibrationResult Measure(
        IMidiOutput output, int channel, IReadOnlyList<int> notes, CalibrationOptions options, OnsetListener onsets, IClock clock, Func<TimeSpan, bool> stop)
    {
        var probe = new LatencyProbe(Profile(output), notes, options, clock.Now);
        onsets.Probe = probe;
        int? sounding = null;
        try
        {
            while (!probe.IsDone(clock.Now))
            {
                if (probe.Due(clock.Now) is { } next)
                {
                    Release(output, channel, ref sounding);
                    output.Send(new NoteOnEvent((SevenBitNumber)next.Note, (SevenBitNumber)next.Velocity) { Channel = (FourBitNumber)channel });
                    sounding = next.Note;
                    continue;
                }

                if (probe.NextAt is not { } at)
                {
                    break;
                }

                // Sleep most of the way (answering a stop at once), then spin the last millisecond.
                var left = at - clock.Now;
                if (left > TimeSpan.FromMilliseconds(2))
                {
                    if (stop(left - TimeSpan.FromMilliseconds(1)))
                    {
                        break;
                    }
                }
                else if (left > TimeSpan.Zero)
                {
                    Thread.SpinWait(50);
                }
            }
        }
        finally
        {
            Release(output, channel, ref sounding);
            onsets.Probe = null;
        }

        return probe.Result();
    }

    private static InstrumentProfile Profile(IMidiOutput output) =>
        output is Core.Safety.SafetyGovernor governor ? governor.Profile : new InstrumentProfile(output.Name, TimeSpan.Zero);

    private static void Release(IMidiOutput output, int channel, ref int? sounding)
    {
        if (sounding is { } note)
        {
            output.Send(new NoteOffEvent((SevenBitNumber)note, SevenBitNumber.MinValue) { Channel = (FourBitNumber)channel });
            sounding = null;
        }
    }

    /// <summary>Opens the onset input named by <paramref name="source"/>.</summary>
    public static OnsetListener Listen(OnsetSource source, IClock clock) =>
        new(PortPicker.Input(source.Port, "--onsets"), source.Trigger, clock);

    /// <summary>"Measured … 12.4 ms (spread 1.1, 0 missed)" for one run.</summary>
    public static string Describe(CalibrationResult result) =>
        result.Latency is { } latency
            ? string.Create(CultureInfo.InvariantCulture,
                $"{latency.TotalMilliseconds:0.#} ms (spread up to {result.Notes.Max(n => n.Spread).TotalMilliseconds:0.#} ms, {result.Notes.Sum(n => n.Missed)} of {result.Notes.Sum(n => n.Missed + n.Latencies.Count)} strikes not heard{Noise(result)})")
            : $"nothing heard{Noise(result)}";

    private static string Noise(CalibrationResult result) =>
        (result.Strays, result.Echoes) switch
        {
            (0, 0) => "",
            var (s, e) => string.Create(CultureInfo.InvariantCulture, $", {s} stray and {e} double onsets"),
        };

    /// <summary>A one-line verdict for the boot check.</summary>
    public static string Describe(string name, LatencyCheck check) => check.Status switch
    {
        CheckStatus.Ok => string.Create(CultureInfo.InvariantCulture,
            $"{name}: latency check ok (within {check.Worst?.Delta.Duration().TotalMilliseconds ?? 0:0.#} ms of its calibration)."),
        CheckStatus.Drifted => string.Create(CultureInfo.InvariantCulture,
            $"{name}: latency drifted: note {check.Worst!.Note} sounds at {check.Worst.Measured.TotalMilliseconds:0.#} ms, the band plays it for {check.Worst.Stored.TotalMilliseconds:0.#} ms. Run metrodom calibrate."),
        CheckStatus.Partial => $"{name}: latency check heard nothing from note(s) {string.Join(", ", check.Silent)}: check those actuators and the onset input.",
        _ => $"{name}: latency check heard nothing: is it powered, and can the onset input hear it?",
    };
}

/// <summary>Where measured latencies live: a JSON file per user, next to the remembered assignments.</summary>
internal static class CalibrationStore
{
    public const string PathVariable = "METRODOM_CALIBRATIONS";

    /// <summary>The file to use: <paramref name="path"/>, else <c>$METRODOM_CALIBRATIONS</c>, else the user's app data folder.</summary>
    public static string Resolve(string? path) =>
        path
        ?? (Environment.GetEnvironmentVariable(PathVariable) is { Length: > 0 } fromEnvironment ? fromEnvironment
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify), "metrodom", "calibrations.json"));

    /// <summary>Reads the book. A missing file is an empty book; a broken one is reported and treated as empty.</summary>
    public static CalibrationBook Load(string path, Action<string>? warn = null)
    {
        if (!File.Exists(path))
        {
            return new CalibrationBook();
        }

        if (CalibrationBook.TryParse(File.ReadAllText(path), out var book, out var error))
        {
            return book;
        }

        warn?.Invoke($"Ignoring the calibrations in {path}: {error}");
        return new CalibrationBook();
    }

    /// <summary>Writes the book, replacing the file in one step.</summary>
    public static void Save(CalibrationBook book, string path)
    {
        ArgumentNullException.ThrowIfNull(book);
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(folder);
        var temp = Path.Combine(folder, $".{Path.GetFileName(path)}.{Environment.ProcessId}.tmp");
        File.WriteAllText(temp, book.ToJson());
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>The quick latency check before a set (ADR 0004): flags drift, changes nothing.</summary>
internal static class LatencyBootCheck
{
    /// <summary>
    /// Checks every output in turn and records the verdicts on the session (shown in the remote and
    /// the API). Returns them by output name. A failing onset input is a warning, never a stop:
    /// the band can still play.
    /// </summary>
    public static IReadOnlyDictionary<string, LatencyCheck> Run(Session session, OnsetSource source, CalibrationOptions? options = null)
    {
        options = (options ?? CalibrationOptions.Quick) with { OnsetLatency = source.Latency };
        var clock = new SystemClock();
        OnsetListener listener;
        try
        {
            listener = Calibrator.Listen(source, clock);
        }
        catch (Exception ex) when (ex is MidiDeviceException or UsageException)
        {
            session.Messages.Add($"Latency check skipped: {ex.Message}");
            return new Dictionary<string, LatencyCheck>();
        }

        using (listener)
        {
            return Run(session, listener, clock, options);
        }
    }

    internal static IReadOnlyDictionary<string, LatencyCheck> Run(Session session, OnsetListener listener, IClock clock, CalibrationOptions options)
    {
        var checks = new Dictionary<string, LatencyCheck>(StringComparer.Ordinal);
        var outputs = session.Outputs;
        var governors = session.Governors;
        var total = outputs.Select((o, i) => new LatencyProbe(governors[i].Profile, Notes(o), options, TimeSpan.Zero).Duration).Aggregate(TimeSpan.Zero, (a, b) => a + b);
        Ui.Note(string.Create(CultureInfo.InvariantCulture, $"Latency check: listening on {listener.Name}, about {total.TotalSeconds:0} s."));
        for (var i = 0; i < outputs.Count && !session.IsStopping; i++)
        {
            var result = Calibrator.Measure(governors[i], outputs[i].Channels[0], Notes(outputs[i]), options, listener, clock, session.WaitForStop);
            if (session.IsStopping)
            {
                break;
            }

            var check = LatencyCheck.Compare(result, governors[i].Profile, options.DriftThreshold);
            checks[outputs[i].Port.Name] = check;
            session.SetLatencyCheck(outputs[i].Port.Name, check);
            var line = Calibrator.Describe(outputs[i].Port.Name, check);
            if (check.Status == CheckStatus.Ok)
            {
                Ui.Note(line);
            }
            else
            {
                session.Messages.Add(line);
                if (Ui.IsInteractive)
                {
                    Ui.Warn(line);
                }
            }
        }

        return checks;
    }

    internal static IReadOnlyList<int> Notes(SessionOutput output) =>
        LatencyProbe.QuickNotes(output.Profile, output.Channels[0] == GeneralMidi.PercussionChannel);
}

/// <summary>The onset options <c>calibrate</c>, <c>play</c> and <c>serve</c> share.</summary>
internal static class OnsetOptions
{
    public const string OnsetsHelp = "MIDI input that hears the instruments: a Note On for every onset (a piezo on a drum trigger module, a mic into a trigger-to-MIDI box, or [bold]metrodom virtual --onsets[/]).";
    public const string OnHelp = "Which messages from [bold]--onsets[/] are onsets, as [bold]--tap-on[/] (default [bold]any[/] Note On).";
    public const string LatencyHelp = "The onset input's own delay in ms (trigger scan, detection), taken off every measurement. Default 0.";

    public static OnsetSource? Source(string? port, string? on, double? latencyMs) =>
        port is null ? null : new OnsetSource(port, TapTrigger.TryParse(on, out var trigger, out _) ? trigger : new TapTrigger(), TimeSpan.FromMilliseconds(latencyMs ?? 0));

    public static string? Validate(string? port, string? on, double? latencyMs)
    {
        if (!TapTrigger.TryParse(on, out _, out var error))
        {
            return $"--onsets-on: {error}";
        }

        if (on is not null && port is null)
        {
            return "--onsets-on needs --onsets <PORT>.";
        }

        if (latencyMs is < 0 or > 200)
        {
            return "--onset-latency must be 0-200 ms.";
        }

        return latencyMs is not null && port is null ? "--onset-latency needs --onsets <PORT>." : null;
    }
}
