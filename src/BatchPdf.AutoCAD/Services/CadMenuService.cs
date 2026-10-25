using System;
using System.Diagnostics;
using Autodesk.AutoCAD.ApplicationServices;
using CoreApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace BatchPdf.AutoCAD.Services
{
    /// <summary>
    /// Provides AutoCAD top-level MenuBar integration.
    /// Injects the "CAD批量打印(&B)" popup menu into AutoCAD's main menu bar via COM late-binding,
    /// with zero external compile-time dependencies (no AcCui.dll required, works in 2020-2024).
    /// </summary>
    public static class CadMenuService
    {
        public const string MenuTitle = "CAD批量打印(&B)";

        // In AutoCAD COM automation (AddMenuItem), the cancel/ESC key is ASCII 3 (ETX / Ctrl+C).
        // Literal caret notation "^C^C" is only supported in CUI/CUIx files; passing it to COM
        // sends literal "^" and "C" characters, causing "Unknown command ^C^C_BPDF".
        private const string Esc = "\u0003\u0003";

        private static readonly string[,] MenuDefinitions = new string[,]
        {
            { "批量出图主面板 (&P)...", Esc + "_BPDF " },
            { "单张打印预览 (&V)...", Esc + "_BPPREVIEW " },
            { "点选学习图框 (&T)...", Esc + "_BPTEACH " },
            { "生成图纸目录表 (&L)...", Esc + "_BPTABLE " },
            { "-", "" },
            { "图框识别规则与设置 (&S)...", Esc + "_BPDFSET " },
            { "打印机与驱动诊断 (&D)...", Esc + "_BPDFTEST " },
            { "查看运行日志 (&G)...", Esc + "_BPDFLOG " }
        };

        /// <summary>
        /// Ensures that the CADBPLOT menu is registered and visible on the AutoCAD MenuBar.
        /// Safe to call multiple times (idempotent).
        /// </summary>
        public static void EnsureMenuInstalled()
        {
            try
            {
                // In headless mode (accoreconsole), there is no GUI or MenuBar.
                string procName = Process.GetCurrentProcess().ProcessName.ToLowerInvariant();
                if (procName.Contains("accoreconsole"))
                {
                    return;
                }

                dynamic acadApp = Application.AcadApplication;
                if (acadApp == null) return;

                dynamic menuGroups = acadApp.MenuGroups;
                if (menuGroups == null || menuGroups.Count == 0) return;

                // Find primary menu group (prefer "ACAD", fallback to first group)
                dynamic primaryGroup = null;
                for (int i = 0; i < menuGroups.Count; i++)
                {
                    dynamic g = menuGroups.Item(i);
                    if (g != null && string.Equals((string)g.Name, "ACAD", StringComparison.OrdinalIgnoreCase))
                    {
                        primaryGroup = g;
                        break;
                    }
                }
                if (primaryGroup == null)
                {
                    primaryGroup = menuGroups.Item(0);
                }
                if (primaryGroup == null) return;

                dynamic menus = primaryGroup.Menus;
                if (menus == null) return;

                // Check if our menu already exists in the group
                dynamic targetMenu = null;
                for (int i = 0; i < menus.Count; i++)
                {
                    dynamic m = menus.Item(i);
                    if (m != null)
                    {
                        string name = (string)m.Name;
                        if (name == MenuTitle || name == "CAD批量打印" || name.StartsWith("CAD批量打印", StringComparison.OrdinalIgnoreCase))
                        {
                            targetMenu = m;
                            break;
                        }
                    }
                }

                if (targetMenu == null)
                {
                    targetMenu = menus.Add(MenuTitle);
                }

                if (targetMenu != null)
                {
                    // Refresh menu items
                    while (targetMenu.Count > 0)
                    {
                        try { targetMenu.Item(0).Delete(); } catch { break; }
                    }

                    int itemCount = MenuDefinitions.GetLength(0);
                    for (int i = 0; i < itemCount; i++)
                    {
                        string label = MenuDefinitions[i, 0];
                        string macro = MenuDefinitions[i, 1];

                        if (label == "-")
                        {
                            targetMenu.AddSeparator(targetMenu.Count + 1);
                        }
                        else
                        {
                            targetMenu.AddMenuItem(targetMenu.Count + 1, label, macro);
                        }
                    }

                    // Insert into AutoCAD MenuBar if not already visible
                    bool onMenuBar = false;
                    try { onMenuBar = targetMenu.OnMenuBar; } catch { }

                    if (!onMenuBar)
                    {
                        int pos = acadApp.MenuBar.Count;
                        targetMenu.InsertInMenuBar(pos);
                    }

                    // Check if MENUBAR is visible (MENUBAR system variable)
                    try
                    {
                        object mb = CoreApp.GetSystemVariable("MENUBAR");
                        if (mb != null && Convert.ToInt32(mb) == 0)
                        {
                            var doc = CoreApp.DocumentManager.MdiActiveDocument;
                            doc?.Editor.WriteMessage("\n[CAD批量打印] 顶部菜单栏已挂载。若未显示菜单栏，可在命令行输入 MENUBAR 并设为 1。\n");
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch (Exception ex)
            {
                // Never let menu failure crash AutoCAD or plugin initialization
                try
                {
                    var doc = CoreApp.DocumentManager.MdiActiveDocument;
                    doc?.Editor.WriteMessage("\n[CAD批量打印] 菜单栏加载提示: " + ex.Message + "\n");
                }
                catch
                {
                }
            }
        }
    }
}
