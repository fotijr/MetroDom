namespace MetroDom.Conductor;

/// <summary>Minimal parser: <c>command positional... --option value --flag</c>.</summary>
internal sealed class CommandLine
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);

    private CommandLine(string? command, List<string> positionals)
    {
        Command = command;
        Positionals = positionals;
    }

    public string? Command { get; }

    public IReadOnlyList<string> Positionals { get; }

    public static CommandLine Parse(IReadOnlyList<string> args)
    {
        var positionals = new List<string>();
        var options = new List<(string Name, string? Value)>();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                var hasValue = i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
                options.Add((arg[2..], hasValue ? args[++i] : null));
            }
            else
            {
                positionals.Add(arg);
            }
        }

        var command = positionals.Count > 0 ? positionals[0] : null;
        var parsed = new CommandLine(command, positionals.Skip(1).ToList());
        foreach (var (name, value) in options)
        {
            parsed._options[name] = value;
        }

        return parsed;
    }

    public string? Positional(int index) => index < Positionals.Count ? Positionals[index] : null;

    public bool Has(string name) => _options.ContainsKey(name);

    public string? Option(string name) => _options.GetValueOrDefault(name);

    /// <summary>Parses <c>--mute 10,11</c> (1-based channels, as musicians count) to 0-based.</summary>
    public IReadOnlyList<int> Channels(string name)
    {
        var raw = Option(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var c) && c is >= 1 and <= 16
                ? c - 1
                : throw new UsageException($"--{name}: '{s}' is not a MIDI channel (1-16)."))
            .ToArray();
    }
}

internal sealed class UsageException(string message) : Exception(message);
