namespace TicTack;

public class DriveDiscovererTests
{
    [Fact]
    public void GetEligibleDrives_RequiresMarkerAndHonorsExclusions()
    {
        var root = Path.Combine(Path.GetTempPath(), "tictack-drive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, ".marker"), "ok");
            var cfg = new ExternalDrivesConfig { MarkerFileName = ".marker", RequireMarkerFile = true };
            var log = new RecordingLogger();
            var drives = new[]
            {
                new DriveDiscoverer.DriveCandidate(root, DriveType.Removable, true),
                new DriveDiscoverer.DriveCandidate(Path.Combine(root, "excluded"), DriveType.Removable, true),
                new DriveDiscoverer.DriveCandidate(Path.Combine(root, "unmarked"), DriveType.Removable, true),
                new DriveDiscoverer.DriveCandidate(Path.Combine(root, "unready"), DriveType.Removable, false)
            };

            var results = DriveDiscoverer.GetEligibleDrives(cfg,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.Combine(root, "excluded") }, log, drives);

             Assert.Equal(new[] { root + Path.DirectorySeparatorChar }, results);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
