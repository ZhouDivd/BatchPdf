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
            var rows = DrawingIndexRowBuilder.BuildFromSheets(sheets);
            return GenerateDrawingIndexTable(doc, rows, settings, origin, out message);
        }

        public static bool GenerateDrawingIndexTable(
            Document doc,
            IList<DrawingIndexExportRow> rows,
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

            var validRows = rows != null
                ? rows.Where(r => r != null).ToList()
                : new List<DrawingIndexExportRow>();

            if (validRows.Count == 0)
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

                    // Estimate scale factor based on first row's source sheet or scale text, fallback to settings
                    double scaleFactor = ResolveScaleFactor(validRows, settings);
                    string[] headers = DrawingIndexRowBuilder.Headers;

                    var rowStrings = new List<string[]>(validRows.Count);
                    for (int r = 0; r < validRows.Count; r++)
                    {
                        var row = validRows[r];
                        string[] cells = row.ToCellArray();
                        cells[0] = (row.Index > 0 ? row.Index : (r + 1)).ToString();
                        for (int c = 1; c < cells.Length; c++)
                        {
                            cells[c] = (cells[c] ?? "").Replace("\r\n", "\n").Replace("\n", "\\P");
                        }
                        rowStrings.Add(cells);
                    }

                    DrawingIndexTableLayout layout = DrawingIndexTableLayoutService.ComputeLayout(headers, rowStrings, scaleFactor);
                    int totalRows = validRows.Count + 1; // Row 0 = Header
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

                    message = string.Format("已成功生成 CAD 图纸目录表，共 {0} 张图纸。", validRows.Count);
                    return true;
                }
                catch (Exception ex)
                {
                    message = "生成图纸目录失败: " + ex.Message;
                    return false;
                }
            }
        }

        private static double ResolveScaleFactor(IList<DrawingIndexExportRow> rows, BatchPdfSettings settings)
        {
            foreach (var row in rows)
            {
                if (row?.SourceSheet?.ScaleValue is double sv && sv > 0)
                {
                    return sv;
                }
                double? parsed = ScaleParser.TryParseScaleValue(row?.Scale);
                if (parsed.HasValue && parsed.Value > 0)
                {
                    return parsed.Value;
                }
            }

            return (settings != null && settings.DefaultScaleValue > 0)
                ? settings.DefaultScaleValue
                : 100.0;
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
    }
}
