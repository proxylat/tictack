namespace TicTack;

public class FileActionArgsTests
{
    [Fact]
    public void MapsPath_Correctly()
    {
        var e = new FileChangedEventArgs(ChangeType.Created, @"C:\src\docs\file.txt", null);
        var args = new FileActionArgs(e, @"C:\src", @"D:\dst");

        Assert.Equal(@"C:\src\docs\file.txt", args.ChangeEvent.FullPath);
        Assert.Equal(Path.Combine(@"D:\dst", @"docs\file.txt"), args.DestPath);
        Assert.Null(args.OldDestPath);
    }

    [Fact]
    public void MapsRootFile_Correctly()
    {
        var e = new FileChangedEventArgs(ChangeType.Created, @"C:\src\file.txt", null);
        var args = new FileActionArgs(e, @"C:\src", @"D:\dst");

        Assert.Equal(Path.Combine(@"D:\dst", "file.txt"), args.DestPath);
    }

    [Fact]
    public void MapsRenamedPath()
    {
        var e = new FileChangedEventArgs(ChangeType.Renamed, @"C:\src\new.txt", @"C:\src\old.txt");
        var args = new FileActionArgs(e, @"C:\src", @"D:\dst");

        Assert.Equal(Path.Combine(@"D:\dst", "new.txt"), args.DestPath);
        Assert.Equal(Path.Combine(@"D:\dst", "old.txt"), args.OldDestPath);
    }

    [Fact]
    public void MapsRenamed_NoOldPath()
    {
        var e = new FileChangedEventArgs(ChangeType.Renamed, @"C:\src\new.txt", null);
        var args = new FileActionArgs(e, @"C:\src", @"D:\dst");

        Assert.Equal(Path.Combine(@"D:\dst", "new.txt"), args.DestPath);
        Assert.Null(args.OldDestPath);
    }

    [Fact]
    public void HandlesPathWithoutBasePrefix()
    {
        var e = new FileChangedEventArgs(ChangeType.Created, @"C:\other\file.txt", null);
        var args = new FileActionArgs(e, @"C:\src", @"D:\dst");

        Assert.Equal(@"C:\other\file.txt", args.DestPath);
    }

    [Fact]
    public void SiblingPathSharingPrefix_IsNotMapped()
    {
        // /a/src must not swallow /a/src2 — the destination would silently
        // become the source path and a later move could relocate the source.
        var e = new FileChangedEventArgs(ChangeType.Created, @"C:\src2\file.txt", null);
        var args = new FileActionArgs(e, @"C:\src", @"D:\dst");

        Assert.Equal(@"C:\src2\file.txt", args.DestPath);
    }

    [Fact]
    public void HandlesRootDest()
    {
        var e = new FileChangedEventArgs(ChangeType.Created, @"C:\src\file.txt", null);
        var args = new FileActionArgs(e, @"C:\src", @"D:\");

        Assert.Equal(Path.Combine(@"D:\", "file.txt"), args.DestPath);
    }

    [Fact]
    public void PreservesSourceBase()
    {
        var e = new FileChangedEventArgs(ChangeType.Created, @"C:\src\file.txt", null);
        var args = new FileActionArgs(e, @"C:\src", @"D:\dst");

        Assert.Equal(@"C:\src", args.SourceBase);
        Assert.Equal(@"D:\dst", args.DestBase);
    }

    [Fact]
    public void MapsSubdirectoryStructure()
    {
        var e = new FileChangedEventArgs(ChangeType.Created,
            @"C:\Sync\Desktop\sub\deep\nested.txt", null);
        var args = new FileActionArgs(e, @"C:\Sync\Desktop", @"E:\Sync\Desktop");

        Assert.Equal(Path.Combine(@"E:\Sync\Desktop", @"sub\deep\nested.txt"), args.DestPath);
    }
}
