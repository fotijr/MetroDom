using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Melanchall.DryWetMidi.Core;
using MetroDom.Core.Lighting;
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
/// A deployment mode's policy (ADR 0005), first cut: the control-surface mapping and what the
/// lights may do.
/// Format <c>metrodom.policy/0</c>, see docs/references/policy-profile.md. Roles, queue rules and
/// limits join it as they're built.
/// </summary>
/// <param name="Mode">"live-band", "crowd-jukebox", "rental", or the owner's own name.</param>
/// <param name="Controls">Which MIDI message triggers each action. Unmapped actions are absent.</param>
public sealed record PolicyProfile(string Mode, IReadOnlyDictionary<ControlAction, string> Controls)
{
    public const string Schema = "metrodom.policy/0";

    /// <summary>What the venue lets the lights do (ADR 0011 §5). Defaults: no strobe, 3 flashes a second, full brightness.</summary>
    public LightLimits Lights { get; init; } = LightLimits.Default;

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

            if (!TryLights(root["lights"], out var lights, out error))
            {
                return false;
            }

            profile = new PolicyProfile(mode.Trim(), controls) { Lights = lights };
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            error = $"not a valid policy file: {ex.Message}";
            return false;
        }
    }

    private static bool TryLights(JsonNode? node, out LightLimits lights, out string? error)
    {
        lights = LightLimits.Default;
        error = null;
        if (node is null)
        {
            return true;
        }

        if (node is not JsonObject obj)
        {
            error = "\"lights\" must be an object.";
            return false;
        }

        QuietHours? quiet = null;
        if (obj["quietHours"] is { } quietNode)
        {
            if (!TryTime(quietNode["from"], out var from) || !TryTime(quietNode["to"], out var to))
            {
                error = "\"lights.quietHours\" needs \"from\" and \"to\" as HH:mm (e.g. \"23:00\").";
                return false;
            }

            quiet = new QuietHours(from, to, quietNode["maxBrightness"]?.GetValue<int>() ?? 30);
        }

        lights = new LightLimits
        {
            Strobe = obj["strobe"]?.GetValue<bool>() ?? false,
            MaxFlashesPerSecond = obj["maxFlashesPerSecond"]?.GetValue<double>() ?? LightLimits.DefaultFlashesPerSecond,
            MaxBrightness = obj["maxBrightness"]?.GetValue<int>() ?? 100,
            Quiet = quiet,
        };
        error = lights.Validate();
        return error is null;
    }

    private static bool TryTime(JsonNode? node, out TimeOnly time)
    {
        time = default;
        return node?.GetValue<string>() is { } text
            && TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
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

        var root = new JsonObject { ["schema"] = Schema, ["mode"] = Mode, ["controls"] = controls };
        if (!Lights.IsDefault)
        {
            var lights = new JsonObject
            {
                ["strobe"] = Lights.Strobe,
                ["maxFlashesPerSecond"] = Lights.MaxFlashesPerSecond,
                ["maxBrightness"] = Lights.MaxBrightness,
            };
            if (Lights.Quiet is { } quiet)
            {
                lights["quietHours"] = new JsonObject
                {
                    ["from"] = quiet.From.ToString("HH:mm", CultureInfo.InvariantCulture),
                    ["to"] = quiet.To.ToString("HH:mm", CultureInfo.InvariantCulture),
                    ["maxBrightness"] = quiet.MaxBrightness,
                };
            }

            root["lights"] = lights;
        }

        return root
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
