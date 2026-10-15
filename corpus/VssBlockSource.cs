using System;
using System.Collections.Generic;
using System.IO;
using TicTack;

namespace Corpus
{
    // IBlockSource over a live VSS snapshot: the ctor freezes the volume,
    // OpenRead streams raw sectors from the snapshot device, Dispose deletes
    // the shadow. Completion-aware: the read wrapper reports a full-length
    // read so writers get BackupComplete on success, AbortBackup on any
    // short or failed read (probes must never Complete — see VssSnapshot).
    public sealed class VssBlockSource : ISparseBlockSource, IVolumeHandleProvider, IMftVolume, IBitLockerVolume
    {
        private readonly VssSnapshot? _snap;
        private readonly BitLockerCaptureScope? _bitlocker;
        private readonly string _device;
        private readonly Lazy<IMftVolume> _mft;
        private bool _fullyRead;
        private bool _disposed;

        public VssBlockSource(string drive, ILogger log)
            : this(drive, log, ManageBdeBitLocker.Instance)
        {
        }

        // Item 12b: the scope arms BEFORE the snapshot freezes the volume
        // (suspend-then-capture) and resumes AFTER teardown; the catch
        // re-enables even when snapshot creation itself throws.
        internal VssBlockSource(string drive, ILogger log, IBitLockerController bitlocker)
        {
            _bitlocker = BitLockerCaptureScope.Arm(drive, bitlocker, log);
            try
            {
                _snap = VssSnapshot.Create(drive, log);
                _device = _snap.DeviceObject.TrimEnd('\\');
                Length = DeviceLength(drive, _device);
                BytesPerCluster = VolumeBitmap.BytesPerCluster(drive + "\\");
                _mft = new Lazy<IMftVolume>(() => new MftVolumeReader(_device));
            }
            catch
            {
                _bitlocker.Dispose();
                throw;
            }
        }

        // Test seam: no shadow (Linux-safe); the factory mirrors the
        // production MftVolumeReader(device) open with a scripted fake.
        internal VssBlockSource(string device, long length, long bytesPerCluster, Func<string, IMftVolume> mftFactory)
        {
            ArgumentException.ThrowIfNullOrEmpty(device);
            ArgumentNullException.ThrowIfNull(mftFactory);
            _snap = null;
            _bitlocker = null;
            _device = device;
            Length = length;
            BytesPerCluster = bytesPerCluster;
            _mft = new Lazy<IMftVolume>(() => mftFactory(device));
        }

        // Length is the FS-readable total, NOT the IOCTL partition
        // length: NTFS reserves the last cluster (backup boot), so the
        // device stream EOFs 4096 bytes short of the partition extent
        // (104722432 vs 104726528 on the toy). A long Length would also
        // break full-read completion (writers would always Abort).
        // RAW volumes have no FS view — IOCTL is exact there.
        internal static long DeviceLength(string drive, string device)
        {
            try { return VolumeBitmap.TotalBytes(drive + "\\"); }
            catch (IOException) { return VolumeBitmap.GetLengthBytes(device); }
        }

        public long Length { get; }

        public long BytesPerCluster { get; }

        public IReadOnlyList<(long Start, long Count)> GetAllocatedRuns()
            => VolumeBitmap.ReadRuns(_device, Length / BytesPerCluster);

        // USN planning reads the journal + MFT from the SAME frozen shadow
        // the bytes come from (never live — skew would tear the manifest).
        string IVolumeHandleProvider.SnapshotDevicePath => _device;

        // IMftVolume over the FROZEN shadow (never live — the same
        // device the bytes come from, mirroring TryOpenVolume). Lazy: an
        // image that never touches the MFT never opens the handle. Open
        // faults normalize to InvalidDataException so both MFT consumers
        // degrade (baseline omitted, planner falls back to full) instead of
        // faulting the image — mid-read IO faults still propagate.
        private IMftVolume Mft
        {
            get
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                try
                {
                    return _mft.Value;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    throw new InvalidDataException($"MFT unavailable on {_device} ({ex.Message})", ex);
                }
            }
        }

        public int BytesPerRecord => Mft.BytesPerRecord;

        int IMftVolume.BytesPerCluster => Mft.BytesPerCluster;

