using System;
using System.Buffers.Binary;
using System.IO;

namespace Corpus
{
    // Reed-Solomon ECC over GF(256), written in-tree (zero new deps).
    // Systematic code: pack bytes are untouched, parity lives in a sidecar.
    // Fixed data width 128; parity scales from ecc_overhead_percent:
    // P = round(128 * pct / 100), min 2 when on (1 parity detects but
    // cannot correct), max 127 (128+127 = 255 = GF(256) codeword limit).
    // 6% -> 8 parity (~6.25%, the 128+8 layout); 50% -> 64 parity.
    // Corrects floor(P/2) corrupt bytes per 128-byte stripe; anything
    // beyond fails loud (-1, the caller keeps its original error).
    public static class RsCodec
    {
        public const int DataWidth = 128;
        public const int MaxParity = 255 - DataWidth;

        private static readonly byte[] Exp = new byte[512];
        private static readonly byte[] Log = new byte[256];

        static RsCodec()
        {
            // Powers of 3: 2 has order 51 mod 0x11D (not primitive),
            // 3 is primitive, so Exp/Log are a full 255-cycle.
            byte x = 1;
            for (var i = 0; i < 255; i++)
            {
                Exp[i] = x;
                Log[x] = (byte)i;
                // x *= 3, i.e. xtime(x) ^ x (the byte cast drops bit 8,
                // which is exactly the 0x11D reduction).
                x = (byte)(((x << 1) ^ (((x & 0x80) != 0) ? 0x1B : 0x00)) ^ x);
            }
            for (var i = 255; i < 512; i++)
                Exp[i] = Exp[i - 255];
        }

        public static int ParityBytesFor(int overheadPercent)
        {
            if (overheadPercent <= 0)
                return 0;
            return Math.Clamp((int)Math.Round(DataWidth * overheadPercent / 100.0), 2, MaxParity);
        }

        public static int StripeCount(long dataLength) => (int)((dataLength + DataWidth - 1) / DataWidth);

        private static byte Mul(byte a, byte b) =>
            (a == 0 || b == 0) ? (byte)0 : Exp[Log[a] + Log[b]];

        private static byte Inv(byte a) => Exp[255 - Log[a]];

        // Generator roots at a^0..a^(P-1), high-first (g[0] = 1 = x^P).
        private static byte[] Generator(int parityCount)
        {
            var g = new byte[] { 1 };
            for (var i = 0; i < parityCount; i++)
            {
                var next = new byte[g.Length + 1];
                var root = Exp[i];
                for (var j = 0; j < g.Length; j++)
                {
                    next[j] ^= g[j];
                    next[j + 1] ^= Mul(g[j], root);
                }
                g = next;
            }
            return g;
        }

        public static byte[] EncodeFile(byte[] data, int parityCount)
        {
            ArgumentNullException.ThrowIfNull(data);
            if (parityCount < 0 || parityCount > MaxParity)
                throw new ArgumentOutOfRangeException(nameof(parityCount));
            if (parityCount == 0 || data.Length == 0)
                return Array.Empty<byte>();
            var stripes = StripeCount(data.Length);
            var gen = Generator(parityCount);
            var parity = new byte[stripes * parityCount];
            var stripe = new byte[DataWidth];
            for (var s = 0; s < stripes; s++)
            {
                var off = s * DataWidth;
                var take = Math.Min(DataWidth, data.Length - off);
                Array.Clear(stripe);
                Buffer.BlockCopy(data, off, stripe, 0, take);
                // LFSR: remainder of stripe(x) * x^P over the generator;
                // parity[0] holds the highest-degree remainder coefficient.
                var dest = s * parityCount;
                for (var i = 0; i < DataWidth; i++)
                {
                    var fb = (byte)(stripe[i] ^ parity[dest]);
                    for (var j = 0; j < parityCount - 1; j++)
                        parity[dest + j] = parity[dest + j + 1];
                    parity[dest + parityCount - 1] = 0;
                    if (fb != 0)
                    {
                        for (var j = 0; j < parityCount; j++)
                            parity[dest + j] ^= Mul(gen[j + 1], fb);
                    }
                }
            }
            return parity;
        }

