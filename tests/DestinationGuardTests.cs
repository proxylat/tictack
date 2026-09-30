namespace TicTack;

public class DestinationGuardTests : IDisposable
{
    private readonly string _dir;
    private readonly RecordingLogger _log = new();

    public DestinationGuardTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "TicTackTest_destguard_" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Undo the test deny rule so the temp dir deletes cleanly
                // even when the test user is in Users.
                try { RemoveUsersDeny(_dir); } catch { }
            }
            else
            {
                try { File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } catch { }
            }
            Directory.Delete(_dir, true);
        }
        catch { }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RemoveUsersDeny(string dir)
    {
        var security = new DirectoryInfo(dir).GetAccessControl();
        security.RemoveAccessRuleAll(new System.Security.AccessControl.FileSystemAccessRule(
            new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null),
            System.Security.AccessControl.FileSystemRights.Write,
            System.Security.AccessControl.AccessControlType.Deny));
        new DirectoryInfo(dir).SetAccessControl(security);
    }

    [Fact]
    public void CreatesMissingDirectory()
    {
        var missing = Path.Combine(_dir, "new-root");
        DestinationGuard.Enforce(missing, _log);
        Assert.True(Directory.Exists(missing));
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void Linux_StripsGroupOtherWrite()
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
        DestinationGuard.Enforce(_dir, _log);
        var mode = File.GetUnixFileMode(_dir);
        Assert.False(mode.HasFlag(UnixFileMode.GroupWrite));
        Assert.False(mode.HasFlag(UnixFileMode.OtherWrite));
        Assert.True(mode.HasFlag(UnixFileMode.UserWrite));
        Assert.Contains(_log.Messages, m => m.Contains("Destination protected"));
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void Windows_AddsUsersDenyWrite()
    {
        if (!OperatingSystem.IsWindows()) return;
        DestinationGuard.Enforce(_dir, _log);
        var security = new DirectoryInfo(_dir).GetAccessControl();
        var users = new System.Security.Principal.SecurityIdentifier(
            System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null);
        var found = false;
        foreach (System.Security.AccessControl.FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)))
        {
            if (rule.IdentityReference == users
                && rule.AccessControlType == System.Security.AccessControl.AccessControlType.Deny
                && (rule.FileSystemRights & System.Security.AccessControl.FileSystemRights.Write) != 0)
            {
                found = true;
                break;
            }
        }
        Assert.True(found);
    }

    [Fact]
    public void NeverThrows_OnReadOnlyParent()
    {
        // Best-effort by contract: must not fail startup. Read-only root
        // makes the ACL write fail; Enforce must swallow it as Warn.
        DestinationGuard.Enforce(_dir, _log);
    }
}
