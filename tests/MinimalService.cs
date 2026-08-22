using System;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;

namespace TicTackTest
{
    class MinimalService : ServiceBase
    {
        public MinimalService()
        {
            ServiceName = "TicTackTest";
            AutoLog = false;
        }

        static void Main()
        {
            ServiceBase.Run(new MinimalService());
        }

        protected override void OnStart(string[] args)
        {
            try
            {
                EventLog.WriteEntry("TicTackTest", "OnStart OK", EventLogEntryType.Information);
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "TicTackTest-ok.txt"), DateTime.Now + " OnStart OK");
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "TicTackTest-error.txt"), ex.ToString());
                throw;
            }
        }
    }
}
