using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using TicTack;

namespace Corpus
{
    // Item-13 TRUTH core: SHA-256 digests over raw MFT record bytes, grouped
    // into image-chunk-sized bands. Any extent relocation (defrag/optimize),
    // size, timestamp, or run-list change alters record bytes → the group
    // digest moves → the file's extents re-capture. Whole-record hashing is
    // deliberately write-sensitive (LSN included): conservative, never blind.
    // Completeness: a relocated file's CURRENT runs dirty the new clusters;
    // the abandoned clusters either belong to another changed record or are
    // free — and freed space is caught by the bitmap hole-flip net.
    // The per-record list below is the §9 SKIP input, not capture truth, so
    // it hashes LSN/USA-insensitive (SkipRecordDigest): every record rewrite
    // bumps the $LogFile sequence number (LSN @0x08, 8 bytes) and the update
    // sequence number in the fixup words, so a mount/VSS touch flips ~all
    // raw digests and the record gate never fires (B6E: 217/256). Masked are
    // ONLY the two per-write counters — sequence@0x10 (record reuse signal),
    // flags, and all content stay sensitive. Groups stay raw: capture plans
    // over-capture, never under.
    public static class MftDigest
    {
        // Skip-decision record-list version. v4 (current) reads the RECORD
        // pass (bitmap liveness classify + SkipRecordDigest) through the
        // LIVE volume handle (design B): shadow-view bytes smear
        // intermittently on allocated records (B6K: FRNs 32/33/34/36 read
        // shadow≠live; v10 FRN 38 flapped run-to-run on the same frozen
        // view), and the smear sits outside every mask — so a
        // record-through-shadow pass can never skip deterministically
        // (fail-safe: the smear keeps vetoing). Live-view user-record bytes
        // are proven stable (B6H: 0/49 over 75s idle; B6K: user rec55
        // stable across a full VSS cycle; live VSS-cycle dirt is exactly
        // the excluded 7-record set), so live-vs-live compares converge in
        // ≤1 transitional gen. v3 (shadow-view) gens compare unequal on
        // smeared records and refuse once to the byte backstop — fail to
        // skip, never wrong-skip — then self-heal (the next gen is written
        // live-view by current code). v2/v1/v0 same as before.
        // The record gate requires current and refuses older gens once to
        // the backstop, then self-heal (the next gen is written by current
        // code). v5 adds SI last-access masking for all FRNs (B6AB: the
        // veto is an atime-only step — content writes always move SI
        // modified-time, so atime alone never hides user data).
        internal const int SkipRecordVersion = 5;

        // Absent-slot sentinels for the skip-decision record list (never
        // 64-hex, so IsLiveDigest stays a length check). A slot is ABSENT
        // — never hashed, never compared — when it carries no restorable
        // data: "empty" (magic != 'FILE': zero/never-allocated) or "dead"
        // (FILE magic, flags&0x01 == 0: inactive record). Live raw reads
        // are byte-static, but FSCTL_GET_NTFS_FILE_RECORD through the VSS
        // shadow returns unstable bytes for unallocated FRNs (B6F: 207
        // empty slots == exactly the run2 flip count), so hashing them
        // vetoes every idle skip. Short/malformed records stay live
        // (hashed): sensitive, never blind.
        internal const string DeadRecordSentinel = "dead";
        internal const string EmptyRecordSentinel = "empty";

        internal static bool IsLiveRecord(byte[] record)
        {
            if (record.Length < 0x18)
                return true;
            return record[0] == (byte)'F' && record[1] == (byte)'I'
                && record[2] == (byte)'L' && record[3] == (byte)'E'
                && (BitConverter.ToUInt16(record, 0x16) & 1) != 0;
        }

        internal static string AbsentSentinel(byte[] record) =>
            record.Length >= 4 && record[0] == (byte)'F' && record[1] == (byte)'I'
                && record[2] == (byte)'L' && record[3] == (byte)'E'
                ? DeadRecordSentinel : EmptyRecordSentinel;

        internal static bool IsLiveDigest(string? digest) =>
            digest != null && digest.Length == 64;

        // Bitmap liveness for one slot: false = proven unallocated (never
        // hashed, even when the shadow view shows FILE magic); true =
        // allocated; null = unknown (bitmap unreadable, absent, or short —
        // byte classification exactly as before, never the reverse).
        internal static bool? BitmapAllocated(byte[]? bitmap, long rec)
        {
            if (bitmap == null || rec < 0 || rec >= (long)bitmap.Length * 8)
                return null;
            return (bitmap[rec / 8] & (1 << (int)(rec % 8))) != 0;
        }

