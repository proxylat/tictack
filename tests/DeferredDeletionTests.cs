namespace TicTack;

public class DeferredDeletionTests
{
    static string TestDir() => Path.Combine(Path.GetTempPath(), "TicTackTest_deferred_" + Guid.NewGuid());

    [Fact]
    public void RecordPending_MergesWithExistingBatch()
    {
        var dir = TestDir();
        Directory.CreateDirectory(dir);
        var a = Path.Combine(dir, "a.txt");
        var b = Path.Combine(dir, "b.txt");
        var c = Path.Combine(dir, "c.txt");
        try
        {
            var dd = new DeferredDeletion(Path.Combine(dir, "deferred.json"), 0, new RecordingLogger());
            dd.RecordPending(new List<string> { a, b }, 100, dir);
            dd.RecordPending(new List<string> { b, c }, 50, dir);

            var action = dd.Check();

            Assert.Equal(DeferredActionType.Proceed, action.Type);
            Assert.NotNull(action.Files);
            Assert.Equal(3, action.Files!.Count);
            Assert.Contains(a, action.Files);
            Assert.Contains(b, action.Files);
            Assert.Contains(c, action.Files);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Save_Failure_IsLogged()
    {
        var dir = TestDir();
        Directory.CreateDirectory(dir);
        var blocker = Path.Combine(dir, "blocker");
        File.WriteAllText(blocker, "not a directory");
        try
        {
            var log = new RecordingLogger();
            var dd = new DeferredDeletion(Path.Combine(blocker, "deferred.json"), 1, log);
            dd.RecordPending(new List<string> { Path.Combine(dir, "gone.txt") }, 1);

            Assert.Contains(log.Messages, m => m.Contains("could not be saved"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Check_ExpiredHold_ProceedsWithMissingFiles()
    {
        var dir = TestDir();
        Directory.CreateDirectory(dir);
        try
        {
            var dd = new DeferredDeletion(Path.Combine(dir, "deferred.json"), 0, new RecordingLogger());
            var a = Path.Combine(dir, "gone-a.txt");
            var b = Path.Combine(dir, "gone-b.txt");
            dd.RecordPending(new List<string> { a, b }, 10, dir);

            var action = dd.Check();

            Assert.Equal(DeferredActionType.Proceed, action.Type);
            Assert.Equal(2, action.Files!.Count);
            Assert.False(dd.HasPending);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Check_ReappearedFiles_Cancels()
    {
        var dir = TestDir();
        Directory.CreateDirectory(dir);
        try
        {
            var dd = new DeferredDeletion(Path.Combine(dir, "deferred.json"), 0, new RecordingLogger());
            var back = Path.Combine(dir, "back.txt");
            dd.RecordPending(new List<string> { back }, 10, dir);
            File.WriteAllText(back, "returned");

            var action = dd.Check();

            Assert.Equal(DeferredActionType.Cancel, action.Type);
            Assert.Empty(action.Files!);
            Assert.False(dd.HasPending);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Check_WithinHold_WaitsAndWarns()
    {
        var dir = TestDir();
        Directory.CreateDirectory(dir);
        try
        {
            var log = new RecordingLogger();
            var dd = new DeferredDeletion(Path.Combine(dir, "deferred.json"), 7, log);
            dd.RecordPending(new List<string> { Path.Combine(dir, "held.txt") }, 10, dir);

            var action = dd.Check();

            Assert.Equal(DeferredActionType.Waiting, action.Type);
            Assert.True(dd.HasPending);
            Assert.Contains(log.Messages, m => m.Contains("remaining"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
