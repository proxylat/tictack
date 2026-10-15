namespace Corpus;

// Fake MFT/volume layer. Every byte layout here mirrors the real NTFS
// structures field-for-field (reviewed against the Win32 semantics, not the
// parser): MFT record header, non-resident attribute header (run offset past
// the 60-byte fixed part), data-run varints (signed LCN deltas, sparse
// offBytes==0), USN_RECORD_V2 rows, FRN seq>>48 reuse checks.
internal sealed class FakeMftVolume : IBlockSource, IMftVolume, ISparseBlockSource
{
    private readonly Dictionary<long, byte[]> _records;
    private readonly Func<ulong, Exception?>? _faultOnRecord;
    private readonly IBlockSource? _inner;

    public int BytesPerRecord { get; }
    public int BytesPerCluster { get; }
    // ISparseBlockSource carries bpc as long (real volumes exceed int
    // clusters); IMftVolume as int (NTFS ioctl field). Explicit to satisfy both.
    long ISparseBlockSource.BytesPerCluster => BytesPerCluster;
    public long RecordCount { get; }
    public IReadOnlyList<(long StartLcn, long ClusterCount)> MftRuns { get; }
    public List<MftEnumEntry> Entries { get; } = new();
    public List<(long Start, long Count)> AllocRuns { get; }
    public long Length { get; }

    public FakeMftVolume(int bytesPerRecord = 1024, int bytesPerCluster = 4096, long recordCount = 20,
        IReadOnlyList<(long StartLcn, long ClusterCount)>? mftRuns = null,
        Dictionary<long, byte[]>? records = null, List<(long Start, long Count)>? allocRuns = null,
        long length = 0, IBlockSource? inner = null, Func<ulong, Exception?>? faultOnRecord = null)
    {
        BytesPerRecord = bytesPerRecord;
        BytesPerCluster = bytesPerCluster;
        RecordCount = recordCount;
        MftRuns = mftRuns ?? new (long, long)[] { (0, 100) };
        _records = records ?? new Dictionary<long, byte[]>();
        _faultOnRecord = faultOnRecord;
        _inner = inner;
        Length = inner?.Length ?? length;
        if (allocRuns != null)
        {
            AllocRuns = allocRuns;
        }
        else
        {
            var clusters = (Length + bytesPerCluster - 1) / bytesPerCluster;
            AllocRuns = clusters > 0 ? new List<(long, long)> { (0, clusters) } : new List<(long, long)>();
        }
    }

    // Missing record numbers read as zeroed slots (uninitialized MFT tail),
    // exactly what the real ioctl returns for never-used record numbers.
    public byte[] GetRecordBytes(ulong fileRef)
    {
        var fault = _faultOnRecord?.Invoke(fileRef);
        if (fault != null)
            throw fault;
        var rec = (long)(fileRef & 0xFFFF_FFFF_FFFF);
        if (_records.TryGetValue(rec, out var b))
            return b;
        return new byte[BytesPerRecord];
    }

    public IReadOnlyList<MftEnumEntry> Enumerate() => Entries;

    public IReadOnlyList<(long Start, long Count)> GetAllocatedRuns() => AllocRuns;

    public Stream OpenRead() => _inner?.OpenRead() ?? new MemoryStream(new byte[Length], false);

    public void Dispose()
    {
        _inner?.Dispose();
    }
}

// Canned MFT record construction. Header: FILE@0, LSN@8, seq@0x10,
// links@0x12, firstAttr@0x14=48, flags@0x16, used@0x18, alloc@0x1C.
// Non-resident attrs: type@0, len@4, res@8=1, nameLen@9, runOff@32=64
// (past the 60-byte fixed part + pad), runs@64. Resident: len 24, res=0.
internal static class MftRecordBuilder
{
    public static byte[] Live(int bpr, ushort seq, params byte[][] attrs) =>
        LiveFlags(bpr, seq, 1, attrs);

    public static byte[] LiveFlags(int bpr, ushort seq, ushort flags, params byte[][] attrs)
    {
        var used = 48;
        foreach (var a in attrs)
            used += a.Length;
        used += 4; // end marker
        var record = new byte[bpr];
        record[0] = (byte)'F'; record[1] = (byte)'I'; record[2] = (byte)'L'; record[3] = (byte)'E';
        BitConverter.GetBytes(seq).CopyTo(record, 0x10);
        BitConverter.GetBytes((ushort)1).CopyTo(record, 0x12);
        BitConverter.GetBytes((ushort)48).CopyTo(record, 0x14);
        BitConverter.GetBytes(flags).CopyTo(record, 0x16);
        BitConverter.GetBytes((uint)used).CopyTo(record, 0x18);
        BitConverter.GetBytes((uint)bpr).CopyTo(record, 0x1C);
        var off = 48;
        foreach (var a in attrs)
        {
            a.CopyTo(record, off);
            off += a.Length;
        }
        BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(record, off);
        return record;
    }

