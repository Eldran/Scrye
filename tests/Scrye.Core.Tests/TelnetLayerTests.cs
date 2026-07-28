using Scrye.Core.Net;
using Xunit;

namespace Scrye.Core.Tests;

public class TelnetLayerTests
{
    [Fact]
    public void RefusesWillWithDont()
    {
        var telnet = new TelnetLayer();
        byte[] data = telnet.Process(new byte[] { 255, 251, 1 }, out byte[] response); // IAC WILL ECHO
        Assert.Empty(data);
        Assert.Equal(new byte[] { 255, 254, 1 }, response);                             // IAC DONT ECHO
    }

    [Fact]
    public void RefusesDoWithWont()
    {
        var telnet = new TelnetLayer();
        byte[] data = telnet.Process(new byte[] { 255, 253, 31 }, out byte[] response); // IAC DO NAWS
        Assert.Empty(data);
        Assert.Equal(new byte[] { 255, 252, 31 }, response);                            // IAC WONT NAWS
    }

    [Fact]
    public void EscapedIacBecomesLiteralByte()
    {
        var telnet = new TelnetLayer();
        byte[] data = telnet.Process(new byte[] { (byte)'a', 255, 255, (byte)'b' }, out _);
        Assert.Equal(new byte[] { (byte)'a', 255, (byte)'b' }, data);
    }

    [Fact]
    public void StripsSubnegotiation()
    {
        var telnet = new TelnetLayer();
        // x IAC SB <71> 1 2 3 IAC SE y  ->  data = "xy"
        byte[] input = { (byte)'x', 255, 250, 71, 1, 2, 3, 255, 240, (byte)'y' };
        byte[] data = telnet.Process(input, out _);
        Assert.Equal(new byte[] { (byte)'x', (byte)'y' }, data);
    }

    [Fact]
    public void SplitAcrossChunksKeepsState()
    {
        var telnet = new TelnetLayer();
        // IAC arrives, then WILL ECHO in the next chunk
        byte[] d1 = telnet.Process(new byte[] { (byte)'a', 255 }, out byte[] r1);
        byte[] d2 = telnet.Process(new byte[] { 251, 1, (byte)'b' }, out byte[] r2);
        Assert.Equal(new byte[] { (byte)'a' }, d1);
        Assert.Empty(r1);
        Assert.Equal(new byte[] { (byte)'b' }, d2);
        Assert.Equal(new byte[] { 255, 254, 1 }, r2);
    }
}
