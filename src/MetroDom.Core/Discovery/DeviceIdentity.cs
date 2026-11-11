using System.Globalization;
using Melanchall.DryWetMidi.Core;

namespace MetroDom.Core.Discovery;

/// <summary>
/// A device's answer to the MIDI Universal SysEx <b>Identity Request</b> (ADR 0007, step 2):
/// who made it, and its family, model and version. See docs/references/midi-identity.md.
/// </summary>
/// <param name="ManufacturerId">SysEx manufacturer ID as hex: one byte (<c>"41"</c>) or three (<c>"00 20 29"</c>).</param>
/// <param name="Family">Device family code (14-bit).</param>
/// <param name="Model">Model number within the family (14-bit).</param>
/// <param name="Version">Software revision, four bytes as <c>"a.b.c.d"</c>.</param>
public sealed record DeviceIdentity(string ManufacturerId, int Family, int Model, string Version)
{
    /// <summary>"All devices" / "any device" SysEx channel.</summary>
    public const byte AllDevices = 0x7F;

    /// <summary>The MIDI Association's ID for non-commercial and educational use, which MetroDom uses.</summary>
    public const string NonCommercialId = "7D";

    /// <summary>
    /// MetroDom's family code under <see cref="NonCommercialId"/>: the bytes are ASCII "MD"
    /// (0x4D, 0x44, sent LSB first), so the family reads as "MD" in a SysEx dump.
    /// </summary>
    public const int MetroDomFamily = 0x4D | (0x44 << 7);

    /// <summary>MetroDom model 0: a software instrument (<c>metrodom virtual</c>), not hardware.</summary>
    public const int VirtualModel = 0;

    private const byte UniversalNonRealTime = 0x7E;
    private const byte GeneralInformation = 0x06;
    private const byte IdentityRequestId = 0x01;
    private const byte IdentityReplyId = 0x02;
    private const byte EndOfExclusive = 0xF7;

    /// <summary>Whether this identifies a MetroDom instrument.</summary>
    public bool IsMetroDom => ManufacturerId == NonCommercialId && Family == MetroDomFamily;

    /// <summary>A known manufacturer's name, or null.</summary>
    public string? ManufacturerName => Manufacturers.Name(ManufacturerId);

    /// <summary>A MetroDom identity for <paramref name="model"/> at <paramref name="version"/>.</summary>
    public static DeviceIdentity MetroDom(int model, Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return new(NonCommercialId, MetroDomFamily, model, string.Create(
            CultureInfo.InvariantCulture,
            $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}.{Math.Max(0, version.Revision)}"));
    }

    /// <summary><c>F0 7E 7F 06 01 F7</c>: "every device, say who you are".</summary>
    public static NormalSysExEvent Request() => new([UniversalNonRealTime, AllDevices, GeneralInformation, IdentityRequestId, EndOfExclusive]);

    /// <summary>Whether <paramref name="midiEvent"/> is an Identity Request addressed to all devices or to <paramref name="deviceId"/>.</summary>
    public static bool IsRequest(MidiEvent midiEvent, byte deviceId = AllDevices) =>
        midiEvent is SysExEvent { Data: [UniversalNonRealTime, var target, GeneralInformation, IdentityRequestId, ..] }
        && (target == AllDevices || target == deviceId);

    /// <summary>Reads an Identity Reply. Anything else (including a request) returns false.</summary>
    public static bool TryParseReply(MidiEvent midiEvent, out DeviceIdentity identity)
    {
        identity = null!;
        if (midiEvent is not SysExEvent { Data: [UniversalNonRealTime, _, GeneralInformation, IdentityReplyId, .. var rest] })
        {
            return false;
        }

        if (rest is [.., EndOfExclusive])
        {
            rest = rest[..^1];
        }

        var idLength = rest is [0x00, ..] ? 3 : 1;
        if (rest.Length != idLength + 8 || rest.Any(b => b > 0x7F))
        {
            return false;
        }

        var manufacturer = string.Join(' ', rest[..idLength].Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
        var body = rest[idLength..];
        identity = new DeviceIdentity(
            manufacturer,
            body[0] | (body[1] << 7),
            body[2] | (body[3] << 7),
            string.Join('.', body[4..8].Select(b => b.ToString(CultureInfo.InvariantCulture))));
        return true;
    }

    /// <summary>This identity as an Identity Reply from <paramref name="deviceId"/>.</summary>
    public NormalSysExEvent ToReply(byte deviceId = AllDevices)
    {
        var manufacturer = ManufacturerId.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(h => byte.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        var version = Version.Split('.').Select(v => byte.Parse(v, CultureInfo.InvariantCulture));
        byte[] data =
        [
            UniversalNonRealTime, deviceId, GeneralInformation, IdentityReplyId,
            .. manufacturer,
            (byte)(Family & 0x7F), (byte)((Family >> 7) & 0x7F),
            (byte)(Model & 0x7F), (byte)((Model >> 7) & 0x7F),
            .. version,
            EndOfExclusive,
        ];
        return new NormalSysExEvent(data);
    }

    /// <summary>The reply to send if <paramref name="midiEvent"/> is an Identity Request for us, else null.</summary>
    public NormalSysExEvent? ReplyTo(MidiEvent midiEvent, byte deviceId = AllDevices) =>
        IsRequest(midiEvent, deviceId) ? ToReply(deviceId) : null;

    /// <summary>One line for people: "MetroDom virtual instrument v0.1.0.0", "Roland · family 2 model 7 · v1.0.0.0".</summary>
    public string Describe()
    {
        if (IsMetroDom)
        {
            var model = Model == VirtualModel ? "virtual instrument" : $"instrument model {Model}";
            return $"MetroDom {model} v{Version}";
        }

        var maker = ManufacturerName ?? $"manufacturer {ManufacturerId}";
        return string.Create(CultureInfo.InvariantCulture, $"{maker} · family {Family} model {Model} · v{Version}");
    }
}

/// <summary>A few SysEx manufacturer IDs, from the MIDI Association's list. Unknown IDs are shown as hex.</summary>
internal static class Manufacturers
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        ["01"] = "Sequential",
        ["07"] = "Kurzweil",
        ["0F"] = "Ensoniq",
        ["10"] = "Oberheim",
        ["11"] = "Apple",
        ["18"] = "E-mu",
        ["40"] = "Kawai",
        ["41"] = "Roland",
        ["42"] = "Korg",
        ["43"] = "Yamaha",
        ["44"] = "Casio",
        ["47"] = "Akai",
        ["7D"] = "Non-commercial",
        ["00 00 0E"] = "Alesis",
        ["00 20 29"] = "Focusrite/Novation",
        ["00 20 32"] = "Behringer",
        ["00 20 6B"] = "Arturia",
        ["00 21 09"] = "Native Instruments",
    };

    public static string? Name(string id) => Names.GetValueOrDefault(id);
}
