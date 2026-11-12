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
/// A deployment mode's policy (ADR 0005): the control-surface mapping, instrument limits, what
/// each role may do, the jukebox's queue rules, how long handed-out tokens last, and what the
/// lights may do. Format <c>metrodom.policy/0</c>, see docs/references/policy-profile.md. Every
/// section is optional and defaults to the safe, closed choice.
/// </summary>
/// <param name="Mode">"live-band", "crowd-jukebox", "rental", or the owner's own name.</param>
/// <param name="Controls">Which MIDI message triggers each action. Unmapped actions are absent.</param>
public sealed record PolicyProfile(string Mode, IReadOnlyDictionary<ControlAction, string> Controls)
{
    public const string Schema = "metrodom.policy/0";

    /// <summary>What the venue lets the lights do (ADR 0011 §5). Defaults: no strobe, 3 flashes a second, full brightness.</summary>
    public LightLimits Lights { get; init; } = LightLimits.Default;

    /// <summary>Limits on every instrument's notes (velocity, rate, quiet hours). Default: none.</summary>
    public Safety.SafetyLimits Limits { get; init; } = Safety.SafetyLimits.None;

    /// <summary>What operators and guests may do. The owner can do everything.</summary>
    public RolePermissions Roles { get; init; } = RolePermissions.Default;

    /// <summary>The jukebox's rules for guest requests. Default: closed.</summary>
    public QueueRules Queue { get; init; } = QueueRules.Closed;

    /// <summary>How long handed-out tokens may last. Default: a day.</summary>
    public SessionRules Sessions { get; init; } = SessionRules.Default;

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

            if (!TryLights(root["lights"], out var lights, out error)
                || !TryLimits(root["limits"], out var limits, out error)
                || !TryRoles(root["roles"], out var roles, out error)
                || !TryQueue(root["queue"], out var queue, out error)
                || !TrySessions(root["sessions"], out var sessions, out error))
            {
                return false;
            }

            profile = new PolicyProfile(mode.Trim(), controls) { Lights = lights, Limits = limits, Roles = roles, Queue = queue, Sessions = sessions };
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

    private static bool TryLimits(JsonNode? node, out Safety.SafetyLimits limits, out string? error)
    {
        limits = Safety.SafetyLimits.None;
        error = null;
        if (node is null)
        {
            return true;
        }

        if (node is not JsonObject obj)
        {
            error = "\"limits\" must be an object.";
            return false;
        }

        var velocity = obj["maxVelocity"]?.GetValue<int>();
        if (velocity is not (null or (>= 1 and <= 127)))
        {
            error = "\"limits.maxVelocity\" must be 1-127.";
            return false;
        }

        var rate = obj["maxNotesPerSecond"]?.GetValue<int>();
        if (rate is < 1)
        {
            error = "\"limits.maxNotesPerSecond\" must be at least 1.";
            return false;
        }

        Safety.QuietHours? quiet = null;
        if (obj["quietHours"] is { } quietNode)
        {
            if (!TryTime(quietNode["from"], out var from) || !TryTime(quietNode["to"], out var to) || from == to)
            {
                error = "\"limits.quietHours\" needs different \"from\" and \"to\" as HH:mm (e.g. \"23:00\").";
                return false;
            }

            var quietVelocity = quietNode["maxVelocity"]?.GetValue<int>() ?? 0;
            if (quietVelocity is < 0 or > 127)
            {
                error = "\"limits.quietHours.maxVelocity\" must be 0-127 (0: no new notes).";
                return false;
            }

            quiet = new Safety.QuietHours(from, to, quietVelocity);
        }

        limits = new Safety.SafetyLimits { MaxVelocity = velocity, MaxNotesPerSecond = rate, Quiet = quiet };
        return true;
    }

