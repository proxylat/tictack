namespace Corpus;

// Item 12b: BitLocker orchestration. All Linux-safe: a fake controller
// drives the scope (ordering, re-enable-on-throw, locked-refusal,
// unprotected-noop), the parser pins manage-bde output shapes, and a
// fake IBitLockerVolume source proves the Store→manifest record.
public sealed class BitLockerTests : IDisposable
{
    private readonly string _dir;

    public BitLockerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "CorpusTest_bitlocker_" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    // Fake over mock: a working in-memory controller with a scripted query
    // result and a call ledger for ordering asserts.
    private sealed class FakeBitLocker : IBitLockerController
    {
        private readonly BitLockerInfo _info;
        public readonly List<string> Calls = new();
        public readonly Queue<BitLockerState> QueryScript = new();
        public bool ThrowOnSuspend;
        public bool ThrowOnResume;

        public FakeBitLocker(BitLockerState state, params string[] keyIds)
        {
            _info = new BitLockerInfo(state, keyIds.ToList());
        }

        public BitLockerInfo Query(string volume)
        {
            Calls.Add("query:" + volume);
            if (QueryScript.Count > 0)
                return new BitLockerInfo(QueryScript.Dequeue(), _info.KeyIds);
            return _info;
        }

        public void Suspend(string volume)
        {
            Calls.Add("suspend:" + volume);
            if (ThrowOnSuspend) throw new InvalidOperationException("suspend failed");
        }

        public void Resume(string volume)
        {
            Calls.Add("resume:" + volume);
            if (ThrowOnResume) throw new InvalidOperationException("resume failed");
        }
    }

    [Fact]
    public void ProtectedUnlocked_SuspendCaptureResume_InOrder()
    {
        var fake = new FakeBitLocker(BitLockerState.ProtectedUnlocked, "{11111111-1111-1111-1111-111111111111}");
        var log = new TestLogger();
        using (var scope = BitLockerCaptureScope.Arm("C:", fake, log))
        {
            fake.Calls.Add("capture");
            // Manifest record: state DURING capture (post-suspend) + key IDs.
            Assert.Equal("Suspended", scope.CaptureState);
            Assert.Equal(new[] { "{11111111-1111-1111-1111-111111111111}" }, scope.CaptureKeyIds);
        }
        Assert.Equal(new[] { "query:C:", "suspend:C:", "capture", "resume:C:" }, fake.Calls);
        Assert.Contains(log.Messages, m => m.StartsWith("INF:BitLocker:") && m.Contains("suspending"));
        Assert.Contains(log.Messages, m => m.StartsWith("INF:BitLocker:") && m.Contains("re-enabled"));
    }

    [Fact]
    public void Resume_Runs_WhenCaptureThrows()
    {
        var fake = new FakeBitLocker(BitLockerState.ProtectedUnlocked);
        var log = new TestLogger();
        var ex = Assert.Throws<InvalidOperationException>(() => CaptureThatThrows(fake, log));
        Assert.Equal("capture blew up", ex.Message);
        Assert.Equal(new[] { "query:C:", "suspend:C:", "capture", "resume:C:" }, fake.Calls);
    }

    // Helper (not a throw-ending block lambda — those bind xUnit's obsolete
    // Throws(Func<Task>) overload instead of Throws(Action)).
    private static void CaptureThatThrows(FakeBitLocker fake, TestLogger log)
    {
        using var scope = BitLockerCaptureScope.Arm("C:", fake, log);
        fake.Calls.Add("capture");
        throw new InvalidOperationException("capture blew up");
    }

    [Fact]
    public void Locked_RefusesNamingVolume_NeverSuspends()
    {
        var fake = new FakeBitLocker(BitLockerState.Locked);
        var ex = Assert.Throws<InvalidDataException>(() =>
            BitLockerCaptureScope.Arm("D:", fake, new TestLogger()));
        Assert.Contains("D:", ex.Message);
        Assert.Contains("manage-bde -unlock D:", ex.Message);
        Assert.Equal(new[] { "query:D:" }, fake.Calls);
    }

    [Fact]
    public void Unprotected_Noop_ZeroStateChange()
    {
        var fake = new FakeBitLocker(BitLockerState.Unprotected);
        var log = new TestLogger();
        using (var scope = BitLockerCaptureScope.Arm("C:", fake, log))
        {
            Assert.Equal("Unprotected", scope.CaptureState);
            Assert.Null(scope.CaptureKeyIds);
        }
        Assert.Equal(new[] { "query:C:" }, fake.Calls);
        Assert.Contains(log.Messages, m => m.StartsWith("INF:BitLocker:") && m.Contains("unprotected"));
    }

