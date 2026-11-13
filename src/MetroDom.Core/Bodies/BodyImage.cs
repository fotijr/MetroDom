using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MetroDom.Core.Calibration;

namespace MetroDom.Core.Bodies;

/// <summary>How a body drives its actuators.</summary>
public enum BodyDriver
{
    /// <summary>Pico GPIO pins 0-28, one per actuator (the M0 bench rig).</summary>
    Gpio = 0,

    /// <summary>A PCA9685 on the body's Qwiic bus, channels 0-15, each full on or full off.</summary>
    Pca9685 = 1,
}

/// <summary>One actuator on a body and the note that fires it.</summary>
/// <param name="Note">MIDI note 0-127.</param>
/// <param name="Output">GPIO pin or PCA9685 channel.</param>
/// <param name="PulseMs">Strike length at velocity 127.</param>
/// <param name="MinPulseMs">Strike length at velocity 1; 0 means every strike is <paramref name="PulseMs"/>.</param>
/// <param name="MinIntervalMs">Fastest repeat; at least the pulse.</param>
/// <param name="LatencyMs">Measured Note On → sound; 0 if not measured yet.</param>
public sealed record BodyActuator(int Note, int Output, int PulseMs, int MinPulseMs, int MinIntervalMs, int LatencyMs)
{
    /// <summary>The accent LEDs this note flashes, or <c>null</c> for none.</summary>
    public AccentLed? Led { get; init; }
}

/// <summary>
/// What a body is: the <a href="../../../docs/references/body-record.md">body record</a> its ID
/// EEPROM holds, read from a <c>metrodom.body/0</c> JSON file. The brain firmware
/// (Instruments/metrodom_brain/src/brain/Body.cpp) parses the same bytes with the same rules.
/// </summary>
public sealed record BodyDefinition(string Id, string Name, BodyDriver Driver, int Address, IReadOnlyList<BodyActuator> Notes)
{
    /// <summary>Channel 0-15 the brain listens on; <c>null</c> for omni (the default).</summary>
    public int? Channel { get; init; }

    public int Polyphony { get; init; } = 1;

    public int MaxOnMs { get; init; } = 45;

    public int MaxDutyPercent { get; init; } = 30;

    public int DutyWindowMs { get; init; } = 1000;

    public int WatchdogMs { get; init; } = 500;

    /// <summary>The body's accent LED strip, or <c>null</c> for none.</summary>
    public AccentStrip? Accent { get; init; }

    /// <summary>Whether any note's strike gets shorter with lower velocity.</summary>
    public bool Velocity => Notes.Any(n => n.MinPulseMs > 0 && n.MinPulseMs < n.PulseMs);

    /// <summary>The profile a brain running this body sends (what the Conductor schedules against).</summary>
    public InstrumentProfile ToProfile() => new(Name, TimeSpan.FromMilliseconds(Notes.Max(n => n.LatencyMs)))
    {
        Notes = Notes.Select(n => n.Note).ToHashSet(),
        NoteLatency = Notes.Where(n => n.LatencyMs > 0).ToDictionary(n => n.Note, n => TimeSpan.FromMilliseconds(n.LatencyMs)),
        NotePulse = Notes.ToDictionary(n => n.Note, n => TimeSpan.FromMilliseconds(n.PulseMs)),
        MinInterval = TimeSpan.FromMilliseconds(Notes.Max(n => n.MinIntervalMs)),
        NoteInterval = Notes.ToDictionary(n => n.Note, n => TimeSpan.FromMilliseconds(n.MinIntervalMs)),
        Channel = Channel,
        Polyphony = Polyphony,
        Safety = new ActuatorSafety(TimeSpan.FromMilliseconds(MaxOnMs), MaxDutyPercent, TimeSpan.FromMilliseconds(DutyWindowMs)),
        SpeaksMetroDom = true,
        Accent = Accent,
        NoteLed = Notes.Where(n => n.Led is not null).ToDictionary(n => n.Note, n => n.Led!),
    };
}

/// <summary>
/// Body records: JSON ↔ definition ↔ bytes. The byte layout, the limits and the error numbers are
/// shared with the brain firmware; both test suites check <c>bodies/m1-xylophone.hex</c> and
/// <c>bodies/m1-xylophone-accent.hex</c> (with accent LEDs). The format byte stays 1 until the owner
/// calls production: until then the layout changes in place, with no old-layout support.
/// </summary>
public static class BodyImage
{
    public const string Schema = "metrodom.body/0";

