using System;
using System.Threading;
using TicTack;

namespace Corpus
{
    // Blocking service lifetime around GcScheduler. The CLI is one-shot,
    // so a long-running verb needs something that starts the scheduler's
    // timer and waits for shutdown (Ctrl+C). All due-check, timer, and GC
    // logic stays in GcScheduler — this only owns Start/Run/Stop/Dispose.
    public sealed class GcHost : IDisposable
    {
        private readonly GcScheduler _scheduler;
        private bool _disposed;

        public GcHost(string storeRoot, string dbPath, CorpusRetentionConfig retention, ILogger log, int chunkMib, int zstdLevel, GcSchedule? schedule = null, string? encryptHook = null)
        {
            _scheduler = new GcScheduler(storeRoot, dbPath, retention, log, chunkMib, zstdLevel, schedule, encryptHook);
        }

        public void Start()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _scheduler.Start();
        }

        // Non-throwing after Dispose: finally-block safe (mirrors Stop).
        public void Stop() => _scheduler.Stop();

        // Blocks until token fires, then stops the scheduler. Pass a token
        // wired to Ctrl+C (see --service wiring in Program.cs).
        public void Run(CancellationToken token)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Start();
            try
            {
                token.WaitHandle.WaitOne();
            }
            finally
            {
                Stop();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _scheduler.Dispose();
        }
    }
}
