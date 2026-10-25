using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using BatchPdf.Core.Models;

namespace BatchPdf.Plot.Services
{
    public class PaperMediaCatalog
    {
        private static readonly double[][] StandardIsoSizesMm = new double[][]
        {
            new[] { 1189.0, 841.0 }, // A0
            new[] { 841.0, 594.0 },  // A1
            new[] { 594.0, 420.0 },  // A2
            new[] { 420.0, 297.0 },  // A3
            new[] { 297.0, 210.0 }   // A4
        };

        /// <summary>
        /// Checks whether DWG To PDF.pc3 is present in AutoCAD's current plot device list.
        /// Never falls back to any other PC3 device.
        /// </summary>
        public DeviceCheckResult EnsurePdfDeviceAvailable()
        {
            var result = new DeviceCheckResult();

            try
            {
                PlotSettingsValidator psv = PlotSettingsValidator.Current;
                StringCollection deviceList = psv.GetPlotDeviceList();

                string matchedRawName = null;
                foreach (string dev in deviceList)
                {
                    if (dev == null) continue;
                    result.AllAvailableDevices.Add(dev);

                    if (string.Equals(dev.Trim(), DeviceCheckResult.RequiredDeviceTargetName, StringComparison.OrdinalIgnoreCase))
                    {
                        matchedRawName = dev;
                    }
                }

                if (!string.IsNullOrEmpty(matchedRawName))
                {
                    result.IsAvailable = true;
                    result.MatchedDeviceName = matchedRawName;
                    result.DiagnosticMessage = string.Format("已就绪：{0}", matchedRawName);
                }
                else
                {
                    result.IsAvailable = false;
                    result.MatchedDeviceName = null;
                    result.DiagnosticMessage = string.Format(
                        "未找到必需的绘图仪 \"{0}\"。请通过 AutoCAD 的 PLOTTERMANAGER 命令检查绘图仪配置。本插件严格不自动切换至其他 PC3 设备。",
                        DeviceCheckResult.RequiredDeviceTargetName);
                }
            }
            catch (Exception ex)
            {
                result.IsAvailable = false;
                result.MatchedDeviceName = null;
                result.DiagnosticMessage = "查询 AutoCAD 绘图设备列表失败: " + ex.Message;
            }

            return result;
        }

