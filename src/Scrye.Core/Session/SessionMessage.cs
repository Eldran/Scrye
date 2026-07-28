namespace Scrye.Core.Session;

/// <summary>Messages processed serially by the <see cref="MudSession"/> loop.
/// Everything that mutates session state (incoming data, user input, later:
/// timer fires and script calls) arrives as one of these, so ordering is
/// deterministic and there is no re-entrancy.</summary>
public abstract record SessionMessage
{
    /// <summary>Raw bytes arrived from the server.</summary>
    public sealed record DataArrived(byte[] Bytes) : SessionMessage;

    /// <summary>The user submitted a line of input.</summary>
    public sealed record UserInput(string Text) : SessionMessage;
}
