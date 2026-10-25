using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using BatchPdf.Core.Configuration;
using BatchPdf.Core.Models;

namespace BatchPdf.AutoCAD.Services
{
    /// <summary>
    /// Generates native AutoCAD Table entities (AcDbTable) in the drawing
    /// for drawing schedule/catalog lists (图纸目录表).
    /// </summary>
    public static class CadTableService
    {
        public static bool GenerateDrawingIndexTable(
            Document doc,
            IList<PrintSheet> sheets,
            BatchPdfSettings settings,
            Point3d origin,
            out string message)
        {
            message = "";
            if (doc == null)
            {
                message = "没有当前活动的 AutoCAD 文档。";
                return false;
            }

            var validSheets = sheets != null
                ? sheets.Where(s => s != null && s.IsSelected).OrderBy(s => s.OrderIndex).ToList()
                : new List<PrintSheet>();

            if (validSheets.Count == 0 && sheets != null && sheets.Count > 0)
            {
                validSheets = sheets.Where(s => s != null).OrderBy(s => s.OrderIndex).ToList();
            }

            if (validSheets.Count == 0)
            {
                message = "图纸清单为空，无法生成图纸目录。";
                return false;
            }

            Database db = doc.Database;
            using (DocumentLock docLock = doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                try
                {
                    BlockTableRecord space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

                    var tb = new Table();
                    tb.Position = origin;
                    tb.SetDatabaseDefaults();

                    // Estimate scale factor based on drawing units or first sheet scale
                    double scaleFactor = 100.0;
                    var firstScaled = validSheets.FirstOrDefault(s => s.ScaleValue.HasValue && s.ScaleValue.Value > 0);
                    if (firstScaled != null && firstScaled.ScaleValue.HasValue)
                    {
                        scaleFactor = firstScaled.ScaleValue.Value;
                    }
                    else if (settings != null && settings.DefaultScaleValue > 0)
                    {
                        scaleFactor = settings.DefaultScaleValue;
                    }

                    // Text height: 3.5mm in paper units * scale
                    double textHeight = Math.Max(2.5, 3.5 * (scaleFactor / 100.0) * 100.0); // e.g. 350 for 1:100
                    if (scaleFactor == 1.0) textHeight = 3.5;
                    double titleTextHeight = textHeight * 1.3;
                    double rowHeight = textHeight * 2.4;

                    int totalRows = validSheets.Count + 1; // Row 0 = Header
                    int totalCols = 7;                     // 序号, 图号, 图名, 图幅, 比例, 出图日期, 备注

                    tb.SetSize(totalRows, totalCols);
                    tb.GenerateLayout();

                    // Unmerge any default header cells if present
                    try
                    {
                        if (tb.Cells[0, 0].IsMerged == true)
                        {
                            tb.UnmergeCells(tb.Cells[0, 0].GetMergeRange());
                        }
                    }
                    catch { }

                    // Column widths in drawing units (proportional to standard paper layout)
                    double unitBase = (scaleFactor == 1.0 ? 1.0 : scaleFactor);
                    double col0W = 15.0 * unitBase; // 序号
                    double col1W = 35.0 * unitBase; // 图号
                    double col2W = 65.0 * unitBase; // 图名
                    double col3W = 25.0 * unitBase; // 图幅
                    double col4W = 20.0 * unitBase; // 比例
                    double col5W = 25.0 * unitBase; // 出图日期
                    double col6W = 25.0 * unitBase; // 备注

                    tb.Columns[0].Width = col0W;
                    tb.Columns[1].Width = col1W;
                    tb.Columns[2].Width = col2W;
                    tb.Columns[3].Width = col3W;
                    tb.Columns[4].Width = col4W;
                    tb.Columns[5].Width = col5W;
                    tb.Columns[6].Width = col6W;

                    string[] headers = new string[] { "序号", "图号", "图纸名称", "图幅", "比例", "出图日期", "备注" };
                    for (int c = 0; c < totalCols; c++)
                    {
                        tb.Rows[0].Height = rowHeight * 1.1;
                        var cell = tb.Cells[0, c];
                        cell.TextString = headers[c];
                        cell.TextHeight = titleTextHeight;
                        cell.Alignment = CellAlignment.MiddleCenter;
                    }

                    string defaultIssueDate = DateTime.Now.ToString("yyyy.MM");

                    for (int r = 0; r < validSheets.Count; r++)
                    {
                        var s = validSheets[r];
                        int rowIdx = r + 1;
                        tb.Rows[rowIdx].Height = rowHeight;

                        // 0: 序号
                        var c0 = tb.Cells[rowIdx, 0];
                        c0.TextString = (r + 1).ToString();
                        c0.TextHeight = textHeight;
                        c0.Alignment = CellAlignment.MiddleCenter;

                        // 1: 图号
                        var c1 = tb.Cells[rowIdx, 1];
                        c1.TextString = s.DrawingNumber ?? "";
                        c1.TextHeight = textHeight;
                        c1.Alignment = CellAlignment.MiddleCenter;

                        // 2: 图名
                        var c2 = tb.Cells[rowIdx, 2];
                        c2.TextString = s.DrawingName ?? "";
                        c2.TextHeight = textHeight;
                        c2.Alignment = CellAlignment.MiddleLeft;

                        // 3: 图幅
                        var c3 = tb.Cells[rowIdx, 3];
                        string paperStr = !string.IsNullOrWhiteSpace(s.TargetPaperLocaleName)
                            ? ExtractCleanPaperName(s.TargetPaperLocaleName)
                            : s.FormattedGeometryInfo;
                        c3.TextString = paperStr;
                        c3.TextHeight = textHeight;
                        c3.Alignment = CellAlignment.MiddleCenter;

                        // 4: 比例
                        var c4 = tb.Cells[rowIdx, 4];
                        string scaleStr = s.ScaleValue.HasValue && s.ScaleValue.Value > 0
                            ? string.Format("1:{0:G}", s.ScaleValue.Value)
                            : (s.FitToPaper ? "Fit" : "-");
                        c4.TextString = scaleStr;
                        c4.TextHeight = textHeight;
                        c4.Alignment = CellAlignment.MiddleCenter;

                        // 5: 出图日期
                        var c5 = tb.Cells[rowIdx, 5];
                        c5.TextString = defaultIssueDate;
                        c5.TextHeight = textHeight;
                        c5.Alignment = CellAlignment.MiddleCenter;

                        // 6: 备注
                        var c6 = tb.Cells[rowIdx, 6];
                        c6.TextString = "";
                        c6.TextHeight = textHeight;
                        c6.Alignment = CellAlignment.MiddleCenter;
                    }

                    space.AppendEntity(tb);
                    tr.AddNewlyCreatedDBObject(tb, true);
                    tr.Commit();

                    message = string.Format("已成功生成 CAD 图纸目录表，共 {0} 张图纸。", validSheets.Count);
                    return true;
                }
                catch (Exception ex)
                {
                    message = "生成图纸目录失败: " + ex.Message;
                    return false;
                }
            }
        }

        private static string ExtractCleanPaperName(string localeName)
        {
            if (string.IsNullOrWhiteSpace(localeName)) return "";
            // E.g. "ISO full bleed A1 (841.00 x 594.00 毫米)" -> "A1" or "A1加长"
            int aIdx = localeName.IndexOf("A0", StringComparison.OrdinalIgnoreCase);
            if (aIdx >= 0) return "A0";
            aIdx = localeName.IndexOf("A1", StringComparison.OrdinalIgnoreCase);
            if (aIdx >= 0) return "A1";
            aIdx = localeName.IndexOf("A2", StringComparison.OrdinalIgnoreCase);
            if (aIdx >= 0) return "A2";
            aIdx = localeName.IndexOf("A3", StringComparison.OrdinalIgnoreCase);
            if (aIdx >= 0) return "A3";
            aIdx = localeName.IndexOf("A4", StringComparison.OrdinalIgnoreCase);
            if (aIdx >= 0) return "A4";

            return localeName.Split('(')[0].Trim();
        }
    }
}
