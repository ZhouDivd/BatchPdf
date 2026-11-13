using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using BatchPdf.Core.Models;

namespace BatchPdf.UI.Forms
{
    public enum DrawingIndexAction
    {
        Cancel = 0,
        ExportExcel = 1,
        CreateCadTable = 2
    }

    /// <summary>
    /// Pre-export confirmation and inline editing dialog allowing users to verify and correct
    /// drawing numbers and titles before exporting to Excel (.xlsx) or generating an AutoCAD table.
    /// </summary>
    public class DrawingIndexExportForm : Form
    {
        private readonly List<DrawingIndexExportRow> _rows;
        private float _uiScale = 1.0f;

        private DataGridView _grid;
        private Label _lblSummary;
        private TextBox _txtBatchDate;
        private Button _btnApplyDate;
        private Button _btnCopyTsv;
        private Button _btnExportExcel;
        private Button _btnCreateCadTable;
        private Button _btnCancel;

        public DrawingIndexAction SelectedAction { get; private set; } = DrawingIndexAction.Cancel;
        public IList<DrawingIndexExportRow> EditedRows => _rows;
        public bool HasSheetMetadataChanges { get; private set; }

        public DrawingIndexExportForm(IList<DrawingIndexExportRow> rows)
        {
            _rows = rows != null
                ? rows.Where(r => r != null).Select(CloneRow).ToList()
                : new List<DrawingIndexExportRow>();

            InitializeComponents();
            PopulateGrid();
        }

        private static DrawingIndexExportRow CloneRow(DrawingIndexExportRow src)
        {
            return new DrawingIndexExportRow
            {
                Index = src.Index,
                SourceSheet = src.SourceSheet,
                DrawingNumber = src.DrawingNumber ?? "",
                DrawingName = src.DrawingName ?? "",
                PaperSize = src.PaperSize ?? "",
                Scale = src.Scale ?? "",
                IssueDate = src.IssueDate ?? "",
                Remarks = src.Remarks ?? ""
            };
        }

        private int S(int px) => (int)Math.Round(px * _uiScale);
        private float SF(float px) => px * _uiScale;
        private Padding SP(int l, int t, int r, int b) => new Padding(S(l), S(t), S(r), S(b));
        private Size SS(int w, int h) => new Size(S(w), S(h));

        private void InitDpiScale()
        {
            AutoScaleMode = AutoScaleMode.None;
            float dpiScale = 1.0f;
            try
            {
                if (DeviceDpi > 0) dpiScale = Math.Max(dpiScale, DeviceDpi / 96.0f);
            }
            catch { }

            try
            {
                using (Graphics g = CreateGraphics())
                {
                    if (g.DpiX > 0) dpiScale = Math.Max(dpiScale, g.DpiX / 96.0f);
                }
            }
            catch { }

            Rectangle workArea = Screen.FromPoint(Cursor.Position).WorkingArea;
            float maxScreenScale = 3.0f;
            if (workArea.Width > 400 && workArea.Height > 400)
            {
                float maxScaleW = (workArea.Width * 0.92f) / 960.0f;
                float maxScaleH = (workArea.Height * 0.90f) / 600.0f;
                maxScreenScale = Math.Max(0.85f, Math.Min(maxScaleW, maxScaleH));
            }

            _uiScale = Math.Min(Math.Max(1.0f, dpiScale), maxScreenScale);
            Font = new Font("Microsoft YaHei UI", 12.0f * _uiScale, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        private void InitializeComponents()
        {
            SuspendLayout();
            InitDpiScale();

            Text = "图纸目录核对与导出 (可修改图号/图名)";
            Size = SS(960, 600);
            MinimumSize = SS(780, 460);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            BackColor = Color.White;

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = SP(12, 10, 12, 12),
                ColumnCount = 1,
                RowCount = 3
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            // Top bar: instructions + batch issue date control
            var topPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 2,
                Margin = SP(0, 0, 0, 8)
            };
            topPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            topPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var leftInfoPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                AutoSize = true,
                WrapContents = false,
                Margin = new Padding(0)
            };

            var lblHint = new Label
            {
                Text = "导出前请核对以下图纸的【图号】与【图纸名称】；单击单元格即可修改，修改会同步回主清单。",
                AutoSize = true,
                ForeColor = Color.FromArgb(30, 58, 138),
                Font = new Font("Microsoft YaHei UI", SF(12.0f), FontStyle.Bold, GraphicsUnit.Pixel),
                Margin = SP(0, 0, 0, 2)
            };

            _lblSummary = new Label
            {
                AutoSize = true,
                ForeColor = Color.FromArgb(90, 95, 105),
                Margin = new Padding(0)
            };

            leftInfoPanel.Controls.Add(lblHint);
            leftInfoPanel.Controls.Add(_lblSummary);

            var datePanel = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Anchor = AnchorStyles.Right | AnchorStyles.Top,
                Margin = SP(8, 2, 0, 0)
            };

