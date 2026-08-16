using System.Collections.Generic;
using System.Reflection;
using Scrye.Core.Session;
using Scrye.Core.State;
using Scrye.Core.Text;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// Switching characters without dropping the connection. 3Scapes registers a MIP client per
/// <b>login</b>, not per socket, so the handshake sent at connect does not carry over — the
/// second character used to get no vitals feed at all, with the previous character's numbers
/// still sitting in the HUD, and the only cure was reconnecting.
///
/// <para>What is pinned here is the shape of the fix rather than its plumbing: a password
/// prompt <i>during</i> the first login must change nothing (that login is already being
/// handshaked), a password prompt afterwards must re-arm, and re-arming must take the old
/// character's data with it while leaving the user's own variables alone.</para>
/// </summary>
public class MipReloginTests
{
    private static MudSession Connected(out AnsiParser ansi, out List<string> output)
    {
        var session = new MudSession(new Scrye.Core.Model.WorldProfile
        {
            Host = "localhost",
            Port = 1,
            EnableMip = true,
        });
        ansi = Private<AnsiParser>(session, "_ansi");
        var lines = new List<string>();
        session.LineReady += l => lines.Add(l.PlainText);
        output = lines;
        // What ConnectAsync does before any bytes arrive; the socket itself is not needed.
        typeof(MudSession).GetMethod("ResetMipForConnect", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(session, null);
        return session;
    }

    private static T Private<T>(MudSession s, string field) =>
        (T)typeof(MudSession).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(s)!;

    private static void SetPrivate(MudSession s, string field, object value) =>
        typeof(MudSession).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(s, value);

    private static int Handshakes(List<string> output) =>
        output.FindAll(l => l.Contains("handshake sent")).Count;

    [Theory]
    [InlineData("Password: ", true)]
    [InlineData("Please enter your password:", true)]
    [InlineData("What is your name:", false)]
    [InlineData("You feel the password slip from your mind.", false)]
    public void Password_prompt_is_told_apart_from_prose(string line, bool expected) =>
        Assert.Equal(expected, AutoLogin.IsPasswordPrompt(line));

    [Fact]
    public void The_password_prompt_of_the_first_login_does_not_re_arm()
    {
        MudSession session = Connected(out AnsiParser ansi, out List<string> output);

        ansi.Feed("Password: \n");

        // Still pending from connect: re-arming here would clear state mid-login for nothing.
        Assert.True(Private<bool>(session, "_mipPending"));
        Assert.DoesNotContain(output, l => l.Contains("new login"));
    }

    [Fact]
    public void The_first_bare_prompt_sends_one_handshake()
    {
        MudSession session = Connected(out AnsiParser ansi, out List<string> output);

        ansi.Feed("Password: \n");
        ansi.Feed(">\n");

        Assert.False(Private<bool>(session, "_mipPending"));
        Assert.True(Private<bool>(session, "_mipSent"));
        Assert.Equal(1, Handshakes(output));
    }

    [Fact]
    public void A_second_login_re_arms_and_gets_its_own_handshake()
    {
        MudSession session = Connected(out AnsiParser ansi, out List<string> output);
        ansi.Feed("Password: \n");
        ansi.Feed(">\n");
        SetPrivate(session, "_mipGotData", true);        // character one is playing

        ansi.Feed("Password: \n");                       // character two takes the connection

        Assert.True(Private<bool>(session, "_mipPending"));
        Assert.False(Private<bool>(session, "_mipGotData"));
        Assert.Contains(output, l => l.Contains("new login"));

        ansi.Feed(">\n");
        Assert.Equal(2, Handshakes(output));
    }

    [Fact]
    public void Re_arming_clears_the_previous_characters_data_but_not_the_users()
    {
        MudSession session = Connected(out AnsiParser ansi, out List<string> output);
        ansi.Feed("Password: \n");
        ansi.Feed(">\n");

        session.Variables.Set("hp", "7053");
        session.Variables.Set("gline1", "H[7053|7053]");
        session.Variables.Set("myalias", "keep me");     // user territory
        session.GameState.Set("character.health.current", StateValue.Num(7053));
        session.GameState.Set("enemy.name", StateValue.Str("a mutant"));
        session.GameState.Set("vik.seid", StateValue.Str("5310"));
        string? mipId = session.Variables.Get("mipid");

        ansi.Feed("Password: \n");

        Assert.Null(session.Variables.Get("hp"));
        Assert.Null(session.Variables.Get("gline1"));
        Assert.Equal("keep me", session.Variables.Get("myalias"));
        // The client id is per-connection, not per-character: keeping it means the new
        // login re-registers under the same id rather than leaking a fresh one each time.
        Assert.Equal(mipId, session.Variables.Get("mipid"));

        Assert.True(session.GameState.Get("character.health.current").IsNull);
        Assert.True(session.GameState.Get("enemy.name").IsNull);
        // vik.* matters specifically: 3s-vitals picks which bars to draw from whether the
        // viking feed is live, so a stale one would leave a non-Viking showing Seid/Vig/Rad.
        Assert.True(session.GameState.Get("vik.seid").IsNull);
    }

    [Fact]
    public void A_world_with_mip_off_is_left_entirely_alone()
    {
        var session = new MudSession(new Scrye.Core.Model.WorldProfile
        {
            Host = "localhost",
            Port = 1,
            EnableMip = false,
        });
        AnsiParser ansi = Private<AnsiParser>(session, "_ansi");
        var output = new List<string>();
        session.LineReady += l => output.Add(l.PlainText);
        session.Variables.Set("hp", "100");

        ansi.Feed("Password: \n");

        Assert.Equal("100", session.Variables.Get("hp"));
        Assert.DoesNotContain(output, l => l.Contains("new login"));
    }
}
