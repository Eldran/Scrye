using System.Text.Json.Serialization;
using Scrye.Core.Text;

namespace Scrye.Companion.Protocol;

/// <summary>
/// One distinct (foreground, background, flags) combination within a batch. Spans reference
/// these by index instead of repeating colours, which is where the wire savings are: a burst
/// of combat lines typically uses a handful of styles across hundreds of spans (§3.1).
///
/// <para>Colours are "#RRGGBB". They are NOT palette indices — <c>AnsiParser</c> resolves
/// ANSI 16/256 codes to 24-bit <see cref="Rgb"/> at parse time and discards the index, so
/// there is nothing to send but the resolved colour (§3.3).</para>
/// </summary>
public sealed record StyleDto(
    [property: JsonPropertyName("fg")] string Fg,
    [property: JsonPropertyName("bg")] string Bg,
    [property: JsonPropertyName("flags")] RunFlags Flags)
{
    public static StyleDto From(in StyledRun run) =>
        new(run.Fore.ToHex(), run.Back.ToHex(), run.Flags);
}

/// <summary>A clickable region: an MXP <c>&lt;SEND&gt;</c>/<c>&lt;A&gt;</c> action or an
/// auto-detected URL. Mirrors <see cref="LinkInfo"/>. <see cref="IsUrl"/> means open in a
/// browser; otherwise <see cref="Action"/> is a command, and <see cref="Prompt"/> asks the
/// client to put it in the input box rather than send it.</summary>
public sealed record LinkDto(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("isUrl")] bool IsUrl,
    [property: JsonPropertyName("prompt")] bool Prompt,
    [property: JsonPropertyName("hint")] string? Hint)
{
    public static LinkDto From(LinkInfo link) =>
        new(link.Action, link.IsUrl, link.Prompt, link.Hint);
}

/// <summary>A run of text sharing one style. <see cref="StyleIndex"/> points into the
/// owning <see cref="OutputBatchMessage.Styles"/>.</summary>
public sealed record OutputSpanDto(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("s")] int StyleIndex,
    [property: JsonPropertyName("link")] LinkDto? Link = null);

/// <summary>
/// One output line. <see cref="IsPrompt"/> marks a line flushed by a telnet GA/EOR rather
/// than a newline — the server is waiting for input on it. Mobile clients should anchor the
/// input bar to the prompt instead of letting it scroll away, which is why it is on the wire.
/// </summary>
public sealed record OutputLineDto(
    [property: JsonPropertyName("sequence")] long Sequence,
    [property: JsonPropertyName("timestamp")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("prompt")] bool IsPrompt,
    [property: JsonPropertyName("spans")] IReadOnlyList<OutputSpanDto> Spans);

/// <summary>
/// A batch of output lines plus the style table they index into. One frame per UI flush
/// (a 33 ms <c>DispatcherTimer</c> tick in <c>WorldViewModel</c>), never one frame per line:
/// 3Scapes combat can emit hundreds of lines a second (§3.1).
/// </summary>
public sealed record OutputBatchMessage(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("styles")] IReadOnlyList<StyleDto> Styles,
    [property: JsonPropertyName("lines")] IReadOnlyList<OutputLineDto> Lines)
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.OutputBatch;
}

/// <summary>
/// Lines a trigger routed into a named capture pane — chat, tells, combat log.
///
/// <para><b>Why this is a separate message rather than a <c>pane</c> field on
/// <see cref="OutputLineDto"/>:</b> capture and gag are independent trigger actions, and the
/// common chat setup uses both — route to a pane <i>and</i> hide from the main output. A
/// gagged line never reaches <c>LineReady</c>, so it never enters scrollback and never
/// appears in an <see cref="OutputBatchMessage"/>. Tagging main-stream lines would therefore
/// miss precisely the lines a chat view exists to show.</para>
///
/// <para>Sequences are real: each capture pane owns a <c>ScrollbackBuffer</c>, so pane lines
/// have their own monotonic sequence space, per pane. Resume for panes is not implemented
/// yet — a reconnecting client rebuilds them — but the numbering is already correct for it.</para>
/// </summary>
public sealed record PaneOutputMessage(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("pane")] string Pane,
    [property: JsonPropertyName("styles")] IReadOnlyList<StyleDto> Styles,
    [property: JsonPropertyName("lines")] IReadOnlyList<OutputLineDto> Lines)
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.PaneOutput;
}
