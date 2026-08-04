namespace Scrye.Core.Util;

/// <summary>
/// A minimal QR Code encoder — byte mode only, versions 1–20, ISO/IEC 18004.
///
/// <para>This exists for one screen: the companion panel shows the phone's URL as a code you
/// point a camera at. Typing <c>https://desktop-d371cn1.tail1a2b3c.ts.net/</c> on a phone
/// keyboard is the kind of small friction that stops a feature being used, and the token
/// path has the same problem in a worse form.</para>
///
/// <para>Hand-rolled rather than a package because every other assembly below the UI takes
/// no NuGet dependency, and the scope needed here is genuinely small: one encoding mode, no
/// Kanji, no structured append, no image output — the caller gets a bool matrix and decides
/// how to draw it. What it does <b>not</b> skimp on is correctness: Reed–Solomon, the full
/// mask-penalty evaluation and the BCH format/version bits are all here, because a QR code
/// that is subtly wrong still <em>looks</em> like a QR code. The tests pin it against known
/// vectors for exactly that reason.</para>
/// </summary>
public static class QrCode
{
    /// <summary>Error-correction level. Higher levels survive more damage but hold less
    /// data. <see cref="Medium"/> is the default here: a code on a screen is not going to be
    /// smudged or printed badly, and the extra capacity keeps the version — and so the
    /// module count — down, which matters when the panel is 300 px wide.</summary>
    public enum Ecc { Low = 0, Medium = 1, Quartile = 2, High = 3 }

    /// <summary>
    /// Encode <paramref name="text"/> as a square matrix of modules, <c>true</c> meaning
    /// dark. The smallest version that fits is chosen automatically.
    /// </summary>
    /// <param name="text">The payload. Encoded as UTF-8 in byte mode.</param>
    /// <param name="ecc">Error-correction level.</param>
    /// <param name="border">Quiet-zone width in modules added on every side. The spec
    /// requires 4; scanners are unreliable without it, so this is not merely cosmetic.</param>
    /// <exception cref="ArgumentException">The text does not fit in version 20 at this
    /// error-correction level.</exception>
    public static bool[,] Encode(string text, Ecc ecc = Ecc.Medium, int border = 4)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegative(border);

        byte[] data = System.Text.Encoding.UTF8.GetBytes(text);

        int version = SmallestVersionFor(data.Length, ecc);
        byte[] codewords = BuildCodewords(data, version, ecc);

        var m = new Matrix(version);
        m.DrawFunctionPatterns();
        m.DrawCodewords(codewords);
        m.ApplyBestMask(ecc);

