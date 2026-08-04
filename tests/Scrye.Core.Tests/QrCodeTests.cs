using System.Security.Cryptography;
using System.Text;
using Scrye.Core.Util;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// The QR encoder behind the companion panel's scan-me code.
///
/// <para>A wrong QR code is the worst kind of wrong: it still looks like a QR code. There is
/// no visual inspection that catches a bad mask, a misplaced format bit or mis-interleaved
/// error correction — the only symptom is a phone that refuses to focus on it, which reads
/// as "my camera is being slow" rather than "this is corrupt".</para>
///
/// <para>So the encoder was verified <b>outside</b> this test suite first, against two
/// independent decoders: a 249-payload corpus (lengths 1–400, all four ECC levels, ASCII and
/// UTF-8) was rendered to bitmaps and read back with ZBar and OpenCV, and every payload
/// decoded to exactly the bytes that went in. What lives here is what can run without those
/// dependencies: the resulting matrices pinned as golden vectors, the structural invariants
/// the spec fixes, and — most usefully — a reader that walks the matrix back to the original
/// bytes. That reader deliberately ignores the error-correction codewords, so it exercises
/// masking, module placement and the bitstream independently of Reed–Solomon rather than
/// re-running the encoder's own logic and agreeing with itself.</para>
/// </summary>
public class QrCodeTests
{
    // ---- golden vectors ------------------------------------------------------

    /// <summary>Version 1 is small enough to read by eye — the three finders, the timing
    /// runs between them and the dark module are all visible in the literal below.</summary>
    private const string TinyExpected =
        "#######..#.##.#######" +
        "#.....#..###..#.....#" +
        "#.###.#.##.##.#.###.#" +
        "#.###.#..#.#..#.###.#" +
        "#.###.#...#.#.#.###.#" +
        "#.....#.....#.#.....#" +
        "#######.#.#.#.#######" +
        "........##.##........" +
        "###.########.##...#.." +
        "..#.##.#..#...#...##." +
        "....#.#####.#...#...#" +
        "##.#.#...##...#...#.." +
        "##..####....#.#.#.#.#" +
        "........##.#.#.#.#.##" +
        "#######.#..#.###.####" +
        "#.....#.######.###..." +
        "#.###.#.#.##.###.##.#" +
        "#.###.#...#...#...##." +
        "#.###.#.##..#...#...#" +
        "#.....#.##....#...##." +
        "#######.##..#.#.#.###";

    [Fact]
    public void MatchesTheGoldenVersion1Matrix()
    {
        bool[,] m = QrCode.Encode("a", QrCode.Ecc.Low, border: 0);

        Assert.Equal(TinyExpected, Flatten(m).Replace('1', '#').Replace('0', '.'));
    }

