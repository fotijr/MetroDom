namespace MetroDom.Core;

/// <summary>MIDI Control Change numbers MetroDom relies on. See docs/references/midi-essentials.md.</summary>
public static class ControlNumbers
{
    /// <summary>CC 7: channel volume.</summary>
    public const byte ChannelVolume = 7;

    /// <summary>CC 120: All Sound Off. Panic, de-energize immediately.</summary>
    public const byte AllSoundOff = 120;

    /// <summary>CC 121: Reset All Controllers.</summary>
    public const byte ResetAllControllers = 121;

    /// <summary>CC 123: All Notes Off.</summary>
    public const byte AllNotesOff = 123;
}