        // One skip-decision record entry. Bit-clear → 'empty' sentinel
        // (B6L: unallocated slots read FILE-live through the shadow while
        // raw live bytes are zeros — hashing them vetoes every idle skip).
        // Bit-set + live → hashed as today; bit-set + non-live → 'dead'
        // (best effort from bytes). Unknown bitmap → AbsentSentinel, i.e.
        // today's byte classification, never a new empty claim.
        internal static string ClassifySkipRecord(byte[] record, long rec, bool? allocated)
        {
            if (allocated == false)
                return EmptyRecordSentinel;
            if (IsLiveRecord(record))
                return SkipRecordDigest(record, rec == (long)MftPathResolver.RootFileRef);
            return allocated == null ? AbsentSentinel(record) : DeadRecordSentinel;
        }

        // True when the runtime type supplies its own bitmap (public or
        // explicit override); false = the DIM default silently returned
        // null. Null-branch only (one InterfaceMap per image, never hot).
        internal static bool OverridesBitmap(IMftVolume vol)
        {
            ArgumentNullException.ThrowIfNull(vol);
            var map = vol.GetType().GetInterfaceMap(typeof(IMftVolume));
            for (var i = 0; i < map.InterfaceMethods.Length; i++)
            {
                if (map.InterfaceMethods[i].Name == nameof(IMftVolume.GetMftAllocationBitmap))
                    return map.TargetMethods[i].DeclaringType != typeof(IMftVolume);
            }
            return false;
        }

        public static string RecordDigest(byte[] record)
        {
            ArgumentNullException.ThrowIfNull(record);
            using var sha = SHA256.Create();
            var len = BitConverter.GetBytes(record.Length);
            sha.TransformBlock(len, 0, len.Length, null, 0);
            sha.TransformFinalBlock(record, 0, record.Length);
            return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        }

        // Skip-comparison digest: RecordDigest over the record with the two
        // per-write counters zeroed. FILE header layout (verified against
        // ParseRecordExtents' seq@0x10/flags@0x16 reads and the fake builder's
        // FILE@0/LSN@8/seq@0x10 map): magic [0,4), USA offset/count [4,8),
        // LSN [8,16), sequence [16,18), ... — the brief's "bytes 0-7" is NOT
        // the LSN (that is magic + fixup offset/count: structural, kept).
        // USA words live at [usaOff, usaOff+usaCount*2) (typically [42,48)
        // or [48,54)); the offset/count themselves stay sensitive
        // (structural). Sector-end fixup tails ([stride-2,stride) per sector,
        // stride derived from the USA count) hold the USN copy and flip on
        // the same re-stamps — masked, never content. The root-SI flag is
        // FRN-5-only (the caller passes rec == RootFileRef): four FILETIMEs,
        // void for namespace. Every other FRN masks only the SI
        // last-access FILETIME (content+24, 8B): the read-only touch
        // (B6AB). SI modified/created/change-time stay sensitive
        // everywhere — every content write moves modified-time. Anything
        // short or malformed hashes raw: sensitive, never blind.
        // B6O ground truth (FRN 38 = U:\big, in-use dir, usaOff=48/count=3/
        // firstAttr=56): a VSS create flips EXACTLY bytes 48,510,1022
        // (USN 0x5B->0x5C + its two sector-trailer copies; LSN/seq/flags/
        // index bit-identical) — all three land inside this mask, so the
        // flip is invisible here, matching the chunk backstop (which
        // tolerates it by zeroing the MFT sys-extent range). Masking stays
        // SAFE for directories: a real child create/delete/rename always
        // rewrites the parent's index bytes ($INDEX_ROOT/ALLOCATION runs)
        // or the child's own record bytes, all of which stay sensitive —
        // only the two per-write counters are blind. Sequence@0x10 stays
        // sensitive for the same reason (slot reuse must veto).
        public static string SkipRecordDigest(byte[] record, bool maskRootTimestamps = false)
        {
            ArgumentNullException.ThrowIfNull(record);
            var masked = (byte[])record.Clone();
            if (masked.Length >= 0x10)
                Array.Clear(masked, 0x08, 8); // LSN: the $LogFile cursor, void for content
            if (masked.Length >= 0x08)
            {
                var usaOff = BitConverter.ToUInt16(masked, 0x04);
                var usaCount = BitConverter.ToUInt16(masked, 0x06);
                if (usaCount > 0 && usaOff >= 0x10 && (long)usaOff + (long)usaCount * 2 <= masked.Length)
                {
                    Array.Clear(masked, usaOff, usaCount * 2); // fixup words: the write counter, void for content
                    MaskSectorTails(masked, usaCount);
                }
            }
            if (maskRootTimestamps)
                MaskStandardInfoTimestamps(masked);
            else
                MaskStandardInfoAccessTime(masked);
            return RecordDigest(masked);
        }