        return m.ToArray(border);
    }

    /// <summary>The module count along one edge, excluding the quiet zone.</summary>
    public static int SizeOfVersion(int version) => version * 4 + 17;

    // ---- capacity ------------------------------------------------------------

    /// <summary>Total data codewords available at each version/ECC, i.e. after the
    /// error-correction codewords are subtracted. Indexed [version-1, ecc].</summary>
    private static readonly short[,] DataCodewords =
    {
        //  L     M     Q     H
        {   19,   16,   13,    9 },  // 1
        {   34,   28,   22,   16 },
        {   55,   44,   34,   26 },
        {   80,   64,   48,   36 },
        {  108,   86,   62,   46 },  // 5
        {  136,  108,   76,   60 },
        {  156,  124,   88,   66 },
        {  194,  154,  110,   86 },
        {  232,  182,  132,  100 },
        {  274,  216,  154,  122 },  // 10
        {  324,  254,  180,  140 },
        {  370,  290,  206,  158 },
        {  428,  334,  244,  180 },
        {  461,  365,  261,  197 },
        {  523,  415,  295,  223 },  // 15
        {  589,  453,  325,  253 },
        {  647,  507,  367,  283 },
        {  721,  563,  397,  313 },
        {  795,  627,  445,  341 },
        {  861,  669,  485,  385 },  // 20
    };

    /// <summary>Error-correction codewords per block, indexed [version-1, ecc].</summary>
    private static readonly byte[,] EccPerBlock =
    {
        { 7, 10, 13, 17 }, { 10, 16, 22, 28 }, { 15, 26, 18, 22 }, { 20, 18, 26, 16 },
        { 26, 24, 18, 22 }, { 18, 16, 24, 28 }, { 20, 18, 18, 26 }, { 24, 22, 22, 26 },
        { 30, 22, 20, 24 }, { 18, 26, 24, 28 }, { 20, 30, 28, 24 }, { 24, 22, 26, 28 },
        { 26, 22, 24, 22 }, { 30, 24, 20, 24 }, { 22, 24, 30, 24 }, { 24, 28, 24, 30 },
        { 28, 28, 28, 28 }, { 30, 26, 28, 28 }, { 28, 26, 26, 26 }, { 28, 26, 30, 28 },
    };

    /// <summary>Number of error-correction blocks, indexed [version-1, ecc]. Data is split
    /// across blocks so a burst of damage cannot exhaust one block's correction budget.</summary>
    private static readonly byte[,] NumBlocks =
    {
        { 1, 1, 1, 1 }, { 1, 1, 1, 1 }, { 1, 1, 2, 2 }, { 1, 2, 2, 4 },
        { 1, 2, 4, 4 }, { 2, 4, 4, 4 }, { 2, 4, 6, 5 }, { 2, 4, 6, 6 },
        { 2, 5, 8, 8 }, { 4, 5, 8, 8 }, { 4, 5, 8, 11 }, { 4, 8, 10, 11 },
        { 4, 9, 12, 16 }, { 4, 9, 16, 16 }, { 6, 10, 12, 18 }, { 6, 10, 17, 16 },
        { 6, 11, 16, 19 }, { 6, 13, 18, 21 }, { 7, 14, 21, 25 }, { 8, 16, 20, 25 },
    };

    private static int SmallestVersionFor(int byteCount, Ecc ecc)
    {
        for (int v = 1; v <= 20; v++)
        {
            // 4 bits of mode indicator + the length field + the payload itself.
            int lengthBits = v <= 9 ? 8 : 16;
            int capacityBits = DataCodewords[v - 1, (int)ecc] * 8;
            if (4 + lengthBits + byteCount * 8 <= capacityBits) return v;
        }

        throw new ArgumentException(
            $"{byteCount} bytes does not fit in a version 20 QR code at ECC {ecc}.", nameof(byteCount));
    }

    // ---- bitstream + Reed-Solomon --------------------------------------------

    private static byte[] BuildCodewords(byte[] data, int version, Ecc ecc)
    {
        int totalDataCodewords = DataCodewords[version - 1, (int)ecc];
        var bits = new BitSink(totalDataCodewords);

        bits.Append(0b0100, 4);                              // byte mode
        bits.Append((uint)data.Length, version <= 9 ? 8 : 16);
        foreach (byte b in data) bits.Append(b, 8);

        // Terminator, then pad to a byte boundary, then the spec's alternating pad bytes.
        int capacityBits = totalDataCodewords * 8;
        bits.Append(0, Math.Min(4, capacityBits - bits.Length));
        bits.Append(0, (8 - bits.Length % 8) % 8);
        for (bool ec = true; bits.Length < capacityBits; ec = !ec)
            bits.Append(ec ? 0xEC : 0x11u, 8);

        return InterleaveWithEcc(bits.ToBytes(), version, ecc);
    }

    /// <summary>
    /// Split the data into blocks, compute Reed–Solomon parity per block, then interleave.
    ///
    /// <para>The interleaving is the part that is easy to get wrong and impossible to notice
    /// by eye: block sizes differ by one codeword between the short and long groups, and the
    /// final stream takes one codeword from each block in turn, skipping blocks that have
    /// run out. Get it wrong and the code scans as garbage — or, worse, occasionally scans
    /// correctly on short inputs where every block happens to be the same length.</para>
    /// </summary>
    private static byte[] InterleaveWithEcc(byte[] data, int version, Ecc ecc)
    {
        int blocks = NumBlocks[version - 1, (int)ecc];
        int eccLen = EccPerBlock[version - 1, (int)ecc];
        int shortLen = data.Length / blocks;
        int longBlocks = data.Length % blocks;          // these carry one extra codeword

        var dataBlocks = new byte[blocks][];
        var eccBlocks = new byte[blocks][];
        byte[] generator = RsGenerator(eccLen);

        for (int i = 0, offset = 0; i < blocks; i++)
        {
            int len = shortLen + (i >= blocks - longBlocks ? 1 : 0);
            dataBlocks[i] = data[offset..(offset + len)];
            eccBlocks[i] = RsRemainder(dataBlocks[i], generator);
            offset += len;
        }

        var result = new List<byte>(data.Length + eccLen * blocks);

        for (int i = 0; i <= shortLen; i++)
            for (int b = 0; b < blocks; b++)
                if (i < dataBlocks[b].Length)
                    result.Add(dataBlocks[b][i]);

        for (int i = 0; i < eccLen; i++)
            for (int b = 0; b < blocks; b++)
                result.Add(eccBlocks[b][i]);

        return result.ToArray();
    }

    /// <summary>The generator polynomial for <paramref name="degree"/> ECC codewords, built
    /// as the product of (x - 2^i) over GF(256).</summary>
    private static byte[] RsGenerator(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;

        byte root = 1;
        for (int i = 0; i < degree; i++)
        {
            for (int j = 0; j < degree; j++)
            {
                result[j] = GfMul(result[j], root);
                if (j + 1 < degree) result[j] ^= result[j + 1];
            }
            root = GfMul(root, 2);
        }

        return result;
    }

    private static byte[] RsRemainder(byte[] data, byte[] generator)
    {
        var remainder = new byte[generator.Length];

        foreach (byte b in data)
        {
            byte factor = (byte)(b ^ remainder[0]);
            Array.Copy(remainder, 1, remainder, 0, remainder.Length - 1);
            remainder[^1] = 0;
            for (int i = 0; i < remainder.Length; i++)
                remainder[i] ^= GfMul(generator[i], factor);
        }

        return remainder;
    }

    /// <summary>Multiply in GF(256) with the QR field's primitive polynomial 0x11D.</summary>
    private static byte GfMul(byte a, byte b)
    {
        int result = 0;
        for (int i = 7; i >= 0; i--)
        {
            result = (result << 1) ^ ((result >> 7) * 0x11D);
            result ^= ((b >> i) & 1) * a;
        }
        return (byte)result;
    }

    private sealed class BitSink(int capacityBytes)
    {
        private readonly List<byte> _bytes = new(capacityBytes);
        private int _bitsInLast;

        public int Length => _bytes.Count * 8 - (_bitsInLast == 0 ? 0 : 8 - _bitsInLast);

        public void Append(uint value, int bits)
        {
            for (int i = bits - 1; i >= 0; i--)
            {
                if (_bitsInLast == 0) { _bytes.Add(0); _bitsInLast = 0; }
                int bit = (int)(value >> i) & 1;
                _bytes[^1] |= (byte)(bit << (7 - _bitsInLast));
                _bitsInLast = (_bitsInLast + 1) % 8;
            }
        }

        public byte[] ToBytes() => _bytes.ToArray();
    }

    // ---- the module matrix ---------------------------------------------------

    private sealed class Matrix(int version)
    {
        private readonly int _size = SizeOfVersion(version);
        private readonly int _version = version;
        private bool[,] _modules = new bool[SizeOfVersion(version), SizeOfVersion(version)];

        /// <summary>Function modules (finders, timing, alignment, format areas) are fixed and
        /// must not be overwritten by data or flipped by the mask.</summary>
        private readonly bool[,] _reserved = new bool[SizeOfVersion(version), SizeOfVersion(version)];

        public void DrawFunctionPatterns()
        {
            for (int i = 0; i < _size; i++)
            {
                SetFunction(6, i, i % 2 == 0);          // timing patterns
                SetFunction(i, 6, i % 2 == 0);
            }

            DrawFinder(3, 3);
            DrawFinder(_size - 4, 3);
            DrawFinder(3, _size - 4);

            int[] centers = AlignmentCenters();
            for (int i = 0; i < centers.Length; i++)
                for (int j = 0; j < centers.Length; j++)
                {
                    // The three finder corners have no alignment pattern.
                    bool atFinder = (i == 0 && j == 0)
                                 || (i == 0 && j == centers.Length - 1)
                                 || (i == centers.Length - 1 && j == 0);
                    if (!atFinder) DrawAlignment(centers[i], centers[j]);
                }

            ReserveFormatAreas();
            if (_version >= 7) DrawVersionInfo();
        }

        private void DrawFinder(int cx, int cy)
        {
            for (int dy = -4; dy <= 4; dy++)
                for (int dx = -4; dx <= 4; dx++)
                {
                    int x = cx + dx, y = cy + dy;
                    if (x < 0 || x >= _size || y < 0 || y >= _size) continue;
                    int d = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    SetFunction(x, y, d != 2 && d <= 3);
                }
        }

        private void DrawAlignment(int cx, int cy)
        {
            for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                    SetFunction(cx + dx, cy + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
        }

        /// <summary>Alignment pattern centre coordinates for this version. The spacing rule
        /// is "evenly spaced, rounded to even, first at 6, last at size-7".</summary>
        private int[] AlignmentCenters()
        {
            if (_version == 1) return [];

            int count = _version / 7 + 2;
            int step = _version == 32
                ? 26
                : (_version * 4 + count * 2 + 1) / (count * 2 - 2) * 2;

            var centers = new int[count];
            centers[0] = 6;
            for (int i = count - 1, pos = _size - 7; i >= 1; i--, pos -= step)
                centers[i] = pos;
            return centers;
        }

        private void ReserveFormatAreas()
        {
            // i == 6 is where the format strips cross the timing patterns. Those two modules
            // belong to the timing pattern and are NOT format bits — reserving them as light
            // here silently erases two timing modules, which is invisible on inspection and
            // makes the code fail to scan on some readers.
            for (int i = 0; i < 9; i++)
            {
                if (i != 6) SetFunction(i, 8, false);
                if (i != 6) SetFunction(8, i, false);
            }
            for (int i = 0; i < 8; i++)
            {
                SetFunction(_size - 1 - i, 8, false);
                SetFunction(8, _size - 1 - i, false);
            }
            SetFunction(8, _size - 8, true);           // the always-dark module
        }

        private void DrawVersionInfo()
        {
            int rem = _version;
            for (int i = 0; i < 12; i++)
                rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
            int bits = _version << 12 | rem;

            for (int i = 0; i < 18; i++)
            {
                bool bit = ((bits >> i) & 1) != 0;
                SetFunction(i / 3, _size - 11 + i % 3, bit);
                SetFunction(_size - 11 + i % 3, i / 3, bit);
            }
        }

        private void SetFunction(int x, int y, bool dark)
        {
            if (x < 0 || x >= _size || y < 0 || y >= _size) return;
            _modules[y, x] = dark;
            _reserved[y, x] = true;
        }

        /// <summary>Lay the codeword bits along the zigzag: two-module-wide columns walked
        /// bottom-to-top then top-to-bottom, skipping the vertical timing column.</summary>
        public void DrawCodewords(byte[] codewords)
        {
            int bit = 0;

            for (int right = _size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;             // the timing column is not a data column

                for (int v = 0; v < _size; v++)
                    for (int c = 0; c < 2; c++)
                    {
                        int x = right - c;
                        bool upward = ((right + 1) & 2) == 0;
                        int y = upward ? _size - 1 - v : v;

                        if (_reserved[y, x]) continue;

                        // Data can run out before the matrix does; the remainder stays light.
                        if (bit < codewords.Length * 8)
                            _modules[y, x] = ((codewords[bit >> 3] >> (7 - (bit & 7))) & 1) != 0;
                        bit++;
                    }
            }
        }

        /// <summary>
        /// Try all eight masks, keep the one the spec's penalty rules like best.
        ///
        /// <para>Masking is not optional decoration: an unmasked code can contain large
        /// blank areas or accidental finder-like runs that confuse scanners. The penalty
        /// function is what turns "looks fine to me" into something deterministic.</para>
        /// </summary>
        public void ApplyBestMask(Ecc ecc)
        {
            bool[,] undrawn = (bool[,])_modules.Clone();
            int bestMask = 0, bestPenalty = int.MaxValue;

            for (int mask = 0; mask < 8; mask++)
            {
                _modules = (bool[,])undrawn.Clone();
                ApplyMask(mask);
                DrawFormatBits(ecc, mask);
                int penalty = Penalty();
                if (penalty < bestPenalty) { bestPenalty = penalty; bestMask = mask; }
            }

            _modules = (bool[,])undrawn.Clone();
            ApplyMask(bestMask);
            DrawFormatBits(ecc, bestMask);
        }

        private void ApplyMask(int mask)
        {
            for (int y = 0; y < _size; y++)
                for (int x = 0; x < _size; x++)
                {
                    if (_reserved[y, x]) continue;
                    bool invert = mask switch
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
                    if (invert) _modules[y, x] = !_modules[y, x];
                }
        }

        private void DrawFormatBits(Ecc ecc, int mask)
        {
            // ECC level uses its own ordering on the wire, not the enum's.
            int level = (int)ecc switch { 0 => 1, 1 => 0, 2 => 3, _ => 2 };
            int data = level << 3 | mask;

            int rem = data;
            for (int i = 0; i < 10; i++)
                rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            int bits = (data << 10 | rem) ^ 0x5412;

            for (int i = 0; i <= 5; i++) _modules[i, 8] = Bit(bits, i);
            _modules[7, 8] = Bit(bits, 6);
            _modules[8, 8] = Bit(bits, 7);
            _modules[8, 7] = Bit(bits, 8);
            for (int i = 9; i < 15; i++) _modules[8, 14 - i] = Bit(bits, i);

            for (int i = 0; i < 8; i++) _modules[8, _size - 1 - i] = Bit(bits, i);
            for (int i = 8; i < 15; i++) _modules[_size - 15 + i, 8] = Bit(bits, i);

            _modules[_size - 8, 8] = true;

            static bool Bit(int value, int i) => ((value >> i) & 1) != 0;
        }

        private int Penalty()
        {
            int penalty = 0;

            // Rule 1: runs of five or more same-coloured modules in a line.
            for (int y = 0; y < _size; y++)
                for (int x = 0; x < _size; x++)
                {
                    penalty += RunPenalty(x, y, 1, 0);
                    penalty += RunPenalty(x, y, 0, 1);
                }

            // Rule 2: 2x2 blocks of one colour.
            for (int y = 0; y < _size - 1; y++)
                for (int x = 0; x < _size - 1; x++)
                    if (_modules[y, x] == _modules[y, x + 1] &&
                        _modules[y, x] == _modules[y + 1, x] &&
                        _modules[y, x] == _modules[y + 1, x + 1])
                        penalty += 3;

            // Rule 3: patterns that look like a finder's 1:1:3:1:1 signature. The window may
            // hang up to four modules off either edge — outside the matrix counts as light —
            // so the scan starts before the origin and ends past the last full window.
            for (int i = 0; i < _size; i++)
                for (int start = -4; start + 11 <= _size + 4; start++)
                {
                    if (LooksLikeFinder(start, i, 1, 0)) penalty += 40;
                    if (LooksLikeFinder(i, start, 0, 1)) penalty += 40;
                }

            // Rule 4: imbalance between dark and light.
            int dark = 0;
            foreach (bool m in _modules) if (m) dark++;
            int percent = dark * 100 / (_size * _size);
            penalty += Math.Abs(percent - 50) / 5 * 10;

            return penalty;
        }

        private int RunPenalty(int x, int y, int dx, int dy)
        {
            // Only score a run from its start, or every module in it would be counted.
            int px = x - dx, py = y - dy;
            if (px >= 0 && py >= 0 && _modules[py, px] == _modules[y, x]) return 0;

            int run = 0;
            bool colour = _modules[y, x];
            for (int i = 0; ; i++)
            {
                int cx = x + dx * i, cy = y + dy * i;
                if (cx >= _size || cy >= _size || _modules[cy, cx] != colour) break;
                run++;
            }

            return run >= 5 ? 3 + (run - 5) : 0;
        }

        private bool LooksLikeFinder(int x, int y, int dx, int dy)
        {
            // The 1:1:3:1:1 dark-light ratio of a finder, with four light modules on one
            // side. Both orientations count: a decoder scanning the other way is fooled just
            // as easily, and checking only one of them picks a worse mask than the spec's.
            ReadOnlySpan<bool> forward =
                [true, false, true, true, true, false, true, false, false, false, false];
            ReadOnlySpan<bool> backward =
                [false, false, false, false, true, false, true, true, true, false, true];

            bool f = true, b = true;
            for (int i = 0; i < forward.Length; i++)
            {
                bool module = Module(x + dx * i, y + dy * i);
                if (module != forward[i]) f = false;
                if (module != backward[i]) b = false;
                if (!f && !b) return false;
            }
            return f || b;
        }

        /// <summary>Outside the matrix reads as light, which is what the quiet zone is.</summary>
        private bool Module(int x, int y) =>
            x >= 0 && x < _size && y >= 0 && y < _size && _modules[y, x];

        public bool[,] ToArray(int border)
        {
            int n = _size + border * 2;
            var result = new bool[n, n];
            for (int y = 0; y < _size; y++)
                for (int x = 0; x < _size; x++)
                    result[y + border, x + border] = _modules[y, x];
            return result;
        }
    }
}
