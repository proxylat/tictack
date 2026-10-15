namespace Corpus;

// Item-4 resolver: name matching (bare/pathed/system/ADS), subtree
// expansion, record-cluster exclusion, enum-reply parsing. FRN low parts
// double as record numbers; file FRN n owns data run (1000+n,1), dir FRN n
// owns index run (2000+n,1); record clusters (n/4 via mftRuns (0,100)) must
// NEVER appear in excluded runs (shared clusters would wipe neighbors).
public sealed class MftResolverTests
{
    private const int Bpr = 1024;
    private const int Bpc = 4096;
    private const uint Dir = MftPathResolver.FileAttributeDirectory;
    private const uint File = 0x20;

    private static readonly List<(long StartLcn, long ClusterCount)> MftRuns = new() { (0, 100) };

    // Tree: pagefile.sys(100), Temp(111 files) + $Recycle.Bin(101),
    // Windows(102)\Temp(103)\junk.tmp(104), Users(105)\u(106)\Temp(107)\
    // t.tmp(108), Docs(109)\pagefile.sys(110 decoy), $Extend(30)\$UsnJrnl(31).
    // FRNs carry seq 700+recNo (enumeration gives exact FRNs; the record
    // bytes below carry the same seq so reuse checks pass).
    private static ulong Frn(long recNo) => ((ulong)(700 + recNo) << 48) | (ulong)recNo;

    private static long RecNo(ulong fileRef) => (long)(fileRef & 0xFFFF_FFFF_FFFF);

    private static List<MftEnumEntry> StandardEntries() => new()
    {
        new(Frn(100), 5, "pagefile.sys", File),
        new(Frn(111), 5, "Temp", File),
        new(Frn(101), 5, "$Recycle.Bin", Dir),
        new(Frn(112), Frn(101), "S-1-5-21", Dir),
        new(Frn(102), 5, "Windows", Dir),
        new(Frn(103), Frn(102), "Temp", Dir),
        new(Frn(104), Frn(103), "junk.tmp", File),
        new(Frn(105), 5, "Users", Dir),
        new(Frn(106), Frn(105), "u", Dir),
        new(Frn(107), Frn(106), "Temp", Dir),
        new(Frn(108), Frn(107), "t.tmp", File),
        new(Frn(109), 5, "Docs", Dir),
        new(Frn(110), Frn(109), "pagefile.sys", File),
        new(Frn(30), 5, "$Extend", Dir),
        new(Frn(31), Frn(30), "$UsnJrnl", File),
    };

    private static Dictionary<long, byte[]> StandardRecords()
    {
        var records = new Dictionary<long, byte[]>();
        foreach (var e in StandardEntries())
        {
            var recNo = RecNo(e.FileRef);
            var isDir = (e.Attributes & Dir) != 0;
            var lcn = isDir ? 2000 + recNo : 1000 + recNo;
            var type = isDir ? 0xA0u : 0x80u; // dirs: $INDEX_ALLOC; files: $DATA
            records[recNo] = MftRecordBuilder.LiveFlags(Bpr, (ushort)(700 + recNo),
                (ushort)(isDir ? 3 : 1),
                MftRecordBuilder.StdInfoAttr(), // 0x10 skipped by the parser
                MftRecordBuilder.DataAttr(true, MftRecordBuilder.EncodeRuns((1, lcn)), type: type));
        }
        return records;
    }

    private static MftExclusionResolver Resolve(params string[] names)
    {
        var records = StandardRecords();
        return MftExclusionResolver.Build(names, StandardEntries(), r => records[RecNo(r)], Bpc, Bpr, MftRuns);
    }

    private static HashSet<long> RunLcns(MftExclusionResolver resolver)
    {
        var lcns = new HashSet<long>();
        foreach (var (start, count) in resolver.ExcludedRuns())
        {
            Assert.Equal(1L, count);
            lcns.Add(start);
        }
        return lcns;
    }

    [Fact]
    public void BareFile_MatchesRootOnly()
    {
        var lcns = RunLcns(Resolve("pagefile.sys"));
        Assert.Contains(1100L, lcns); // root pagefile.sys (FRN 100)
        Assert.DoesNotContain(1110L, lcns); // Docs\pagefile.sys decoy untouched
    }

    [Fact]
    public void BareDir_MatchesAnywherePlusRootFile()
    {
        var lcns = RunLcns(Resolve("Temp"));
        Assert.Contains(2103L, lcns); // Windows\Temp index
        Assert.Contains(1104L, lcns); // Windows\Temp\junk.tmp
        Assert.Contains(2107L, lcns); // Users\u\Temp index
        Assert.Contains(1108L, lcns); // Users\u\Temp\t.tmp
        Assert.Contains(1111L, lcns); // root file Temp (documented: bare matches root files)
    }

