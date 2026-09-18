namespace TicTack;

[Trait("Category", "Unit")]
public sealed class TrackedDeleterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tictack-deleter-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingLogger _log = new();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Delete_DeletionFails_LogsLabelAndReturnsFalse()
    {
        var deleter = new TrackedDeleter(new FailDeletion("boom"), null, _log);

        var ok = await deleter.DeleteAsync(null, "/dst/a.txt", "a.txt", "Parity deletion failed", CancellationToken.None);

        Assert.False(ok);
        Assert.Contains(_log.Messages, m => m.Contains("Parity deletion failed: /dst/a.txt: boom"));
    }

    [Fact]
    public async Task Delete_StateDbFailure_DoesNotFailTheDeletion()
    {
        var db = new StateDb(Path.Combine(_root, "state.db"), _log);
        db.Dispose();
        var deleter = new TrackedDeleter(new OkDeletion(), db, _log);

        var ok = await deleter.DeleteAsync(null, "/dst/a.txt", "a.txt", "Parity deletion failed", CancellationToken.None);

        Assert.True(ok);
        Assert.Contains(_log.Messages, m => m.Contains("StateDb delete failed"));
    }

    private sealed class OkDeletion : IDeletionStrategy
    {
        public Task<ActionResult> HandleDeletionAsync(string? sourcePath, string destPath, CancellationToken ct) =>
            Task.FromResult(ActionResult.Ok());
    }

    private sealed class FailDeletion : IDeletionStrategy
    {
        private readonly string _message;
        public FailDeletion(string message) => _message = message;
        public Task<ActionResult> HandleDeletionAsync(string? sourcePath, string destPath, CancellationToken ct) =>
            Task.FromResult(ActionResult.Fail(_message));
    }
}
