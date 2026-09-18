using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Threading;

namespace TicTack
{
    // In-process performance counters, consumed out-of-proc with zero new tools:
    //   dotnet-counters monitor -p <pid> --counters TicTack
    //   dotnet-counters collect -p <pid> --counters TicTack --format json -o counters.json
    // (add the TicTack provider to the diag.sh tier-1 counters line for a
    // permanent record). Always-on updates are a few Interlocked ops per file;
    // Stopwatch timing only runs while a listener is attached (IsEnabled), so
    // unobserved sync stays at zero overhead.
    [EventSource(Name = "TicTack")]
    public sealed class TicTackEventSource : EventSource
    {
        public static readonly TicTackEventSource Log = new TicTackEventSource();

        private long _filesCopied;
        private long _bytesCopied;
        private readonly IncrementingPollingCounter _filesCopiedCounter;
        private readonly PollingCounter _bytesCopiedCounter;
        private readonly EventCounter _copyTime;
        private readonly EventCounter _fsyncTime;
        private readonly List<PollingCounter> _queueCounters = new List<PollingCounter>();
        private readonly object _lock = new object();

        private TicTackEventSource()
        {
            // All counters are field-rooted: an EventSource does not keep its
            // counters alive, and a collected counter silently stops reporting.
            _filesCopiedCounter = new IncrementingPollingCounter("files-copied", this, () => Interlocked.Read(ref _filesCopied))
            {
                DisplayName = "Files copied",
                DisplayRateTimeScale = TimeSpan.FromSeconds(1)
            };
            _bytesCopiedCounter = new PollingCounter("bytes-copied", this, () => Interlocked.Read(ref _bytesCopied))
            {
                DisplayName = "Bytes copied (total)"
            };
            _copyTime = new EventCounter("copy-time-ms", this)
            {
                DisplayName = "Copy time",
                DisplayUnits = "ms"
            };
            _fsyncTime = new EventCounter("fsync-time-ms", this)
            {
                DisplayName = "Fsync time",
                DisplayUnits = "ms"
            };
        }

        // NOTE: every public helper below is [NonEvent]. Without it, EventSource
        // inspects public methods during initialization, and a method like
        // RegisterQueueCounter(Func<double>) silently disables ALL counter
        // emission from the source — with ConstructionException left null,
        // so there is no error to find. Verified by probe test, do not remove.
        [NonEvent]
        public void FileCopied(long bytes)
        {
            Interlocked.Increment(ref _filesCopied);
            Interlocked.Add(ref _bytesCopied, bytes);
        }

        [NonEvent]
        public void CopyCompleted(double milliseconds) => _copyTime.WriteMetric((float)milliseconds);

        [NonEvent]
        public void FsyncCompleted(double milliseconds) => _fsyncTime.WriteMetric((float)milliseconds);

        // One pending-events gauge per pipeline (usually one source = one row).
        // The returned token MUST be disposed by the owner: until then the
        // counter is rooted by this static source, and the counter roots the
        // pipeline its callback captures.
        [NonEvent]
        public IDisposable RegisterQueueCounter(Func<double> readPending)
        {
            var counter = new PollingCounter("pending-events", this, readPending)
            {
                DisplayName = "Pending events"
            };
            lock (_lock)
                _queueCounters.Add(counter);
            return new QueueCounter(this, counter);
        }

        private sealed class QueueCounter : IDisposable
        {
            private readonly TicTackEventSource _source;
            private PollingCounter? _counter;

            public QueueCounter(TicTackEventSource source, PollingCounter counter)
            {
                _source = source;
                _counter = counter;
            }

            public void Dispose()
            {
                var counter = Interlocked.Exchange(ref _counter, null);
                if (counter == null) return;
                lock (_source._lock)
                    _source._queueCounters.Remove(counter);
                counter.Dispose();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _filesCopiedCounter.Dispose();
                _bytesCopiedCounter.Dispose();
                _copyTime.Dispose();
                _fsyncTime.Dispose();
                lock (_lock)
                {
                    foreach (var c in _queueCounters) c.Dispose();
                    _queueCounters.Clear();
                }
            }
            base.Dispose(disposing);
        }
    }
}