        // Sector-end fixup tails: every sector's last word holds the USN
        // copy, so a USA re-stamp flips them alongside the USA block (B6K:
        // USA-only flips on FRNs 6/33/34 move [510:512)/[1022:1024) too —
        // masking only the USA block leaks a free veto per VSS cycle).
        // Stride derived from the USA count (sectors = count-1); only exact
        // 512-divisible strides are trusted, else strict (never blind).
        internal static void MaskSectorTails(byte[] masked, ushort usaCount)
        {
            var sectors = usaCount - 1;
            if (sectors <= 0 || masked.Length % sectors != 0)
                return;
            var stride = masked.Length / sectors;
            if (stride < 512 || stride % 512 != 0)
                return;
            for (var s = 1; s <= sectors; s++)
            {
                var tail = s * stride - 2;
                if (tail >= 0 && tail + 2 <= masked.Length)
                    Array.Clear(masked, tail, 2);
            }
        }

        // Resident $STANDARD_INFORMATION (0x10) timestamp mask, FRN-5-only.
        // Clears the four FILETIME fields (32B at content start); everything
        // else — file attributes, index roots, filenames — stays sensitive.
        // MaskStandardInfoAccessTime below clears only the last-access
        // field (content+24, 8B) for every other FRN. Malformed or
        // non-resident SI masks nothing (strict, never blind).
        internal static void MaskStandardInfoTimestamps(byte[] masked)
        {
            if (masked.Length < 0x18)
                return;
            var first = BitConverter.ToUInt16(masked, 0x14);
            var used = BitConverter.ToUInt32(masked, 0x18);
            var end = (int)Math.Min(used, (uint)masked.Length);
            if (first < 42 || first >= end)
                return;
            var off = (int)first;
            while (true)
            {
                if (off + 8 > end)
                    return;
                var type = BitConverter.ToUInt32(masked, off);
                if (type == 0xFFFFFFFF)
                    return;
                var len = BitConverter.ToUInt32(masked, off + 4);
                if (len < 8 || off + len > end || off + len <= off)
                    return;
                if (type == 0x10 && len >= 24 && masked[off + 8] == 0)
                {
                    var contentLen = BitConverter.ToUInt32(masked, off + 16);
                    var contentOff = BitConverter.ToUInt16(masked, off + 20);
                    if (contentLen >= 32 && (long)contentOff + 32 <= len
                        && off + contentOff + 32 <= masked.Length)
                        Array.Clear(masked, off + contentOff, 32);
                    return;
                }
                off += (int)len;
            }
        }

        // Resident $STANDARD_INFORMATION (0x10) LAST-ACCESS mask, every
        // FRN. Clears only the fourth FILETIME (content+24, 8B): NTFS
        // $STANDARD_INFORMATION content is creation@+0, modified@+8,
        // MFT-change@+16, accessed@+24, so +24 is the read-only touch
        // (B6AB: FRN 38's veto steps sit at record @104-108 = attr
        // content start +24). Modified/created/change-time stay
        // sensitive: every content write moves modified-time, so no user
        // data hides behind this mask. Same walk discipline as the
        // FRN-5 full mask above (strict, never blind).
        internal static void MaskStandardInfoAccessTime(byte[] masked)
        {
            if (masked.Length < 0x18)
                return;
            var first = BitConverter.ToUInt16(masked, 0x14);
            var used = BitConverter.ToUInt32(masked, 0x18);
            var end = (int)Math.Min(used, (uint)masked.Length);
            if (first < 42 || first >= end)
                return;
            var off = (int)first;
            while (true)
            {
                if (off + 8 > end)
                    return;
                var type = BitConverter.ToUInt32(masked, off);
                if (type == 0xFFFFFFFF)
                    return;
                var len = BitConverter.ToUInt32(masked, off + 4);
                if (len < 8 || off + len > end || off + len <= off)
                    return;
                if (type == 0x10 && len >= 24 && masked[off + 8] == 0)
                {
                    var contentLen = BitConverter.ToUInt32(masked, off + 16);
                    var contentOff = BitConverter.ToUInt16(masked, off + 20);
                    if (contentLen >= 32 && (long)contentOff + 32 <= len
                        && off + contentOff + 32 <= masked.Length)
                        Array.Clear(masked, off + contentOff + 24, 8);
                    return;
                }
                off += (int)len;
            }
        }

        // Group i covers MFT-stream bytes [i*groupBytes,(i+1)*groupBytes):
        // records [i*per,(i+1)*per) at `per` records per group. Streaming —
        // no whole-MFT alloc at any file count. GroupsAndRecords shares the
        // single MFT pass between capture groups and skip-decision records.
        // The optional log enables ONE bounded INFO line naming the bitmap
        // outcome (skip-path callers pass it; silent otherwise).
        public static (List<string> Groups, List<string> Records) ComputeGroupsAndRecords(IMftVolume vol, int groupBytes, ILogger? log = null) =>
            ComputeGroupsAndRecords(vol, vol, groupBytes, log);

