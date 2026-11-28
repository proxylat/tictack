using System;
using System.Collections.Generic;
using System.IO;

namespace Corpus
{
    // Parses NTFS $ATTRIBUTE_LIST (0x20), resident or non-resident, and collects
    // non-resident data runs from extension MFT records ordered by LowestVcn.
    internal static class MftAttributeList
    {
        internal static List<(long StartLcn, long ClusterCount)> CollectExtents(
            byte[] baseRecord,
            ulong baseFileRef,
            int bytesPerCluster,
            Func<ulong, byte[]> readRecord,
            Func<long, byte[]>? readCluster,
            params uint[] wantTypes)
        {
            var baseNum = baseFileRef & 0xFFFF_FFFF_FFFF;
            var entries = ReadEntries(baseRecord, bytesPerCluster, readCluster);
            var extRefs = new HashSet<ulong>();
            foreach (var (type, fileRef) in entries)
            {
                if (Array.IndexOf(wantTypes, type) < 0)
                    continue;
                var recNum = fileRef & 0xFFFF_FFFF_FFFF;
                if (recNum != baseNum)
                    extRefs.Add(fileRef);
            }
            var runs = new List<(long LowestVcn, List<(long StartLcn, long ClusterCount)> Runs)>();
            foreach (var extRef in extRefs)
            {
                var extRec = readRecord(extRef);
                if (extRec.Length < 48 || extRec[0] != (byte)'F' || extRec[1] != (byte)'I' || extRec[2] != (byte)'L' || extRec[3] != (byte)'E')
                    throw new InvalidDataException($"extension MFT record {extRef:X16} corrupt");
                foreach (var (off, len) in MftExtentReader.AttributeSpans(extRec))
                {
                    var attrType = BitConverter.ToUInt32(extRec, off);
                    if (Array.IndexOf(wantTypes, attrType) < 0)
                        continue;
                    if (len < 34 || extRec[off + 8] == 0)
                        continue;
                    var lowestVcn = BitConverter.ToInt64(extRec, off + 16);
                    var runOff = BitConverter.ToUInt16(extRec, off + 32);
                    if (runOff >= len)
                        throw new InvalidDataException("extension MFT run offset outside attribute");
                    runs.Add((lowestVcn, MftExtentReader.DecodeDataRuns(extRec.AsSpan(off + runOff, (int)len - runOff))));
                }
            }
            runs.Sort((a, b) => a.LowestVcn.CompareTo(b.LowestVcn));
            var result = new List<(long StartLcn, long ClusterCount)>();
            foreach (var (_, r) in runs)
                result.AddRange(r);
            return result;
        }

        internal static List<(uint Type, ulong FileRef)> ReadEntries(
            byte[] record, int bytesPerCluster, Func<long, byte[]>? readCluster)
        {
            var result = new List<(uint Type, ulong FileRef)>();
            foreach (var (off, len) in MftExtentReader.AttributeSpans(record))
            {
                if (BitConverter.ToUInt32(record, off) != 0x20)
                    continue;
                if (len < 24)
                    throw new InvalidDataException("$ATTRIBUTE_LIST truncated");
                byte[] listBytes;
                if (record[off + 8] == 0)
                {
                    var valLen = (int)BitConverter.ToUInt32(record, off + 16);
                    var valOff = BitConverter.ToUInt16(record, off + 20);
                    if (valOff + valLen > len)
                        throw new InvalidDataException("resident $ATTRIBUTE_LIST out of bounds");
                    listBytes = record.AsSpan(off + valOff, valLen).ToArray();
                }
                else
                {
                    if (readCluster == null)
                        throw new InvalidDataException("non-resident $ATTRIBUTE_LIST requires cluster reader");
                    if (len < 64)
                        throw new InvalidDataException("non-resident $ATTRIBUTE_LIST header truncated");
                    var dataSize = BitConverter.ToInt64(record, off + 48);
                    if (dataSize <= 0 || dataSize > 16L * 1024 * 1024)
                        throw new InvalidDataException($"non-resident $ATTRIBUTE_LIST implausible size {dataSize}");
                    var runOff = BitConverter.ToUInt16(record, off + 32);
                    if (runOff >= len)
                        throw new InvalidDataException("non-resident $ATTRIBUTE_LIST run offset out of bounds");
                    var runs = MftExtentReader.DecodeDataRuns(record.AsSpan(off + runOff, (int)len - runOff));
                    listBytes = ReadRuns(runs, (int)dataSize, bytesPerCluster, readCluster);
                }
                ParseEntryBytes(listBytes, result);
            }
            return result;
        }

        private static byte[] ReadRuns(
            List<(long StartLcn, long ClusterCount)> runs, int size, int bytesPerCluster, Func<long, byte[]> readCluster)
        {
            var buf = new byte[size];
            var pos = 0;
            foreach (var (startLcn, count) in runs)
            {
                if (startLcn < 0)
                    throw new InvalidDataException("sparse run inside $ATTRIBUTE_LIST");
                for (var c = 0L; c < count && pos < size; c++)
                {
                    var cluster = readCluster(startLcn + c);
                    var take = Math.Min(bytesPerCluster, size - pos);
                    Buffer.BlockCopy(cluster, 0, buf, pos, take);
                    pos += take;
                }
            }
            if (pos < size)
                throw new InvalidDataException("$ATTRIBUTE_LIST runs shorter than data size");
            return buf;
        }

        private static void ParseEntryBytes(byte[] data, List<(uint Type, ulong FileRef)> result)
        {
            var pos = 0;
            while (pos + 26 <= data.Length)
            {
                var type = BitConverter.ToUInt32(data, pos);
                var recLen = BitConverter.ToUInt16(data, pos + 4);
                if (recLen < 26 || pos + recLen > data.Length)
                    throw new InvalidDataException($"$ATTRIBUTE_LIST entry corrupt length {recLen}");
                var fileRef = BitConverter.ToUInt64(data, pos + 16);
                result.Add((type, fileRef));
                pos += recLen;
            }
        }
    }
}