    public static byte[] DataAttr(bool nonResident, byte[] runs, byte nameLen = 0, uint type = 0x80)
    {
        if (!nonResident)
        {
            var res = new byte[24];
            BitConverter.GetBytes(type).CopyTo(res, 0);
            BitConverter.GetBytes(24u).CopyTo(res, 4);
            res[8] = 0;
            return res;
        }
        var attr = new byte[64 + runs.Length];
        BitConverter.GetBytes(type).CopyTo(attr, 0);
        BitConverter.GetBytes((uint)attr.Length).CopyTo(attr, 4);
        attr[8] = 1;
        attr[9] = nameLen;
        BitConverter.GetBytes((ushort)64).CopyTo(attr, 32);
        runs.CopyTo(attr, 64);
        return attr;
    }

    public static byte[] StdInfoAttr()
    {
        var attr = new byte[24];
        BitConverter.GetBytes(0x10u).CopyTo(attr, 0);
        BitConverter.GetBytes(24u).CopyTo(attr, 4);
        return attr;
    }

    // (clusterCount, lcnDelta): null delta = sparse hole (offBytes 0, no
    // clusters). Minimal-byte LE; deltas signed two's-complement, so a
    // high-bit-positive grows a byte rather than aliasing negative.
    public static byte[] EncodeRuns(params (long Count, long? Delta)[] runs)
    {
        var out_ = new List<byte>();
        foreach (var (count, delta) in runs)
        {
            if (count <= 0)
                throw new ArgumentOutOfRangeException(nameof(runs));
            var lenBytes = LenBytes((ulong)count);
            var offBytes = delta == null ? 0 : SignedBytes(delta.Value);
            out_.Add((byte)(lenBytes | (offBytes << 4)));
            for (var b = 0; b < lenBytes; b++)
                out_.Add((byte)((ulong)count >> (8 * b)));
            if (delta != null)
            {
                var u = (ulong)delta.Value;
                for (var b = 0; b < offBytes; b++)
                    out_.Add((byte)(u >> (8 * b)));
            }
        }
        out_.Add(0x00);
        return out_.ToArray();
    }

    private static int LenBytes(ulong v)
    {
        for (var n = 1; n < 8; n++)
        {
            if (v < (1UL << (8 * n)))
                return n;
        }
        return 8;
    }

    private static int SignedBytes(long v)
    {
        for (var n = 1; n < 8; n++)
        {
            if (v >= -(1L << ((8 * n) - 1)) && v < (1L << ((8 * n) - 1)))
                return n;
        }
        return 8;
    }
}

// Canned FSCTL_ENUM_USN_DATA replies. Row = USN_RECORD_V2: len@0,
// major@4, minor@6, frn@8, parent@16, attrs@52, nameLen(bytes)@56,
// nameOff@58=60, UTF-16LE name@60. Buffer = next-start@0 + rows.
internal static class MftEnumBuffer
{
    public static byte[] Record(ulong frn, ulong parent, string name, uint attrs, ushort major = 2)
    {
        var nameBytes = System.Text.Encoding.Unicode.GetBytes(name);
        var rec = new byte[60 + nameBytes.Length];
        BitConverter.GetBytes((uint)rec.Length).CopyTo(rec, 0);
        BitConverter.GetBytes(major).CopyTo(rec, 4);
        BitConverter.GetBytes((ushort)1).CopyTo(rec, 6);
        BitConverter.GetBytes(frn).CopyTo(rec, 8);
        BitConverter.GetBytes(parent).CopyTo(rec, 16);
        BitConverter.GetBytes(attrs).CopyTo(rec, 52);
        BitConverter.GetBytes((ushort)nameBytes.Length).CopyTo(rec, 56);
        BitConverter.GetBytes((ushort)60).CopyTo(rec, 58);
        nameBytes.CopyTo(rec, 60);
        return rec;
    }

    public static byte[] Buffer(ulong next, params byte[][] records)
    {
        var total = 8;
        foreach (var r in records)
            total += r.Length;
        var buf = new byte[total];
        BitConverter.GetBytes(next).CopyTo(buf, 0);
        var off = 8;
        foreach (var r in records)
        {
            r.CopyTo(buf, off);
            off += r.Length;
        }
        return buf;
    }
}