        public long RecordCount => Mft.RecordCount;

        public IReadOnlyList<(long StartLcn, long ClusterCount)> MftRuns => Mft.MftRuns;

        public IReadOnlyList<MftEnumEntry> Enumerate() => Mft.Enumerate();

        public byte[] GetRecordBytes(ulong fileRef) => Mft.GetRecordBytes(fileRef);

        public Stream OpenRead()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var fs = new FileStream(_device, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return new VssReadStream(fs, Length, () => _fullyRead = true);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_mft.IsValueCreated)
                _mft.Value.Dispose();
            if (_fullyRead) _snap?.Complete();
            _snap?.Dispose();
            // Resume AFTER shadow teardown: the suspension covers teardown too.
            _bitlocker?.Dispose();
        }

        // IBitLockerVolume: the manifest records the capture-time state via
        // the same `as`-pattern Store uses for the other optional providers.
        string? IBitLockerVolume.BitLockerState => _bitlocker?.CaptureState;
        List<string>? IBitLockerVolume.BitLockerKeyIds => _bitlocker?.CaptureKeyIds;
    }

    // Segment-buffered read passthrough over a shadow device. Two jobs:
    // it fires the callback once the stream has yielded its full length
    // (Length is the known volume length, not the device handle's, whose
    // size query may throw), and it reads the device in explicit 256MB
    // segments (seek + big-count fills into one big array, served to callers
    // via memcpy). The shape is load-bearing, not an optimization: small
    // destination buffers misread shadow devices past ~8MB (deterministic,
    // GC-address-dependent garbage — 1MB reads diverge 32MB from big reads
    // of the same frozen device, in both runtimes), while big-array reads
    // match live within skew every time. So the device never sees a small
    // read here: every fill is one explicit-offset 256MB (or tail) span.
    // FileBlockSource needs none of this — plain files don't desync — so
    // the quirk stays local to VSS reads. Seeks are lazy (reposition only;
    // the window refills on the next read), so arbitrary seek patterns stay
    // correct; the chunker only ever moves forward anyway.
    internal sealed class VssReadStream : Stream
    {
        private const int DefaultSegBytes = 256 * 1024 * 1024;
        private readonly Stream _inner;
        private readonly long _length;
        private readonly Action _onFull;
        private readonly byte[] _seg;
        private readonly int _segBytes;
        private long _segStart = -1;
        private int _segLen;
        private long _pos;
        private bool _fired;

        public VssReadStream(Stream inner, long length, Action onFull)
            : this(inner, length, onFull, DefaultSegBytes)
        {
        }

        internal VssReadStream(Stream inner, long length, Action onFull, int segBytes)
        {
            _inner = inner;
            _length = length;
            _onFull = onFull;
            _segBytes = segBytes;
            _seg = new byte[segBytes];
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _pos; set => _pos = value; }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin)
        {
            _pos = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _pos + offset,
                _ => _length + offset,
            };
            return _pos;
        }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var total = 0;
            while (count > 0)
            {
                var start = (_pos / _segBytes) * _segBytes;
                if (start != _segStart)
                {
                    Fill(start);
                    _segStart = start;
                }
                var take = (int)Math.Min(count, _segStart + _segLen - _pos);
                if (take <= 0) break;
                Buffer.BlockCopy(_seg, (int)(_pos - _segStart), buffer, offset, take);
                offset += take;
                count -= take;
                _pos += take;
                total += take;
            }
            if (_pos >= _length && !_fired)
            {
                _fired = true;
                _onFull();
            }
            return total;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }

        // One explicit-offset fill; never reads past Length (tail segments
        // are short). A short device read is a fault, not EOF — the frozen
        // view must be full — so fail loud (no completion on Abort paths).
        private void Fill(long start)
        {
            _inner.Seek(start, SeekOrigin.Begin);
            var want = (int)Math.Min(_segBytes, _length - start);
            var got = 0;
            while (got < want)
            {
                var n = _inner.Read(_seg, got, want - got);
                if (n <= 0) break;
                got += n;
            }
            if (got != want) throw new IOException($"short shadow read at {start}: {got} of {want}");
            _segLen = got;
        }
    }
}