    [Fact]
    public void Suspended_NoStateChange_NoReenable()
    {
        var fake = new FakeBitLocker(BitLockerState.Suspended, "{22222222-2222-2222-2222-222222222222}");
        var log = new TestLogger();
        using (var scope = BitLockerCaptureScope.Arm("C:", fake, log))
        {
            Assert.Equal("Suspended", scope.CaptureState);
            Assert.Equal(new[] { "{22222222-2222-2222-2222-222222222222}" }, scope.CaptureKeyIds);
        }
        Assert.Equal(new[] { "query:C:" }, fake.Calls);
        Assert.Contains(log.Messages, m => m.StartsWith("INF:BitLocker:") && m.Contains("already suspended"));
    }

    [Fact]
    public void Unknown_WarnsAndProceeds()
    {
        var fake = new FakeBitLocker(BitLockerState.Unknown);
        var log = new TestLogger();
        using var scope = BitLockerCaptureScope.Arm("C:", fake, log);
        Assert.Equal("Unknown", scope.CaptureState);
        Assert.Equal(new[] { "query:C:" }, fake.Calls);
        Assert.Contains(log.Messages, m => m.StartsWith("WRN:BitLocker:"));
    }

    [Fact]
    public void SuspendFailure_WarnsAndProceeds_ProtectedUnlocked()
    {
        // 12a: VSS captures protected-unlocked volumes fine — a suspend
        // failure must not lose the backup. Re-query confirms no
        // half-applied suspend, so teardown resumes nothing.
        var fake = new FakeBitLocker(BitLockerState.ProtectedUnlocked) { ThrowOnSuspend = true };
        var log = new TestLogger();
        using (var scope = BitLockerCaptureScope.Arm("C:", fake, log))
        {
            Assert.Equal("ProtectedUnlocked", scope.CaptureState);
        }
        Assert.Equal(new[] { "query:C:", "suspend:C:", "query:C:" }, fake.Calls);
        Assert.Contains(log.Messages, m => m.StartsWith("WRN:BitLocker:") && m.Contains("capturing protected-unlocked"));
    }

    [Fact]
    public void SuspendFailure_HalfApplied_ResumesImmediately()
    {
        var fake = new FakeBitLocker(BitLockerState.ProtectedUnlocked) { ThrowOnSuspend = true };
        fake.QueryScript.Enqueue(BitLockerState.ProtectedUnlocked);
        fake.QueryScript.Enqueue(BitLockerState.Suspended);
        var log = new TestLogger();
        using (var scope = BitLockerCaptureScope.Arm("C:", fake, log))
        {
            Assert.Equal("ProtectedUnlocked", scope.CaptureState);
        }
        Assert.Equal(new[] { "query:C:", "suspend:C:", "query:C:", "resume:C:" }, fake.Calls);
        Assert.Contains(log.Messages, m => m.StartsWith("WRN:BitLocker:") && m.Contains("half-applied"));
    }

    [Fact]
    public void SuspendFailure_HalfAppliedResumeFails_ErrorsLoud_ProceedsSuspended()
    {
        var fake = new FakeBitLocker(BitLockerState.ProtectedUnlocked) { ThrowOnSuspend = true, ThrowOnResume = true };
        fake.QueryScript.Enqueue(BitLockerState.ProtectedUnlocked);
        fake.QueryScript.Enqueue(BitLockerState.Suspended);
        var log = new TestLogger();
        using (var scope = BitLockerCaptureScope.Arm("C:", fake, log))
        {
            // Manifest names the honest end state, not the wish.
            Assert.Equal("Suspended", scope.CaptureState);
        }
        Assert.Equal(new[] { "query:C:", "suspend:C:", "query:C:", "resume:C:" }, fake.Calls);
        Assert.Contains(log.Messages, m => m.StartsWith("ERR:BitLocker:") && m.Contains("manage-bde -protectors -enable C:"));
    }

    [Fact]
    public void ResumeFailure_LogsError_DoesNotThrow()
    {
        var fake = new FakeBitLocker(BitLockerState.ProtectedUnlocked) { ThrowOnResume = true };
        var log = new TestLogger();
        using (var scope = BitLockerCaptureScope.Arm("C:", fake, log))
        {
        }
        Assert.Equal(new[] { "query:C:", "suspend:C:", "resume:C:", "resume:C:" }, fake.Calls);
        Assert.Contains(log.Messages, m => m.StartsWith("ERR:BitLocker:") && m.Contains("manage-bde -protectors -enable C:"));
    }

