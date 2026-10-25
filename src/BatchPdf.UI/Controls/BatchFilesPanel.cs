using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using BatchPdf.Core.Configuration;
using BatchPdf.Core.Models;
using BatchPdf.Core.Services;
using BatchPdf.Infrastructure.Services;
using BatchPdf.Plot.Services;

namespace BatchPdf.UI.Controls
{
    public class BatchFilesPanel : UserControl
    {
        private readonly BatchPdfSettings _settings;
        private readonly LogService _logService;
        private readonly PaperMediaCatalog _mediaCatalog;
        private readonly float _uiScale;

        private DataGridView _gridFiles;
        private CheckBox _chkIncludeSubfolders;
        private RadioButton _rbSinglePdf;
        private RadioButton _rbMergedPdf;
        private RadioButton _rbDwgDir;
        private RadioButton _rbCustomDir;
        private TextBox _txtCustomDir;
        private Button _btnBrowseCustomDir;
        private CheckBox _chkAutoCrop;
        private ComboBox _cboPlotStyle;
        private ProgressBar _progressBar;
        private Label _lblStatus;
        private TextBox _txtConsoleLogs;
        private Button _btnStartBatch;
        private Button _btnStopBatch;
        private Button _btnOpenOutputDir;
        private Button _btnViewReport;

        private readonly BindingList<BatchDwgItem> _fileItems = new BindingList<BatchDwgItem>();
        private CancellationTokenSource _cts;
        private Process _runningProcess;
        private string _lastReportPath;
        private string _lastOutputDir;

        public class BatchDwgItem : INotifyPropertyChanged
        {
            private bool _isSelected = true;
            private string _status = "等待处理";
            private string _sheets = "-";
            private string _duration = "-";

            public bool IsSelected
            {
                get => _isSelected;
                set { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); }
            }

            public int Index { get; set; }
            public string FilePath { get; set; }
            public string FileName => Path.GetFileName(FilePath);
            public string DirectoryPath => Path.GetDirectoryName(FilePath);
            public long FileSizeBytes { get; set; }
            public string FileSizeFormatted
            {
                get
                {
                    if (FileSizeBytes < 1024) return FileSizeBytes + " B";
                    if (FileSizeBytes < 1024 * 1024) return (FileSizeBytes / 1024.0).ToString("F1") + " KB";
                    return (FileSizeBytes / (1024.0 * 1024.0)).ToString("F2") + " MB";
                }
            }

            public string Status
            {
                get => _status;
                set { _status = value; OnPropertyChanged(nameof(Status)); }
            }

            public string Sheets
            {
                get => _sheets;
                set { _sheets = value; OnPropertyChanged(nameof(Sheets)); }
            }

            public string Duration
            {
                get => _duration;
                set { _duration = value; OnPropertyChanged(nameof(Duration)); }
            }

            public event PropertyChangedEventHandler PropertyChanged;
            protected void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
        }

        public BatchFilesPanel(
            BatchPdfSettings settings,
            LogService logService,
            PaperMediaCatalog mediaCatalog = null,
            float uiScale = 1.0f)
        {
            _settings = settings ?? BatchPdfSettings.CreateDefault();
            _logService = logService ?? new LogService();
            _mediaCatalog = mediaCatalog ?? new PaperMediaCatalog();
            _uiScale = uiScale > 0.5f ? uiScale : 1.0f;

            InitializeLayout();
            RefreshPlotStyles();
        }

        private int S(int px) => (int)Math.Round(px * _uiScale);
        private float SF(float px) => px * _uiScale;
        private Padding SP(int l, int t, int r, int b) => new Padding(S(l), S(t), S(r), S(b));