        // In-place repair. Returns stripes corrected (0 = already clean),
        // -1 = unrecoverable (shape errors throw: caller bugs, not rot).
        public static int TryRepair(byte[] data, byte[] parity, int parityCount)
        {
            ArgumentNullException.ThrowIfNull(data);
            ArgumentNullException.ThrowIfNull(parity);
            if (parityCount < 0 || parityCount > MaxParity)
                throw new ArgumentOutOfRangeException(nameof(parityCount));
            if (parityCount == 0)
                return 0;
            var stripes = StripeCount(data.Length);
            if (parity.Length != stripes * parityCount)
                throw new ArgumentException("parity length does not match data length", nameof(parity));
            var repaired = 0;
            var code = new byte[DataWidth + parityCount];
            for (var s = 0; s < stripes; s++)
            {
                var n = DataWidth + parityCount;
                var off = s * DataWidth;
                var realLen = Math.Min(DataWidth, data.Length - off);
                Array.Clear(code);
                Buffer.BlockCopy(data, off, code, 0, realLen);
                Buffer.BlockCopy(parity, s * parityCount, code, DataWidth, parityCount);
                if (IsClean(code, n))
                    continue;
                if (!LocateAndFix(code, n, realLen))
                    return -1;
                Buffer.BlockCopy(code, 0, data, off, realLen);
                Buffer.BlockCopy(code, DataWidth, parity, s * parityCount, parityCount);
                // Loud guarantee: the fix must verify, never be trusted.
                if (!IsClean(code, n))
                    return -1;
                repaired++;
            }
            return repaired;
        }

        private static bool IsClean(byte[] code, int n)
        {
            var p = n - DataWidth;
            for (var i = 0; i < p; i++)
            {
                byte acc = 0;
                for (var j = 0; j < n; j++)
                {
                    if (code[j] != 0)
                        acc ^= Mul(code[j], Exp[(i * (n - 1 - j)) % 255]);
                }
                if (acc != 0)
                    return false;
            }
            return true;
        }

        private static bool LocateAndFix(byte[] code, int n, int realLen)
        {
            var p = n - DataWidth;
            var synd = new byte[p];
            for (var i = 0; i < p; i++)
            {
                byte acc = 0;
                for (var j = 0; j < n; j++)
                {
                    if (code[j] != 0)
                        acc ^= Mul(code[j], Exp[(i * (n - 1 - j)) % 255]);
                }
                synd[i] = acc;
            }
            // Berlekamp-Massey -> error locator (loc[0] = 1).
            var loc = new byte[p + 1];
            var prev = new byte[p + 1];
            loc[0] = 1;
            prev[0] = 1;
            var locCap = p + 1;
            var l = 0;
            var m = 1;
            byte b = 1;
            for (var k = 0; k < p; k++)
            {
                byte d = synd[k];
                for (var i = 1; i <= l; i++)
                    d ^= Mul(loc[i], synd[k - i]);
                if (d == 0)
                {
                    m++;
                    continue;
                }
                var t = (byte[])loc.Clone();
                var coef = Mul(d, Inv(b));
                if (prev.Length + m > locCap)
                {
                    locCap = prev.Length + m;
                    Array.Resize(ref loc, locCap);
                }
                for (var i = 0; i < prev.Length; i++)
                {
                    if (prev[i] != 0)
                        loc[i + m] ^= Mul(prev[i], coef);
                }
                if (2 * l <= k)
                {
                    l = k + 1 - l;
                    prev = t;
                    b = d;
                    m = 1;
                }
                else
                {
                    m++;
                }
            }
            if (l == 0 || 2 * l > p)
                return false;
            // Chien: X = a^(n-1-j); error at j iff locator(X^-1) == 0.
            var positions = new int[l];
            var found = 0;
            for (var j = 0; j < n && found < l; j++)
            {
                var xinv = Exp[(255 - ((n - 1 - j) % 255)) % 255];
                byte acc = 0;
                var pow = (byte)1;
                for (var i = 0; i <= l && i < loc.Length; i++)
                {
                    if (loc[i] != 0)
                        acc ^= Mul(loc[i], pow);
                    pow = Mul(pow, xinv);
                }
                if (acc == 0)
                    positions[found++] = j;
            }
            if (found != l)
                return false;
            // Magnitudes via Vandermonde (first L syndromes), confirmed
            // against ALL syndromes before touching a single byte.
            var xs = new byte[l];
            for (var j = 0; j < l; j++)
                xs[j] = Exp[(n - 1 - positions[j]) % 255];
            var mags = SolveVandermonde(xs, synd, l);
            if (mags == null)
                return false;
            for (var i = 0; i < p; i++)
            {
                byte acc = 0;
                for (var j = 0; j < l; j++)
                {
                    if (mags[j] != 0)
                        acc ^= Mul(mags[j], Exp[(i * Log[xs[j]]) % 255]);
                }
                if (acc != synd[i])
                    return false;
            }
            for (var j = 0; j < l; j++)
            {
                var pos = positions[j];
                if (pos >= realLen && pos < DataWidth)
                    return false; // zero-pad region: the count lied
                code[pos] ^= mags[j];
            }
            return true;
        }