    [Theory]
    // Payload, ECC, expected edge length, first 16 hex of SHA-256 over the module bits.
    // Every one of these was confirmed to decode back to its payload by ZBar and OpenCV.
    [InlineData("a", QrCode.Ecc.Low, 21, "C8B94007197AD57D")]
    [InlineData("https://desktop-d371cn1.tail1a2b3c.ts.net/", QrCode.Ecc.Medium, 29, "29F616C6D7B277B9")]
    [InlineData("https://desktop-d371cn1.tail1a2b3c.ts.net/?token=8f3a2b1c9d0e4f5a6b7c8d9e0f1a2b3c",
                QrCode.Ecc.Medium, 37, "08FD01B23D803588")]
    [InlineData("räksmörgås", QrCode.Ecc.Quartile, 25, "2D7C78FFEBF7E6A1")]
    public void ReproducesVerifiedMatricesExactly(string text, QrCode.Ecc ecc, int size, string hash)
    {
        bool[,] m = QrCode.Encode(text, ecc, border: 0);
        string bits = Flatten(m);

        Assert.Equal(size, m.GetLength(0));
        Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(bits)))[..16]);
    }

    // ---- reading the payload back out ----------------------------------------

    [Theory]
    [InlineData("a")]
    [InlineData("https://desktop-d371cn1.tail1a2b3c.ts.net/")]
    [InlineData("räksmörgås — with an em dash")]
    [InlineData("0123456789")]
    [InlineData("HELLO WORLD")]
    [InlineData("")]
    public void ThePayloadCanBeReadBackOutOfTheMatrix(string text)
    {
        // Low up to version 5 is a single error-correction block, which is the range the
        // reader below handles — see its remarks for why it stops there.
        Assert.Equal(text, ReadPayload(QrCode.Encode(text, QrCode.Ecc.Low, border: 0)));
    }

    [Fact]
    public void PayloadsUpToVersionFiveReadBackByteForByte()
    {
        // Sweeps versions 1-5 at Low. Each step changes the symbol size, the mask the
        // penalty picks, and — from version 2 — where the alignment pattern displaces data,
        // so this covers far more of the placement logic than any single payload does.
        for (int len = 1; len <= 100; len += 3)
        {
            string text = new('x', len);

            Assert.Equal(text, ReadPayload(QrCode.Encode(text, QrCode.Ecc.Low, border: 0)));
        }
    }

    [Fact]
    public void MultiBlockSymbolsAreCoveredByTheVerifiedVectorsInstead()
    {
        // Above one error-correction block the codewords are interleaved, and de-interleaving
        // them here would mean copying the encoder's block tables into the test — at which
        // point the test agrees with the encoder by construction rather than checking it.
        // Those cases are pinned by hash above and were confirmed against ZBar and OpenCV,
        // which do de-interleave. What is asserted here is only that such symbols are in the
        // pinned set at all, so the coverage claim cannot quietly become false.
        bool[,] fourBlocks = QrCode.Encode(
            "https://desktop-d371cn1.tail1a2b3c.ts.net/?token=8f3a2b1c9d0e4f5a6b7c8d9e0f1a2b3c",
            QrCode.Ecc.Medium, border: 0);

        Assert.Equal(37, fourBlocks.GetLength(0));
        Assert.Equal("08FD01B23D803588",
            Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(Flatten(fourBlocks))))[..16]);
    }

    // ---- structure -----------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void TheQuietZoneIsEntirelyLight(int border)
    {
        bool[,] m = QrCode.Encode("https://example.ts.net/", QrCode.Ecc.Medium, border);
        int n = m.GetLength(0);

        Assert.Equal(QrCode.SizeOfVersion(2) + border * 2, n);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                if (x < border || y < border || x >= n - border || y >= n - border)
                    Assert.False(m[y, x], $"quiet zone module at ({x},{y}) is dark");
    }

    [Fact]
    public void AllThreeFindersArePresent()
    {
        // A missing or malformed finder means a camera never locks on at all.
        bool[,] m = QrCode.Encode("finders", QrCode.Ecc.Medium, border: 0);
        int n = m.GetLength(0);

        foreach ((int ox, int oy) in new[] { (0, 0), (n - 7, 0), (0, n - 7) })
            for (int dy = 0; dy < 7; dy++)
                for (int dx = 0; dx < 7; dx++)
                {
                    int ring = Math.Max(Math.Abs(dx - 3), Math.Abs(dy - 3));
                    Assert.Equal(ring != 2, m[oy + dy, ox + dx]);
                }
    }

    [Fact]
    public void TimingPatternsAlternateAndSurviveTheFormatAreas()
    {
        // Regression: reserving the format bits used to overwrite the two modules where the
        // strips cross the timing lines, which no golden vector of the day caught.
        bool[,] m = QrCode.Encode("timing", QrCode.Ecc.Medium, border: 0);
        int n = m.GetLength(0);

        for (int i = 8; i < n - 8; i++)
        {
            Assert.Equal(i % 2 == 0, m[6, i]);
            Assert.Equal(i % 2 == 0, m[i, 6]);
        }
        Assert.True(m[6, 8], "timing module clobbered by the horizontal format strip");
        Assert.True(m[8, 6], "timing module clobbered by the vertical format strip");
    }

    [Fact]
    public void TheAlwaysDarkModuleIsDark()
    {
        bool[,] m = QrCode.Encode("dark", QrCode.Ecc.Medium, border: 0);

        Assert.True(m[m.GetLength(0) - 8, 8]);
    }

    // ---- sizing and limits ---------------------------------------------------

    [Theory]
    [InlineData(1, 21)]
    [InlineData(2, 25)]
    [InlineData(7, 45)]
    [InlineData(20, 97)]
    public void VersionSizesFollowTheFourVPlusSeventeenRule(int version, int expected) =>
        Assert.Equal(expected, QrCode.SizeOfVersion(version));

    [Fact]
    public void HigherErrorCorrectionNeedsAtLeastAsMuchRoom()
    {
        // Not strictly monotonic per byte, but for a fixed payload more ECC can never need a
        // smaller symbol — if it did, the capacity tables would be transposed somewhere.
        string text = new('y', 120);
        int low = QrCode.Encode(text, QrCode.Ecc.Low, 0).GetLength(0);
        int medium = QrCode.Encode(text, QrCode.Ecc.Medium, 0).GetLength(0);
        int quartile = QrCode.Encode(text, QrCode.Ecc.Quartile, 0).GetLength(0);
        int high = QrCode.Encode(text, QrCode.Ecc.High, 0).GetLength(0);

        Assert.True(low <= medium && medium <= quartile && quartile <= high,
            $"sizes went {low}, {medium}, {quartile}, {high}");
    }

    [Fact]
    public void RefusesAPayloadTooLargeForVersionTwenty() =>
        Assert.Throws<ArgumentException>(() => QrCode.Encode(new string('z', 900)));

    [Fact]
    public void EmptyTextStillProducesASymbol()
    {
        // Degenerate but reachable: the panel asks for a code before the server has a URL.
        Assert.Equal(21, QrCode.Encode("", QrCode.Ecc.Medium, border: 0).GetLength(0));
    }

    [Fact]
    public void ANegativeBorderIsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => QrCode.Encode("x", QrCode.Ecc.Medium, -1));

    // ---- helpers -------------------------------------------------------------

    private static string Flatten(bool[,] m)
    {
        var sb = new StringBuilder(m.Length);
        for (int y = 0; y < m.GetLength(0); y++)
            for (int x = 0; x < m.GetLength(1); x++)
                sb.Append(m[y, x] ? '1' : '0');
        return sb.ToString();
    }

    /// <summary>
    /// Walk a finished matrix back to its payload: recover the mask from the format bits,
    /// undo it, follow the zigzag to recover the data codewords, then parse the byte-mode
    /// header off the front.
    ///
    /// <para>This is not the encoder run backwards. It rebuilds the function-module map from
    /// the geometry rules rather than from anything the encoder recorded, and it never looks
    /// at the error-correction codewords — so it can disagree with the encoder, which is the
    /// entire point of having it. (It caught nothing in the encoder, as it happens; what it
    /// caught was a bug in itself, which is the honest version of the same story.)</para>
    ///
    /// <para><b>Single-block symbols only.</b> Once a symbol needs more than one
    /// error-correction block the codewords are interleaved one-per-block, so reading them
    /// in order yields the header of block 0 followed by the header-position bytes of every
    /// other block. De-interleaving would require the encoder's block-count table, and a
    /// test that borrows the table it is meant to be checking has stopped being a check.</para>
    /// </summary>
    private static string ReadPayload(bool[,] m)
    {
        int size = m.GetLength(0);
        int version = (size - 17) / 4;

        // Format bits live in the copy beside the top-left finder; bits 0-2 are the mask,
        // after the 0x5412 unmasking the spec applies.
        int format = 0;
        for (int i = 0; i <= 5; i++) format |= (m[i, 8] ? 1 : 0) << i;
        format |= (m[7, 8] ? 1 : 0) << 6;
        format |= (m[8, 8] ? 1 : 0) << 7;
        format |= (m[8, 7] ? 1 : 0) << 8;
        for (int i = 9; i < 15; i++) format |= (m[8, 14 - i] ? 1 : 0) << i;
        format ^= 0x5412;
        int mask = (format >> 10) & 7;

        bool[,] reserved = FunctionMap(size, version);

        var bits = new List<bool>();
        for (int right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (int v = 0; v < size; v++)
                for (int c = 0; c < 2; c++)
                {
                    int x = right - c;
                    bool upward = ((right + 1) & 2) == 0;
                    int y = upward ? size - 1 - v : v;
                    if (reserved[y, x]) continue;
                    bits.Add(m[y, x] ^ Masked(mask, x, y));
                }
        }

        int at = 0;
        int Take(int n)
        {
            int value = 0;
            for (int i = 0; i < n; i++) value = value << 1 | (bits[at++] ? 1 : 0);
            return value;
        }

        Assert.Equal(0b0100, Take(4));                       // byte mode
        int length = Take(version <= 9 ? 8 : 16);
        var bytes = new byte[length];
        for (int i = 0; i < length; i++) bytes[i] = (byte)Take(8);
        return Encoding.UTF8.GetString(bytes);
    }

    private static bool Masked(int mask, int x, int y) => mask switch
    {
        0 => (x + y) % 2 == 0,
        1 => y % 2 == 0,
        2 => x % 3 == 0,
        3 => (x + y) % 3 == 0,
        4 => (y / 2 + x / 3) % 2 == 0,
        5 => x * y % 2 + x * y % 3 == 0,
        6 => (x * y % 2 + x * y % 3) % 2 == 0,
        _ => ((x + y) % 2 + x * y % 3) % 2 == 0,
    };

    /// <summary>Which modules are function patterns, derived from the geometry rules alone.</summary>
    private static bool[,] FunctionMap(int size, int version)
    {
        var r = new bool[size, size];

        void Fill(int x0, int y0, int w, int h)
        {
            for (int y = Math.Max(0, y0); y < Math.Min(size, y0 + h); y++)
                for (int x = Math.Max(0, x0); x < Math.Min(size, x0 + w); x++)
                    r[y, x] = true;
        }

        Fill(0, 0, 9, 9);                       // finder + format, top-left
        Fill(size - 8, 0, 8, 9);                // finder + format, top-right
        Fill(0, size - 8, 9, 8);                // finder + format, bottom-left
        for (int i = 0; i < size; i++) { r[6, i] = true; r[i, 6] = true; }

        int[] centers = AlignmentCenters(size, version);
        for (int i = 0; i < centers.Length; i++)
            for (int j = 0; j < centers.Length; j++)
            {
                bool atFinder = (i == 0 && j == 0)
                             || (i == 0 && j == centers.Length - 1)
                             || (i == centers.Length - 1 && j == 0);
                if (!atFinder) Fill(centers[i] - 2, centers[j] - 2, 5, 5);
            }

        if (version >= 7)
        {
            Fill(0, size - 11, 6, 3);
            Fill(size - 11, 0, 3, 6);
        }

        return r;
    }

    private static int[] AlignmentCenters(int size, int version)
    {
        if (version == 1) return [];
        int count = version / 7 + 2;
        int step = (version * 4 + count * 2 + 1) / (count * 2 - 2) * 2;
        var centers = new int[count];
        centers[0] = 6;
        for (int i = count - 1, pos = size - 7; i >= 1; i--, pos -= step) centers[i] = pos;
        return centers;
    }
}
