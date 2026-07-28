using System.Text;
using Scrye.Core.Net;
using Xunit;

namespace Scrye.Core.Tests;

public class TelnetLayerTests
{
    private static (TelnetLayer t, List<byte> sent) NewLayer()
    {
        var t = new TelnetLayer();
        var sent = new List<byte>();
        t.SendData += b => sent.AddRange(b);
        return (t, sent);
    }

    [Fact]
    public void SupportedWillGetsDo()
    {
        var (t, sent) = NewLayer();
        byte[] data = t.Process(new byte[] { 255, 251, 201 });  // IAC WILL GMCP
        Assert.Empty(data);
        Assert.Equal(new byte[] { 255, 253, 201 }, sent);       // IAC DO GMCP
    }

    [Fact]
    public void UnsupportedWillGetsDont()
    {
        var (t, sent) = NewLayer();
        t.Process(new byte[] { 255, 251, 99 });                 // IAC WILL <99>
        Assert.Equal(new byte[] { 255, 254, 99 }, sent);        // IAC DONT 99
    }

    [Fact]
    public void DoNawsRepliesWillAndSize()
    {
        var (t, sent) = NewLayer();
        t.WindowSize = () => (100, 30);
        t.Process(new byte[] { 255, 253, 31 });                 // IAC DO NAWS
        // IAC WILL NAWS, then IAC SB NAWS 0 100 0 30 IAC SE
        Assert.Equal(new byte[] { 255, 251, 31, 255, 250, 31, 0, 100, 0, 30, 255, 240 }, sent);
    }

    [Fact]
    public void EchoWillTogglesServerEcho()
    {
        var (t, _) = NewLayer();
        bool? state = null;
        t.ServerEchoChanged += on => state = on;
        t.Process(new byte[] { 255, 251, 1 });                  // WILL ECHO
        Assert.True(state);
        t.Process(new byte[] { 255, 252, 1 });                  // WONT ECHO
        Assert.False(state);
    }

    [Fact]
    public void TerminalTypeCyclesThroughThree()
    {
        var (t, sent) = NewLayer();
        t.Process(new byte[] { 255, 253, 24 });                 // DO TTYPE -> WILL TTYPE
        sent.Clear();
        byte[] send = { 255, 250, 24, 1, 255, 240 };            // IAC SB TTYPE SEND IAC SE
        t.Process(send);
        string first = Ascii(sent); sent.Clear();
        t.Process(send); string second = Ascii(sent); sent.Clear();
        t.Process(send); string third = Ascii(sent);
        Assert.Contains("Scrye", first);
        Assert.Contains("XTERM", second);
        Assert.Contains("MTTS", third);
    }

    [Fact]
    public void GmcpMessageIsParsed()
    {
        var (t, _) = NewLayer();
        string? pkg = null, json = null;
        t.GmcpReceived += (p, j) => { pkg = p; json = j; };
        var sb = new List<byte> { 255, 250, 201 };
        sb.AddRange(Encoding.UTF8.GetBytes("Char.Vitals {\"hp\":42}"));
        sb.Add(255); sb.Add(240);
        t.Process(sb.ToArray());
        Assert.Equal("Char.Vitals", pkg);
        Assert.Equal("{\"hp\":42}", json);
    }

    [Fact]
    public void SendGmcpWrapsCorrectly()
    {
        var (t, sent) = NewLayer();
        t.SendGmcp("Core.Hello", "{\"client\":\"Scrye\"}");
        Assert.Equal(255, sent[0]); Assert.Equal(250, sent[1]); Assert.Equal(201, sent[2]);
        Assert.Equal(240, sent[^1]); Assert.Equal(255, sent[^2]);
        string body = Encoding.UTF8.GetString(sent.Skip(3).Take(sent.Count - 5).ToArray());
        Assert.Equal("Core.Hello {\"client\":\"Scrye\"}", body);
    }

    [Fact]
    public void MsspIsParsed()
    {
        var (t, _) = NewLayer();
        IReadOnlyDictionary<string, string>? vars = null;
        t.MsspReceived += d => vars = d;
        var sb = new List<byte> { 255, 250, 70, 1 };
        sb.AddRange(Encoding.ASCII.GetBytes("NAME")); sb.Add(2); sb.AddRange(Encoding.ASCII.GetBytes("3Scapes"));
        sb.Add(1); sb.AddRange(Encoding.ASCII.GetBytes("PLAYERS")); sb.Add(2); sb.AddRange(Encoding.ASCII.GetBytes("42"));
        sb.Add(255); sb.Add(240);
        t.Process(sb.ToArray());
        Assert.NotNull(vars);
        Assert.Equal("3Scapes", vars!["NAME"]);
        Assert.Equal("42", vars["PLAYERS"]);
    }

    [Fact]
    public void EscapedIacBecomesLiteralByte()
    {
        var (t, _) = NewLayer();
        byte[] data = t.Process(new byte[] { (byte)'a', 255, 255, (byte)'b' });
        Assert.Equal(new byte[] { (byte)'a', 255, (byte)'b' }, data);
    }

    [Fact]
    public void DataAroundNegotiationSurvives()
    {
        var (t, _) = NewLayer();
        byte[] data = t.Process(new byte[] { (byte)'x', 255, 251, 201, (byte)'y' }); // x IAC WILL GMCP y
        Assert.Equal(new byte[] { (byte)'x', (byte)'y' }, data);
    }

    private static string Ascii(List<byte> b) => Encoding.ASCII.GetString(b.ToArray());
}