    [Fact]
    public void ParseState_LockedBeatsUnlockedSubstring()
    {
        const string locked = "Lock Status: Locked\nProtection Status: Protection On\n";
        Assert.Equal(BitLockerState.Locked, BitLockerParser.ParseState(locked, Array.Empty<string>()));
        const string unlocked = "Lock Status: Unlocked\nProtection Status: Protection On\n";
        Assert.Equal(BitLockerState.ProtectedUnlocked, BitLockerParser.ParseState(unlocked, Array.Empty<string>()));
    }

    [Fact]
    public void ParseState_ProtectionOff_DisambiguatesViaProtectors()
    {
        const string off = "Lock Status: Unlocked\nProtection Status: Protection Off\n";
        Assert.Equal(BitLockerState.Suspended,
            BitLockerParser.ParseState(off, new[] { "{33333333-3333-3333-3333-333333333333}" }));
        Assert.Equal(BitLockerState.Unprotected,
            BitLockerParser.ParseState(off, Array.Empty<string>()));
    }

    [Fact]
    public void ParseKeyIds_KeepsIdsOnly_NeverKeyMaterial()
    {
        const string protectors = """
            Numerical Password:
              ID: {AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA}
              Password: 111111-222222-333333-444444-555555-666666-777777-888888
            TPM:
              ID: {BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB}
            """;
        var ids = BitLockerParser.ParseKeyIds(protectors);
        Assert.Equal(new[] { "{AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA}", "{BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB}" }, ids);
        Assert.DoesNotContain(ids, id => id.Contains("111111"));
    }

    [Fact]
    public void Manifest_RecordsBitLocker_RoundTripsHmac()
    {
        var m = ManifestStore.Build(1, "bl", 1024, 1, 3, "key-1", "", new List<ManifestEntry>(),
            bitLockerState: "Suspended", bitLockerKeyIds: new List<string> { "{AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA}" });
        var dir = Path.Combine(_dir, "manifests");
        var key = new byte[32];
        var path = ManifestStore.Save(dir, m, key);
        var loaded = ManifestStore.Load(path, key);
        Assert.Equal("Suspended", loaded.BitLockerState);
        Assert.Equal(new[] { "{AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA}" }, loaded.BitLockerKeyIds);
    }

    [Fact]
    public void Manifest_Default_OmitsBitLockerFields()
    {
        var m = ManifestStore.Build(1, "plain", 1024, 1, 3, "key-1", "", new List<ManifestEntry>());
        var json = System.Text.Json.JsonSerializer.Serialize(m);
        Assert.DoesNotContain("BitLocker", json, StringComparison.Ordinal);
    }

    // Fake volume source: FileBlockSource bytes + a scripted BitLocker
    // record, proving the Store `as`-pattern picks it up.
    private sealed class BitLockerFileSource : IBlockSource, IBitLockerVolume
    {
        private readonly FileBlockSource _inner;
        public BitLockerFileSource(string path, string state, List<string>? keyIds)
        {
            _inner = new FileBlockSource(path);
            BitLockerState = state;
            BitLockerKeyIds = keyIds;
        }
        public long Length => _inner.Length;
        public string? BitLockerState { get; }
        public List<string>? BitLockerKeyIds { get; }
        public Stream OpenRead() => _inner.OpenRead();
        public void Dispose() => _inner.Dispose();
    }

    [Fact]
    public void Store_Image_RecordsBitLockerFromSource()
    {
        var vol = Path.Combine(_dir, "vol.img");
        File.WriteAllBytes(vol, new byte[1024]);
        using var store = new Store(Path.Combine(_dir, "store"), Path.Combine(_dir, "chunks.db"), 1, 1, new TestLogger());
        using var source = new BitLockerFileSource(vol, "Suspended", new List<string> { "{AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA}" });
        var m = store.Image(source, "bl");
        Assert.Equal("Suspended", m.BitLockerState);
        Assert.Equal(new[] { "{AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA}" }, m.BitLockerKeyIds);
    }

    [Fact]
    public void Store_Image_PlainSource_OmitsBitLocker()
    {
        var vol = Path.Combine(_dir, "vol2.img");
        File.WriteAllBytes(vol, new byte[1024]);
        using var store = new Store(Path.Combine(_dir, "store2"), Path.Combine(_dir, "chunks2.db"), 1, 1, new TestLogger());
        using var source = new FileBlockSource(vol);
        var m = store.Image(source, "plain");
        Assert.Null(m.BitLockerState);
        Assert.Null(m.BitLockerKeyIds);
    }
}
