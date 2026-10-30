namespace Scrye.Core.Text;

/// <summary>
/// Where a scrollback view was in its <see cref="ScrollbackBuffer"/>, kept so the view can come
/// back to it (Joakim, 30 Oct 2026: switching between connected characters threw the chat pane
/// back to the start of the session). The place is anchored to the buffer's line SEQUENCE
/// numbers, not to a pixel offset or a line index, so it survives lines arriving and old ones
/// being trimmed off the front while the view was away.
///
/// <para>A view that was following the newest line comes back following it - at the newest
/// line, not where the newest line used to be. A view that was scrolled back to read comes back
/// to the same line, with the lines that arrived meanwhile counted as new.</para>
/// </summary>
/// <param name="Following">The view was at the bottom, following new lines.</param>
/// <param name="TopLine">The absolute line (sequence, fractional) at the top of the view.</param>
/// <param name="TotalAtSave">BaseSequence + Count when the place was taken: one past the newest line.</param>
/// <param name="PendingAtSave">New lines the view had not shown yet when the place was taken.</param>
public readonly record struct ScrollPlace(bool Following, double TopLine, long TotalAtSave, int PendingAtSave)
{
    /// <summary>A view's place now: its scroll offset and line height turned into a line of the buffer.</summary>
    public static ScrollPlace Capture(bool following, double offsetY, double lineHeight,
                                      long baseSequence, int count, int pending)
    {
        double top = baseSequence + (lineHeight > 0 ? Math.Max(0, offsetY) / lineHeight : 0);
        return new ScrollPlace(following, top, baseSequence + count, following ? 0 : Math.Max(0, pending));
    }

    /// <summary>
    /// The offset to scroll to now, whether that is following, and how many lines are new. The
    /// newest line when following; otherwise the saved line, clamped to what the buffer still
    /// holds (a line trimmed off the front leaves you at the oldest one kept) - and a place
    /// that lands at the bottom anyway is following again, as the view itself would decide.
    /// </summary>
    public (double OffsetY, bool Following, int Pending) Restore(long baseSequence, int count,
                                                                 double lineHeight, double viewport)
    {
        double bottom = Math.Max(0, count * lineHeight - Math.Max(0, viewport));
        if (Following) return (bottom, true, 0);
        // (no line height yet: every line is at 0, which is the bottom - following, below)
        double y = Math.Clamp((TopLine - baseSequence) * lineHeight, 0, bottom);
        if (y >= bottom - 2) return (bottom, true, 0);          // the view's own "at the bottom"
        long arrived = Math.Max(0, baseSequence + count - TotalAtSave);
        return (y, false, (int)Math.Min(int.MaxValue, PendingAtSave + arrived));
    }
}
