namespace MetroDom.Core.Timing;

/// <summary>
/// Live song-structure control on top of <see cref="LatencyScheduler"/> jumps (ADR 0012):
/// <b>vamp</b> repeats the current section until released, <b>outro</b> goes to the outro when the
/// current section ends. Both act at section boundaries, so the band never cuts off mid-phrase.
/// Every method returns a line for people saying what will happen.
/// </summary>
/// <remarks>Thread-safe as far as the scheduler is: keys and foot pedals may call from different threads.</remarks>
public sealed class SectionNavigator
{
    private readonly LatencyScheduler _scheduler;

    /// <param name="scheduler">The playing song.</param>
    /// <param name="sections">Its sections in the scheduler's song time (shifted after a count-in).</param>
    public SectionNavigator(LatencyScheduler scheduler, IReadOnlyList<Section> sections)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(sections);
        _scheduler = scheduler;
        Sections = sections;
    }

    public IReadOnlyList<Section> Sections { get; }

    /// <summary>The section being heard, or -1 (no sections, or still counting in).</summary>
    public int Current
    {
        get
        {
            var position = _scheduler.Position;
            return Sections.Count == 0 || position < Sections[0].Start ? -1 : SongSections.IndexAt(Sections, position);
        }
    }

    /// <summary>Whether a vamp is armed.</summary>
    public bool Vamping => _scheduler.PendingJump is { Repeat: true };

    /// <summary>Where an armed one-shot jump goes, or null.</summary>
    public Section? Heading => _scheduler.PendingJump is { Repeat: false } jump
        ? Sections.FirstOrDefault(s => s.Start == jump.To)
        : null;

    /// <summary>Vamp on (repeat the current section) or off (carry on when it ends).</summary>
    public string ToggleVamp()
    {
        if (Sections.Count == 0)
        {
            return "No sections: the song has no markers to vamp on.";
        }

        if (Vamping)
        {
            _scheduler.Disarm();
            return "Vamp off: carrying on when this section ends.";
        }

        // The song clock runs ahead of what's heard; if it already left this section, vamp the next.
        for (var i = Math.Max(0, Current); i < Sections.Count; i++)
        {
            var section = Sections[i];
            if (section.End > section.Start && _scheduler.ArmJump(section.End, section.Start, repeat: true))
            {
                return $"Vamp on: repeating {section.Name} until released.";
            }
        }

        return "Too late to vamp: the song is ending.";
    }

    /// <summary>Goes to the outro when the current section ends.</summary>
    public string JumpToOutro()
    {
        var outro = SongSections.OutroIndex(Sections);
        if (outro < 0)
        {
            return "No sections: the song has no markers to jump to.";
        }

        for (var i = Math.Max(0, Current); i < outro; i++)
        {
            if (Sections[i].End == Sections[outro].Start)
            {
                _scheduler.Disarm();
                return $"{Sections[outro].Name} is next.";
            }

            if (_scheduler.ArmJump(Sections[i].End, Sections[outro].Start))
            {
                return $"Going to {Sections[outro].Name} after {Sections[i].Name}.";
            }
        }

        return $"Already in {Sections[outro].Name}.";
    }

    /// <summary>One line for the status view: "Chorus (3/6) · vamping" or null without sections.</summary>
    public string? Describe()
    {
        if (Sections.Count == 0)
        {
            return null;
        }

        var current = Current;
        var where = current < 0 ? "counting in" : $"{Sections[current].Name} ({current + 1}/{Sections.Count})";
        return Vamping ? $"{where} · vamping" : Heading is { } next ? $"{where} · then {next.Name}" : where;
    }
}
