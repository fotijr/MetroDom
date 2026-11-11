using System.Text.Json;
using System.Text.Json.Nodes;
using Melanchall.DryWetMidi.Core;
using MetroDom.Core.Timing;

namespace MetroDom.Core.Policy;

/// <summary>What a foot controller (or any MIDI control surface) can ask the band to do (ADR 0012 §4).</summary>
public enum ControlAction
{
    /// <summary>Start the song when waiting; stop it when playing.</summary>
    StartStop,

    /// <summary>Go to the next song in the setlist now.</summary>
    Next,

    /// <summary>One tap of tap tempo.</summary>
    Tap,

    /// <summary>Toggle vamp on the current section.</summary>
    Vamp,

    /// <summary>Go to the outro when the current section ends.</summary>
    Outro,

    /// <summary>Stop every actuator now.</summary>
    Panic,
}

/// <summary>
/// A deployment mode's policy (ADR 0005), first cut: only the control-surface mapping exists.
/// Format <c>metrodom.policy/0</c>, see docs/references/policy-profile.md. Roles, queue rules and
/// limits join it as they're built.
/// </summary>
/// <param name="Mode">"live-band", "crowd-jukebox", "rental", or the owner's own name.</param>
/// <param name="Controls">Which MIDI message triggers each action. Unmapped actions are absent.</param>
public sealed record PolicyProfile(string Mode, IReadOnlyDictionary<ControlAction, string> Controls)
{
    public const string Schema = "metrodom.policy/0";

    private static readonly Dictionary<string, ControlAction> ActionNames = new(StringComparer.Ordinal)
    {
        ["start-stop"] = ControlAction.StartStop,
        ["next"] = ControlAction.Next,
        ["tap"] = ControlAction.Tap,
        ["vamp"] = ControlAction.Vamp,
        ["outro"] = ControlAction.Outro,
        ["panic"] = ControlAction.Panic,
    };

    /// <summary>
    /// The shipped <c>live-band</c> profile (also <c>policies/live-band.json</c>): a sustain pedal
    /// taps (CC 64), and five switches on CC 80-83 and 85 (General Purpose controllers, unused by
    /// General MIDI sounds), as the planned <c>footswitch-midi</c> module will send.
    /// </summary>
    public static PolicyProfile LiveBand { get; } = new("live-band", new Dictionary<ControlAction, string>
    {
        [ControlAction.StartStop] = "cc:80",
        [ControlAction.Next] = "cc:81",
        [ControlAction.Vamp] = "cc:82",
        [ControlAction.Outro] = "cc:83",
        [ControlAction.Panic] = "cc:85",
        [ControlAction.Tap] = "cc:64",
    });

    /// <summary>The file name of an action: "start-stop".</summary>
    public static string NameOf(ControlAction action) => ActionNames.First(a => a.Value == action).Key;

    /// <summary>Reads a profile, or returns false with a reason a person can act on.</summary>
    public static bool TryParse(string json, out PolicyProfile profile, out string? error)
    {
        profile = LiveBand;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root)
            {
                error = "not a JSON object.";
                return false;
            }

            if (root["schema"]?.GetValue<string>() != Schema)
            {
                error = $"\"schema\" must be {Schema}.";
                return false;
            }

            var mode = root["mode"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(mode))
            {
                error = "missing \"mode\".";
                return false;
            }

            var controls = new Dictionary<ControlAction, string>();
            foreach (var (name, value) in root["controls"] as JsonObject ?? [])
            {
                if (!ActionNames.TryGetValue(name, out var action))
                {
                    error = $"unknown control '{name}'. Known: {string.Join(", ", ActionNames.Keys)}.";
                    return false;
                }

                var trigger = value?.GetValue<string>();
                if (!TapTrigger.TryParse(trigger, out var parsed, out var triggerError) || trigger is null || parsed.Kind == TapKind.Any)
                {
                    error = $"control '{name}': {triggerError ?? "needs note[:N], cc[:N] or pc[:N], not 'any'."}";
                    return false;
                }

                controls[action] = parsed.ToString();
            }

            if (controls.GroupBy(c => c.Value).FirstOrDefault(g => g.Count() > 1) is { } clash)
            {
                error = $"'{clash.Key}' is mapped to both {string.Join(" and ", clash.Select(c => NameOf(c.Key)))}.";
                return false;
            }

            profile = new PolicyProfile(mode.Trim(), controls);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            error = $"not a valid policy file: {ex.Message}";
            return false;
        }
    }

    /// <summary>The profile as JSON, controls in a fixed order.</summary>
    public string ToJson()
    {
        var controls = new JsonObject();
        foreach (var (name, action) in ActionNames)
        {
            if (Controls.TryGetValue(action, out var trigger))
            {
                controls[name] = trigger;
            }
        }

        return new JsonObject { ["schema"] = Schema, ["mode"] = Mode, ["controls"] = controls }
            .ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    /// <summary>A fresh matcher for these controls (it tracks pedal up/down, so one per input).</summary>
    public ControlMap NewControlMap() => new(Controls);
}

/// <summary>
/// Turns incoming MIDI into <see cref="ControlAction"/>s. Each control is a <see cref="TapTrigger"/>,
/// so a pedal press fires once and its release never does. Not thread-safe: one input thread, or a lock.
/// </summary>
public sealed class ControlMap
{
    private readonly (ControlAction Action, TapTrigger Trigger)[] _triggers;

    public ControlMap(IReadOnlyDictionary<ControlAction, string> controls)
    {
        ArgumentNullException.ThrowIfNull(controls);
        _triggers = [.. controls.Select(c => (c.Key, TapTrigger.TryParse(c.Value, out var t, out var error)
            ? t
            : throw new ArgumentException($"{PolicyProfile.NameOf(c.Key)}: {error}", nameof(controls))))];
    }

    /// <summary>The actions <paramref name="midiEvent"/> triggers (usually none or one).</summary>
    public IReadOnlyList<ControlAction> Match(MidiEvent midiEvent)
    {
        List<ControlAction>? fired = null;
        foreach (var (action, trigger) in _triggers)
        {
            // Every trigger sees every message, so each tracks its own pedal's up/down.
            if (trigger.IsTap(midiEvent))
            {
                (fired ??= []).Add(action);
            }
        }

        return fired ?? (IReadOnlyList<ControlAction>)[];
    }
}