    /// <summary>Longest pulse any body may ask for (firmware <c>kCeilingOnMs</c>).</summary>
    public const int CeilingOnMs = 250;

    /// <summary>Highest duty any body may ask for (firmware <c>kCeilingDutyPercent</c>).</summary>
    public const int CeilingDutyPercent = 50;

    public const int MaxActuators = 16;

    /// <summary>Brain pins the backplane uses (Qwiic SDA, SCL, the PCA9685's /OE), so a GPIO body can't.</summary>
    public static readonly IReadOnlySet<int> ReservedPins = new HashSet<int> { 4, 5, 6 };

    /// <summary>The accent strip's data pin, which a GPIO body with accent LEDs can't use.</summary>
    public const int LedPin = 7;

    /// <summary>Most accent LEDs on a body (firmware <c>kMaxLeds</c>).</summary>
    public const int MaxLeds = 64;

    /// <summary>Brightest any body's LEDs may be (firmware <c>kCeilingLedPercent</c>).</summary>
    public const int CeilingLedPercent = 25;
    public const int MaxIdLength = 32;
    public const int MaxNameLength = 48;
    public const int HeaderBytes = 20;
    public const int ActuatorBytes = 10;
    public const int LedHeaderBytes = 4;
    public const int LedBytes = 5;
    public const int MaxBytes = HeaderBytes + (MaxActuators * ActuatorBytes) + MaxIdLength + MaxNameLength
        + LedHeaderBytes + (MaxActuators * LedBytes) + 2;
    private const byte Version = 1;
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private const byte Omni = 0xFF;

