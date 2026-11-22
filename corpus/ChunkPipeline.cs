using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;

namespace Corpus
{
    internal readonly record struct HashedChunk(Chunk Chunk, byte[] Digest, string Hex);

    // Batches non-hole chunks and runs exclusion-zeroing + keyed SHA-256 in
    // parallel across cores while preserving strict chunk order for the
    // downstream PackWriter / landing / ManifestEntry writer.
    internal static class ChunkPipeline
    {
        public static int ResolveWorkers(int configured) =>
            configured > 0 ? Math.Clamp(configured, 1, 16) : Math.Clamp(Environment.ProcessorCount, 2, 16);

        public static IEnumerable<HashedChunk> HashBatched(
            IEnumerable<Chunk> source,
            byte[] key,
            IReadOnlyList<(long StartCluster, long ClusterCount)>? exclRuns,
            long exclBpc,
            int configuredWorkers)
        {
            int workers = ResolveWorkers(configuredWorkers);
            if (workers <= 1)
            {
                foreach (var chunk in source)
                    yield return HashOne(chunk, key, exclRuns, exclBpc);
                yield break;
            }

            const long maxBatchBytes = 64L * 1024 * 1024;
            var batch = new List<Chunk>(workers);
            long batchBytes = 0;
            Exception? readError = null;

            using var enumerator = source.GetEnumerator();
            while (true)
            {
                bool hasNext;
                try { hasNext = enumerator.MoveNext(); }
                catch (Exception ex) { readError = ex; break; }
                if (!hasNext) break;

                var chunk = enumerator.Current;
                if (chunk.IsHole)
                {
                    foreach (var h in FlushBatch(batch, key, exclRuns, exclBpc, workers))
                        yield return h;
                    batch.Clear();
                    batchBytes = 0;
                    yield return new HashedChunk(chunk, Array.Empty<byte>(), string.Empty);
                    continue;
                }

                batch.Add(chunk);
                batchBytes += chunk.Data.Length;
                if (batch.Count >= workers || batchBytes >= maxBatchBytes)
                {
                    foreach (var h in FlushBatch(batch, key, exclRuns, exclBpc, workers))
                        yield return h;
                    batch.Clear();
                    batchBytes = 0;
                }
            }

            if (batch.Count > 0)
            {
                foreach (var h in FlushBatch(batch, key, exclRuns, exclBpc, workers))
                    yield return h;
            }
            if (readError != null)
                ExceptionDispatchInfo.Capture(readError).Throw();
        }

        private static HashedChunk[] FlushBatch(
            List<Chunk> batch,
            byte[] key,
            IReadOnlyList<(long StartCluster, long ClusterCount)>? exclRuns,
            long exclBpc,
            int workers)
        {
            var results = new HashedChunk[batch.Count];
            if (batch.Count == 1)
            {
                results[0] = HashOne(batch[0], key, exclRuns, exclBpc);
                return results;
            }

            Parallel.For(0, batch.Count, new ParallelOptions { MaxDegreeOfParallelism = workers }, i =>
            {
                results[i] = HashOne(batch[i], key, exclRuns, exclBpc);
            });
            return results;
        }

        private static HashedChunk HashOne(
            Chunk chunk,
            byte[] key,
            IReadOnlyList<(long StartCluster, long ClusterCount)>? exclRuns,
            long exclBpc)
        {
            if (chunk.IsHole)
                return new HashedChunk(chunk, Array.Empty<byte>(), string.Empty);
            if (exclRuns != null && exclRuns.Count > 0)
                CapturePolicy.ZeroExcludedRanges(chunk.Data, chunk.Offset, exclRuns, exclBpc);
            var digest = ChunkId.ComputeId(chunk.Data, key);
            return new HashedChunk(chunk, digest, ChunkId.ToHex(digest));
        }
    }
}
