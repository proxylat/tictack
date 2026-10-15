using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using TicTack;

namespace Corpus
{
    // Item 8: offline-drive ransomware story — registered rotation (3+) with
    // overdue nags and ≥90d depth. State is a new <store>/rotation.json
    // sidecar (manifest and config schemas untouched). Nags are log Warn
    // only: no alert_path mechanism exists in Corpus (CorpusLoggingConfig has
    // Level/Path/Console only), so alert wiring is a follow-up.
    public sealed class RotationDrive
    {
        public string Id { get; set; } = "";
        public string Label { get; set; } = "";
        public string LastSeenUtc { get; set; } = "";
        public string LastBackupUtc { get; set; } = "";
    }

    public sealed class RotationState
    {
        public int SchemaVersion { get; set; } = 1;
        public List<RotationDrive> Drives { get; set; } = new();
    }

    public sealed class DriveRotationStore
    {
        private readonly string _path;

        public DriveRotationStore(string storeRoot)
        {
            ArgumentNullException.ThrowIfNull(storeRoot);
            _path = Path.Combine(storeRoot, "rotation.json");
        }

        public RotationState Load()
        {
            if (!File.Exists(_path))
                return new RotationState();
            RotationState? state;
            try
            {
                state = JsonSerializer.Deserialize<RotationState>(File.ReadAllText(_path));
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"rotation state {_path} is not valid JSON.", ex);
            }
            if (state is null)
                throw new InvalidDataException($"rotation state {_path} is empty.");
            if (state.SchemaVersion > 1)
                throw new InvalidDataException($"rotation state {_path} has unsupported SchemaVersion {state.SchemaVersion} (this build reads v1).");
            return state;
        }

        public void Save(RotationState state)
        {
            ArgumentNullException.ThrowIfNull(state);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state));
            File.Move(tmp, _path, overwrite: true);
        }

        // Re-register updates the label only; dates are never reset by it.
        public void Register(string id, string label)
        {
            ArgumentNullException.ThrowIfNull(id);
            ArgumentNullException.ThrowIfNull(label);
            var state = Load();
            var found = false;
            foreach (var d in state.Drives)
            {
                if (string.Equals(d.Id, id, StringComparison.Ordinal))
                {
                    d.Label = label;
                    found = true;
                }
            }
            if (!found)
                state.Drives.Add(new RotationDrive { Id = id, Label = label });
            Save(state);
        }

        public void MarkSeen(string id, bool backedUp, DateTimeOffset now)
        {
            ArgumentNullException.ThrowIfNull(id);
            var state = Load();
            RotationDrive? drive = null;
            foreach (var d in state.Drives)
            {
                if (string.Equals(d.Id, id, StringComparison.Ordinal))
                    drive = d;
            }
            if (drive == null)
                throw new InvalidDataException($"drive {id} is not registered (register it before marking)");
            drive.LastSeenUtc = now.ToString("o", CultureInfo.InvariantCulture);
            if (backedUp)
                drive.LastBackupUtc = drive.LastSeenUtc;
            Save(state);
        }
    }

    public static class DriveRotationCheck
    {
        public const int MinDrives = 3;
        public const int DepthDays = 90;

        // 0 = healthy rotation; 1 = nagged (Warn lines, never Error — nothing
        // failed, an offline copy just needs attention). Fresh = a backup
        // landed within the overdue window; deep = a ≥90d-old backup exists
        // (predates ransomware dwell — assumes per-drive stores never delete
        // another drive's backup, which holds: each drive keeps its own copy).
        public static int Check(string storeRoot, ILogger log, DateTimeOffset? nowUtc = null, int overdueDays = 7)
        {
            ArgumentNullException.ThrowIfNull(storeRoot);
            ArgumentNullException.ThrowIfNull(log);
            var now = nowUtc ?? DateTimeOffset.UtcNow;
            var state = new DriveRotationStore(storeRoot).Load();
            var nagged = false;
            if (state.Drives.Count == 0)
            {
                log.Warn($"rotation: no drives registered (want {MinDrives}+ offline drives for ransomware survival)");
                return 1;
            }
            if (state.Drives.Count < MinDrives)
            {
                log.Warn($"rotation: {state.Drives.Count} drives registered, want {MinDrives}+ (one attached drive is not an offline story)");
                nagged = true;
            }
            var fresh = false;
            var deep = false;
            foreach (var d in state.Drives)
            {
                if (TryParse(d.LastSeenUtc, out var seen) && (now - seen).TotalDays > overdueDays)
                {
                    log.Warn($"rotation: drive {d.Label} ({d.Id}) unseen for {(now - seen).TotalDays:F0}d (rotate it in)");
                    nagged = true;
                }
                else if (!TryParse(d.LastSeenUtc, out _))
                {
                    log.Warn($"rotation: drive {d.Label} ({d.Id}) never seen (attach it for its first backup)");
                    nagged = true;
                }
                if (TryParse(d.LastBackupUtc, out var backed))
                {
                    if ((now - backed).TotalDays <= overdueDays)
                        fresh = true;
                    if ((now - backed).TotalDays >= DepthDays)
                        deep = true;
                }
            }
            if (!fresh)
            {
                log.Warn("rotation: no backup within the window (attach the next drive and back up)");
                nagged = true;
            }
            if (!deep)
            {
                log.Warn($"rotation: no {DepthDays}d-deep offline copy yet (rotation still maturing — dwell math needs one old offline drive)");
                nagged = true;
            }
            if (!nagged)
                log.Info($"rotation: healthy ({state.Drives.Count} drives, fresh + {DepthDays}d-deep copies present)");
            return nagged ? 1 : 0;
        }

        private static bool TryParse(string text, out DateTimeOffset value) =>
            DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value);
    }
}
