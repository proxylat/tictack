using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace TicTack
{
    // Expiry-heap accelerator for drain_strategy=ready_queue: a take is
    // O(log n) instead of an O(pending) scan. Best-effort hints only —
    // _pendingEvents.TryRemove stays the exactly-once claim, so duplicates
    // and stale entries are skipped, never double-processed. Pure logic over
    // caller-owned dictionaries: no pipeline references, directly testable.
    public sealed class ReadyDrain
    {
        private readonly PriorityQueue<string, DateTime> _readyQueue = new PriorityQueue<string, DateTime>();
        private readonly object _readyLock = new object();
        private readonly bool _enabled;

        public ReadyDrain(bool enabled)
        {
            _enabled = enabled;
        }

        public void Signal(string path, DateTime until)
        {
            if (!_enabled) return;
            lock (_readyLock)
                _readyQueue.Enqueue(path, until);
        }

        public bool TryTake(
            DateTime now,
            ConcurrentDictionary<string, FileChangedEventArgs> pendingEvents,
            ConcurrentDictionary<string, DateTime> debounce,
            out FileChangedEventArgs result,
            out TimeSpan? wait)
        {
            result = null!;
            wait = null;
            DateTime? next = null;
            if (_enabled && TryTakeQueued(now, pendingEvents, debounce, out result, out wait))
                return true;
            foreach (var item in pendingEvents)
            {
                if (!debounce.TryGetValue(item.Key, out var until) || until <= now)
                {
                    if (pendingEvents.TryRemove(item.Key, out var candidate))
                    {
                        result = candidate;
                        debounce.TryRemove(item.Key, out _);
                        return true;
                    }
                }
                else if (!next.HasValue || until < next.Value)
                {
                    next = until;
                }
            }
            if (next.HasValue)
                wait = next.Value - now;
            return false;
        }

        // Heap-first take for drain_strategy=ready_queue. Pops the earliest
        // expiry; stale entries (already claimed, or re-signalled with a newer
        // debounce) are skipped or re-queued, never processed twice. Returns
        // false when the heap is empty or nothing in it is expired yet — the
        // caller falls back to the scan, which also covers entries
        // SweepDebounced re-drives straight into _pendingEvents.
        private bool TryTakeQueued(
            DateTime now,
            ConcurrentDictionary<string, FileChangedEventArgs> pendingEvents,
            ConcurrentDictionary<string, DateTime> debounce,
            out FileChangedEventArgs result,
            out TimeSpan? wait)
        {
            result = null!;
            wait = null;
            while (true)
            {
                string key = string.Empty;
                lock (_readyLock)
                {
                    if (_readyQueue.Count == 0)
                        return false;
                    _readyQueue.TryPeek(out key!, out var until);
                    if (until > now)
                    {
                        wait = until - now;
                        return false;
                    }
                    _readyQueue.Dequeue();
                }
                if (!pendingEvents.TryRemove(key, out var candidate))
                    continue;
                if (debounce.TryGetValue(key, out var current) && current > now)
                {
                    pendingEvents.TryAdd(key, candidate);
                    lock (_readyLock)
                        _readyQueue.Enqueue(key, current);
                    wait = current - now;
                    return false;
                }
                debounce.TryRemove(key, out _);
                result = candidate;
                return true;
            }
        }
    }
}
