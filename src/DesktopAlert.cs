using System;
using System.IO;

namespace TicTack
{
    public static class DesktopAlert
    {
        private static readonly object Lock = new object();
        private static DateTime _lastAlert;

        private static string? _alertPath;

        public static void Configure(string? path)
        {
            _alertPath = path;
        }

        public static void Write(string level, string message, Exception? ex = null)
        {
            var now = DateTime.Now;
            lock (Lock)
            {
                if ((now - _lastAlert).TotalSeconds < 30) return;
                _lastAlert = now;
            }

            var dir = _alertPath;
            if (string.IsNullOrEmpty(dir)) return;

            try
            {
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var ts = now.ToString("yyyyMMdd-HHmmss");
                var path = Path.Combine(dir, "TicTack-" + level + "-" + ts + ".txt");
                using (var w = new StreamWriter(path))
                {
                    w.WriteLine("TicTack " + level + " — " + now.ToString("yyyy-MM-dd HH:mm:ss"));
                    w.WriteLine(new string('-', 50));
                    w.WriteLine(message);
                    if (ex != null)
                    {
                        w.WriteLine();
                        w.WriteLine("Exception:");
                        w.WriteLine(ex.ToString());
                    }
                }
            }
            catch { }
        }
    }
}