    /// <summary>
    /// What's wrong with <paramref name="body"/>, in words, or <c>null</c> if a brain will accept it.
    /// Same rules, in the same order, as the firmware's <c>validateBody</c>.
    /// </summary>
    public static string? Validate(BodyDefinition body)
    {
        ArgumentNullException.ThrowIfNull(body);
        int outputs;
        switch (body.Driver)
        {
            case BodyDriver.Gpio when body.Address != 0:
                return "a GPIO body has no I2C address.";
            case BodyDriver.Gpio:
                outputs = 29;
                break;
            case BodyDriver.Pca9685 when body.Address is < 0x40 or > 0x7F:
                return "a PCA9685's address must be 0x40-0x7F.";
            case BodyDriver.Pca9685:
                outputs = 16;
                break;
            default:
                return "the driver must be \"gpio\" or \"pca9685\".";
        }

        if (body.Notes.Count is < 1 or > MaxActuators)
        {
            return $"a body has 1-{MaxActuators} notes (this one has {body.Notes.Count}).";
        }

        if (body.Channel is < 0 or > 15)
        {
            return "\"midi.channel\" must be 1-16.";
        }

        if (body.Polyphony is < 1 or > MaxActuators)
        {
            return $"\"polyphony\" must be 1-{MaxActuators}.";
        }

        if (body.MaxOnMs is < 1 or > CeilingOnMs)
        {
            return $"\"safety.maxOnMs\" must be 1-{CeilingOnMs} (the firmware's ceiling).";
        }

        if (body.MaxDutyPercent is < 1 or > CeilingDutyPercent)
        {
            return $"\"safety.maxDutyPercent\" must be 1-{CeilingDutyPercent} (the firmware's ceiling).";
        }

        if (body.DutyWindowMs is < 100 or > 10000)
        {
            return "\"safety.dutyWindowMs\" must be 100-10000.";
        }

        if (body.WatchdogMs is < 200 or > 8000)
        {
            return "\"safety.watchdogMs\" must be 200-8000.";
        }

        for (var i = 0; i < body.Notes.Count; i++)
        {
            var a = body.Notes[i];
            if (a.Note is < 0 or > 127 || body.Notes.Take(i).Any(b => b.Note == a.Note))
            {
                return $"note {a.Note}: notes must be 0-127 and each listed once.";
            }

            if (a.Output < 0 || a.Output >= outputs || body.Notes.Take(i).Any(b => b.Output == a.Output))
            {
                return $"note {a.Note}: output {a.Output} must be 0-{outputs - 1} and used by one note only.";
            }

            if (body.Driver == BodyDriver.Gpio && ReservedPins.Contains(a.Output))
            {
                return $"note {a.Note}: GPIO {a.Output} is taken by the backplane (4 and 5 are its I2C, 6 the PCA9685's /OE).";
            }

            if (body.Driver == BodyDriver.Gpio && body.Accent is not null && a.Output == LedPin)
            {
                return $"note {a.Note}: GPIO {LedPin} drives the accent LEDs on a body that has them.";
            }

            if (a.PulseMs < 1 || a.PulseMs > body.MaxOnMs)
            {
                return $"note {a.Note}: \"pulseMs\" must be 1-{body.MaxOnMs} (safety.maxOnMs).";
            }

            if (a.MinPulseMs < 0 || a.MinPulseMs > a.PulseMs)
            {
                return $"note {a.Note}: \"minPulseMs\" can't be more than \"pulseMs\".";
            }

            if (a.MinIntervalMs < a.PulseMs || a.MinIntervalMs > 10000)
            {
                return $"note {a.Note}: \"minIntervalMs\" must be at least \"pulseMs\" and at most 10000.";
            }

            if (a.LatencyMs is < 0 or > 1000)
            {
                return $"note {a.Note}: \"latencyMs\" must be 0-1000.";
            }
        }

        if (body.Id.Length is < 1 or > MaxIdLength || !body.Id.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
        {
            return $"\"id\" must be 1-{MaxIdLength} characters of a-z, 0-9 and '-'.";
        }

        return body.Name.Length is < 1 or > MaxNameLength || !body.Name.All(c => c is >= ' ' and < (char)0x7F)
            ? $"\"name\" must be 1-{MaxNameLength} printable ASCII characters."
            : ValidateAccent(body);
    }

    /// <summary>The firmware's <c>validateLeds</c>.</summary>
    private static string? ValidateAccent(BodyDefinition body)
    {
        if (body.Accent is not { } strip)
        {
            return body.Notes.FirstOrDefault(n => n.Led is not null) is { } lit
                ? $"note {lit.Note}: \"led\" needs an \"accent\" strip."
                : null;
        }

        if (strip.Leds is < 1 or > MaxLeds)
        {
            return $"\"accent.leds\" must be 1-{MaxLeds}.";
        }

        if (strip.MaxBrightnessPercent is < 1 or > CeilingLedPercent)
        {
            return $"\"accent.maxBrightnessPercent\" must be 1-{CeilingLedPercent} (the firmware's ceiling).";
        }

        if (strip.FlashMs is < 10 or > 1000)
        {
            return "\"accent.flashMs\" must be 10-1000.";
        }

        for (var i = 0; i < body.Notes.Count; i++)
        {
            if (body.Notes[i].Led is not { } led)
            {
                continue;
            }

            var note = body.Notes[i].Note;
            if (led.First < 0 || led.Count is < 1 or > 255 || led.First + led.Count > strip.Leds)
            {
                return $"note {note}: its LEDs must be on the strip (0-{strip.Leds - 1}).";
            }

            if (led.Color is < 0 or > 0xFFFFFF)
            {
                return $"note {note}: \"color\" must be #000000-#FFFFFF.";
            }

            if (body.Notes.Take(i).FirstOrDefault(o => o.Led is { } other && led.First < other.First + other.Count && other.First < led.First + led.Count) is { } overlap)
            {
                return $"note {note}: its LEDs overlap note {overlap.Note}'s.";
            }
        }

        return null;
    }

    /// <summary>The record a brain stores. Throws if <paramref name="body"/> isn't valid.</summary>
    public static byte[] Encode(BodyDefinition body)
    {
        if (Validate(body) is { } error)
        {
            throw new ArgumentException(error, nameof(body));
        }

        var total = RecordLength(body.Notes.Count, body.Id.Length, body.Name.Length);
        var bytes = new byte[total];
        "MDB"u8.CopyTo(bytes);
        bytes[3] = Version;
        Put16(bytes, 4, total);
        bytes[6] = (byte)body.Driver;
        bytes[7] = (byte)body.Address;
        bytes[8] = body.Channel is { } channel ? (byte)channel : Omni;
        bytes[9] = (byte)body.Polyphony;
        bytes[10] = (byte)body.MaxDutyPercent;
        bytes[11] = (byte)body.Notes.Count;
        Put16(bytes, 12, body.MaxOnMs);
        Put16(bytes, 14, body.DutyWindowMs);
        Put16(bytes, 16, body.WatchdogMs);
        bytes[18] = (byte)body.Id.Length;
        bytes[19] = (byte)body.Name.Length;
        var at = HeaderBytes;
        foreach (var a in body.Notes)
        {
            bytes[at] = (byte)a.Note;
            bytes[at + 1] = (byte)a.Output;
            Put16(bytes, at + 2, a.PulseMs);
            Put16(bytes, at + 4, a.MinPulseMs);
            Put16(bytes, at + 6, a.MinIntervalMs);
            Put16(bytes, at + 8, a.LatencyMs);
            at += ActuatorBytes;
        }

        Encoding.ASCII.GetBytes(body.Id, bytes.AsSpan(at));
        Encoding.ASCII.GetBytes(body.Name, bytes.AsSpan(at + body.Id.Length));
        at += body.Id.Length + body.Name.Length;
        if (body.Accent is { } strip)
        {
            // No strip: the block stays all zeros.
            bytes[at] = (byte)strip.Leds;
            bytes[at + 1] = (byte)strip.MaxBrightnessPercent;
            Put16(bytes, at + 2, strip.FlashMs);
            at += LedHeaderBytes;
            foreach (var a in body.Notes)
            {
                if (a.Led is { } led)
                {
                    bytes[at] = (byte)led.First;
                    bytes[at + 1] = (byte)led.Count;
                    bytes[at + 2] = (byte)(led.Color >> 16);
                    bytes[at + 3] = (byte)(led.Color >> 8);
                    bytes[at + 4] = (byte)led.Color;
                }

                at += LedBytes;
            }
        }

        Put16(bytes, total - 2, Crc16(bytes.AsSpan(0, total - 2)));
        return bytes;
    }

    /// <summary>Reads a record (trailing bytes, like the rest of an EEPROM, are ignored).</summary>
    public static bool TryDecode(ReadOnlySpan<byte> bytes, out BodyDefinition body, out string? error)
    {
        body = null!;
        if (bytes.Length < HeaderBytes || bytes[0] != 'M' || bytes[1] != 'D' || bytes[2] != 'B')
        {
            error = "not a body record.";
            return false;
        }

        if (bytes[3] != Version)
        {
            error = $"body record format {bytes[3]}; this Conductor reads format {Version}.";
            return false;
        }

        var total = Get16(bytes, 4);
        if (total < HeaderBytes + 2 || total > MaxBytes || total > bytes.Length)
        {
            error = "bad length.";
            return false;
        }

        if (Crc16(bytes[..(total - 2)]) != Get16(bytes, total - 2))
        {
            error = "bad CRC.";
            return false;
        }

        int count = bytes[11], idLength = bytes[18], nameLength = bytes[19];
        if (total != RecordLength(count, idLength, nameLength))
        {
            error = "bad length.";
            return false;
        }

        var text = HeaderBytes + (count * ActuatorBytes);
        var ledsAt = text + idLength + nameLength;
        AccentStrip? accent = null;
        if (bytes[ledsAt] != 0)
        {
            accent = new AccentStrip(bytes[ledsAt], bytes[ledsAt + 1], Get16(bytes, ledsAt + 2));
        }
        else if ((bytes[ledsAt + 1] | bytes[ledsAt + 2] | bytes[ledsAt + 3]) != 0)
        {
            error = "bad accent LEDs.";
            return false;
        }

        var notes = new List<BodyActuator>(count);
        for (var i = 0; i < count; i++)
        {
            var at = HeaderBytes + (i * ActuatorBytes);
            var led = ledsAt + LedHeaderBytes + (i * LedBytes);
            notes.Add(new BodyActuator(bytes[at], bytes[at + 1], Get16(bytes, at + 2), Get16(bytes, at + 4), Get16(bytes, at + 6), Get16(bytes, at + 8))
            {
                Led = bytes[led + 1] == 0
                    ? null
                    : new AccentLed(bytes[led], bytes[led + 1], (bytes[led + 2] << 16) | (bytes[led + 3] << 8) | bytes[led + 4]),
            });
            if (bytes[led + 1] == 0 && (bytes[led] | bytes[led + 2] | bytes[led + 3] | bytes[led + 4]) != 0)
            {
                error = "bad accent LEDs.";
                return false;
            }
        }

        body = new BodyDefinition(
            Encoding.ASCII.GetString(bytes.Slice(text, idLength)),
            Encoding.ASCII.GetString(bytes.Slice(text + idLength, nameLength)),
            (BodyDriver)bytes[6],
            bytes[7],
            notes)
        {
            Channel = bytes[8] == Omni ? null : bytes[8],
            Polyphony = bytes[9],
            MaxDutyPercent = bytes[10],
            MaxOnMs = Get16(bytes, 12),
            DutyWindowMs = Get16(bytes, 14),
            WatchdogMs = Get16(bytes, 16),
            Accent = accent,
        };
        error = Validate(body);
        return error is null;
    }

    /// <summary>CRC-16/CCITT-FALSE (poly 0x1021, init 0xFFFF), as the firmware computes it.</summary>
    public static int Crc16(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= b << 8;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x8000) != 0 ? ((crc << 1) ^ 0x1021) & 0xFFFF : (crc << 1) & 0xFFFF;
            }
        }

        return crc;
    }

    /// <summary>Reads a <c>metrodom.body/0</c> file's text.</summary>
    public static bool TryParseJson(string json, out BodyDefinition body, out string? error)
    {
        body = null!;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject obj)
            {
                error = "a body file is a JSON object.";
                return false;
            }

            var schema = obj["schema"]?.GetValue<string>();
            if (schema != Schema)
            {
                error = schema is null ? "missing \"schema\"." : $"schema '{schema}' is not {Schema}.";
                return false;
            }

            if (obj["notes"] is not JsonArray array)
            {
                error = "missing \"notes\".";
                return false;
            }

            var notes = new List<BodyActuator>();
            foreach (var item in array)
            {
                if (item?["note"]?.GetValue<int>() is not { } note || item["output"]?.GetValue<int>() is not { } output
                    || item["pulseMs"]?.GetValue<int>() is not { } pulse || item["minIntervalMs"]?.GetValue<int>() is not { } interval)
                {
                    error = "every note needs \"note\", \"output\", \"pulseMs\" and \"minIntervalMs\".";
                    return false;
                }

                AccentLed? led = null;
                if (item["led"] is { } ledNode)
                {
                    if (ledNode["first"]?.GetValue<int>() is not { } first || ledNode["count"]?.GetValue<int>() is not { } count
                        || !AccentLed.TryParseColor(ledNode["color"]?.GetValue<string>(), out var color))
                    {
                        error = $"note {note}: \"led\" needs \"first\", \"count\" and a \"color\" like \"#FF8000\".";
                        return false;
                    }

                    led = new AccentLed(first, count, color);
                }

                notes.Add(new BodyActuator(note, output, pulse, item["minPulseMs"]?.GetValue<int>() ?? 0, interval, item["latencyMs"]?.GetValue<int>() ?? 0) { Led = led });
            }

            var driverNode = obj["driver"];
            var type = driverNode?["type"]?.GetValue<string>();
            var driver = type switch
            {
                "gpio" => BodyDriver.Gpio,
                "pca9685" => BodyDriver.Pca9685,
                _ => (BodyDriver?)null,
            };
            if (driver is null)
            {
                error = "\"driver.type\" must be \"gpio\" or \"pca9685\".";
                return false;
            }

            if (!TryAddress(driverNode!["address"], driver.Value, out var address))
            {
                error = "\"driver.address\" must be a number or a string like \"0x40\".";
                return false;
            }

            var safety = obj["safety"];
            if (safety?["maxOnMs"]?.GetValue<int>() is not { } maxOn || safety["maxDutyPercent"]?.GetValue<int>() is not { } duty)
            {
                error = "\"safety.maxOnMs\" and \"safety.maxDutyPercent\" are required.";
                return false;
            }

            AccentStrip? accent = null;
            if (obj["accent"] is { } accentNode)
            {
                if (accentNode["leds"]?.GetValue<int>() is not { } leds || accentNode["maxBrightnessPercent"]?.GetValue<int>() is not { } brightness)
                {
                    error = "\"accent\" needs \"leds\" and \"maxBrightnessPercent\".";
                    return false;
                }

                accent = new AccentStrip(leds, brightness, accentNode["flashMs"]?.GetValue<int>() ?? 80);
            }

            int? channel = obj["midi"]?["channel"]?.GetValue<int>() is { } c ? c - 1 : null;
            body = new BodyDefinition(obj["id"]?.GetValue<string>() ?? "", obj["name"]?.GetValue<string>() ?? "", driver.Value, address, notes)
            {
                Channel = channel,
                Polyphony = obj["polyphony"]?.GetValue<int>() ?? 1,
                MaxOnMs = maxOn,
                MaxDutyPercent = duty,
                DutyWindowMs = safety["dutyWindowMs"]?.GetValue<int>() ?? 1000,
                WatchdogMs = safety["watchdogMs"]?.GetValue<int>() ?? 500,
                Accent = accent,
            };
            error = Validate(body);
            return error is null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            error = $"not a valid body file: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// <paramref name="body"/> as a <c>metrodom.body/0</c> file: what <c>metrodom body read --out</c>
    /// saves, and <see cref="TryParseJson"/> reads back to the same body.
    /// </summary>
    public static string ToJson(BodyDefinition body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var driver = new JsonObject { ["type"] = body.Driver == BodyDriver.Pca9685 ? "pca9685" : "gpio" };
        if (body.Driver == BodyDriver.Pca9685)
        {
            driver["address"] = string.Create(CultureInfo.InvariantCulture, $"0x{body.Address:X2}");
        }

        var obj = new JsonObject
        {
            ["schema"] = Schema,
            ["id"] = body.Id,
            ["name"] = body.Name,
            ["driver"] = driver,
        };
        if (body.Channel is { } channel)
        {
            obj["midi"] = new JsonObject { ["channel"] = channel + 1 };
        }

        obj["polyphony"] = body.Polyphony;
        obj["safety"] = new JsonObject
        {
            ["maxOnMs"] = body.MaxOnMs,
            ["maxDutyPercent"] = body.MaxDutyPercent,
            ["dutyWindowMs"] = body.DutyWindowMs,
            ["watchdogMs"] = body.WatchdogMs,
        };
        if (body.Accent is { } strip)
        {
            obj["accent"] = new JsonObject
            {
                ["leds"] = strip.Leds,
                ["maxBrightnessPercent"] = strip.MaxBrightnessPercent,
                ["flashMs"] = strip.FlashMs,
            };
        }

        var notes = new JsonArray();
        foreach (var a in body.Notes)
        {
            var entry = new JsonObject { ["note"] = a.Note, ["output"] = a.Output, ["pulseMs"] = a.PulseMs };
            if (a.MinPulseMs > 0)
            {
                entry["minPulseMs"] = a.MinPulseMs;
            }

            entry["minIntervalMs"] = a.MinIntervalMs;
            if (a.LatencyMs > 0)
            {
                entry["latencyMs"] = a.LatencyMs;
            }

            if (a.Led is { } led)
            {
                entry["led"] = new JsonObject { ["first"] = led.First, ["count"] = led.Count, ["color"] = led.Hex };
            }

            notes.Add(entry);
        }

        obj["notes"] = notes;
        return obj.ToJsonString(Indented) + "\n";
    }

    /// <summary>
    /// <paramref name="body"/> with the latencies <c>metrodom calibrate</c> measured, rounded to the
    /// record's whole milliseconds (notes measured without their own value get the overall one).
    /// </summary>
    public static BodyDefinition WithMeasured(BodyDefinition body, MeasuredLatency measured)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(measured);
        return body with
        {
            Notes = [.. body.Notes.Select(a =>
            {
                var latency = measured.NoteLatency.TryGetValue(a.Note, out var own) ? own : measured.Latency;
                return a with { LatencyMs = (int)Math.Clamp(Math.Round(latency.TotalMilliseconds, MidpointRounding.AwayFromZero), 0, 1000) };
            })],
        };
    }

    /// <summary>Upper-case hex, two characters a byte: the fixture format and what Body Write carries.</summary>
    public static string ToHex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes);

    private static bool TryAddress(JsonNode? node, BodyDriver driver, out int address)
    {
        address = 0;
        if (node is null)
        {
            address = driver == BodyDriver.Pca9685 ? 0x40 : 0; // the PCA9685's address with no jumpers
            return true;
        }

        if (node.GetValueKind() == JsonValueKind.Number)
        {
            address = node.GetValue<int>();
            return true;
        }

        var text = node.GetValue<string>();
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address);
    }

    private static int RecordLength(int count, int idLength, int nameLength) =>
        HeaderBytes + (count * (ActuatorBytes + LedBytes)) + idLength + nameLength + LedHeaderBytes + 2;

    private static int Get16(ReadOnlySpan<byte> bytes, int at) => bytes[at] | (bytes[at + 1] << 8);

    private static void Put16(byte[] bytes, int at, int value)
    {
        bytes[at] = (byte)(value & 0xFF);
        bytes[at + 1] = (byte)((value >> 8) & 0xFF);
    }
}
