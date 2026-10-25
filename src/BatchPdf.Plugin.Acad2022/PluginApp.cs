using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices.Core;
using Autodesk.AutoCAD.Runtime;
using BatchPdf.AutoCAD.Services;
using BatchPdf.Infrastructure.Services;

[assembly: ExtensionApplication(typeof(BatchPdf.Plugin.PluginApp))]
[assembly: CommandClass(typeof(BatchPdf.Plugin.BatchPdfCommands))]
[assembly: CommandClass(typeof(BatchPdf.Plugin.HeadlessCommands))]

namespace BatchPdf.Plugin
{
    public class PluginApp : IExtensionApplication
    {
        static PluginApp()
        {
            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
        }

        public void Initialize()
        {
            try
            {
                string proc = Process.GetCurrentProcess().ProcessName.ToLowerInvariant();
                bool isHeadless = proc.Contains("accoreconsole");

                var doc = Application.DocumentManager.MdiActiveDocument;
                if (doc != null)
                {
                    doc.Editor.WriteMessage(
                        "\n[BatchPdf] AutoCAD 批量打印插件已就绪。" +
                        (isHeadless ? " (无头控制台模式)\n" : "顶栏已挂载「CAD批量打印」菜单。\n常用命令: BPDF / BPLOT (主界面) | BPPREVIEW (单张预览) | BPTEACH (学习图框) | BPTABLE (图纸目录表) | BPDFSCAN (扫描) | BPDFSET (设置) | BPDFLOG (日志)\n"));
                }

                if (!isHeadless)
                {
                    CadMenuService.EnsureMenuInstalled();
                }

                var log = new LogService();
                log.LogInfo("PluginLoaded", "BatchPdf plugin initialized in " + proc);
            }
            catch
            {
            }
        }

        public void Terminate()
        {
        }

        private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            try
            {
                string pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (string.IsNullOrEmpty(pluginDir)) return null;

                var asmName = new AssemblyName(args.Name);
                string candidatePath = Path.Combine(pluginDir, asmName.Name + ".dll");
                if (File.Exists(candidatePath))
                {
                    return Assembly.LoadFrom(candidatePath);
                }
            }
            catch
            {
            }
            return null;
        }
    }
}