            var lblDate = new Label
            {
                Text = "统一出图日期:",
                AutoSize = true,
                Margin = SP(0, 5, 4, 0)
            };
            _txtBatchDate = new TextBox
            {
                Text = _rows.Count > 0 ? (_rows[0].IssueDate ?? DateTime.Now.ToString("yyyy.MM")) : DateTime.Now.ToString("yyyy.MM"),
                Width = S(86),
                Margin = SP(0, 2, 4, 0)
            };
            _btnApplyDate = new Button
            {
                Text = "应用全部",
                Size = SS(76, 26),
                UseVisualStyleBackColor = true,
                Margin = new Padding(0)
            };
            _btnApplyDate.Click += OnApplyBatchDateClick;

            datePanel.Controls.Add(lblDate);
            datePanel.Controls.Add(_txtBatchDate);
            datePanel.Controls.Add(_btnApplyDate);

            topPanel.Controls.Add(leftInfoPanel, 0, 0);
            topPanel.Controls.Add(datePanel, 1, 0);

            // Middle DataGridView
            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.CellSelect,
                EditMode = DataGridViewEditMode.EditOnEnter,
                MultiSelect = true,
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                GridColor = Color.FromArgb(218, 222, 228),
                AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.DisplayedCells,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                ColumnHeadersHeight = S(32)
            };
            _grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            _grid.DefaultCellStyle.Padding = SP(4, 3, 4, 3);
            _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(241, 245, 249);
            _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(31, 41, 55);
            _grid.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei UI", SF(12.0f), FontStyle.Bold, GraphicsUnit.Pixel);
            _grid.EnableHeadersVisualStyles = false;

            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "ColIndex",
                HeaderText = "序号",
                Width = S(54),
                ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleCenter, BackColor = Color.FromArgb(248, 250, 252) }
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "ColDrawingNumber",
                HeaderText = "图号 (可改)",
                Width = S(168),
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "ColDrawingName",
                HeaderText = "图纸名称 (可改)",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                MinimumWidth = S(240),
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "ColPaperSize",
                HeaderText = "图幅",
                Width = S(78),
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleCenter }
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "ColScale",
                HeaderText = "比例",
                Width = S(76),
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleCenter }
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "ColIssueDate",
                HeaderText = "出图日期",
                Width = S(88),
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleCenter }
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "ColRemarks",
                HeaderText = "备注",
                Width = S(128),
                SortMode = DataGridViewColumnSortMode.NotSortable
            });

            _grid.CellValueChanged += OnGridCellValueChanged;

            // Bottom action bar
            var bottomPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                ColumnCount = 2,
                Margin = SP(0, 10, 0, 0)
            };
            bottomPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            bottomPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var leftActions = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0)
            };

            _btnCopyTsv = new Button
            {
                Text = "📋 复制表格 (可直接粘贴到在线表格)",
                Size = SS(245, 34),
                UseVisualStyleBackColor = true,
                Margin = new Padding(0)
            };
            _btnCopyTsv.Click += OnCopyTsvClick;
            leftActions.Controls.Add(_btnCopyTsv);

            var rightActions = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Margin = new Padding(0)
            };

            _btnCancel = new Button
            {
                Text = "取消",
                Size = SS(86, 34),
                UseVisualStyleBackColor = true,
                DialogResult = DialogResult.Cancel,
                Margin = SP(8, 0, 0, 0)
            };
            _btnCancel.Click += (s, e) =>
            {
                SelectedAction = DrawingIndexAction.Cancel;
                Close();
            };

            _btnCreateCadTable = new Button
            {
                Text = "📊 生成 CAD 目录表...",
                Size = SS(160, 34),
                UseVisualStyleBackColor = true,
                Margin = SP(8, 0, 0, 0)
            };
            _btnCreateCadTable.Click += (s, e) => CommitAndFinish(DrawingIndexAction.CreateCadTable);

            _btnExportExcel = new Button
            {
                Text = "📥 导出 Excel (.xlsx)...",
                Size = SS(170, 34),
                BackColor = Color.FromArgb(22, 163, 74),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Microsoft YaHei UI", SF(12.0f), FontStyle.Bold, GraphicsUnit.Pixel),
                Margin = new Padding(0)
            };
            _btnExportExcel.FlatAppearance.BorderSize = 0;
            _btnExportExcel.Click += (s, e) => CommitAndFinish(DrawingIndexAction.ExportExcel);

            // RightToLeft flow adds rightmost control first
            rightActions.Controls.Add(_btnCancel);
            rightActions.Controls.Add(_btnCreateCadTable);
            rightActions.Controls.Add(_btnExportExcel);

            bottomPanel.Controls.Add(leftActions, 0, 0);
            bottomPanel.Controls.Add(rightActions, 1, 0);

            root.Controls.Add(topPanel, 0, 0);
            root.Controls.Add(_grid, 0, 1);
            root.Controls.Add(bottomPanel, 0, 2);

            Controls.Add(root);
            CancelButton = _btnCancel;
            ResumeLayout(true);
        }

        private void PopulateGrid()
        {
            _grid.Rows.Clear();
            foreach (var row in _rows)
            {
                int idx = _grid.Rows.Add(
                    row.Index,
                    row.DrawingNumber,
                    row.DrawingName,
                    row.PaperSize,
                    row.Scale,
                    row.IssueDate,
                    row.Remarks);
                DataGridViewRow gridRow = _grid.Rows[idx];
                gridRow.Tag = row;
                gridRow.MinimumHeight = S(28);
                UpdateCellHighlight(gridRow);
            }

            UpdateSummaryLabel();
        }

        private void OnGridCellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _grid.Rows.Count) return;
            DataGridViewRow gridRow = _grid.Rows[e.RowIndex];
            if (!(gridRow.Tag is DrawingIndexExportRow item)) return;

            PullGridRowToModel(gridRow, item);
            UpdateCellHighlight(gridRow);
            UpdateSummaryLabel();
        }

        private static void PullGridRowToModel(DataGridViewRow gridRow, DrawingIndexExportRow item)
        {
            item.DrawingNumber = ReadCell(gridRow, "ColDrawingNumber");
            item.DrawingName = ReadCell(gridRow, "ColDrawingName");
            item.PaperSize = ReadCell(gridRow, "ColPaperSize");
            item.Scale = ReadCell(gridRow, "ColScale");
            item.IssueDate = ReadCell(gridRow, "ColIssueDate");
            item.Remarks = ReadCell(gridRow, "ColRemarks");
        }

        private static string ReadCell(DataGridViewRow row, string colName)
        {
            return Convert.ToString(row.Cells[colName].Value ?? "").Trim();
        }

        private void UpdateCellHighlight(DataGridViewRow gridRow)
        {
            Color missingBg = Color.FromArgb(254, 243, 199);
            gridRow.Cells["ColDrawingNumber"].Style.BackColor =
                string.IsNullOrWhiteSpace(ReadCell(gridRow, "ColDrawingNumber")) ? missingBg : Color.White;
            gridRow.Cells["ColDrawingName"].Style.BackColor =
                string.IsNullOrWhiteSpace(ReadCell(gridRow, "ColDrawingName")) ? missingBg : Color.White;
        }

        private void UpdateSummaryLabel()
        {
            int missingNo = _rows.Count(r => string.IsNullOrWhiteSpace(r.DrawingNumber));
            int missingName = _rows.Count(r => string.IsNullOrWhiteSpace(r.DrawingName));
            if (missingNo == 0 && missingName == 0)
            {
                _lblSummary.Text = string.Format("共 {0} 张图纸，图号与图名均已填写（高亮底色表示空缺项）。", _rows.Count);
                _lblSummary.ForeColor = Color.FromArgb(21, 128, 61);
            }
            else
            {
                _lblSummary.Text = string.Format("共 {0} 张图纸 | 待补充：空白图号 {1} 项、空白图名 {2} 项（黄色高亮可点击补全）。", _rows.Count, missingNo, missingName);
                _lblSummary.ForeColor = Color.FromArgb(180, 83, 9);
            }
        }

        private void OnApplyBatchDateClick(object sender, EventArgs e)
        {
            _grid.EndEdit();
            string newDate = (_txtBatchDate.Text ?? "").Trim();
            foreach (DataGridViewRow gridRow in _grid.Rows)
            {
                gridRow.Cells["ColIssueDate"].Value = newDate;
                if (gridRow.Tag is DrawingIndexExportRow item)
                {
                    item.IssueDate = newDate;
                }
            }
        }

        private void SyncAllEditsToRowsAndSheets()
        {
            _grid.EndEdit();
            bool changed = false;

            foreach (DataGridViewRow gridRow in _grid.Rows)
            {
                if (!(gridRow.Tag is DrawingIndexExportRow item)) continue;
                PullGridRowToModel(gridRow, item);

                if (item.SourceSheet != null)
                {
                    string prevNo = (item.SourceSheet.DrawingNumber ?? "").Trim();
                    string prevName = (item.SourceSheet.DrawingName ?? "").Trim();
                    string newNo = (item.DrawingNumber ?? "").Trim();
                    string newName = (item.DrawingName ?? "").Trim();

                    if (!string.Equals(prevNo, newNo, StringComparison.Ordinal))
                    {
                        item.SourceSheet.DrawingNumber = newNo;
                        changed = true;
                    }
                    if (!string.Equals(prevName, newName, StringComparison.Ordinal))
                    {
                        item.SourceSheet.DrawingName = newName;
                        changed = true;
                    }
                }
            }

            HasSheetMetadataChanges = changed;
        }

        private void OnCopyTsvClick(object sender, EventArgs e)
        {
            SyncAllEditsToRowsAndSheets();
            if (_rows.Count == 0) return;

            var sb = new StringBuilder();
            sb.AppendLine(string.Join("\t", DrawingIndexRowBuilder.Headers));
            foreach (var row in _rows)
            {
                string[] cells = row.ToCellArray();
                for (int i = 0; i < cells.Length; i++)
                {
                    cells[i] = (cells[i] ?? "").Replace("\t", " ").Replace("\r\n", " ").Replace("\n", " ");
                }
                sb.AppendLine(string.Join("\t", cells));
            }

            try
            {
                Clipboard.SetText(sb.ToString());
                MessageBox.Show(
                    this,
                    string.Format("已复制 {0} 行图纸目录到剪贴板，可直接 Ctrl+V 粘贴到 Excel、飞书、腾讯文档或网页在线表格。", _rows.Count),
                    "复制成功",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "复制到剪贴板失败: " + ex.Message, "复制失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void CommitAndFinish(DrawingIndexAction action)
        {
            SyncAllEditsToRowsAndSheets();
            SelectedAction = action;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
