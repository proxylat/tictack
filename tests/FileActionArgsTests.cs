namespace TicTack;

public class FileActionArgsTests
{
    [Fact]
    public void MapsPath_Correctly()
    {
        var e = new FileChangedEventArgs(ChangeType.Created, "/home/src/docs/file.txt", null);
        var args = new FileActionArgs(e, "/home/src", "/mnt/dst");

        Assert.Equal("/home/src/docs/file.txt", args.ChangeEvent.FullPath);
        Assert.Equal("/mnt/dst/docs/file.txt", args.DestPath);
        Assert.Null(args.OldDestPath);
    }

    [Fact]
    public void MapsRootFile_Correctly()
    {
        var e = new FileChangedEventArgs(ChangeType.Created, "/home/src/file.txt", null);
        var args = new FileActionArgs(e, "/home/src", "/mnt/dst");

        Assert.Equal("/mnt/dst/file.txt", args.DestPath);
    }

    [Fact]
    public void MapsRenamedPath()
    {
        var e = new FileChangedEventArgs(ChangeType.Renamed, "/home/src/new.txt", "/home/src/old.txt");
        var args = new FileActionArgs(e, "/home/src", "/mnt/dst");

        Assert.Equal("/mnt/dst/new.txt", args.DestPath);
        Assert.Equal("/mnt/dst/old.txt", args.OldDestPath);
    }

    [Fact]
    public void MapsRenamed_NoOldPath()
    {
        var e = new FileChangedEventArgs(ChangeType.Renamed, "/home/src/new.txt", null);
        var args = new FileActionArgs(e, "/home/src", "/mnt/dst");

        Assert.Equal("/mnt/dst/new.txt", args.DestPath);
        Assert.Null(args.OldDestPath);
    }

    [Fact]
    public void HandlesPathWithoutBasePrefix()
    {
        var e = new FileChangedEventArgs(ChangeType.Created, "/other/path/file.txt", null);
        var args = new FileActionArgs(e, "/home/src", "/mnt/dst");

        Assert.Equal("/other/path/file.txt", args.DestPath);
    }

    [Fact]
    public void HandlesRootDest()
    {
        var e = new FileChangedEventArgs(ChangeType.Created, "/home/src/file.txt", null);
        var args = new FileActionArgs(e, "/home/src", "/mnt/");

        Assert.Equal("/mnt/file.txt", args.DestPath);
    }

    [Fact]
    public void PreservesSourceBase()
    {
        var e = new FileChangedEventArgs(ChangeType.Created, "/home/src/file.txt", null);
        var args = new FileActionArgs(e, "/home/src", "/mnt/dst");

        Assert.Equal("/home/src", args.SourceBase);
        Assert.Equal("/mnt/dst", args.DestBase);
    }

    [Fact]
    public void MapsSubdirectoryStructure()
    {
        var e = new FileChangedEventArgs(ChangeType.Created,
            "/home/user/Desktop/sub/deep/nested.txt", null);
        var args = new FileActionArgs(e, "/home/user/Desktop", "/mnt/Sync/Desktop");

        Assert.Equal("/mnt/Sync/Desktop/sub/deep/nested.txt", args.DestPath);
    }
}
