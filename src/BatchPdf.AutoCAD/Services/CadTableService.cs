using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using BatchPdf.Core.Configuration;
using BatchPdf.Core.Models;
using BatchPdf.Core.Services;

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

                    // Estimate scale factor based on drawing units or first scaled sheet
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

                    string[] headers = new string[] { "序号", "图号", "图纸名称", "图幅", "比例", "出图日期", "备注" };
                    string defaultIssueDate = DateTime.Now.ToString("yyyy.MM");

                    var rowStrings = new List<string[]>(validSheets.Count);
                    for (int r = 0; r < validSheets.Count; r++)
                    {
                        var s = validSheets[r];
                        string paperStr = !string.IsNullOrWhiteSpace(s.TargetPaperLocaleName)
                            ? ExtractCleanPaperName(s.TargetPaperLocaleName)
                            : s.FormattedGeometryInfo;
                        string scaleStr = s.ScaleValue.HasValue && s.ScaleValue.Value > 0
                            ? string.Format("1:{0:G}", s.ScaleValue.Value)
                            : (s.FitToPaper ? "Fit" : "-");

                        rowStrings.Add(new[]
                        {
                            (r + 1).ToString(),
                            (s.DrawingNumber ?? "").Trim(),
                            (s.DrawingName ?? "").Trim(),
                            (paperStr ?? "").Trim(),
                            scaleStr,
                            defaultIssueDate,
                            ""
                        });
                    }

                    DrawingIndexTableLayout layout = DrawingIndexTableLayoutService.ComputeLayout(headers, rowStrings, scaleFactor);
                    int totalRows = validSheets.Count + 1; // Row 0 = Header
                    int totalCols = headers.Length;

                    tb.SuppressRegenerateTable(true);
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

                    // Normalize table-level default margins if needed, per-cell edges are explicitly set below
                    tb.SuppressRegenerateTable(false);
                    tb.SuppressRegenerateTable(true);

                    for (int c = 0; c < totalCols; c++)
                    {
                        tb.Columns[c].Width = layout.ColumnWidths[c];
                    }

                    tb.Rows[0].Height = layout.RowHeights[0];
                    for (int c = 0; c < totalCols; c++)
                    {
                        var cell = tb.Cells[0, c];
                        ApplyCellMargins(tb, 0, c, layout.HorzMargin, layout.VertMargin);
                        cell.TextString = headers[c];
                        cell.TextHeight = layout.HeaderTextHeight;
                        cell.Alignment = CellAlignment.MiddleCenter;
                    }

                    for (int r = 0; r < rowStrings.Count; r++)
                    {
                        int rowIdx = r + 1;
                        var cols = rowStrings[r];
                        tb.Rows[rowIdx].Height = layout.RowHeights[rowIdx];

                        for (int c = 0; c < totalCols; c++)
                        {
                            var cell = tb.Cells[rowIdx, c];
                            ApplyCellMargins(tb, rowIdx, c, layout.HorzMargin, layout.VertMargin);
                            cell.TextString = cols[c];
                            cell.TextHeight = layout.DataTextHeight;
                            cell.Alignment = (c == 2) ? CellAlignment.MiddleLeft : CellAlignment.MiddleCenter;
                        }
                    }

                    tb.SuppressRegenerateTable(false);
                    tb.RecomputeTableBlock(true);

                    // Second pass using AutoCAD's actual font layout metrics: guarantee no row is ever shorter than MinimumHeight
                    bool adjustedRowHeight = false;
                    for (int r = 0; r < totalRows; r++)
                    {
                        try
                        {
                            double minNeeded = tb.Rows[r].MinimumHeight;
                            double safeMin = minNeeded + layout.VertMargin * 0.4;
                            if (minNeeded > 0 && tb.Rows[r].Height < safeMin)
                            {
                                tb.Rows[r].Height = safeMin;
                                adjustedRowHeight = true;
                            }
                        }
                        catch { }
                    }
                    if (adjustedRowHeight)
                    {
                        tb.RecomputeTableBlock(true);
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

        private static void ApplyCellMargins(Table tb, int row, int col, double horzMargin, double vertMargin)
        {
            try
            {
                var cell = tb.Cells[row, col];
                cell.Borders.Left.Margin = horzMargin;
                cell.Borders.Right.Margin = horzMargin;
                cell.Borders.Top.Margin = vertMargin;
                cell.Borders.Bottom.Margin = vertMargin;
            }
            catch { }
        }

        private static string ExtractCleanPaperName(string localeName)
        {
            if (string.IsNullOrWhiteSpace(localeName)) return "";
            string suffix = localeName.IndexOf("加长", StringComparison.OrdinalIgnoreCase) >= 0 ? "加长" : "";
            string[] bases = { "A0", "A1", "A2", "A3", "A4" };
            foreach (string b in bases)
            {
                if (localeName.IndexOf(b, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return b + suffix;
                }
            }

            return localeName.Split('(')[0].Trim();
        }
    }
}
