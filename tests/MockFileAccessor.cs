namespace TicTack;

public class MockFileAccessor : IFileAccessor
{
    public Stream OpenRead(string path)
    {
        return File.OpenRead(path);
    }
}