        // §9 design B, dual-view form: groups hash the SHADOW view (capture
        // truth — the bulk bytes come from the frozen snapshot), records
        // hash the LIVE view (bitmap liveness classify + SkipRecordDigest —
        // live user-record bytes are stable, shadow bytes smear). Same
        // volume, two handles; identical views hash identically, so
        // single-view callers behave exactly as before.
        public static (List<string> Groups, List<string> Records) ComputeGroupsAndRecords(IMftVolume groupsVol, IMftVolume recordsVol, int groupBytes, ILogger? log = null)
        {
            ArgumentNullException.ThrowIfNull(groupsVol);
            ArgumentNullException.ThrowIfNull(recordsVol);
            if (groupBytes <= 0) throw new ArgumentOutOfRangeException(nameof(groupBytes));
            var per = Math.Max(1, groupBytes / groupsVol.BytesPerRecord);
            var groups = new List<string>();
            var records = new List<string>();
            // Liveness source: the $MFT $BITMAP when readable (stable
            // through the shadow), else null → per-slot byte fallback.
            // Read from the RECORDS view: on the live view the bitmap is an
            // allocated stream like any other (same stable read class §7's
            // hole-skip relies on). One read for the whole pass, never per
            // record; any fault → null (fail-safe: unknown is never
            // unallocated).
            byte[]? bitmap = null;
            string? bitmapNote = null;
            try
            {
                bitmap = recordsVol.GetMftAllocationBitmap();
                if (bitmap == null)
                    bitmapNote = OverridesBitmap(recordsVol)
                        ? $"absent: {recordsVol.GetType().Name} returned null"
                        : $"no-override: {recordsVol.GetType().Name} uses the interface default";
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                bitmapNote = $"read-throw:{ex.GetType().Name}";
            }
            if (log != null)
            {
                if (bitmap != null && (long)bitmap.Length * 8 >= recordsVol.RecordCount)
                    log.Info($"mft-bitmap: ok {bitmap.Length} bytes for {recordsVol.RecordCount} records");
                else if (bitmap != null)
                    log.Info($"mft-bitmap: short {bitmap.Length} bytes for {recordsVol.RecordCount} records (tail slots unknown)");
                else
                    log.Info($"mft-bitmap: null ({bitmapNote})");
            }
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var inGroup = 0;
            for (var rec = 0L; rec < groupsVol.RecordCount; rec++)
            {
                var bytes = groupsVol.GetRecordBytes((ulong)rec);
                hash.AppendData(BitConverter.GetBytes(bytes.Length));
                hash.AppendData(bytes);
                if (++inGroup == per)
                {
                    groups.Add(Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
                    inGroup = 0;
                }
            }
            if (inGroup > 0)
                groups.Add(Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
            for (var rec = 0L; rec < recordsVol.RecordCount; rec++)
            {
                var bytes = ReadStableRecordBytes(recordsVol, (ulong)rec);
                // Skip input is live-only: dead/empty slots carry no
                // restorable data and their shadow-view bytes are unstable,
                // so they are excluded here (sentinel, never hashed) and
                // Gate1 never compares them. FRN 5
                // (root: architectural, cf. MftPathResolver.RootFileRef)
                // additionally masks SI timestamps: a VSS cycle bumps the
                // root mtime whenever SVI gains/loses children, while index
                // changes (namespace) must still trip the gate.
                var entry = ClassifySkipRecord(bytes, rec, BitmapAllocated(bitmap, rec)); // skip input: LSN/USA/tails/SI-timestamps-insensitive; groups above stay raw
                records.Add(entry);
            }
            return (groups, records);
        }

        public static List<string> ComputeGroups(IMftVolume vol, int groupBytes) =>
            ComputeGroupsAndRecords(vol, groupBytes).Groups;

        // Capture-time per-record stabilization (B6R: 6/6 vetoes on FRN 38
        // = U:\big while the bins prove the USN re-stamp itself is invisible
        // to SkipRecordDigest — the veto is a torn PARENT-side read the
        // retry can never cure, since it only re-reads current). Each
        // records-pass slot is read twice back-to-back and accepted on byte
        // agreement; on mismatch the slot is re-read (bounded) until two
        // consecutive reads agree. A torn read won't repeat, persistent
        // dirt will. Never wrong-skips: worst case returns the last read,
        // i.e. today's single-read behavior. Groups stay single-read:
        // capture truth over-captures, never skips.
        internal const int MaxStableRecordReads = 4;

        internal static byte[] ReadStableRecordBytes(IMftVolume vol, ulong rec)
        {
            var prev = vol.GetRecordBytes(rec);
            for (var i = 1; i < MaxStableRecordReads; i++)
            {
                var next = vol.GetRecordBytes(rec);
                if (next.SequenceEqual(prev))
                    return next;
                prev = next;
            }
            return prev;
        }

        // Changed group indexes. Growth (new files) dirties the extra groups;
        // shrink means reformat-level surgery → null (full re-read).
        public static List<int>? DiffGroups(IReadOnlyList<string> parent, IReadOnlyList<string> current)
        {
            ArgumentNullException.ThrowIfNull(parent);
            ArgumentNullException.ThrowIfNull(current);
            if (current.Count < parent.Count)
                return null;
            var changed = new List<int>();
            for (var i = 0; i < current.Count; i++)
            {
                if (i >= parent.Count || !string.Equals(parent[i], current[i], StringComparison.Ordinal))
                    changed.Add(i);
            }
            return changed;
        }
    }

    // Skip-decision sys mask for the §12.8 gates (NEVER for capture plans:
    // incrementals re-read sys extents — a stale $MFT corrupts restores).
    // FRN 0..15 ($MFT/$MFTMirr/$LogFile/$Bitmap/...) churn on every mount,
    // so a skip gate counting them as user changes never fires on live
    // NTFS. Plus explicitly-advisory NAMED metadata resolved from CURRENT
    // geometry (never a blind FRN widening):
    //   $TxfLog subtree — TxF containers churn on every mount/VSS touch yet
    //     live outside FRN 0..15, so the FRN mask alone never covers them.
    //     SKIP-SAFE: TxF is dead (deprecated since Win10) — in-flight
    //     transactions abort on crash, so no restore ever needs these bytes.
    //   $UsnJrnl host (incl. the $J stream: 0x80 matches every $DATA stream
    //     incl. named ADS) — the journal is advisory and one interval stale
    //     by design; a post-restore cursor past its EOF falls back to full
    //     re-read. SKIP-SAFE.
    //   VSS machinery (B6K: a bare create/delete cycle moves LIVE bytes on 7
    //     records, all outside the above): $Tops + $TxfLog.blf by bare-$
    //     name (orphan-proof: the $Extend chain degrades on live boxes, and
    //     bare-$ needs no parents), orphan TxF containers by
    //     "TxfLogContainer" prefix (the dir row itself may be absent, so the
    //     subtree never covers them), the SVI dir BY NAME (FRNs vary) with
    //     its subtree, and the transient {guid} diff files by brace pattern
    //     scoped to SVI children (a same-shaped user file elsewhere is NOT
    //     excluded). SKIP-SAFE: VSS diff bytes belong to the snapshot, not
    //     the volume — each cycle mints a new GUID and deletes the last.
    //     Deletion-visibility delay is accepted (store keeps chunks; the
    //     next gen drops the file); growth-tail and user-record paths stay
    //     strict, so live user data never wrong-skips.
    // Current geometry only: a relocated journal/TxF/VSS file leaves the
    // abandoned bytes unmasked, so the masked compare fails there — fail to
    // skip, never wrong-skip. Reuse of abandoned clusters by a user file
    // always flips that file's own record too, which Gate 1 refuses
    // (non-advisory FRN). Null = unreadable/unknown → the caller stays
    // strict.
    internal static class MftSkipMask
    {
        internal const string TxfLogPath = "$Extend\\$RmMetadata\\$TxfLog";
        internal const string UsnJrnlPath = "$Extend\\$UsnJrnl";
        // Bare $-names, unioned with the pathed forms above: the live
        // FSCTL_ENUM_USN_DATA listing does not resolve the $Extend chain to
        // the pathed form (B6E: pathed yields 0 refs while the "$UsnJrnl:$J"
        // exclusion — the same $-anywhere rule — resolves on the same
        // shadow), so FullPath degrades and pathed equality misses a PRESENT
        // row. Standing item-4 assumption (see MftPathResolver): user files
        // never carry the $ prefix, so a bare $ name is exactly as safe here
        // as the exclusions already are.
        internal const string TxfLogName = "$TxfLog";
        internal const string UsnJrnlName = "$UsnJrnl";
        // B6K VSS-machinery names. $Tops/$TxfLog.blf live beside (not under)
        // the $TxfLog dir name, so the subtree never covers them; the
        // container prefix and the diff-file pattern cover rows whose
        // parents are absent from the enumeration (orphans). The SVI name is
        // matched case-insensitively like every other resolver input; a
        // user dir with exactly this name is pathological (OS-reserved).
        internal const string TopsName = "$Tops";
        internal const string TxfLogBlfName = "$TxfLog.blf";
        internal const string TxfContainerPrefix = "TxfLogContainer";
        internal const string SviDirName = "System Volume Information";

        // VSS diff-file shape: one or more braced GUIDs ("{g1}", "{g1}{g2}").
        // Pure syntax check — the caller scopes it to SVI children, so a
        // same-shaped user file elsewhere (GodMode dirs, braced names) is
        // never excluded by this.
        internal static bool IsShadowDiffName(string name)
        {
            ArgumentNullException.ThrowIfNull(name);
            if (name.Length < 10 || name[0] != '{' || name[name.Length - 1] != '}')
                return false;
            var i = 0;
            while (i < name.Length)
            {
                if (name[i] != '{')
                    return false;
                var close = name.IndexOf('}', i + 1);
                if (close < 0)
                    return false;
                var inner = name.Substring(i + 1, close - i - 1);
                if (inner.Length < 8)
                    return false;
                foreach (var c in inner)
                {
                    if (!char.IsAsciiHexDigit(c) && c != '-')
                        return false;
                }
                i = close + 1;
            }
            return true;
        }

        internal static (List<(long StartLcn, long ClusterCount)> Runs, HashSet<long> AdvisoryRecords)? Resolve(IMftVolume vol, ILogger log)
        {
            ArgumentNullException.ThrowIfNull(vol);
            ArgumentNullException.ThrowIfNull(log);
            var runs = new List<(long StartLcn, long ClusterCount)>();
            var advisory = new HashSet<long>();
            var dropped = new List<string>();
            var sysOk = 0;
            for (var sys = 0UL; sys < 16 && (long)sys < vol.RecordCount; sys++)
            {
                List<(long StartLcn, long ClusterCount)>? extents;
                try
                {
                    var bytes = vol.GetRecordBytes(sys);
                    extents = MftExtentReader.ParseRecordExtents(bytes, sys,
                        vol.BytesPerCluster, vol.BytesPerRecord, vol.MftRuns, skipSequenceCheck: true);
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    log.Info($"sys-extents: unresolved (frn {sys}: {ex.GetType().Name})");
                    return null;
                }
                if (extents == null)
                {
                    dropped.Add($"{sys}:null");
                    continue;
                }
                sysOk++;
                runs.AddRange(extents);
            }
            List<MftEnumEntry> entries;
            try
            {
                entries = new List<MftEnumEntry>(vol.Enumerate());
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                log.Info($"sys-extents: unresolved (enumerate: {ex.GetType().Name})");
                return null;
            }
            var txfRefs = MftPathResolver.Resolve([TxfLogPath, TxfLogName], entries);
            var jrnlRefs = MftPathResolver.Resolve([UsnJrnlPath, UsnJrnlName], entries);
            var vssRefs = new HashSet<ulong>(MftPathResolver.Resolve([TopsName, TxfLogBlfName], entries));
            vssRefs.UnionWith(MftPathResolver.Resolve([SviDirName], entries));
            vssRefs.UnionWith(DiffFileRefs(entries));
            vssRefs.UnionWith(TxfContainerRefs(entries));
            var txfOk = AddAdvisory(vol, txfRefs, runs, advisory, dropped, log);
            var jrnlOk = AddAdvisory(vol, jrnlRefs, runs, advisory, dropped, log);
            var vssOk = AddAdvisory(vol, vssRefs, runs, advisory, dropped, log);
            if (txfOk == null || jrnlOk == null || vssOk == null)
                return null;
            log.Info($"sys-extents: {sysOk} sys ok, advisory txf {txfOk}/{txfRefs.Count} journal {jrnlOk}/{jrnlRefs.Count} vss {vssOk}/{vssRefs.Count} (dropped: [{string.Join(",", dropped)}])");
            return (runs, advisory);
        }

        // Returns parsed count, null on unreadable (caller stays strict).
        // Parse-null (dead slot) drops the FRN but keeps its advisory
        // membership: a dead container holds no file, so nothing to mask.
        // VSS diff/container FRNs reuse stale dead slots across cycles
        // (B6K rec56): the membership is per-cycle geometry, never pinned.
        private static int? AddAdvisory(IMftVolume vol, HashSet<ulong> refs,
            List<(long StartLcn, long ClusterCount)> runs, HashSet<long> advisory, List<string> dropped, ILogger log)
        {
            var ok = 0;
            foreach (var frn in refs)
            {
                var rec = (long)(frn & 0xFFFF_FFFF_FFFF);
                advisory.Add(rec);
                List<(long StartLcn, long ClusterCount)>? extents;
                try
                {
                    extents = MftExtentReader.ParseRecordExtents(vol.GetRecordBytes(frn), frn,
                        vol.BytesPerCluster, vol.BytesPerRecord, vol.MftRuns);
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    log.Info($"sys-extents: unresolved (frn {rec}: {ex.GetType().Name})");
                    return null;
                }
                if (extents == null)
                {
                    dropped.Add($"{rec}:null");
                    continue;
                }
                ok++;
                runs.AddRange(extents);
            }
            return ok;
        }

        // Transient VSS diff files: braced-{guid} names, direct children of
        // an SVI dir. Scoped to SVI children on purpose — a same-shaped user
        // file elsewhere (GodMode dirs, braced names) must NOT be excluded.
        // Files only (diff files are never dirs). If the SVI dir row itself
        // is absent from the enumeration there is no anchor and this yields
        // nothing (strict) — the subtree resolve above is the primary path.
        private static HashSet<ulong> DiffFileRefs(IReadOnlyList<MftEnumEntry> entries)
        {
            var sviDirs = new HashSet<ulong>();
            foreach (var e in entries)
            {
                if (string.Equals(e.Name, SviDirName, StringComparison.OrdinalIgnoreCase))
                    sviDirs.Add(e.FileRef);
            }
            var refs = new HashSet<ulong>();
            if (sviDirs.Count == 0)
                return refs;
            foreach (var e in entries)
            {
                if (!sviDirs.Contains(e.ParentRef))
                    continue;
                if ((e.Attributes & MftPathResolver.FileAttributeDirectory) != 0)
                    continue;
                if (IsShadowDiffName(e.Name))
                    refs.Add(e.FileRef);
            }
            return refs;
        }

        // Orphan TxF containers: the $TxfLog dir row itself may be absent
        // from the enumeration (B6E shape), so the subtree never covers
        // them — match the container prefix instead. Files only; no user
        // file carries this prefix outside TxF.
        private static HashSet<ulong> TxfContainerRefs(IReadOnlyList<MftEnumEntry> entries)
        {
            var refs = new HashSet<ulong>();
            foreach (var e in entries)
            {
                if ((e.Attributes & MftPathResolver.FileAttributeDirectory) != 0)
                    continue;
                if (e.Name.StartsWith(TxfContainerPrefix, StringComparison.OrdinalIgnoreCase))
                    refs.Add(e.FileRef);
            }
            return refs;
        }
    }

    internal static class MftDigestPlanner
    {
        internal static (HashSet<long> Dirty, List<string> Groups, List<string> Records)? PlanDirty(
            IMftVolume vol, SnapshotManifest parent, int chunkBytes, ILogger log)
        {
            ArgumentNullException.ThrowIfNull(vol);
            ArgumentNullException.ThrowIfNull(parent);
            ArgumentNullException.ThrowIfNull(log);
            // The record-size probe is the first MFT touch: a dead snapshot
            // device fails here, before ComputeGroups' own guard — degrade
            // to full like an unreadable MFT, never throw (Store's baseline
            // path wraps ComputeGroups only, so this hole is planner-only).
            int recordBytes;
            try
            {
                recordBytes = vol.BytesPerRecord;
            }
            catch (InvalidDataException ex)
            {
                log.Info($"full image (MFT unreadable: {ex.Message})");
                return null;
            }
            if (recordBytes != parent.MftRecordBytes)
            {
                log.Info("full image (MFT record size changed, reformat?)");
                return null;
            }
            (List<string> Groups, List<string> Records) current;
            // §9 design B: the RECORD pass reads the LIVE volume (stable
            // user-record bytes), while groups + every extent below stay on
            // the frozen shadow (capture truth — bulk bytes come from there).
            // Residual TOCTOU: the live read happens ms after the snapshot
            // froze, so a write landing in that gap is live-visible but
            // shadow-absent — the plan then misses it and the gate may skip
            // this run. The write is picked up by the NEXT run (deferred,
            // never lost): its live bytes still differ from the stored
            // live-view digests, so the next record gate refuses and the gen
            // records them. Convergence: ≤1 transitional gen.
            IMftVolume? live = null;
            try
            {
                live = (vol as ILiveMftVolumeProvider)?.TryOpenLiveMft(log);
                try
                {
                    current = MftDigest.ComputeGroupsAndRecords(vol, live ?? vol, chunkBytes, log);
                }
                catch (InvalidDataException ex)
                {
                    log.Info($"full image (MFT unreadable: {ex.Message})");
                    return null;
                }
            }
            finally
            {
                if (live != null && !ReferenceEquals(live, vol))
                    live.Dispose();
            }
            var changed = MftDigest.DiffGroups(parent.MftChunkDigests ?? new List<string>(), current.Groups);
            if (changed == null)
            {
                log.Info("full image (MFT shrank, reformat?)");
                return null;
            }
            var dirty = new HashSet<long>();
            var per = Math.Max(1, chunkBytes / vol.BytesPerRecord);
            foreach (var g in changed)
            {
                var first = (long)g * per;
                var past = Math.Min(first + per, vol.RecordCount);
                for (var rec = first; rec < past; rec++)
                {
                    byte[] bytes;
                    try
                    {
                        bytes = vol.GetRecordBytes((ulong)rec);
                    }
                    catch (InvalidDataException ex)
                    {
                        log.Info($"full image (MFT record {rec} unreadable: {ex.Message})");
                        return null;
                    }
                    if (bytes.Length < 48)
                    {
                        log.Info($"full image (MFT record {rec} truncated)");
                        return null;
                    }
                    // Scan FRN carries the record's own seq (no enumeration
                    // here): live records verify, dead slots filter, exactly
                    // like the USN path's reuse checks.
                    var fileRef = ((ulong)BitConverter.ToUInt16(bytes, 0x10) << 48) | (ulong)rec;
                    List<(long StartLcn, long ClusterCount)>? runs;
                    try
                    {
                        runs = MftExtentReader.ParseRecordExtents(bytes, fileRef,
                            vol.BytesPerCluster, vol.BytesPerRecord, vol.MftRuns);
                    }
                    catch (InvalidDataException ex)
                    {
                        log.Info($"full image (MFT record {rec} unparseable: {ex.Message})");
                        return null;
                    }
                    if (runs == null)
                        continue; // dead slot → bitmap net covers the freed clusters
                    foreach (var c in CapturePolicy.DirtyChunksForRuns(runs, vol.BytesPerCluster, chunkBytes))
                        dirty.Add(c);
                }
            }
            // NTFS self-metadata ($MFT/$LogFile/$Bitmap/...): their CONTENT
            // mutates without changing their own record bytes, so group
            // digests can't see it — always re-capture FRNs 0..15 (same
            // policy as the USN path, same synthetic-seq bypass).
            for (var sys = 0UL; sys < 16 && (long)sys < vol.RecordCount; sys++)
            {
                byte[] bytes;
                try
                {
                    bytes = vol.GetRecordBytes(sys);
                }
                catch (InvalidDataException ex)
                {
                    log.Info($"full image (MFT record for system file {sys} unreadable: {ex.Message})");
                    return null;
                }
                List<(long StartLcn, long ClusterCount)>? sysRuns;
                try
                {
                    sysRuns = MftExtentReader.ParseRecordExtents(bytes, sys,
                        vol.BytesPerCluster, vol.BytesPerRecord, vol.MftRuns, skipSequenceCheck: true);
                }
                catch (InvalidDataException ex)
                {
                    log.Info($"full image (MFT record for system file {sys} unreadable: {ex.Message})");
                    return null;
                }
                if (sysRuns == null)
                    continue;
                foreach (var c in CapturePolicy.DirtyChunksForRuns(sysRuns, vol.BytesPerCluster, chunkBytes))
                    dirty.Add(c);
            }
            return (dirty, current.Groups, current.Records);
        }
    }

    // FSCTL-mediated MFT-digest-compare incrementals. Digest = truth (covers
    // journal wrap, untrusted cursors, and relocation without USN entries);
    // USN stays as the hint for pre-digest parents and non-MFT sources; the
    // bitmap hole-flip net unions in freed-cluster reuse. Null = full.
    public sealed class MftDigestChangeSource : IChangeSource
    {
        public IncrementalPlan? Plan(SnapshotManifest parent, IBlockSource source, int chunkBytes, ILogger log)
        {
            ArgumentNullException.ThrowIfNull(parent);
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(log);
            IMftVolume? vol = source as IMftVolume;
            var owned = false;
            if (vol == null)
            {
                vol = TryOpenVolume(source, log);
                owned = vol != null;
            }
            if (vol == null)
                return new UsnChangeSource().Plan(parent, source, chunkBytes, log);
            try
            {
                if (parent.MftChunkDigests == null || parent.MftChunkDigests.Count == 0)
                    return new UsnChangeSource().Plan(parent, source, chunkBytes, log);
                var plan = MftDigestPlanner.PlanDirty(vol, parent, chunkBytes, log);
                if (plan == null)
                    return null;
                var flips = HoleFlipNet(parent, source, vol.BytesPerCluster, chunkBytes);
                if (flips == null)
                    return null;
                foreach (var c in flips)
                    plan.Value.Dirty.Add(c);
                var cursor = UsnChangeSource.TryCurrentCursor(source, log) ?? parent.ChangeCursor ?? "";
                return new IncrementalPlan(plan.Value.Dirty, cursor, plan.Value.Groups, vol.BytesPerRecord, plan.Value.Records);
            }
            finally
            {
                if (owned)
                    vol.Dispose();
            }
        }

        private static IMftVolume? TryOpenVolume(IBlockSource source, ILogger log)
        {
            if (!OperatingSystem.IsWindows() || source is FileBlockSource || source is not IVolumeHandleProvider vol)
                return null; // USN delegate below logs the reason, same as today
            try
            {
                return new MftVolumeReader(vol.SnapshotDevicePath);
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException)
            {
                log.Info("MFT digest unavailable, trying USN (" + ex.Message + ")");
                return null;
            }
        }

        private static HashSet<long>? HoleFlipNet(SnapshotManifest parent, IBlockSource source, long bytesPerCluster, int chunkBytes)
        {
            IReadOnlyList<(long Start, long Count)> runs;
            if (source is ISparseBlockSource sparse)
                runs = sparse.GetAllocatedRuns();
            else
                runs = new (long Start, long Count)[] { (0, (source.Length + bytesPerCluster - 1) / bytesPerCluster) };
            return VolumeBitmap.HoleFlipChunks(parent.Chunks, runs, bytesPerCluster, source.Length, chunkBytes);
        }
    }
}