    [Fact]
    public void Pathed_MatchesThatSubtreeOnly()
    {
        var lcns = RunLcns(Resolve("Windows\\Temp"));
        Assert.Contains(2103L, lcns);
        Assert.Contains(1104L, lcns);
        Assert.DoesNotContain(2107L, lcns); // Users\u\Temp untouched
        Assert.DoesNotContain(1108L, lcns);
        Assert.DoesNotContain(1111L, lcns);
    }

    [Fact]
    public void SystemName_MatchesAnyDepth()
    {
        var jrnl = RunLcns(Resolve("$UsnJrnl:$J")); // ADS suffix strips to the host file
        Assert.Contains(1031L, jrnl); // $Extend\$UsnJrnl (depth 2, $-matched)
        var bin = RunLcns(Resolve("$Recycle.Bin"));
        Assert.Contains(2101L, bin);
        Assert.Contains(2112L, bin); // subtree SID dir
    }

    [Fact]
    public void Matching_IsCaseAndSeparatorInsensitive()
    {
        Assert.Equal(RunLcns(Resolve("Windows\\Temp")), RunLcns(Resolve("WINDOWS/temp")));
        Assert.Equal(RunLcns(Resolve("pagefile.sys")), RunLcns(Resolve("PAGEFILE.SYS")));
    }

    [Fact]
    public void LogFile_StrippedEvenWhenListed()
    {
        var only = Resolve("$LogFile");
        Assert.Empty(only.ExclusionNames);
        Assert.Empty(only.ExcludedRuns());
        var mixed = Resolve("$LogFile", "pagefile.sys");
        Assert.Equal(["pagefile.sys"], mixed.ExclusionNames);
        Assert.Equal([1100L], RunLcns(mixed));
    }

    [Fact]
    public void MissingName_YieldsNoRuns()
    {
        var r = Resolve("nope.sys");
        Assert.Equal(["nope.sys"], r.ExclusionNames);
        Assert.Empty(r.ExcludedRuns());
    }

    [Fact]
    public void RecordClusters_NeverExcluded()
    {
        // FRN 100's record cluster (100/4 = LCN 25) packs neighbors' records:
        // data runs yes, record clusters never.
        var lcns = RunLcns(Resolve("pagefile.sys"));
        Assert.DoesNotContain(25L, lcns);
        var all = RunLcns(Resolve("pagefile.sys", "Temp", "$Recycle.Bin", "$UsnJrnl:$J", "Windows\\Temp"));
        foreach (var e in StandardEntries())
            Assert.DoesNotContain(RecNo(e.FileRef) / 4, all);
    }

    [Fact]
    public void EnumParser_GoldenRows()
    {
        var buf = MftEnumBuffer.Buffer(77,
            MftEnumBuffer.Record(100, 5, "pagefile.sys", File),
            MftEnumBuffer.Record(103, 102, "Temp", Dir));
        var (next, entries) = MftEnumParser.ParseEnumBuffer(buf, buf.Length);
        Assert.Equal(77UL, next);
        Assert.Equal(2, entries.Count);
        Assert.Equal(new MftEnumEntry(100, 5, "pagefile.sys", File), entries[0]);
        Assert.Equal(new MftEnumEntry(103, 102, "Temp", Dir), entries[1]);
    }

    [Fact]
    public void EnumParser_EmptyDone()
    {
        var buf = MftEnumBuffer.Buffer(0);
        var (next, entries) = MftEnumParser.ParseEnumBuffer(buf, buf.Length);
        Assert.Equal(0UL, next);
        Assert.Empty(entries);
    }

    [Fact]
    public void EnumParser_RejectsV3TruncatedAndShort()
    {
        var v3 = MftEnumBuffer.Buffer(1, MftEnumBuffer.Record(100, 5, "x", File, major: 3));
        Assert.Throws<InvalidDataException>(() => MftEnumParser.ParseEnumBuffer(v3, v3.Length));
        var row = MftEnumBuffer.Record(100, 5, "pagefile.sys", File);
        var trunc = MftEnumBuffer.Buffer(1, row);
        Assert.Throws<InvalidDataException>(() => MftEnumParser.ParseEnumBuffer(trunc, trunc.Length - 3));
        Assert.Throws<InvalidDataException>(() => MftEnumParser.ParseEnumBuffer(new byte[8], 7));
    }
}
