using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scrye.Core.Events;

/// <summary>Header line of a <c>.scryerec</c> recording.</summary>
public sealed record RecordingHeader
{
    public int Version { get; init; } = 1;
    public string World { get; init; } = "";
    public DateTimeOffset StartedUtc { get; init; }
}

/// <summary>A loaded recording: its header plus the ordered event stream.</summary>
public sealed class SessionRecording
{
    public RecordingHeader Header { get; }
    public IReadOnlyList<SessionEvent> Events { get; }

    public SessionRecording(RecordingHeader header, IReadOnlyList<SessionEvent> events)
    {
        Header = header;
        Events = events;
    }

    /// <summary>Wall-clock span from the first to the last event (zero if &lt;2 events).</summary>
    public TimeSpan Duration =>
        Events.Count < 2 ? TimeSpan.Zero : Events[^1].TimeUtc - Events[0].TimeUtc;
}

/// <summary>
/// Captures the full event stream and persists it as a <c>.scryerec</c> file:
/// JSON-lines (one header object, then one event per line). Line-oriented so it
/// streams, appends, and diffs in git cleanly — and so a corrupt tail never loses
/// the whole capture. This is Foundation A's record half; replay is
/// <see cref="SessionReplayer"/>.
/// </summary>
public sealed class SessionRecorder : IEventSink
{
    private static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly List<SessionEvent> _events = new();

    public RecordingHeader Header { get; }
    public IReadOnlyList<SessionEvent> Events => _events;

    public SessionRecorder(string world, DateTimeOffset startedUtc)
        => Header = new RecordingHeader { World = world, StartedUtc = startedUtc };

    public void OnEvent(SessionEvent ev) => _events.Add(ev);

    /// <summary>Serialize header + events to JSON-lines text.</summary>
    public string ToJsonLines()
    {
        var sb = new StringBuilder();
        sb.Append(JsonSerializer.Serialize(Header, Compact)).Append('\n');
        foreach (SessionEvent ev in _events)
            sb.Append(JsonSerializer.Serialize(ev, Compact)).Append('\n');
        return sb.ToString();
    }

    public void Save(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, ToJsonLines());
    }

    public static SessionRecording Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>Parse JSON-lines text. Blank lines are skipped; a missing/invalid
    /// header yields an empty default so partial captures still load.</summary>
    public static SessionRecording Parse(string text)
    {
        string[] lines = text.Split('\n');
        RecordingHeader header = new();
        var events = new List<SessionEvent>();
        bool haveHeader = false;

        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;

            if (!haveHeader)
            {
                try { header = JsonSerializer.Deserialize<RecordingHeader>(line, Compact) ?? header; }
                catch (JsonException) { /* first line wasn't a header; fall through to events */ }
                haveHeader = true;
                if (line.Contains("\"version\"")) continue; // it really was the header line
            }

            try
            {
                SessionEvent? ev = JsonSerializer.Deserialize<SessionEvent>(line, Compact);
                if (ev is not null) events.Add(ev);
            }
            catch (JsonException) { /* skip a corrupt line rather than fail the load */ }
        }

        return new SessionRecording(header, events);
    }
}