        private void InitializeLayout()
        {
            AutoScaleMode = AutoScaleMode.None;
            Dock = DockStyle.Fill;
            Font = new Font("Microsoft YaHei UI", 12.0f * _uiScale, FontStyle.Regular, GraphicsUnit.Pixel);

            var mainTable = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = SP(6, 6, 6, 6)
            };
            mainTable.RowStyles.Add(new RowStyle(SizeType.Absolute, SF(40F))); // Top toolbar
            mainTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));    // Middle (Grid + Settings)
            mainTable.RowStyles.Add(new RowStyle(SizeType.Absolute, SF(170F))); // Bottom progress & console

            mainTable.Controls.Add(BuildTopToolbar(), 0, 0);
            mainTable.Controls.Add(BuildMiddleSection(), 0, 1);
            mainTable.Controls.Add(BuildBottomSection(), 0, 2);

            Controls.Add(mainTable);
        }

        private Control BuildTopToolbar()
        {
            var pnl = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = Padding.Empty,
                Padding = SP(0, 2, 0, 2)
            };

            var btnAddFiles = new Button
            {
                Text = "➕ 添加 DWG 文件...",
                Height = S(32),
                AutoSize = true,
                BackColor = Color.FromArgb(235, 245, 255),
                FlatStyle = FlatStyle.System
            };
            btnAddFiles.Click += (s, e) => OnAddFilesClick();

            var btnAddFolder = new Button
            {
                Text = "📁 添加文件夹...",
                Height = S(32),
                AutoSize = true,
                BackColor = Color.FromArgb(235, 245, 255),
                FlatStyle = FlatStyle.System
            };
            btnAddFolder.Click += (s, e) => OnAddFolderClick();

            _chkIncludeSubfolders = new CheckBox
            {
                Text = "包含子文件夹",
                Checked = true,
                Height = S(32),
                AutoSize = true,
                Padding = SP(4, 0, 8, 0)
            };

            var btnSelectAll = new Button
            {
                Text = "全选",
                Height = S(32),
                Width = S(65),
                FlatStyle = FlatStyle.System
            };
            btnSelectAll.Click += (s, e) => SetAllSelection(true);

            var btnInvert = new Button
            {
                Text = "反选",
                Height = S(32),
                Width = S(65),
                FlatStyle = FlatStyle.System
            };
            btnInvert.Click += (s, e) => InvertSelection();

            var btnRemoveSelected = new Button
            {
                Text = "移除选中",
                Height = S(32),
                AutoSize = true,
                FlatStyle = FlatStyle.System
            };
            btnRemoveSelected.Click += (s, e) => RemoveSelectedFiles();

            var btnClearAll = new Button
            {
                Text = "🗑️ 清空",
                Height = S(32),
                Width = S(75),
                FlatStyle = FlatStyle.System
            };
            btnClearAll.Click += (s, e) =>
            {
                if (_fileItems.Count == 0) return;
                if (MessageBox.Show("确定清空列表中的所有 DWG 文件吗？", "清空确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    _fileItems.Clear();
                }
            };

            var lblHint = new Label
            {
                Text = "💡 支持直接从 Windows 资源管理器将 DWG 文件或文件夹拖入下方列表中",
                ForeColor = Color.DimGray,
                AutoSize = true,
                Height = S(32),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = SP(12, 6, 0, 0)
            };

            pnl.Controls.Add(btnAddFiles);
            pnl.Controls.Add(btnAddFolder);
            pnl.Controls.Add(_chkIncludeSubfolders);
            pnl.Controls.Add(btnSelectAll);
            pnl.Controls.Add(btnInvert);
            pnl.Controls.Add(btnRemoveSelected);
            pnl.Controls.Add(btnClearAll);
            pnl.Controls.Add(lblHint);

            return pnl;
        }

        private Control BuildMiddleSection()
        {
            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                FixedPanel = FixedPanel.Panel2
            };

            void SafeAdjustSplitter()
            {
                if (split.Width < 300) return;
                try
                {
                    int p1Min = Math.Max(25, S(160));
                    int p2Min = Math.Max(25, S(240));
                    int maxDist = split.Width - p2Min;
                    if (maxDist > p1Min)
                    {
                        int targetDist = Math.Max(p1Min, Math.Min(maxDist, split.Width - S(300)));
                        split.Panel1MinSize = p1Min;
                        split.Panel2MinSize = p2Min;
                        split.SplitterDistance = targetDist;
                    }
                }
                catch { }
            }

            split.SizeChanged += (s, e) => SafeAdjustSplitter();
            split.HandleCreated += (s, e) =>
            {
                try
                {
                    if (split.IsHandleCreated)
                    {
                        split.BeginInvoke((Action)SafeAdjustSplitter);
                    }
                }
                catch { }
            };

            // Left: DataGridView
            _gridFiles = new DataGridView
            {
                Dock = DockStyle.Fill,
                AutoGenerateColumns = false,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                AllowDrop = true,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = true,
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
                Font = new Font("Microsoft YaHei UI", 12.0f * _uiScale, FontStyle.Regular, GraphicsUnit.Pixel)
            };
            _gridFiles.RowTemplate.Height = S(26);
            _gridFiles.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei UI", 12.0f * _uiScale, FontStyle.Regular, GraphicsUnit.Pixel);
            _gridFiles.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
            _gridFiles.ColumnHeadersDefaultCellStyle.Padding = new Padding(S(2), S(4), S(2), S(4));
            _gridFiles.DefaultCellStyle.Font = new Font("Microsoft YaHei UI", 12.0f * _uiScale, FontStyle.Regular, GraphicsUnit.Pixel);

            try
            {
                typeof(DataGridView).InvokeMember("DoubleBuffered",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.SetProperty,
                    null, _gridFiles, new object[] { true });
            }
            catch { }

            _gridFiles.DragEnter += OnGridDragEnter;
            _gridFiles.DragDrop += OnGridDragDrop;

            // Empty-state watermark when list is empty
            _gridFiles.Paint += (s, e) =>
            {
                if (_fileItems == null || _fileItems.Count == 0)
                {
                    string hintTitle = "📁 暂无待打印的 DWG 图纸";
                    string hintSub = "点击上方「➕ 添加 DWG 文件」或「📁 添加文件夹」\r\n或直接从 Windows 资源管理器将 DWG 文件/文件夹拖拽至此处";

                    var rect = _gridFiles.ClientRectangle;
                    int headerH = _gridFiles.ColumnHeadersVisible ? _gridFiles.ColumnHeadersHeight : 0;
                    rect.Y += headerH;
                    rect.Height -= headerH;
                    if (rect.Height > S(60) && rect.Width > S(100))
                    {
                        using (var brushTitle = new SolidBrush(Color.FromArgb(120, 130, 140)))
                        using (var brushSub = new SolidBrush(Color.FromArgb(160, 170, 180)))
                        using (var fontTitle = new Font("Microsoft YaHei UI", 14.0f * _uiScale, FontStyle.Bold, GraphicsUnit.Pixel))
                        using (var fontSub = new Font("Microsoft YaHei UI", 12.0f * _uiScale, FontStyle.Regular, GraphicsUnit.Pixel))
                        using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        {
                            var titleRect = new Rectangle(rect.X, rect.Y + rect.Height / 2 - S(38), rect.Width, S(30));
                            var subRect = new Rectangle(rect.X, rect.Y + rect.Height / 2, rect.Width, S(52));
                            e.Graphics.DrawString(hintTitle, fontTitle, brushTitle, titleRect, sf);
                            e.Graphics.DrawString(hintSub, fontSub, brushSub, subRect, sf);
                        }
                    }
                }
            };

            var colCheck = new DataGridViewCheckBoxColumn
            {
                DataPropertyName = nameof(BatchDwgItem.IsSelected),
                HeaderText = "选择",
                Width = S(48),
                SortMode = DataGridViewColumnSortMode.Automatic
            };
            colCheck.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;

            var colIndex = new DataGridViewTextBoxColumn
            {
                DataPropertyName = nameof(BatchDwgItem.Index),
                HeaderText = "序号",
                Width = S(48),
                ReadOnly = true
            };
            colIndex.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
            colIndex.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;

            var colName = new DataGridViewTextBoxColumn
            {
                DataPropertyName = nameof(BatchDwgItem.FileName),
                HeaderText = "DWG 文件名",
                Width = S(240),
                ReadOnly = true
            };

            var colStatus = new DataGridViewTextBoxColumn
            {
                DataPropertyName = nameof(BatchDwgItem.Status),
                HeaderText = "状态 / 进度",
                Width = S(120),
                ReadOnly = true
            };

            var colSheets = new DataGridViewTextBoxColumn
            {
                DataPropertyName = nameof(BatchDwgItem.Sheets),
                HeaderText = "图框数",
                Width = S(80),
                ReadOnly = true
            };
            colSheets.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
            colSheets.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;

            var colSize = new DataGridViewTextBoxColumn
            {
                DataPropertyName = nameof(BatchDwgItem.FileSizeFormatted),
                HeaderText = "文件大小",
                Width = S(90),
                ReadOnly = true
            };
            colSize.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight;
            colSize.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;

            var colTime = new DataGridViewTextBoxColumn
            {
                DataPropertyName = nameof(BatchDwgItem.Duration),
                HeaderText = "用时",
                Width = S(80),
                ReadOnly = true
            };
            colTime.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
            colTime.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;

            var colPath = new DataGridViewTextBoxColumn
            {
                DataPropertyName = nameof(BatchDwgItem.DirectoryPath),
                HeaderText = "所在目录",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                MinimumWidth = S(150),
                ReadOnly = true
            };

            _gridFiles.Columns.AddRange(colCheck, colIndex, colName, colStatus, colSheets, colSize, colTime, colPath);
            _gridFiles.DataSource = _fileItems;

            split.Panel1.Controls.Add(_gridFiles);

            // Right: Settings card
            split.Panel2.Controls.Add(BuildSettingsPanel());

            return split;
        }

        private Control BuildSettingsPanel()
        {
            var pnl = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                Padding = SP(6, 4, 6, 4)
            };

            var grpMode = new GroupBox
            {
                Text = "输出模式",
                Dock = DockStyle.Top,
                Height = S(85),
                Padding = SP(8, 8, 8, 8)
            };
            _rbSinglePdf = new RadioButton
            {
                Text = "独立 PDF (一图框一文件)",
                Location = new Point(S(12), S(22)),
                AutoSize = true,
                Checked = (_settings.OutputMode == OutputMode.SinglePdfPerSheet)
            };
            _rbMergedPdf = new RadioButton
            {
                Text = "按 DWG 合并为单多页 PDF",
                Location = new Point(S(12), S(50)),
                AutoSize = true,
                Checked = (_settings.OutputMode == OutputMode.MergedMultiPagePdf)
            };
            grpMode.Controls.Add(_rbSinglePdf);
            grpMode.Controls.Add(_rbMergedPdf);

            var grpDir = new GroupBox
            {
                Text = "输出保存目录",
                Dock = DockStyle.Top,
                Height = S(110),
                Padding = SP(8, 8, 8, 8)
            };
            _rbDwgDir = new RadioButton
            {
                Text = "保存在每个 DWG 同级目录",
                Location = new Point(S(12), S(22)),
                AutoSize = true,
                Checked = true
            };
            _rbCustomDir = new RadioButton
            {
                Text = "统一输出到指定目录:",
                Location = new Point(S(12), S(48)),
                AutoSize = true
            };
            _txtCustomDir = new TextBox
            {
                Location = new Point(S(12), S(74)),
                Width = S(185),
                Enabled = false
            };
            _btnBrowseCustomDir = new Button
            {
                Text = "浏览...",
                Location = new Point(S(202), S(73)),
                Width = S(65),
                Height = S(25),
                Enabled = false,
                FlatStyle = FlatStyle.System
            };
            _rbCustomDir.CheckedChanged += (s, e) =>
            {
                _txtCustomDir.Enabled = _rbCustomDir.Checked;
                _btnBrowseCustomDir.Enabled = _rbCustomDir.Checked;
            };
            _btnBrowseCustomDir.Click += (s, e) =>
            {
                using (var fbd = new FolderBrowserDialog())
                {
                    fbd.Description = "请选择批量 PDF 统一输出目录";
                    if (fbd.ShowDialog() == DialogResult.OK)
                    {
                        _txtCustomDir.Text = fbd.SelectedPath;
                    }
                }
            };

            grpDir.Controls.Add(_rbDwgDir);
            grpDir.Controls.Add(_rbCustomDir);
            grpDir.Controls.Add(_txtCustomDir);
            grpDir.Controls.Add(_btnBrowseCustomDir);

            var grpOpt = new GroupBox
            {
                Text = "优化与出图策略",
                Dock = DockStyle.Top,
                Height = S(110),
                Padding = SP(8, 8, 8, 8)
            };
            _chkAutoCrop = new CheckBox
            {
                Text = "自动裁剪非标图框白边 (无损矢量)",
                Location = new Point(S(12), S(24)),
                AutoSize = true,
                Checked = _settings.AutoCropNonStandardPdf
            };
            var lblStyle = new Label
            {
                Text = "打印样式表 (CTB):",
                Location = new Point(S(12), S(56)),
                AutoSize = true
            };
            _cboPlotStyle = new ComboBox
            {
                Location = new Point(S(12), S(76)),
                Width = S(185),
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            _cboPlotStyle.SelectedIndexChanged += (s, e) =>
            {
                string sel = _cboPlotStyle.SelectedItem?.ToString();
                if (sel == "无 (None)" || sel == "None" || sel == "无")
                {
                    _settings.DefaultPlotStyle = "";
                }
                else if (!string.IsNullOrWhiteSpace(sel))
                {
                    _settings.DefaultPlotStyle = sel;
                }
            };

            var btnRefreshStyles = new Button
            {
                Text = "🔄",
                Location = new Point(S(201), S(75)),
                Width = S(32),
                Height = S(26),
                FlatStyle = FlatStyle.System
            };
            var btnOpenStylesFolder = new Button
            {
                Text = "📂",
                Location = new Point(S(236), S(75)),
                Width = S(32),
                Height = S(26),
                FlatStyle = FlatStyle.System
            };
            var tip = new ToolTip();
            tip.SetToolTip(btnRefreshStyles, "刷新打印样式表列表");
            tip.SetToolTip(btnOpenStylesFolder, "在文件资源管理器中打开 AutoCAD 打印样式表 (Plot Styles) 所在目录");

            btnRefreshStyles.Click += (s, e) => RefreshPlotStyles();
            btnOpenStylesFolder.Click += (s, e) => OpenPlotStylesFolder();

            grpOpt.Controls.Add(_chkAutoCrop);
            grpOpt.Controls.Add(lblStyle);
            grpOpt.Controls.Add(_cboPlotStyle);
            grpOpt.Controls.Add(btnRefreshStyles);
            grpOpt.Controls.Add(btnOpenStylesFolder);

            pnl.Controls.Add(grpOpt);
            pnl.Controls.Add(grpDir);
            pnl.Controls.Add(grpMode);

            return pnl;
        }

        private Control BuildBottomSection()
        {
            var pnl = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 2,
                Padding = SP(0, 4, 0, 0)
            };
            pnl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            pnl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, SF(220F)));
            pnl.RowStyles.Add(new RowStyle(SizeType.Absolute, SF(32F)));
            pnl.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            // Row 0, Col 0: Progress bar + label
            var pnlProgress = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = Padding.Empty
            };
            _progressBar = new ProgressBar
            {
                Width = S(280),
                Height = S(22),
                Margin = SP(0, 4, 10, 0)
            };
            _lblStatus = new Label
            {
                Text = "就绪。请添加或拖拽 DWG 文件到列表中，然后点击“开始批量出图”。",
                AutoSize = true,
                Height = S(26),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.DarkSlateGray
            };
            pnlProgress.Controls.Add(_progressBar);
            pnlProgress.Controls.Add(_lblStatus);

            // Row 0, Col 1: Action buttons
            var pnlActions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Margin = Padding.Empty
            };
            _btnStartBatch = new Button
            {
                Text = "🚀 开始批量出图",
                Height = S(28),
                AutoSize = true,
                BackColor = Color.FromArgb(0, 120, 215),
                ForeColor = Color.White,
                Font = new Font("Microsoft YaHei UI", 13.0f * _uiScale, FontStyle.Bold, GraphicsUnit.Pixel),
                FlatStyle = FlatStyle.Flat
            };
            _btnStartBatch.Click += async (s, e) => await StartBatchPlotAsync();

            _btnStopBatch = new Button
            {
                Text = "⏹️ 停止",
                Height = S(28),
                Width = S(75),
                Enabled = false,
                FlatStyle = FlatStyle.System
            };
            _btnStopBatch.Click += (s, e) => StopBatchPlot();

            pnlActions.Controls.Add(_btnStartBatch);
            pnlActions.Controls.Add(_btnStopBatch);

            // Row 1, Col 0: Real-time Console Log Box
            _txtConsoleLogs = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(248, 249, 250),
                Font = new Font("Consolas", 12.0f * _uiScale, FontStyle.Regular, GraphicsUnit.Pixel),
                Margin = SP(0, 4, 6, 0)
            };

            // Row 1, Col 1: Post-run buttons
            var pnlPostButtons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Margin = SP(0, 4, 0, 0)
            };
            _btnOpenOutputDir = new Button
            {
                Text = "📂 打开输出目录",
                Height = S(32),
                Width = S(200),
                Enabled = false,
                FlatStyle = FlatStyle.System,
                Margin = SP(0, 2, 0, 4)
            };
            _btnOpenOutputDir.Click += (s, e) =>
            {
                if (!string.IsNullOrEmpty(_lastOutputDir) && Directory.Exists(_lastOutputDir))
                {
                    Process.Start("explorer.exe", _lastOutputDir);
                }
            };

            _btnViewReport = new Button
            {
                Text = "📋 查看本次报告 (CSV)",
                Height = S(32),
                Width = S(200),
                Enabled = false,
                FlatStyle = FlatStyle.System,
                Margin = SP(0, 2, 0, 4)
            };
            _btnViewReport.Click += (s, e) =>
            {
                if (!string.IsNullOrEmpty(_lastReportPath) && File.Exists(_lastReportPath))
                {
                    Process.Start(_lastReportPath);
                }
            };

            pnlPostButtons.Controls.Add(_btnOpenOutputDir);
            pnlPostButtons.Controls.Add(_btnViewReport);

            pnl.Controls.Add(pnlProgress, 0, 0);
            pnl.Controls.Add(pnlActions, 1, 0);
            pnl.Controls.Add(_txtConsoleLogs, 0, 1);
            pnl.Controls.Add(pnlPostButtons, 1, 1);

            return pnl;
        }

        #region Drag and Drop & File Management

        private void OnGridDragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effect = DragDropEffects.Copy;
            }
        }

        private void OnGridDragDrop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files != null && files.Length > 0)
                {
                    AddFilesOrDirectories(files, _chkIncludeSubfolders.Checked);
                }
            }
        }

        private void OnAddFilesClick()
        {
            using (var ofd = new OpenFileDialog())
            {
                ofd.Title = "选择要批量打印的 AutoCAD DWG 图纸";
                ofd.Filter = "AutoCAD 图纸 (*.dwg)|*.dwg|所有文件 (*.*)|*.*";
                ofd.Multiselect = true;
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    AddFilesOrDirectories(ofd.FileNames, false);
                }
            }
        }

        private void OnAddFolderClick()
        {
            using (var fbd = new FolderBrowserDialog())
            {
                fbd.Description = "请选择包含 DWG 图纸的文件夹";
                if (fbd.ShowDialog() == DialogResult.OK)
                {
                    AddFilesOrDirectories(new[] { fbd.SelectedPath }, _chkIncludeSubfolders.Checked);
                }
            }
        }

        public void AddFilesOrDirectories(IEnumerable<string> paths, bool includeSubfolders)
        {
            var discovered = new List<string>();
            foreach (string p in paths)
            {
                if (File.Exists(p))
                {
                    if (string.Equals(Path.GetExtension(p), ".dwg", StringComparison.OrdinalIgnoreCase))
                    {
                        discovered.Add(p);
                    }
                }
                else if (Directory.Exists(p))
                {
                    var opt = includeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                    try
                    {
                        discovered.AddRange(Directory.GetFiles(p, "*.dwg", opt));
                    }
                    catch { }
                }
            }

            var existing = new HashSet<string>(_fileItems.Select(x => x.FilePath), StringComparer.OrdinalIgnoreCase);
            int addedCount = 0;
            foreach (string dwg in discovered)
            {
                if (!existing.Contains(dwg))
                {
                    existing.Add(dwg);
                    long len = 0;
                    try { len = new FileInfo(dwg).Length; } catch { }

                    _fileItems.Add(new BatchDwgItem
                    {
                        Index = _fileItems.Count + 1,
                        FilePath = dwg,
                        FileSizeBytes = len
                    });
                    addedCount++;
                }
            }

            RenumberItems();
            _lblStatus.Text = $"已添加 {addedCount} 个 DWG 文件，列表共 {_fileItems.Count} 个。";
        }

        private void SetAllSelection(bool selected)
        {
            foreach (var it in _fileItems) it.IsSelected = selected;
            _gridFiles.Refresh();
        }

        private void InvertSelection()
        {
            foreach (var it in _fileItems) it.IsSelected = !it.IsSelected;
            _gridFiles.Refresh();
        }

        private void RemoveSelectedFiles()
        {
            var toRemove = _fileItems.Where(it => it.IsSelected).ToList();
            foreach (var it in toRemove) _fileItems.Remove(it);
            RenumberItems();
        }

        private void RenumberItems()
        {
            for (int i = 0; i < _fileItems.Count; i++)
            {
                _fileItems[i].Index = i + 1;
            }
        }

        #endregion

        #region Execution

        private async Task StartBatchPlotAsync()
        {
            var selectedItems = _fileItems.Where(it => it.IsSelected).ToList();
            if (selectedItems.Count == 0)
            {
                MessageBox.Show("请先勾选需要出图的 DWG 文件！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Find headless script
            string scriptPath = FindHeadlessScript();
            if (string.IsNullOrEmpty(scriptPath) || !File.Exists(scriptPath))
            {
                MessageBox.Show("未找到无头打印核心脚本 BatchPdfHeadless.ps1，请确保 tools/headless 目录完整！", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            string outDir = null;
            if (_rbCustomDir.Checked && !string.IsNullOrWhiteSpace(_txtCustomDir.Text))
            {
                outDir = _txtCustomDir.Text.Trim();
                if (!Directory.Exists(outDir))
                {
                    try { Directory.CreateDirectory(outDir); } catch { }
                }
            }

            string outputMode = _rbMergedPdf.Checked ? "merged" : "single";
            string autoCrop = _chkAutoCrop.Checked ? "true" : "false";
            string plotStyle = _cboPlotStyle.SelectedItem?.ToString();
            if (string.Equals(plotStyle, "无", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(plotStyle, "None", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(plotStyle, "无 (None)", StringComparison.OrdinalIgnoreCase))
            {
                plotStyle = "None";
            }
            else if (string.IsNullOrWhiteSpace(plotStyle))
            {
                plotStyle = "monochrome.ctb";
            }

            _btnStartBatch.Enabled = false;
            _btnStopBatch.Enabled = true;
            _btnOpenOutputDir.Enabled = false;
            _btnViewReport.Enabled = false;
            _progressBar.Value = 0;
            _progressBar.Maximum = selectedItems.Count;
            _txtConsoleLogs.Clear();

            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;

            AppendLog("================ 开始批量出图任务 ================");
            AppendLog($"待处理 DWG: {selectedItems.Count} 个 | 模式: {outputMode} | 裁白边: {autoCrop} | 样式: {plotStyle}");
            if (!string.IsNullOrEmpty(outDir)) AppendLog($"统一输出目录: {outDir}");

            int processedCount = 0;
            var swTotal = Stopwatch.StartNew();

            try
            {
                for (int i = 0; i < selectedItems.Count; i++)
                {
                    if (token.IsCancellationRequested) break;

                    var item = selectedItems[i];
                    item.Status = "正在处理...";
                    _lblStatus.Text = $"正在处理 ({i + 1}/{selectedItems.Count}): {item.FileName}";

                    var swFile = Stopwatch.StartNew();
                    bool success = await RunHeadlessForSingleDwgAsync(scriptPath, item.FilePath, outDir, outputMode, autoCrop, plotStyle, token);
                    swFile.Stop();

                    item.Duration = $"{swFile.Elapsed.TotalSeconds:F1}s";
                    if (success)
                    {
                        item.Status = "已完成";
                        processedCount++;
                    }
                    else
                    {
                        item.Status = token.IsCancellationRequested ? "已中止" : "出图失败";
                    }

                    _progressBar.Value = i + 1;
                }

                swTotal.Stop();
                if (token.IsCancellationRequested)
                {
                    AppendLog($"\n[中止] 用户已手动中止任务。已完成: {processedCount}/{selectedItems.Count}");
                    _lblStatus.Text = $"任务已中止。已完成 {processedCount}/{selectedItems.Count} 个文件。";
                }
                else
                {
                    AppendLog($"\n[全部完成] 耗时 {swTotal.Elapsed.TotalSeconds:F1}s，成功完成 {processedCount}/{selectedItems.Count} 个 DWG 出图！");
                    _lblStatus.Text = $"批量出图完成！成功: {processedCount}，用时: {swTotal.Elapsed.TotalSeconds:F1}s。";
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[异常] 任务异常终止: {ex.Message}");
                _lblStatus.Text = "执行出错: " + ex.Message;
            }
            finally
            {
                _btnStartBatch.Enabled = true;
                _btnStopBatch.Enabled = false;

                _lastOutputDir = !string.IsNullOrEmpty(outDir) ? outDir : Path.GetDirectoryName(selectedItems[0].FilePath);
                _btnOpenOutputDir.Enabled = Directory.Exists(_lastOutputDir);

                // Look for generated CSV report
                try
                {
                    string searchDir = _lastOutputDir;
                    if (Directory.Exists(searchDir))
                    {
                        var rep = Directory.GetFiles(searchDir, "BatchPdf_无头打印报告_*.csv").OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
                        if (rep != null)
                        {
                            _lastReportPath = rep;
                            _btnViewReport.Enabled = true;
                        }
                    }
                }
                catch { }
            }
        }

        private Task<bool> RunHeadlessForSingleDwgAsync(
            string scriptPath,
            string dwgPath,
            string outDir,
            string outputMode,
            string autoCrop,
            string plotStyle,
            CancellationToken token)
        {
            return Task.Run(() =>
            {
                var argsBuilder = new StringBuilder();
                argsBuilder.Append("-NoProfile -ExecutionPolicy Bypass ");
                argsBuilder.AppendFormat("-File \"{0}\" ", scriptPath);
                argsBuilder.AppendFormat("-Paths \"{0}\" ", dwgPath);
                argsBuilder.AppendFormat("-OutputMode {0} ", outputMode);
                argsBuilder.AppendFormat("-AutoCrop {0} ", autoCrop);
                argsBuilder.AppendFormat("-PlotStyle \"{0}\" ", plotStyle);
                argsBuilder.Append("-NoPause ");
                if (!string.IsNullOrEmpty(outDir))
                {
                    argsBuilder.AppendFormat("-OutputDir \"{0}\" ", outDir);
                }

                // Pass the current AutoCAD directory and current plugin DLL so the headless runner never fails
                string acadDir = null;
                try
                {
                    acadDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName);
                }
                catch { }
                if (string.IsNullOrEmpty(acadDir) || !File.Exists(Path.Combine(acadDir, "accoreconsole.exe")))
                {
                    acadDir = AppDomain.CurrentDomain.BaseDirectory;
                }
                if (!string.IsNullOrEmpty(acadDir) && File.Exists(Path.Combine(acadDir, "accoreconsole.exe")))
                {
                    argsBuilder.AppendFormat("-AcadDir \"{0}\" ", acadDir.TrimEnd('\\', '/'));
                }

                string currentDll = null;
                try
                {
                    currentDll = typeof(BatchFilesPanel).Assembly.Location;
                }
                catch { }
                if (!string.IsNullOrEmpty(currentDll) && File.Exists(currentDll))
                {
                    argsBuilder.AppendFormat("-PluginDll \"{0}\" ", currentDll);
                }

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = argsBuilder.ToString(),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                try
                {
                    using (var proc = new Process { StartInfo = psi })
                    {
                        _runningProcess = proc;
                        proc.OutputDataReceived += (s, e) =>
                        {
                            if (!string.IsNullOrWhiteSpace(e.Data))
                            {
                                AppendLog("  " + e.Data);
                            }
                        };
                        proc.ErrorDataReceived += (s, e) =>
                        {
                            if (!string.IsNullOrWhiteSpace(e.Data))
                            {
                                AppendLog("  [ERR] " + e.Data);
                            }
                        };

                        proc.Start();
                        proc.BeginOutputReadLine();
                        proc.BeginErrorReadLine();

                        while (!proc.WaitForExit(500))
                        {
                            if (token.IsCancellationRequested)
                            {
                                try { proc.Kill(); } catch { }
                                return false;
                            }
                        }

                        return proc.ExitCode == 0;
                    }
                }
                catch (Exception ex)
                {
                    AppendLog($"  [进程启动失败] {ex.Message}");
                    return false;
                }
                finally
                {
                    _runningProcess = null;
                }
            }, token);
        }

        private void StopBatchPlot()
        {
            if (_cts != null && !_cts.IsCancellationRequested)
            {
                _cts.Cancel();
                try
                {
                    _runningProcess?.Kill();
                }
                catch { }
                _btnStopBatch.Enabled = false;
                _lblStatus.Text = "正在终止任务...";
            }
        }

        private void AppendLog(string message)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(AppendLog), message);
                return;
            }

            _txtConsoleLogs.AppendText(message + Environment.NewLine);
            _txtConsoleLogs.SelectionStart = _txtConsoleLogs.TextLength;
            _txtConsoleLogs.ScrollToCaret();
        }

        private string FindHeadlessScript()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string assemblyDir = Path.GetDirectoryName(typeof(BatchFilesPanel).Assembly.Location) ?? "";
            string[] candidates = new string[]
            {
                Path.Combine(assemblyDir, "BatchPdfHeadless.ps1"),
                Path.Combine(assemblyDir, "tools", "headless", "BatchPdfHeadless.ps1"),
                Path.Combine(assemblyDir, "..", "BatchPdfHeadless.ps1"),
                Path.Combine(assemblyDir, "..", "tools", "headless", "BatchPdfHeadless.ps1"),
                Path.Combine(assemblyDir, "..", "..", "BatchPdfHeadless.ps1"),
                Path.Combine(assemblyDir, "..", "..", "tools", "headless", "BatchPdfHeadless.ps1"),
                Path.Combine(baseDir, "BatchPdfHeadless.ps1"),
                Path.Combine(baseDir, "tools", "headless", "BatchPdfHeadless.ps1"),
                @"C:\ProgramData\Autodesk\ApplicationPlugins\BatchPdf.bundle\BatchPdfHeadless.ps1",
                @"C:\ProgramData\Autodesk\ApplicationPlugins\BatchPdf.bundle\tools\headless\BatchPdfHeadless.ps1",
                @"D:\01AIcreatevIDEO\CADBPLOT\BatchPdfHeadless.ps1",
                @"D:\01AIcreatevIDEO\CADBPLOT\tools\headless\BatchPdfHeadless.ps1"
            };

            foreach (var c in candidates)
            {
                try
                {
                    string full = Path.GetFullPath(c);
                    if (File.Exists(full)) return full;
                }
                catch { }
            }

            return null;
        }

        public void RefreshPlotStyles(IEnumerable<string> externalStyles = null)
        {
            if (_cboPlotStyle == null) return;

            var styles = new List<string>();
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (externalStyles != null)
            {
                foreach (string s in externalStyles)
                {
                    if (!string.IsNullOrWhiteSpace(s) && set.Add(s.Trim()))
                    {
                        styles.Add(s.Trim());
                    }
                }
            }

            if (_mediaCatalog != null)
            {
                var catStyles = _mediaCatalog.GetAvailablePlotStyleSheets(null);
                foreach (string s in catStyles)
                {
                    if (!string.IsNullOrWhiteSpace(s) && set.Add(s.Trim()))
                    {
                        styles.Add(s.Trim());
                    }
                }
            }

            if (set.Add("monochrome.ctb")) styles.Add("monochrome.ctb");
            if (set.Add("acad.ctb")) styles.Add("acad.ctb");
            if (set.Add("grayscale.ctb")) styles.Add("grayscale.ctb");

            string currentSelected = _cboPlotStyle.SelectedItem?.ToString();
            if (string.IsNullOrWhiteSpace(currentSelected))
            {
                currentSelected = _settings?.DefaultPlotStyle;
            }

            _cboPlotStyle.BeginUpdate();
            try
            {
                _cboPlotStyle.Items.Clear();
                _cboPlotStyle.Items.Add("无 (None)");

                foreach (string s in styles)
                {
                    _cboPlotStyle.Items.Add(s);
                }

                int selectIdx = -1;
                if (!string.IsNullOrWhiteSpace(currentSelected))
                {
                    for (int i = 0; i < _cboPlotStyle.Items.Count; i++)
                    {
                        if (string.Equals(_cboPlotStyle.Items[i].ToString(), currentSelected, StringComparison.OrdinalIgnoreCase))
                        {
                            selectIdx = i;
                            break;
                        }
                    }
                }

                if (selectIdx < 0)
                {
                    for (int i = 0; i < _cboPlotStyle.Items.Count; i++)
                    {
                        if (string.Equals(_cboPlotStyle.Items[i].ToString(), "monochrome.ctb", StringComparison.OrdinalIgnoreCase))
                        {
                            selectIdx = i;
                            break;
                        }
                    }
                }

                _cboPlotStyle.SelectedIndex = selectIdx >= 0 ? selectIdx : 0;
            }
            finally
            {
                _cboPlotStyle.EndUpdate();
            }
        }

        private void OpenPlotStylesFolder()
        {
            try
            {
                string path = null;

                // 1. Check registry PrinterStyleSheetDir
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
                                                        path = dir;
                                                        break;
                                                    }
                                                }
                                            }
                                            if (!string.IsNullOrEmpty(path)) break;
                                        }
                                        if (!string.IsNullOrEmpty(path)) break;
                                    }
                                }
                                if (!string.IsNullOrEmpty(path)) break;
                            }
                        }
                    }
                }
                catch { }

                if (string.IsNullOrEmpty(path))
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
                                                            path = trimmed;
                                                            break;
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
                    catch { }
                }

                if (string.IsNullOrEmpty(path))
                {
                    string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    string autodeskDir = Path.Combine(appData, "Autodesk");
                    if (Directory.Exists(autodeskDir))
                    {
                        var dirs = Directory.GetDirectories(autodeskDir, "Plot Styles", SearchOption.AllDirectories);
                        if (dirs.Length > 0)
                        {
                            path = dirs[0];
                        }
                    }
                }

                if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                {
                    Process.Start("explorer.exe", path);
                }
                else
                {
                    MessageBox.Show(this, "未能定位到 AutoCAD 打印样式表存放目录，请检查 AutoCAD 安装状态。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "打开目录失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        #endregion
    }
}
