using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using Microsoft.Win32;

namespace BatchPdf.Installer
{
    public static class InstallerEngine
    {
        public static readonly string DefaultBundleDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Autodesk", "ApplicationPlugins", "BatchPdf.bundle");

        public static List<string> DetectAutoCADs()
        {
            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string[] hives = new string[]
            {
                @"SOFTWARE\Autodesk\AutoCAD",
                @"SOFTWARE\WOW6432Node\Autodesk\AutoCAD"
            };

            foreach (var hiveName in hives)
            {
                using (var baseKey = Registry.LocalMachine.OpenSubKey(hiveName))
                {
                    if (baseKey == null) continue;
                    foreach (var rel in baseKey.GetSubKeyNames())
                    {
                        using (var relKey = baseKey.OpenSubKey(rel))
                        {
                            if (relKey == null) continue;
                            foreach (var sub in relKey.GetSubKeyNames())
                            {
                                using (var subKey = relKey.OpenSubKey(sub))
                                {
                                    if (subKey == null) continue;
                                    string loc = subKey.GetValue("AcadLocation") as string ??
                                                 subKey.GetValue("Location") as string;
                                    if (!string.IsNullOrEmpty(loc) && Directory.Exists(loc))
                                    {
                                        string name = subKey.GetValue("ProductName") as string ?? ("AutoCAD " + rel);
                                        if (seen.Add(loc))
                                        {
                                            list.Add($"{name} ({loc})");
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            return list;
        }

        public static bool IsAutoCadRunning()
        {
            try
            {
                var procs = Process.GetProcessesByName("acad");
                return procs.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        public static void Install(
            string targetDir,
            bool createShortcut,
            bool registerContextMenu,
            bool registerCadAutoLoad,
            Action<string> log)
        {
            log("开始安装 BatchPdf 插件包...");

            if (!Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            // 1. Extract embedded bundle zip
            log("1/4 正在释放插件核心文件与运行组件...");
            var asm = Assembly.GetExecutingAssembly();
            using (var stream = asm.GetManifestResourceStream("BatchPdf.bundle.zip"))
            {
                if (stream == null)
                {
                    throw new InvalidOperationException("未找到内置的 BatchPdf.bundle.zip 资源文件，请确保安装包完整！");
                }

                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    foreach (var entry in archive.Entries)
                    {
                        string destPath = Path.Combine(targetDir, entry.FullName);
                        if (string.IsNullOrEmpty(entry.Name))
                        {
                            Directory.CreateDirectory(destPath);
                            continue;
                        }

                        string parent = Path.GetDirectoryName(destPath);
                        if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                        {
                            Directory.CreateDirectory(parent);
                        }

                        entry.ExtractToFile(destPath, overwrite: true);
                    }
                }
            }
            log("    插件文件解压完成。");

            // 2. Register AutoCAD Auto-load
            if (registerCadAutoLoad)
            {
                log("2/4 正在配置 AutoCAD 注册表自动加载项...");
                RegisterAutoCadAutoLoad(targetDir, log);
            }
            else
            {
                log("2/4 跳过注册表自动加载项写入。");
            }

            // 3. Create Desktop Shortcut
            if (createShortcut)
            {
                log("3/4 正在创建桌面快捷方式...");
                try
                {
                    string cmdPath = Path.Combine(targetDir, "BatchPlot_Headless.cmd");
                    if (File.Exists(cmdPath))
                    {
                        CreateDesktopShortcut(
                            "【BatchPdf】极速批量打印 (免开CAD)",
                            cmdPath,
                            targetDir,
                            "AutoCAD DWG 免开CAD极速批量打印与智能识别工具");
                        log("    桌面快捷方式创建成功。");
                    }
                }
                catch (Exception ex)
                {
                    log("    [警告] 创建快捷方式失败: " + ex.Message);
                }
            }
            else
            {
                log("3/4 跳过创建桌面快捷方式。");
            }

            // 4. Register Windows Explorer Context Menu
            if (registerContextMenu)
            {
                log("4/4 正在注册 Windows 右键出图菜单...");
                try
                {
                    RegisterContextMenu(targetDir);
                    log("    右键菜单注册成功（支持右键点击 DWG 文件或文件夹直接批量打印）。");
                }
                catch (Exception ex)
                {
                    log("    [警告] 注册右键菜单失败: " + ex.Message);
                }
            }
            else
            {
                log("4/4 跳过注册右键菜单。");
            }

            log("");
            log("==================================================");
            log("恭喜！BatchPdf 批量打印利器已成功安装部署完成！");
            log("使用提示:");
            log("  1. 打开 AutoCAD (2019~2024)，在命令行输入 BPDF 即可开启图形批量出图界面。");
            log("  2. 双击桌面「【BatchPdf】极速批量打印 (免开CAD)」或右键 DWG 文件，可免开 CAD 极速出图。");
            log("==================================================");
        }

        public static void Uninstall(string targetDir, Action<string> log)
        {
            log("开始卸载 BatchPdf 插件...");

            // 1. Remove Context Menu
            try
            {
                log("1/4 清理右键菜单注册项...");
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\*\shell\BatchPdfPlot", throwOnMissingSubKey: false);
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Directory\shell\BatchPdfPlot", throwOnMissingSubKey: false);
                log("    右键菜单已清理。");
            }
            catch (Exception ex) { log("    清理右键菜单提示: " + ex.Message); }

            // 2. Remove Desktop Shortcut
            try
            {
                log("2/4 清理桌面快捷方式...");
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string lnk = Path.Combine(desktop, "【BatchPdf】极速批量打印 (免开CAD).lnk");
                if (File.Exists(lnk)) File.Delete(lnk);
                log("    桌面快捷方式已删除。");
            }
            catch (Exception ex) { log("    清理快捷方式提示: " + ex.Message); }

            // 3. Remove AutoCAD Registry AutoLoad
            try
            {
                log("3/4 清理 AutoCAD 注册表自动加载配置...");
                UnregisterAutoCadAutoLoad();
                log("    AutoCAD 注册表项已清理。");
            }
            catch (Exception ex) { log("    清理注册表提示: " + ex.Message); }

            // 4. Remove Bundle Files
            try
            {
                log("4/4 删除插件目录文件: " + targetDir);
                if (Directory.Exists(targetDir))
                {
                    Directory.Delete(targetDir, recursive: true);
                }
                log("    插件文件目录已删除。");
            }
            catch (Exception ex)
            {
                log("    [提示] 部分文件可能被 CAD 占用未能立即删除，重启电脑后可自动清除: " + ex.Message);
            }

            log("");
            log("BatchPdf 插件已成功从本机卸载。");
        }

        private static void RegisterAutoCadAutoLoad(string targetDir, Action<string> log)
        {
            string[] commands = new string[] { "BPDF", "BPLOT", "BPDFSCAN", "BPDFTEST", "BPDFSET", "BPDFLOG", "BPPREVIEW", "BPTEACH", "BPTABLE", "BPDFBATCH" };
            using (var root = Registry.CurrentUser.OpenSubKey(@"Software\Autodesk\AutoCAD", writable: true))
            {
                if (root == null) return;
                foreach (var relName in root.GetSubKeyNames())
                {
                    bool is2020 = relName.StartsWith("R23", StringComparison.OrdinalIgnoreCase);
                    string dllPath = is2020
                        ? Path.Combine(targetDir, "Contents", "2020", "BatchPdf.Acad2020.dll")
                        : Path.Combine(targetDir, "Contents", "2022", "BatchPdf.Acad2022.dll");
                    string appName = is2020 ? "BatchPdf2020" : "BatchPdf2022";

                    using (var relKey = root.OpenSubKey(relName, writable: true))
                    {
                        if (relKey == null) continue;
                        foreach (var prodName in relKey.GetSubKeyNames())
                        {
                            using (var prodKey = relKey.OpenSubKey(prodName, writable: true))
                            {
                                if (prodKey == null) continue;
                                using (var appsKey = prodKey.CreateSubKey("Applications"))
                                {
                                    if (appsKey == null) continue;
                                    using (var myAppKey = appsKey.CreateSubKey(appName))
                                    {
                                        if (myAppKey == null) continue;
                                        myAppKey.SetValue("DESCRIPTION", "BatchPdf for " + relName, RegistryValueKind.String);
                                        myAppKey.SetValue("LOADCTRLS", 6, RegistryValueKind.DWord);
                                        myAppKey.SetValue("MANAGED", 1, RegistryValueKind.DWord);
                                        myAppKey.SetValue("LOADER", dllPath, RegistryValueKind.String);

                                        using (var cmdsKey = myAppKey.CreateSubKey("Commands"))
                                        {
                                            if (cmdsKey != null)
                                            {
                                                foreach (var c in commands)
                                                {
                                                    cmdsKey.SetValue(c, c, RegistryValueKind.String);
                                                }
                                            }
                                        }
                                        log($"    已自动注册到 AutoCAD {relName} ({prodName})");
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        private static void UnregisterAutoCadAutoLoad()
        {
            using (var root = Registry.CurrentUser.OpenSubKey(@"Software\Autodesk\AutoCAD", writable: true))
            {
                if (root == null) return;
                foreach (var relName in root.GetSubKeyNames())
                {
                    using (var relKey = root.OpenSubKey(relName, writable: true))
                    {
                        if (relKey == null) continue;
                        foreach (var prodName in relKey.GetSubKeyNames())
                        {
                            using (var prodKey = relKey.OpenSubKey(prodName, writable: true))
                            {
                                if (prodKey == null) continue;
                                using (var appsKey = prodKey.OpenSubKey("Applications", writable: true))
                                {
                                    if (appsKey == null) continue;
                                    appsKey.DeleteSubKeyTree("BatchPdf2020", throwOnMissingSubKey: false);
                                    appsKey.DeleteSubKeyTree("BatchPdf2022", throwOnMissingSubKey: false);
                                }
                            }
                        }
                    }
                }
            }
        }

        private static void CreateDesktopShortcut(string name, string targetPath, string workingDir, string description)
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string shortcutPath = Path.Combine(desktop, name + ".lnk");

            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType != null)
            {
                dynamic shell = Activator.CreateInstance(shellType);
                dynamic shortcut = shell.CreateShortcut(shortcutPath);
                shortcut.TargetPath = targetPath;
                shortcut.WorkingDirectory = workingDir;
                shortcut.Description = description;
                shortcut.WindowStyle = 1;
                shortcut.Save();
            }
        }

        private static void RegisterContextMenu(string targetDir)
        {
            string ps1 = Path.Combine(targetDir, "BatchPdfHeadless.ps1");
            string cmdLine = $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"{ps1}\" -Paths \"%1\"";

            // 1. For *.dwg files
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\*\shell\BatchPdfPlot"))
            {
                if (key != null)
                {
                    key.SetValue("", "使用 BatchPdf 批量打印 (免开CAD)");
                    using (var cmd = key.CreateSubKey("command"))
                    {
                        cmd?.SetValue("", cmdLine);
                    }
                }
            }

            // 2. For folders
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Directory\shell\BatchPdfPlot"))
            {
                if (key != null)
                {
                    key.SetValue("", "使用 BatchPdf 批量打印 (免开CAD)");
                    using (var cmd = key.CreateSubKey("command"))
                    {
                        cmd?.SetValue("", cmdLine);
                    }
                }
            }
        }
    }
}
