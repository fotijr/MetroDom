using Spectre.Console;

namespace MetroDom.Conductor.Cli;

/// <summary>Shared console conventions. Results go to stdout, diagnostics to stderr.</summary>
internal static class Ui
{
    /// <summary>Stderr, so piping stdout (e.g. <c>metrodom devices | grep</c>) stays clean.</summary>
    public static IAnsiConsole Error { get; } = AnsiConsole.Create(new AnsiConsoleSettings
    {
        Out = new AnsiConsoleOutput(Console.Error),
    });

    /// <summary>
    /// True when a human is at the keyboard: we may prompt and redraw a live view. False when
    /// piped, redirected, or under CI, where output must stay plain, line-oriented text.
    /// </summary>
    public static bool IsInteractive =>
        AnsiConsole.Profile.Capabilities.Interactive && !Console.IsInputRedirected && !Console.IsOutputRedirected;

    public static void Fail(string message) => Error.MarkupLine($"[red bold]error:[/] {Markup.Escape(message)}");

    public static void Warn(string message) => Error.MarkupLine($"[yellow]warning:[/] {Markup.Escape(message)}");

    public static void Note(string message) => Error.MarkupLine($"[grey]{Markup.Escape(message)}[/]");

    public static void Success(string message) => AnsiConsole.MarkupLine($"[green]✓[/] {Markup.Escape(message)}");
}
