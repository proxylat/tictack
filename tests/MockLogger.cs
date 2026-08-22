using System.Collections.Concurrent;

namespace TicTack;

public class MockLogger : ILogger
{
    public ConcurrentBag<string> Messages { get; } = new();

    public void Debug(string msg) => Messages.Add("DBG:" + msg);
    public void Info(string msg) => Messages.Add("INF:" + msg);
    public void Warn(string msg) => Messages.Add("WRN:" + msg);
    public void Error(string msg, Exception? ex = null) =>
        Messages.Add("ERR:" + msg + (ex != null ? "|" + ex.Message : ""));
}
