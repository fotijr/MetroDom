namespace MetroDom.Conductor;

/// <summary>The user asked for something impossible (bad port, missing file…). Exit code 2.</summary>
internal sealed class UsageException(string message) : Exception(message);