        private static byte[]? SolveVandermonde(byte[] xs, byte[] synd, int l)
        {
            var a = new byte[l, l + 1];
            for (var i = 0; i < l; i++)
            {
                // Row i is X_j^i per column (X_j != 0 always, so Log is safe).
                for (var j = 0; j < l; j++)
                    a[i, j] = i == 0 ? (byte)1 : Exp[(i * Log[xs[j]]) % 255];
                a[i, l] = synd[i];
            }
            for (var col = 0; col < l; col++)
            {
                var pivot = -1;
                for (var r = col; r < l; r++)
                {
                    if (a[r, col] != 0)
                    {
                        pivot = r;
                        break;
                    }
                }
                if (pivot < 0)
                    return null;
                if (pivot != col)
                {
                    for (var c = col; c <= l; c++)
                    {
                        var tmp = a[col, c];
                        a[col, c] = a[pivot, c];
                        a[pivot, c] = tmp;
                    }
                }
                var inv = Inv(a[col, col]);
                for (var c = col; c <= l; c++)
                    a[col, c] = Mul(a[col, c], inv);
                for (var r = 0; r < l; r++)
                {
                    if (r != col && a[r, col] != 0)
                    {
                        var f = a[r, col];
                        for (var c = col; c <= l; c++)
                            a[r, c] ^= Mul(f, a[col, c]);
                    }
                }
            }
            var e = new byte[l];
            for (var i = 0; i < l; i++)
                e[i] = a[i, l];
            return e;
        }
    }

    // Additive pack sidecar: "<pack>.ecc" beside the sealed pack.
    // Header: magic "RSE1" + dataWidth i32 + parityCount i32 + dataLen i64.
    // The pack format never changes: old snapshots (no sidecar) read
    // exactly as before; repair runs only after a read already failed.
    public static class PackEcc
    {
        public const string SidecarSuffix = ".ecc";

        public static string SidecarPath(string packPath) => packPath + SidecarSuffix;

        public static void WriteSidecar(string packPath, int overheadPercent)
        {
            var parityCount = RsCodec.ParityBytesFor(overheadPercent);
            if (parityCount == 0)
                return;
            var data = File.ReadAllBytes(packPath);
            WriteSidecarData(packPath, data, RsCodec.EncodeFile(data, parityCount), parityCount);
        }

        private static void WriteSidecarData(string packPath, byte[] data, byte[] parity, int parityCount)
        {
            var path = SidecarPath(packPath);
            var tmp = path + ".tmp";
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                Span<byte> header = stackalloc byte[20];
                header[0] = (byte)'R';
                header[1] = (byte)'S';
                header[2] = (byte)'E';
                header[3] = (byte)'1';
                BinaryPrimitives.WriteInt32LittleEndian(header.Slice(4), RsCodec.DataWidth);
                BinaryPrimitives.WriteInt32LittleEndian(header.Slice(8), parityCount);
                BinaryPrimitives.WriteInt64LittleEndian(header.Slice(12), data.Length);
                stream.Write(header);
                if (parity.Length > 0)
                    stream.Write(parity, 0, parity.Length);
                stream.Flush(true);
            }
            File.Move(tmp, path, true);
        }

        // True = the pack now reads clean (repaired, or already clean).
        // False = no usable sidecar or unrecoverable. Never throws: the
        // caller's original error stays the signal, never the repair.
        public static bool TryRepairPack(string packPath)
        {
            try
            {
                var path = SidecarPath(packPath);
                if (!File.Exists(path))
                    return false;
                var raw = File.ReadAllBytes(path);
                if (raw.Length < 20 || raw[0] != (byte)'R' || raw[1] != (byte)'S' || raw[2] != (byte)'E' || raw[3] != (byte)'1')
                    return false;
                var span = raw.AsSpan();
                if (BinaryPrimitives.ReadInt32LittleEndian(span.Slice(4)) != RsCodec.DataWidth)
                    return false;
                var parityCount = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(8));
                var dataLength = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(12));
                if (parityCount < 2 || parityCount > RsCodec.MaxParity || dataLength < 0)
                    return false;
                if (raw.Length != 20 + RsCodec.StripeCount(dataLength) * parityCount)
                    return false;
                var data = File.ReadAllBytes(packPath);
                if (data.Length != dataLength)
                    return false;
                var parity = new byte[raw.Length - 20];
                Buffer.BlockCopy(raw, 20, parity, 0, parity.Length);
                var repaired = RsCodec.TryRepair(data, parity, parityCount);
                if (repaired < 0)
                    return false;
                if (repaired == 0)
                    return true;
                var tmp = packPath + ".rsfix.tmp";
                using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(data, 0, data.Length);
                    stream.Flush(true);
                }
                File.Move(tmp, packPath, true);
                // Parity self-heals: recompute over the fixed bytes so a
                // sidecar bitflip cannot doom the next repair.
                WriteSidecarData(packPath, data, RsCodec.EncodeFile(data, parityCount), parityCount);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