        /// <summary>
        /// Dynamically reads all available paper media and their physical sizes/margins from DWG To PDF.pc3.
        /// </summary>
        public List<PaperMedia> RefreshMediaList(out DeviceCheckResult deviceResult)
        {
            var mediaList = new List<PaperMedia>();
            deviceResult = EnsurePdfDeviceAvailable();
            if (!deviceResult.IsAvailable)
            {
                return mediaList;
            }

            try
            {
                PlotSettingsValidator psv = PlotSettingsValidator.Current;
                using (PlotSettings ps = new PlotSettings(true))
                {
                    psv.SetPlotConfigurationName(ps, deviceResult.MatchedDeviceName, null);
                    psv.RefreshLists(ps);

                    StringCollection canonicalNames = psv.GetCanonicalMediaNameList(ps);
                    if (canonicalNames == null || canonicalNames.Count == 0)
                    {
                        deviceResult.IsAvailable = false;
                        deviceResult.DiagnosticMessage = string.Format(
                            "绘图仪 \"{0}\" 的可用纸张媒体列表为空，请检查 PC3/PMP 文件是否损坏。",
                            deviceResult.MatchedDeviceName);
                        return mediaList;
                    }

                    for (int i = 0; i < canonicalNames.Count; i++)
                    {
                        string canonicalName = canonicalNames[i];
                        if (string.IsNullOrWhiteSpace(canonicalName)) continue;

                        try
                        {
                            psv.SetCanonicalMediaName(ps, canonicalName);
                            string localeName = psv.GetLocaleMediaName(ps, i);
                            if (string.IsNullOrWhiteSpace(localeName))
                            {
                                localeName = psv.GetLocaleMediaName(ps, canonicalName);
                            }

                            Point2d paperSize = ps.PlotPaperSize;
                            Extents2d margins = ps.PlotPaperMargins;

                            var media = new PaperMedia
                            {
                                CanonicalName = canonicalName,
                                LocaleName = string.IsNullOrWhiteSpace(localeName) ? canonicalName : localeName,
                                WidthMm = paperSize.X,
                                HeightMm = paperSize.Y,
                                MarginsMm = new PaperMargins(
                                    margins.MinPoint.X,
                                    margins.MinPoint.Y,
                                    margins.MaxPoint.X,
                                    margins.MaxPoint.Y),
                                IsCustomOrLengthened = IsCustomOrLengthenedPaper(canonicalName, localeName, paperSize.X, paperSize.Y)
                            };

                            mediaList.Add(media);
                        }
                        catch
                        {
                            // Skip individual corrupted media entry without failing the entire catalog
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                deviceResult.IsAvailable = false;
                deviceResult.DiagnosticMessage = "刷新 DWG To PDF.pc3 纸张媒体列表失败: " + ex.Message;
            }

            return mediaList;
        }

        /// <summary>
        /// Reads the physical size and margins of a single canonical media name from DWG To PDF.pc3.
        /// </summary>
        public PaperMedia ReadPaperPhysicalSize(string canonicalMediaName)
        {
            if (string.IsNullOrWhiteSpace(canonicalMediaName)) return null;

            DeviceCheckResult dev = EnsurePdfDeviceAvailable();
            if (!dev.IsAvailable) return null;

            try
            {
                PlotSettingsValidator psv = PlotSettingsValidator.Current;
                using (PlotSettings ps = new PlotSettings(true))
                {
                    psv.SetPlotConfigurationName(ps, dev.MatchedDeviceName, canonicalMediaName);
                    psv.RefreshLists(ps);
                    psv.SetCanonicalMediaName(ps, canonicalMediaName);

                    string localeName = psv.GetLocaleMediaName(ps, canonicalMediaName);
                    Point2d size = ps.PlotPaperSize;
                    Extents2d margins = ps.PlotPaperMargins;

                    return new PaperMedia
                    {
                        CanonicalName = canonicalMediaName,
                        LocaleName = string.IsNullOrWhiteSpace(localeName) ? canonicalMediaName : localeName,
                        WidthMm = size.X,
                        HeightMm = size.Y,
                        MarginsMm = new PaperMargins(
                            margins.MinPoint.X,
                            margins.MinPoint.Y,
                            margins.MaxPoint.X,
                            margins.MaxPoint.Y),
                        IsCustomOrLengthened = IsCustomOrLengthenedPaper(canonicalMediaName, localeName, size.X, size.Y)
                    };
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Returns available plot style sheets (CTB or STB depending on the database's PlotStyleMode).
        /// Combines AutoCAD validator list, configured printer style sheet paths, and local plot styles directories.
        /// </summary>
        public List<string> GetAvailablePlotStyleSheets(Database db)
        {
            var styles = new List<string>();
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            bool isCtbMode = db == null || db.PlotStyleMode;
            string expectedExt = isCtbMode ? ".ctb" : ".stb";

            // 1. Query AutoCAD PlotSettingsValidator
            try
            {
                PlotSettingsValidator psv = PlotSettingsValidator.Current;
                StringCollection rawList = psv.GetPlotStyleSheetList();
                if (rawList != null)
                {
                    foreach (string s in rawList)
                    {
                        if (!string.IsNullOrWhiteSpace(s) &&
                            s.EndsWith(expectedExt, StringComparison.OrdinalIgnoreCase))
                        {
                            string trimmed = s.Trim();
                            if (set.Add(trimmed))
                            {
                                styles.Add(trimmed);
                            }
                        }
                    }
                }
            }
            catch
            {
            }

            bool isHeadless = System.Diagnostics.Process.GetCurrentProcess().ProcessName.IndexOf("accoreconsole", StringComparison.OrdinalIgnoreCase) >= 0;

            // 2. Scan AutoCAD Registry PrinterStyleSheetDir (safe for both GUI and headless accoreconsole)
            try
            {
                using (var hkcu = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Autodesk\AutoCAD"))
                {
                    if (hkcu != null)
                    {
                        foreach (string acadVer in hkcu.GetSubKeyNames())
                        {
                            using (var verKey = hkcu.OpenSubKey(acadVer))
                            {
                                if (verKey == null) continue;
                                foreach (string prodKey in verKey.GetSubKeyNames())
                                {
                                    using (var profRoot = verKey.OpenSubKey(prodKey + @"\Profiles"))
                                    {
                                        if (profRoot == null) continue;
                                        foreach (string profName in profRoot.GetSubKeyNames())
                                        {
                                            using (var genKey = profRoot.OpenSubKey(profName + @"\General"))
                                            {
                                                if (genKey == null) continue;
                                                string dir = genKey.GetValue("PrinterStyleSheetDir") as string;
                                                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                                                {
                                                    foreach (string f in Directory.GetFiles(dir, "*" + expectedExt))
                                                    {
                                                        string name = Path.GetFileName(f);
                                                        if (set.Add(name))
                                                        {
                                                            styles.Add(name);
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
            }

            // 2b. In GUI mode only, try AutoCAD Preferences PrinterStyleSheetPath via Reflection
            if (!isHeadless)
            {
                try
                {
                    var appType = Type.GetType("Autodesk.AutoCAD.ApplicationServices.Application, acmgd");
                    if (appType != null)
                    {
                        var prefProp = appType.GetProperty("Preferences");
                        if (prefProp != null)
                        {
                            object prefObj = prefProp.GetValue(null, null);
                            if (prefObj != null)
                            {
                                var filesProp = prefObj.GetType().GetProperty("Files");
                                if (filesProp != null)
                                {
                                    object filesObj = filesProp.GetValue(prefObj, null);
                                    if (filesObj != null)
                                    {
                                        var stylePathProp = filesObj.GetType().GetProperty("PrinterStyleSheetPath");
                                        if (stylePathProp != null)
                                        {
                                            string stylePaths = stylePathProp.GetValue(filesObj, null) as string;
                                            if (!string.IsNullOrWhiteSpace(stylePaths))
                                            {
                                                foreach (string p in stylePaths.Split(';'))
                                                {
                                                    string trimmed = p.Trim();
                                                    if (Directory.Exists(trimmed))
                                                    {
                                                        foreach (string f in Directory.GetFiles(trimmed, "*" + expectedExt))
                                                        {
                                                            string name = Path.GetFileName(f);
                                                            if (set.Add(name))
                                                            {
                                                                styles.Add(name);
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch
                {
                }
            }

            // 3. Fallback scan: Search user AppData Autodesk Plot Styles folders
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string autodeskDir = Path.Combine(appData, "Autodesk");
                if (Directory.Exists(autodeskDir))
                {
                    foreach (string dir in Directory.GetDirectories(autodeskDir, "Plot Styles", SearchOption.AllDirectories))
                    {
                        foreach (string f in Directory.GetFiles(dir, "*" + expectedExt))
                        {
                            string name = Path.GetFileName(f);
                            if (set.Add(name))
                            {
                                styles.Add(name);
                            }
                        }
                    }
                }
            }
            catch
            {
            }

            // 4. Scan current DWG directory (if DWG has local CTB/STB)
            try
            {
                if (db != null && !string.IsNullOrWhiteSpace(db.Filename))
                {
                    string dwgDir = Path.GetDirectoryName(db.Filename);
                    if (!string.IsNullOrWhiteSpace(dwgDir) && Directory.Exists(dwgDir))
                    {
                        foreach (string f in Directory.GetFiles(dwgDir, "*" + expectedExt))
                        {
                            string name = Path.GetFileName(f);
                            if (set.Add(name))
                            {
                                styles.Add(name);
                            }
                        }
                    }
                }
            }
            catch
            {
            }

            // Sort: monochrome.ctb and acad.ctb first if present, then alphabetical
            styles.Sort((a, b) =>
            {
                if (string.Equals(a, "monochrome" + expectedExt, StringComparison.OrdinalIgnoreCase)) return -1;
                if (string.Equals(b, "monochrome" + expectedExt, StringComparison.OrdinalIgnoreCase)) return 1;
                if (string.Equals(a, "acad" + expectedExt, StringComparison.OrdinalIgnoreCase)) return -1;
                if (string.Equals(b, "acad" + expectedExt, StringComparison.OrdinalIgnoreCase)) return 1;
                return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            });

            return styles;
        }

        private static bool IsCustomOrLengthenedPaper(string canonicalName, string localeName, double widthMm, double heightMm)
        {
            if (!string.IsNullOrEmpty(canonicalName) &&
                canonicalName.StartsWith("UserDefined", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(localeName) &&
                (localeName.IndexOf("加长", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 localeName.IndexOf("Custom", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return true;
            }

            double longEdge = Math.Max(widthMm, heightMm);
            double shortEdge = Math.Min(widthMm, heightMm);

            foreach (var std in StandardIsoSizesMm)
            {
                if (Math.Abs(longEdge - std[0]) < 3.0 && Math.Abs(shortEdge - std[1]) < 3.0)
                {
                    return false;
                }
            }

            return false;
        }
    }
}
