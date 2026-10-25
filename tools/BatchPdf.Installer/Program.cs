using System;
using System.IO;
using System.Windows.Forms;

namespace BatchPdf.Installer
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool isSilent = false;
            bool isUninstall = false;
            string targetDir = InstallerEngine.DefaultBundleDir;

            foreach (var a in args)
            {
                if (string.Equals(a, "/silent", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, "-s", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, "/s", StringComparison.OrdinalIgnoreCase))
                {
                    isSilent = true;
                }
                else if (string.Equals(a, "/uninstall", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(a, "-u", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(a, "/u", StringComparison.OrdinalIgnoreCase))
                {
                    isUninstall = true;
                }
                else if (a.StartsWith("/dir=", StringComparison.OrdinalIgnoreCase))
                {
                    targetDir = a.Substring(5).Trim('"');
                }
            }

            if (isSilent)
            {
                try
                {
                    if (isUninstall)
                    {
                        InstallerEngine.Uninstall(targetDir, Console.WriteLine);
                    }
                    else
                    {
                        InstallerEngine.Install(targetDir, true, true, true, Console.WriteLine);
                    }
                    return 0;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("Error: " + ex.Message);
                    return 1;
                }
            }

            Application.Run(new InstallerForm());
            return 0;
        }
    }
}