    private static bool TryRoles(JsonNode? node, out RolePermissions roles, out string? error)
    {
        roles = RolePermissions.Default;
        error = null;
        if (node is null)
        {
            return true;
        }

        if (node is not JsonObject obj)
        {
            error = "\"roles\" must be an object.";
            return false;
        }

        var sets = new Dictionary<string, IReadOnlySet<Grant>>
        {
            ["operator"] = RolePermissions.Default.Operator,
            ["guest"] = RolePermissions.Default.Guest,
        };
        foreach (var (name, value) in obj)
        {
            if (name == "owner")
            {
                error = "\"roles.owner\" can't be set: the owner can always do everything.";
                return false;
            }

            if (!sets.ContainsKey(name))
            {
                error = $"unknown role '{name}'. Known: operator, guest.";
                return false;
            }

            if (value is not JsonArray list)
            {
                error = $"\"roles.{name}\" must be a list of permissions, e.g. [\"view\", \"panic\"].";
                return false;
            }

            var permissions = new HashSet<Grant>();
            foreach (var item in list)
            {
                var text = item?.GetValue<string>() ?? "";
                if (!RolePermissions.TryParse(text, out var permission))
                {
                    error = $"\"roles.{name}\": unknown permission '{text}'. Known: {string.Join(", ", RolePermissions.AllNames)}.";
                    return false;
                }

                permissions.Add(permission);
            }

            sets[name] = permissions;
        }

        error = RolePermissions.Validate(sets["operator"], sets["guest"]);
        if (error is not null)
        {
            return false;
        }

        roles = new RolePermissions(sets["operator"], sets["guest"]);
        return true;
    }

    private static bool TryQueue(JsonNode? node, out QueueRules queue, out string? error)
    {
        queue = QueueRules.Closed;
        error = null;
        if (node is null)
        {
            return true;
        }

        if (node is not JsonObject obj)
        {
            error = "\"queue\" must be an object.";
            return false;
        }

        var maxPerGuest = obj["maxPerGuest"]?.GetValue<int>() ?? QueueRules.Closed.MaxPerGuest;
        var cooldown = obj["cooldownSeconds"]?.GetValue<double>() ?? QueueRules.Closed.Cooldown.TotalSeconds;
        if (maxPerGuest is < 1 or > 20)
        {
            error = "\"queue.maxPerGuest\" must be 1-20.";
            return false;
        }

        if (cooldown is < 0 or > 3600)
        {
            error = "\"queue.cooldownSeconds\" must be 0-3600.";
            return false;
        }

        queue = new QueueRules(
            obj["requests"]?.GetValue<bool>() ?? false,
            obj["votes"]?.GetValue<bool>() ?? false,
            maxPerGuest,
            TimeSpan.FromSeconds(cooldown));
        return true;
    }

    private static bool TrySessions(JsonNode? node, out SessionRules sessions, out string? error)
    {
        sessions = SessionRules.Default;
        error = null;
        if (node is null)
        {
            return true;
        }

        if (node is not JsonObject obj)
        {
            error = "\"sessions\" must be an object.";
            return false;
        }

        var hours = obj["maxTokenHours"]?.GetValue<double>() ?? SessionRules.Default.MaxTokenLife.TotalHours;
        if (hours <= 0 || TimeSpan.FromHours(hours) > SessionRules.Ceiling)
        {
            error = $"\"sessions.maxTokenHours\" must be more than 0 and at most {SessionRules.Ceiling.TotalHours:0} (a week).";
            return false;
        }

        sessions = new SessionRules(TimeSpan.FromHours(hours));
        return true;
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

        if (Limits != Safety.SafetyLimits.None)
        {
            var limits = new JsonObject();
            if (Limits.MaxVelocity is { } velocity)
            {
                limits["maxVelocity"] = velocity;
            }

            if (Limits.MaxNotesPerSecond is { } rate)
            {
                limits["maxNotesPerSecond"] = rate;
            }

            if (Limits.Quiet is { } quiet)
            {
                limits["quietHours"] = new JsonObject
                {
                    ["from"] = quiet.Start.ToString("HH:mm", CultureInfo.InvariantCulture),
                    ["to"] = quiet.End.ToString("HH:mm", CultureInfo.InvariantCulture),
                    ["maxVelocity"] = quiet.MaxVelocity,
                };
            }

            root["limits"] = limits;
        }

        if (Roles != RolePermissions.Default)
        {
            root["roles"] = new JsonObject
            {
                ["operator"] = new JsonArray([.. Roles.For(Role.Operator).Select(p => (JsonNode)RolePermissions.NameOf(p))]),
                ["guest"] = new JsonArray([.. Roles.For(Role.Guest).Select(p => (JsonNode)RolePermissions.NameOf(p))]),
            };
        }

        if (Queue != QueueRules.Closed)
        {
            root["queue"] = new JsonObject
            {
                ["requests"] = Queue.Requests,
                ["votes"] = Queue.Votes,
                ["maxPerGuest"] = Queue.MaxPerGuest,
                ["cooldownSeconds"] = Queue.Cooldown.TotalSeconds,
            };
        }

        if (Sessions != SessionRules.Default)
        {
            root["sessions"] = new JsonObject { ["maxTokenHours"] = Sessions.MaxTokenLife.TotalHours };
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
