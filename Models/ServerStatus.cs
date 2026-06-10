using System;

namespace WhisperNote.Models;

public readonly struct ServerStatus
{
    public string Message { get; }
    public ServerStatusKind Kind { get; }

    public ServerStatus(string message, ServerStatusKind kind)
    {
        Message = message;
        Kind = kind;
    }

    public static ServerStatus Offline => new("Server offline", ServerStatusKind.Gray);
    public static ServerStatus Online => new("Server online", ServerStatusKind.Green);
    public static ServerStatus Launching => new("Launching...", ServerStatusKind.Orange);
    public static ServerStatus Failed(string message) => new(message, ServerStatusKind.Red);
    public static ServerStatus Cloud(string providerName) => new($"Cloud · {providerName}", ServerStatusKind.Green);
}

public enum ServerStatusKind
{
    Gray,
    Green,
    Orange,
    Red
}
