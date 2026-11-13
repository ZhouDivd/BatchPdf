using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using AcDb = Autodesk.AutoCAD.DatabaseServices;
using AcEd = Autodesk.AutoCAD.EditorInput;
using BatchPdf.AutoCAD.Services;
using BatchPdf.Core.Configuration;
using BatchPdf.Core.Models;
using BatchPdf.Core.Services;
using BatchPdf.Infrastructure.Services;
using BatchPdf.Plot.Services;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;
using CoreApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace BatchPdf.UI.Forms
{
    public class BatchPdfForm : Form
    {
        private Document _doc;
        private readonly ConfigService _configService;
        private readonly LogService _logService;
        private readonly PaperMediaCatalog _mediaCatalog;
        private readonly FrameScanner _frameScanner;
        private readonly CadInteractionService _cadInteraction;
        private readonly PaperMatcher _paperMatcher;
        private readonly NamingService _namingService;
        private readonly SheetPreparationService _sheetPrepService;
        private readonly PlotSettingsBuilder _plotSettingsBuilder;
        private readonly PlotJobRunner _plotJobRunner;
        private readonly RectangleGeometryAnalyzer _geometryAnalyzer = new RectangleGeometryAnalyzer();

        private BatchPdfSettings _settings;
        private DeviceCheckResult _deviceCheck;
        private List<PaperMedia> _availableMedia = new List<PaperMedia>();
        private List<string> _availablePlotStyles = new List<string>();
        private List<PrintSheet> _sheets = new List<PrintSheet>();
        private Extents2D? _searchRegion;
        private bool _isUpdatingUi;
        private bool _cancelRequested;
        private float _uiScale = 1.0f;
        private TableLayoutPanel _mainLayout;
        private Control _topSettingsSection;
        private Button _btnToggleCollapse;
        private bool _isWindowCollapsed;
        private Size _expandedWindowSize;
        private Size _expandedMinSize;
        private static Action _pendingCommandAction;

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        public static void ExecutePendingCommandAction()
        {
            Action act = _pendingCommandAction;
            _pendingCommandAction = null;
            act?.Invoke();
        }

        private void RunInCommandContext(Action action, bool hideForm = false)
        {
            if (action == null) return;
            Document activeDoc = CoreApp.DocumentManager.MdiActiveDocument ?? _doc;
            if (activeDoc == null) return;
            _doc = activeDoc;

            if (!CoreApp.DocumentManager.IsApplicationContext)
            {
                if (hideForm) Hide();
                try
                {
                    action();
                }
                finally
                {
                    if (hideForm && !IsDisposed)
                    {
                        Show();
                        Activate();
                    }
                }
                return;
            }

            if (hideForm)
            {
                Hide();
            }

            _pendingCommandAction = () =>
            {
                try
                {
                    action();
                }
                finally
                {
                    if (hideForm && !IsDisposed)
                    {
                        Show();
                        Activate();
                    }
                }
            };

            try
            {
                if (AcadApp.MainWindow != null && AcadApp.MainWindow.Handle != IntPtr.Zero)
                {
                    SetForegroundWindow(AcadApp.MainWindow.Handle);
                    SetFocus(AcadApp.MainWindow.Handle);
                }
            }
            catch { }

            string cmdPrefix = string.IsNullOrEmpty(activeDoc.CommandInProgress) ? "" : "\u0003\u0003";
            activeDoc.SendStringToExecute(cmdPrefix + "BPDF_EXEC ", true, false, false);
        }

        public void AttachToDocument(Document doc)
        {
            if (doc != null && doc != _doc)
            {
                try { _cadInteraction.ClearSheetOrderTransients(_doc); } catch { }
                _doc = doc;
                _searchRegion = null;
                PopulateLayerAndBlockDropdowns();
                UpdateSearchRegionUiState();
                RefreshPlotStyleList();
                if (_txtMergedFileName != null && _settings != null)
                {
                    _txtMergedFileName.Text = _namingService.FormatMergedFileName(_settings.MergedFileNameTemplate, _doc?.Name);
                }
                if (_settings != null && _settings.RecognitionMode != FrameRecognitionMode.ManualPick)
                {
                    ScanModelSpaceFrames();
                }
            }
        }

        // 1. Left Top: 图框识别 & 图层与搜图范围 & 图纸排列顺序
        private RadioButton _rbModeManualPick;
        private RadioButton _rbModeByLayer;
        private RadioButton _rbModeByBlock;
        private CheckBox _chkIncludeNonPlottable;
        private CheckBox _chkIncludePaperSpaceLayouts;

        private Button _btnPickLayerOrBlock;
        private Button _btnClearLayerBlock;
        private ComboBox _cmbLayerList;
        private ComboBox _cmbBlockList;
        private Label _lblSearchRegionStatus;
        private Button _btnFullRegion;

        private RadioButton _rbSortRowFirst;
        private RadioButton _rbSortColFirst;
        private RadioButton _rbSortSelection;
        private RadioButton _rbDirLeftToRight;
        private RadioButton _rbDirRightToLeft;
        private RadioButton _rbDirTopToBottom;
        private RadioButton _rbDirBottomToTop;
        private Label _lblSortOrderDesc;

        private Button _btnHighlightFrames;
        private Button _btnSelectSearchRegion;
        private Button _btnRescan;
        private Label _lblRecognizedCount;

        // 2. Right Top: 打印机/样式 & 纸张/比例 & 打印偏移/方向 & 打印输出
        private Label _lblDeviceStatus;
        private Button _btnRefreshPc3;
        private ComboBox _cmbPlotStyle;
        private ComboBox _cmbColorMode;
        private NumericUpDown _nudCopies;
        private CheckBox _chkPrintLineweights;
        private CheckBox _chkUsePlotStyle;

        private ComboBox _cmbManualPaper;
        private Button _btnFilterPaper;
        private Button _btnApplyManualPaper;
        private CheckBox _chkFitToPaper;
        private CheckBox _chkAutoMatchPaper;
        private CheckBox _chkUseCustomScale;
        private TextBox _txtScaleNum;
        private TextBox _txtScaleDen;
        private Button _btnApplyScaleToSelected;

        private CheckBox _chkCenterPlot;
        private TextBox _txtOffsetX;
        private TextBox _txtOffsetY;
        private RadioButton _rbOrientAuto;
        private RadioButton _rbOrientPortrait;
        private RadioButton _rbOrientLandscape;
        private CheckBox _chkUpsideDown;

        private RadioButton _rbSinglePdf;
        private RadioButton _rbMergedPdf;
        private CheckBox _chkSaveToDwgDir;
        private CheckBox _chkSaveToCustomDir;
        private TextBox _txtOutputDir;
        private Button _btnBrowseDir;
        private Button _btnOpenOutputDir;
        private CheckBox _chkAppendPaperSize;
        private TextBox _txtFileNameTemplate;
        private TextBox _txtMergedFileName;
        private ComboBox _cmbDuplicatePolicy;
        private CheckBox _chkAutoCropPdf;
        private TextBox _txtCropMarginMm;
        private Button _btnCropExistingPdfs;
        private string _lastMergedOutputFilePath;

        // 3. Middle: Grid toolbar & Sheet DataGridView
        private DataGridView _gridSheets;

        // 4. Bottom: Confirm, Page Stats, Print, Close, Progress
        private CheckBox _chkConfirmPageCount;
        private Button _btnPageStats;
        private Button _btnRetryFailed;
        private Button _btnStartPlot;
        private Button _btnClose;
        private Label _lblSummaryCounts;
        private Label _lblCurrentTaskStatus;
        private ProgressBar _progressBar;
        private LinkLabel _lnkOpenLog;
        private BatchPdf.UI.Controls.BatchFilesPanel _batchFilesPanel;

        public BatchPdfForm(
            Document doc,
            ConfigService configService,
            LogService logService)
        {
            _doc = doc;
            _configService = configService ?? new ConfigService();
            _logService = logService ?? new LogService();
            _mediaCatalog = new PaperMediaCatalog();
            _frameScanner = new FrameScanner();
            _cadInteraction = new CadInteractionService();
            _paperMatcher = new PaperMatcher();
            _namingService = new NamingService();
            _sheetPrepService = new SheetPreparationService(_paperMatcher, _namingService);
            _plotSettingsBuilder = new PlotSettingsBuilder(_mediaCatalog);
            _plotJobRunner = new PlotJobRunner(_mediaCatalog, _plotSettingsBuilder, _namingService, _logService);

            _settings = _configService.LoadSettings();

            InitializeComponents();
            InitializeEnvironmentAndScan();
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
                IntPtr acadHwnd = AcadApp.MainWindow != null ? AcadApp.MainWindow.Handle : IntPtr.Zero;
                if (acadHwnd != IntPtr.Zero)
                {
                    uint winDpi = GetDpiForWindow(acadHwnd);
                    if (winDpi >= 96 && winDpi <= 480)
                    {
                        dpiScale = Math.Max(dpiScale, winDpi / 96.0f);
                    }
                    using (Graphics g = Graphics.FromHwnd(acadHwnd))
                    {
                        if (g.DpiX > 0) dpiScale = Math.Max(dpiScale, g.DpiX / 96.0f);
                    }
                }
            }
            catch { }

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
                float maxScaleW = (workArea.Width * 0.95f) / 1300.0f;
                float maxScaleH = (workArea.Height * 0.92f) / 860.0f;
                maxScreenScale = Math.Max(0.85f, Math.Min(maxScaleW, maxScaleH));
            }

            _uiScale = Math.Min(Math.Max(1.0f, dpiScale), maxScreenScale);

            // Use GraphicsUnit.Pixel so Font pixel height is 100% locked to _uiScale (12px at 1.0x = 9pt at 96 DPI),
            // completely immune to GDI Point-to-Pixel DPI virtualization mismatches on 4K displays.
            Font = new Font("Microsoft YaHei UI", 12.0f * _uiScale, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            // Prevent WinForms default partial scaling from desynchronizing TableLayoutPanel RowStyles/ColumnStyles
        }

        private void InitializeComponents()
        {
            SuspendLayout();
            InitDpiScale();

#if ACAD2020
            Text = "批量打印 PDF (DWG To PDF.pc3) - AutoCAD 2020 [非模态：可随时直接操作 CAD 视图]";
#else
            Text = "批量打印 PDF (DWG To PDF.pc3) - AutoCAD 2022 [非模态：可随时直接操作 CAD 视图]";
#endif
            Rectangle workArea = Screen.FromPoint(Cursor.Position).WorkingArea;
            int targetW = S(1300);
            int targetH = S(860);
            if (workArea.Width > 400 && workArea.Height > 400)
            {
                targetW = Math.Min(targetW, (int)(workArea.Width * 0.96));
                targetH = Math.Min(targetH, (int)(workArea.Height * 0.94));
            }
            Size = new Size(targetW, targetH);
            MinimumSize = new Size(Math.Min(targetW, S(1140)), Math.Min(targetH, S(740)));
            _expandedWindowSize = Size;
            _expandedMinSize = MinimumSize;
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;

            _mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = SP(8, 8, 8, 8)
            };
            _mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, SF(396F))); // Top TSSD-style settings panels
            _mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, SF(38F)));  // Grid action bar
            _mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));      // Sheet audit DataGridView
            _mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, SF(74F)));  // Bottom bar ([打印前确认页数] [页数统计] [打印(P)] [关闭])

            _topSettingsSection = BuildTopSettingsSection();
            _mainLayout.Controls.Add(_topSettingsSection, 0, 0);
            _mainLayout.Controls.Add(BuildGridToolbar(), 0, 1);
            _mainLayout.Controls.Add(BuildSheetGridPanel(), 0, 2);
            _mainLayout.Controls.Add(BuildBottomBar(), 0, 3);

            var rootTab = new TabControl
            {
                Dock = DockStyle.Fill,
                Font = new Font("Microsoft YaHei UI", 12.5f * _uiScale, FontStyle.Regular, GraphicsUnit.Pixel),
                Padding = new Point(S(12), S(5))
            };

            var tabCurrent = new TabPage("📑 当前图纸出图");
            tabCurrent.Controls.Add(_mainLayout);

            _batchFilesPanel = new BatchPdf.UI.Controls.BatchFilesPanel(_settings, _logService, _mediaCatalog, _uiScale)
            {
                Dock = DockStyle.Fill
            };
            var tabBatch = new TabPage("📁 多文档/多文件夹批量出图");
            tabBatch.Controls.Add(_batchFilesPanel);

            rootTab.TabPages.Add(tabCurrent);
            rootTab.TabPages.Add(tabBatch);

            Controls.Add(rootTab);

            KeyDown += OnFormKeyDown;
            FormClosing += (s, e) =>
            {
                SaveCurrentUiToSettings();
                try { _cadInteraction.ClearSheetOrderTransients(_doc); } catch { }
            };
            ResumeLayout(true);
        }

        private void ToggleWindowCollapse()
        {
            SuspendLayout();
            try
            {
                if (!_isWindowCollapsed)
                {
                    if (WindowState == FormWindowState.Maximized)
                    {
                        WindowState = FormWindowState.Normal;
                    }
                    _expandedWindowSize = Size;
                    _expandedMinSize = MinimumSize;

                    _topSettingsSection.Visible = false;
                    _gridSheets.Visible = false;
                    _mainLayout.RowStyles[0].SizeType = SizeType.Absolute;
                    _mainLayout.RowStyles[0].Height = 0F;
                    _mainLayout.RowStyles[2].SizeType = SizeType.Absolute;
                    _mainLayout.RowStyles[2].Height = 0F;

                    int collapsedHeight = S(195);
                    MinimumSize = new Size(S(760), collapsedHeight);
                    Size = new Size(_expandedWindowSize.Width, collapsedHeight);
                    if (_btnToggleCollapse != null)
                    {
                        _btnToggleCollapse.Text = "展开完整界面 ▼";
                        _btnToggleCollapse.BackColor = Color.FromArgb(255, 244, 214);
                    }
                    _isWindowCollapsed = true;
                    _lblCurrentTaskStatus.Text = "窗口已折叠为迷你工具条，您可在 CAD 视图中自由缩放、编辑图形，完成后点击“展开完整界面 ▼”或“重新扫描”。";
                }
                else
                {
                    _topSettingsSection.Visible = true;
                    _gridSheets.Visible = true;
                    _mainLayout.RowStyles[0].SizeType = SizeType.Absolute;
                    _mainLayout.RowStyles[0].Height = SF(396F);
                    _mainLayout.RowStyles[2].SizeType = SizeType.Percent;
                    _mainLayout.RowStyles[2].Height = 100F;

                    MinimumSize = _expandedMinSize;
                    Size = _expandedWindowSize;
                    if (_btnToggleCollapse != null)
                    {
                        _btnToggleCollapse.Text = "折叠窗口 ▲ (看图)";
                        _btnToggleCollapse.BackColor = Color.FromArgb(228, 244, 254);
                    }
                    _isWindowCollapsed = false;
                }
            }
            finally
            {
                ResumeLayout(true);
            }
        }

        private Control BuildTopSettingsSection()
        {
            var split = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(0)
            };
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, SF(496F)));
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

            split.Controls.Add(BuildLeftFrameRecognitionPanel(), 0, 0);
            split.Controls.Add(BuildRightPlotterAndOutputPanel(), 1, 0);

            return split;
        }

        private Control BuildLeftFrameRecognitionPanel()
        {
            var leftLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Margin = SP(0, 0, 6, 0)
            };
            leftLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, SF(180F))); // Row 0: [图框识别] + [指定图层/图块与搜图范围]
            leftLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, SF(168F))); // Row 1: [图纸排列顺序]
            leftLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, SF(46F)));  // Row 2: [框选搜图范围] [在图中显示顺序] [重新扫描] 识别图框: N

            // Row 0: Split into Left "图框识别" and Right "指定图层/图块与搜图范围"
            var topRowSplit = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(0)
            };
            topRowSplit.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, SF(175F)));
            topRowSplit.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

            // 1A. 图框识别
            var grpRecog = new GroupBox
            {
                Text = "图框识别",
                Dock = DockStyle.Fill,
                Padding = SP(10, 4, 4, 2),
                Margin = SP(0, 0, 6, 2)
            };
            var recogPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false
            };
            _rbModeManualPick = new RadioButton { Text = "点选图框", AutoSize = true, Margin = SP(2, 2, 2, 2) };
            _rbModeByLayer = new RadioButton { Text = "指定图框图层", AutoSize = true, Margin = SP(2, 2, 2, 2) };
            _rbModeByBlock = new RadioButton { Text = "指定图框图块", AutoSize = true, Margin = SP(2, 2, 2, 2) };
            _chkIncludeNonPlottable = new CheckBox { Text = "识别非打印图形", AutoSize = true, Margin = SP(2, 4, 2, 1) };
            _chkIncludePaperSpaceLayouts = new CheckBox { Text = "包含布局(PaperSpace)", AutoSize = true, Margin = SP(2, 2, 2, 1) };

            _rbModeManualPick.CheckedChanged += OnRecognitionModeChanged;
            _rbModeByLayer.CheckedChanged += OnRecognitionModeChanged;
            _rbModeByBlock.CheckedChanged += OnRecognitionModeChanged;
            _chkIncludeNonPlottable.CheckedChanged += (s, e) =>
            {
                if (_isUpdatingUi) return;
                _settings.IncludeNonPlottableLayers = _chkIncludeNonPlottable.Checked;
                PopulateLayerAndBlockDropdowns();
                ScanModelSpaceFrames();
            };
            _chkIncludePaperSpaceLayouts.CheckedChanged += (s, e) =>
            {
                if (_isUpdatingUi) return;
                _settings.IncludePaperSpaceLayouts = _chkIncludePaperSpaceLayouts.Checked;
                ScanModelSpaceFrames();
            };

            recogPanel.Controls.AddRange(new Control[]
            {
                _rbModeManualPick, _rbModeByLayer, _rbModeByBlock, _chkIncludeNonPlottable, _chkIncludePaperSpaceLayouts
            });
            grpRecog.Controls.Add(recogPanel);

            // 1B. 指定图层/图块 与 搜图范围
            var grpLayerBlock = new GroupBox
            {
                Text = "指定图层/图块 与 搜图范围",
                Dock = DockStyle.Fill,
                Padding = SP(8, 4, 8, 4),
                Margin = SP(0, 0, 0, 2)
            };
            var lbTable = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 4
            };
            lbTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            lbTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            lbTable.RowStyles.Add(new RowStyle(SizeType.Percent, 27F));
            lbTable.RowStyles.Add(new RowStyle(SizeType.Percent, 25F));
            lbTable.RowStyles.Add(new RowStyle(SizeType.Percent, 25F));
            lbTable.RowStyles.Add(new RowStyle(SizeType.Percent, 23F));

            var pickBtnFlow = CreateInlineFlow();
            _btnPickLayerOrBlock = new Button
            {
                Text = "点选图层 + 框选搜图范围 <",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = SS(214, 27),
                Padding = SP(4, 1, 4, 1),
                BackColor = Color.FromArgb(228, 241, 254),
                FlatStyle = FlatStyle.Standard,
                Font = new Font(Font, FontStyle.Bold),
                Anchor = AnchorStyles.Left,
                Margin = SP(0, 1, 6, 1)
            };
            _btnPickLayerOrBlock.Click += OnPickLayerOrBlockFromDrawingClick;

            _btnClearLayerBlock = new Button
            {
                Text = "重置",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = SS(58, 27),
                Padding = SP(4, 1, 4, 1),
                Anchor = AnchorStyles.Left,
                Margin = SP(0, 1, 0, 1)
            };
            _btnClearLayerBlock.Click += OnClearLayerBlockClick;
            pickBtnFlow.Controls.Add(_btnPickLayerOrBlock);
            pickBtnFlow.Controls.Add(_btnClearLayerBlock);

            lbTable.Controls.Add(pickBtnFlow, 0, 0);
            lbTable.SetColumnSpan(pickBtnFlow, 2);

            _cmbLayerList = new ComboBox
            {
                Dock = DockStyle.Fill,
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                DropDownStyle = ComboBoxStyle.DropDown,
                Margin = SP(0, 2, 0, 2)
            };
            _cmbBlockList = new ComboBox
            {
                Dock = DockStyle.Fill,
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                DropDownStyle = ComboBoxStyle.DropDown,
                Margin = SP(0, 2, 0, 2)
            };

            _cmbLayerList.SelectedIndexChanged += OnActiveLayerOrBlockChanged;
            _cmbLayerList.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) OnActiveLayerOrBlockChanged(s, e); };
            _cmbBlockList.SelectedIndexChanged += OnActiveLayerOrBlockChanged;
            _cmbBlockList.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) OnActiveLayerOrBlockChanged(s, e); };

            AddLabeledControl(lbTable, 1, 0, "图框图层:", _cmbLayerList);
            AddLabeledControl(lbTable, 2, 0, "图框图块:", _cmbBlockList);

            var regionRowFlow = CreateInlineFlow();
            _lblSearchRegionStatus = new Label
            {
                Text = "搜图范围: 全图模型空间",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                ForeColor = Color.FromArgb(40, 90, 140),
                Margin = SP(0, 2, 6, 2)
            };
            _btnFullRegion = new Button
            {
                Text = "恢复全图",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = SS(74, 23),
                Padding = SP(4, 0, 4, 0),
                Anchor = AnchorStyles.Left,
                Visible = false,
                Margin = SP(2, 1, 0, 1)
            };
            _btnFullRegion.Click += (s, e) =>
            {
                _searchRegion = null;
                UpdateSearchRegionUiState();
                ScanModelSpaceFrames();
            };
            regionRowFlow.Controls.Add(_lblSearchRegionStatus);
            regionRowFlow.Controls.Add(_btnFullRegion);
            lbTable.Controls.Add(regionRowFlow, 0, 3);
            lbTable.SetColumnSpan(regionRowFlow, 2);

            grpLayerBlock.Controls.Add(lbTable);

            topRowSplit.Controls.Add(grpRecog, 0, 0);
            topRowSplit.Controls.Add(grpLayerBlock, 1, 0);

            // 2. 图纸排列顺序
            var grpOrder = new GroupBox
            {
                Text = "图纸排列顺序 (识别后可随时切换，实时重排图纸顺序)",
                Dock = DockStyle.Fill,
                Padding = SP(8, 4, 8, 4),
                Margin = SP(0, 2, 0, 2)
            };
            var orderTable = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 4
            };
            orderTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            orderTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            orderTable.RowStyles.Add(new RowStyle(SizeType.Percent, 25F));
            orderTable.RowStyles.Add(new RowStyle(SizeType.Percent, 25F));
            orderTable.RowStyles.Add(new RowStyle(SizeType.Percent, 25F));
            orderTable.RowStyles.Add(new RowStyle(SizeType.Percent, 25F));

            // Row 0: 主排列方式
            var modeFlow = CreateInlineFlow();
            _rbSortRowFirst = new RadioButton { Text = "先行后列", AutoSize = true, Anchor = AnchorStyles.Left, Margin = SP(0, 2, 14, 2) };
            _rbSortColFirst = new RadioButton { Text = "先列后行", AutoSize = true, Anchor = AnchorStyles.Left, Margin = SP(0, 2, 14, 2) };
            _rbSortSelection = new RadioButton { Text = "按选择顺序", AutoSize = true, Anchor = AnchorStyles.Left, Margin = SP(0, 2, 4, 2) };
            _rbSortRowFirst.CheckedChanged += OnSortOrderChanged;
            _rbSortColFirst.CheckedChanged += OnSortOrderChanged;
            _rbSortSelection.CheckedChanged += OnSortOrderChanged;
            modeFlow.Controls.AddRange(new Control[] { _rbSortRowFirst, _rbSortColFirst, _rbSortSelection });
            AddLabeledControl(orderTable, 0, 0, "排列方式:", modeFlow);

            // Row 1: 左右方向
            var horizFlow = CreateInlineFlow();
            _rbDirLeftToRight = new RadioButton { Text = "从左往右 (→)", AutoSize = true, Anchor = AnchorStyles.Left, Margin = SP(0, 2, 18, 2) };
            _rbDirRightToLeft = new RadioButton { Text = "从右往左 (←)", AutoSize = true, Anchor = AnchorStyles.Left, Margin = SP(0, 2, 4, 2) };
            _rbDirLeftToRight.CheckedChanged += OnSortOrderChanged;
            _rbDirRightToLeft.CheckedChanged += OnSortOrderChanged;
            horizFlow.Controls.AddRange(new Control[] { _rbDirLeftToRight, _rbDirRightToLeft });
            AddLabeledControl(orderTable, 1, 0, "左右顺序:", horizFlow);

            // Row 2: 上下方向
            var vertFlow = CreateInlineFlow();
            _rbDirTopToBottom = new RadioButton { Text = "从上往下 (↓)", AutoSize = true, Anchor = AnchorStyles.Left, Margin = SP(0, 2, 18, 2) };
            _rbDirBottomToTop = new RadioButton { Text = "从下往上 (↑)", AutoSize = true, Anchor = AnchorStyles.Left, Margin = SP(0, 2, 4, 2) };
            _rbDirTopToBottom.CheckedChanged += OnSortOrderChanged;
            _rbDirBottomToTop.CheckedChanged += OnSortOrderChanged;
            vertFlow.Controls.AddRange(new Control[] { _rbDirTopToBottom, _rbDirBottomToTop });
            AddLabeledControl(orderTable, 2, 0, "上下顺序:", vertFlow);

            // Row 3: 当前排列规则说明
            _lblSortOrderDesc = new Label
            {
                Text = "当前顺序: 先行后列：从左往右 (→)，从上往下 (↓)",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(0, 102, 204),
                Font = new Font(Font, FontStyle.Bold)
            };
            orderTable.Controls.Add(_lblSortOrderDesc, 0, 3);
            orderTable.SetColumnSpan(_lblSortOrderDesc, 2);

            grpOrder.Controls.Add(orderTable);

            // 3. [框选搜图范围 <] [在图中显示顺序] [重新扫描] 识别图框: N
            var scanActionFlow = CreateInlineFlow();
            scanActionFlow.Padding = SP(0, 3, 0, 0);
            _btnSelectSearchRegion = CreateToolButton("框选搜图范围 <", 114, OnSelectSearchRegionClick);
            _btnHighlightFrames = CreateToolButton("在图中显示顺序", 114, OnHighlightFramesClick);
            _btnRescan = CreateToolButton("重新扫描", 84, (s, e) => ScanModelSpaceFrames());
            _lblRecognizedCount = new Label
            {
                Text = "识别图框: 0",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Font = new Font(Font, FontStyle.Bold),
                ForeColor = Color.FromArgb(0, 102, 204),
                Margin = SP(4, 2, 0, 2)
            };
            scanActionFlow.Controls.AddRange(new Control[]
            {
                _btnSelectSearchRegion, _btnHighlightFrames, _btnRescan, _lblRecognizedCount
            });

            leftLayout.Controls.Add(topRowSplit, 0, 0);
            leftLayout.Controls.Add(grpOrder, 0, 1);
            leftLayout.Controls.Add(scanActionFlow, 0, 2);

            return leftLayout;
        }

        private Control BuildRightPlotterAndOutputPanel()
        {
            var rightLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Margin = new Padding(0)
            };
            rightLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, SF(92F)));  // 打印机与打印样式
            rightLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, SF(92F)));  // 纸张与比例
            rightLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, SF(66F)));  // 打印偏移 & 打印方向
            rightLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, SF(136F))); // 打印输出 与 非标PDF自动裁剪

            // 1. 打印机与打印样式
            var grpPlotter = new GroupBox
            {
                Text = "打印机与打印样式",
                Dock = DockStyle.Fill,
                Padding = SP(8, 4, 8, 4),
                Margin = SP(0, 0, 0, 2)
            };
            var plotterTable = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2
            };
            plotterTable.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            plotterTable.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));

            var plotterRow1 = CreateInlineFlow();
            plotterRow1.Controls.Add(CreateInlineLabel("绘图仪:", 0, 4));
            _lblDeviceStatus = new Label
            {
                Text = "DWG To PDF.pc3",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = SP(0, 2, 8, 2),
                Font = new Font(Font, FontStyle.Bold)
            };
            _btnRefreshPc3 = CreateInlineButton("刷新PC3", 74, 24, (s, e) => RefreshPc3AndReevaluateSheets());
            _btnRefreshPc3.Margin = SP(0, 1, 14, 1);
            plotterRow1.Controls.Add(_lblDeviceStatus);
            plotterRow1.Controls.Add(_btnRefreshPc3);

            plotterRow1.Controls.Add(CreateInlineLabel("份数:", 4, 4));
            _nudCopies = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 99,
                Value = 1,
                Width = S(52),
                Anchor = AnchorStyles.Left,
                Margin = SP(0, 2, 10, 2)
            };
            _chkPrintLineweights = new CheckBox { Text = "打印对象线宽", AutoSize = true, Anchor = AnchorStyles.Left, Margin = SP(6, 2, 10, 2) };
            _chkUsePlotStyle = new CheckBox { Text = "按样式打印", AutoSize = true, Anchor = AnchorStyles.Left, Margin = SP(6, 2, 0, 2) };
            _chkPrintLineweights.CheckedChanged += OnPlotStyleSettingsChanged;
            _chkUsePlotStyle.CheckedChanged += OnPlotStyleSettingsChanged;
            plotterRow1.Controls.AddRange(new Control[] { _nudCopies, _chkPrintLineweights, _chkUsePlotStyle });

            var styleFlow = CreateInlineFlow();
            styleFlow.Controls.Add(CreateInlineLabel("打印样式表:", 0, 4));
            _cmbPlotStyle = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = S(220),
                Anchor = AnchorStyles.Left,
                Margin = SP(0, 2, 16, 2)
            };
            styleFlow.Controls.Add(_cmbPlotStyle);
            styleFlow.Controls.Add(CreateInlineLabel("颜色模式:", 4, 4));
            _cmbColorMode = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = S(155),
                Anchor = AnchorStyles.Left,
                Margin = SP(0, 2, 0, 2)
            };
            _cmbColorMode.Items.AddRange(new object[] { "黑白(Monochrome)", "彩色(Color)", "灰度(Grayscale)", "按显示" });
            _cmbPlotStyle.SelectedIndexChanged += OnPlotStyleSettingsChanged;
            _cmbColorMode.SelectedIndexChanged += OnPlotStyleSettingsChanged;
            styleFlow.Controls.Add(_cmbColorMode);

            plotterTable.Controls.Add(plotterRow1, 0, 0);
            plotterTable.Controls.Add(styleFlow, 0, 1);
            grpPlotter.Controls.Add(plotterTable);

            // 2. 纸张与比例
            var grpPaperScale = new GroupBox
            {
                Text = "纸张与打印比例",
                Dock = DockStyle.Fill,
                Padding = SP(8, 4, 8, 4),
                Margin = SP(0, 2, 0, 2)
            };
            var psTable = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2
            };
            psTable.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            psTable.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));

            var paperRow = CreateInlineFlow();
            paperRow.Controls.Add(CreateInlineLabel("尺寸:", 0, 4));
            _cmbManualPaper = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = S(260),
                Anchor = AnchorStyles.Left,
                Margin = SP(0, 2, 4, 2)
            };
            _cmbManualPaper.SelectedIndexChanged += OnManualPaperSelectionChanged;
            _btnFilterPaper = CreateInlineButton("过滤纸张...", 82, 24, OnFilterPaperClick);
            _btnFilterPaper.Margin = SP(2, 1, 4, 1);
            _btnApplyManualPaper = CreateInlineButton("应用纸张到选中行", 128, 24, OnApplyManualPaperToSelectedClick);
            _btnApplyManualPaper.Margin = SP(2, 1, 8, 1);
            _chkAutoMatchPaper = new CheckBox { Text = "自动匹配纸张尺寸", AutoSize = true, Anchor = AnchorStyles.Left, Margin = SP(4, 2, 0, 2) };
            _chkAutoMatchPaper.CheckedChanged += OnAutoMatchCheckedChanged;
            paperRow.Controls.AddRange(new Control[]
            {
                _cmbManualPaper, _btnFilterPaper, _btnApplyManualPaper, _chkAutoMatchPaper
            });

            var scaleRow = CreateInlineFlow();
            _chkFitToPaper = new CheckBox { Text = "布满图纸(Fit)", AutoSize = true, Anchor = AnchorStyles.Left, Margin = SP(0, 2, 12, 2) };
            _chkFitToPaper.CheckedChanged += OnScaleModeChanged;
            _chkUseCustomScale = new CheckBox { Text = "指定固定比例:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = SP(4, 2, 4, 2) };
            _chkUseCustomScale.CheckedChanged += OnScaleModeChanged;
            _txtScaleNum = new TextBox
            {
                Width = S(38),
                Text = "1",
                TextAlign = HorizontalAlignment.Center,
                Anchor = AnchorStyles.Left,
                Margin = SP(0, 2, 2, 2)
            };
            _txtScaleDen = new TextBox
            {
                Width = S(56),
                Text = "100",
                TextAlign = HorizontalAlignment.Center,
                Anchor = AnchorStyles.Left,
                Margin = SP(2, 2, 2, 2)
            };
            _btnApplyScaleToSelected = CreateInlineButton("应用比例到选中行", 128, 24, OnApplyScaleToSelectedClick);
            _btnApplyScaleToSelected.Margin = SP(8, 1, 0, 1);

            scaleRow.Controls.Add(_chkFitToPaper);
            scaleRow.Controls.Add(_chkUseCustomScale);
            scaleRow.Controls.Add(_txtScaleNum);
            scaleRow.Controls.Add(CreateInlineLabel("毫米 =", 2, 2));
            scaleRow.Controls.Add(_txtScaleDen);
            scaleRow.Controls.Add(CreateInlineLabel("图形单位", 2, 4));
            scaleRow.Controls.Add(_btnApplyScaleToSelected);

            psTable.Controls.Add(paperRow, 0, 0);
            psTable.Controls.Add(scaleRow, 0, 1);
            grpPaperScale.Controls.Add(psTable);

            // 3. 打印偏移(单位:毫米) & 打印方向
            var grpOffsetOrient = new GroupBox
            {
                Text = "打印偏移(毫米) 与 打印方向",
                Dock = DockStyle.Fill,
                Padding = SP(8, 4, 8, 4),
                Margin = SP(0, 2, 0, 2)
            };
            var ooFlow = CreateInlineFlow();
            _chkCenterPlot = new CheckBox { Text = "居中打印", AutoSize = true, Anchor = AnchorStyles.Left, Margin = SP(0, 2, 8, 2) };
            _chkCenterPlot.CheckedChanged += (s, e) =>
            {
                _txtOffsetX.Enabled = !_chkCenterPlot.Checked;
                _txtOffsetY.Enabled = !_chkCenterPlot.Checked;
                if (!_isUpdatingUi) SaveCurrentUiToSettings();
            };
            _txtOffsetX = new TextBox { Width = S(48), Text = "0.00", Enabled = false, Anchor = AnchorStyles.Left, Margin = SP(0, 2, 4, 2) };
            _txtOffsetY = new TextBox { Width = S(48), Text = "0.00", Enabled = false, Anchor = AnchorStyles.Left, Margin = SP(0, 2, 4, 2) };

            _rbOrientAuto = new RadioButton { Text = "自适应", AutoSize = true, Anchor = AnchorStyles.Left, Margin = SP(10, 2, 6, 2) };
            _rbOrientPortrait = new RadioButton { Text = "纵向", AutoSize = true, Anchor = AnchorStyles.Left, Margin = SP(4, 2, 6, 2) };
            _rbOrientLandscape = new RadioButton { Text = "横向", AutoSize = true, Anchor = AnchorStyles.Left, Margin = SP(4, 2, 6, 2) };
            _chkUpsideDown = new CheckBox { Text = "上下颠倒", AutoSize = true, Anchor = AnchorStyles.Left, Margin = SP(8, 2, 0, 2) };

            ooFlow.Controls.Add(_chkCenterPlot);
            ooFlow.Controls.Add(CreateInlineLabel("X:", 2, 2));
            ooFlow.Controls.Add(_txtOffsetX);
            ooFlow.Controls.Add(CreateInlineLabel("Y:", 4, 2));
            ooFlow.Controls.Add(_txtOffsetY);
            ooFlow.Controls.Add(CreateInlineLabel(" | 方向:", 8, 2));
            ooFlow.Controls.AddRange(new Control[] { _rbOrientAuto, _rbOrientPortrait, _rbOrientLandscape, _chkUpsideDown });
            grpOffsetOrient.Controls.Add(ooFlow);

            // 4. 打印输出 与 非标PDF自动裁剪
            var grpOutput = new GroupBox
            {
                Text = "打印输出 与 非标PDF自动裁剪",
                Dock = DockStyle.Fill,
                Padding = SP(8, 4, 8, 4),
                Margin = SP(0, 2, 0, 2)
            };
            var outTable = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3
            };
            outTable.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33F));
            outTable.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33F));
            outTable.RowStyles.Add(new RowStyle(SizeType.Percent, 33.34F));

            var outRow1 = CreateInlineFlow();
            _rbSinglePdf = new RadioButton { Text = "每张图单独PDF", AutoSize = true, Anchor = AnchorStyles.Left, Margin = SP(0, 2, 8, 2) };
            _rbMergedPdf = new RadioButton { Text = "合并多页PDF", AutoSize = true, Anchor = AnchorStyles.Left, Margin = SP(0, 2, 10, 2) };
            _chkSaveToDwgDir = new CheckBox { Text = "保存到与DWG相同目录", AutoSize = true, Anchor = AnchorStyles.Left, Margin = SP(2, 2, 8, 2) };
            _chkSaveToCustomDir = new CheckBox { Text = "指定目录:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = SP(0, 2, 2, 2) };
            _txtOutputDir = new TextBox { Width = S(190), Anchor = AnchorStyles.Left, Margin = SP(0, 2, 3, 2) };
            _btnBrowseDir = CreateInlineButton("浏览...", 62, 24, OnBrowseOutputDirClick);
            _btnBrowseDir.Margin = SP(2, 1, 3, 1);
            _btnOpenOutputDir = CreateInlineButton("打开目录", 72, 24, OnOpenOutputDirClick);
            _btnOpenOutputDir.Margin = SP(2, 1, 0, 1);

            _rbSinglePdf.CheckedChanged += OnSettingsChanged;
            _rbMergedPdf.CheckedChanged += OnSettingsChanged;
            _chkSaveToDwgDir.CheckedChanged += (s, e) =>
            {
                if (_isUpdatingUi) return;
                if (_chkSaveToDwgDir.Checked) _chkSaveToCustomDir.Checked = false;
                else if (!_chkSaveToCustomDir.Checked) _chkSaveToCustomDir.Checked = true;
                UpdateOutputDirControlsState();
                SaveCurrentUiToSettings();
            };
            _chkSaveToCustomDir.CheckedChanged += (s, e) =>
            {
                if (_isUpdatingUi) return;
                if (_chkSaveToCustomDir.Checked) _chkSaveToDwgDir.Checked = false;
                else if (!_chkSaveToDwgDir.Checked) _chkSaveToDwgDir.Checked = true;
                UpdateOutputDirControlsState();
                SaveCurrentUiToSettings();
            };

            outRow1.Controls.AddRange(new Control[]
            {
                _rbSinglePdf, _rbMergedPdf, _chkSaveToDwgDir, _chkSaveToCustomDir,
                _txtOutputDir, _btnBrowseDir, _btnOpenOutputDir
            });

            var outRow2 = CreateInlineFlow();
            _chkAppendPaperSize = new CheckBox { Text = "文件名自动添加图幅", AutoSize = true, Anchor = AnchorStyles.Left, Margin = SP(0, 2, 8, 2) };
            _chkAppendPaperSize.CheckedChanged += (s, e) =>
            {
                if (_isUpdatingUi) return;
                _settings.AppendPaperSizeToFileName = _chkAppendPaperSize.Checked;
                ReevaluateAllSheets(preserveCustomFileName: false);
            };
            _txtFileNameTemplate = new TextBox { Width = S(128), Anchor = AnchorStyles.Left, Margin = SP(0, 2, 4, 2) };
            _txtMergedFileName = new TextBox { Width = S(128), Anchor = AnchorStyles.Left, Margin = SP(0, 2, 4, 2) };
            _cmbDuplicatePolicy = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = S(115),
                Anchor = AnchorStyles.Left,
                Margin = SP(0, 2, 0, 2)
            };
            _cmbDuplicatePolicy.Items.AddRange(new object[] { "自动追加序号", "覆盖前确认", "跳过重名文件" });

            _txtFileNameTemplate.TextChanged += OnNamingSettingsChanged;
            _txtMergedFileName.TextChanged += OnNamingSettingsChanged;
            _cmbDuplicatePolicy.SelectedIndexChanged += OnSettingsChanged;

            outRow2.Controls.Add(_chkAppendPaperSize);
            outRow2.Controls.Add(CreateInlineLabel("单页模板:", 4, 2));
            outRow2.Controls.Add(_txtFileNameTemplate);
            outRow2.Controls.Add(CreateInlineLabel("合并名:", 6, 2));
            outRow2.Controls.Add(_txtMergedFileName);
            outRow2.Controls.Add(CreateInlineLabel("重名:", 6, 2));
            outRow2.Controls.Add(_cmbDuplicatePolicy);

            var outRow3 = CreateInlineFlow();
            _chkAutoCropPdf = new CheckBox
            {
                Text = "非标/加长图框打印后自动裁剪PDF白边 (标准图框不裁剪)",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                ForeColor = Color.FromArgb(0, 102, 153),
                Font = new Font(Font, FontStyle.Bold),
                Margin = SP(0, 2, 10, 2)
            };
            _chkAutoCropPdf.CheckedChanged += (s, e) =>
            {
                if (_isUpdatingUi) return;
                SaveCurrentUiToSettings();
            };

            _txtCropMarginMm = new TextBox
            {
                Width = S(46),
                Text = "0",
                TextAlign = HorizontalAlignment.Center,
                Anchor = AnchorStyles.Left,
                Margin = SP(0, 2, 2, 2)
            };
            _txtCropMarginMm.TextChanged += (s, e) =>
            {
                if (_isUpdatingUi) return;
                SaveCurrentUiToSettings();
            };

            _btnCropExistingPdfs = CreateInlineButton("立即裁剪已输出PDF", 132, 24, OnCropExistingPdfsClick);
            _btnCropExistingPdfs.BackColor = Color.FromArgb(228, 244, 254);
            _btnCropExistingPdfs.FlatStyle = FlatStyle.Standard;
            _btnCropExistingPdfs.Margin = SP(10, 1, 0, 1);

            outRow3.Controls.Add(_chkAutoCropPdf);
            outRow3.Controls.Add(CreateInlineLabel("默认留白距离:", 4, 2));
            outRow3.Controls.Add(_txtCropMarginMm);
            outRow3.Controls.Add(CreateInlineLabel("毫米(mm)", 2, 4));
            outRow3.Controls.Add(_btnCropExistingPdfs);

            outTable.Controls.Add(outRow1, 0, 0);
            outTable.Controls.Add(outRow2, 0, 1);
            outTable.Controls.Add(outRow3, 0, 2);
            grpOutput.Controls.Add(outTable);

            rightLayout.Controls.Add(grpPlotter, 0, 0);
            rightLayout.Controls.Add(grpPaperScale, 0, 1);
            rightLayout.Controls.Add(grpOffsetOrient, 0, 2);
            rightLayout.Controls.Add(grpOutput, 0, 3);

            return rightLayout;
        }

        private Control BuildGridToolbar()
        {
            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = SP(0, 3, 0, 3)
            };

            _btnToggleCollapse = CreateToolButton("折叠窗口 ▲ (看图)", 118, (s, e) => ToggleWindowCollapse());
            _btnToggleCollapse.BackColor = Color.FromArgb(228, 244, 254);
            _btnToggleCollapse.FlatStyle = FlatStyle.Standard;

            var btnPrintPreview = CreateToolButton("👁️ 打印预览", 92, OnPlotPreviewSelectedSheetClick);
            var btnPreview = CreateToolButton("定位视图", 84, OnPreviewSelectedSheetClick);
            var btnTeach = CreateToolButton("🎯 拾取学习", 92, OnTeachFrameClick);
            var btnCreateTable = CreateToolButton("📊 生成图纸目录", 112, OnCreateDrawingTableClick);
            var btnManualAdd = CreateToolButton("手动拉框添加", 98, OnManualPickClick);
            var btnSelectAll = CreateToolButton("全选 (Ctrl+A)", 96, (s, e) => SetAllSelection(true));
            var btnInvert = CreateToolButton("反选 (Ctrl+Q)", 96, (s, e) => InvertSelection());
            var btnUp = CreateToolButton("上移 (↑)", 78, (s, e) => MoveSelectedRow(-1));
            var btnDown = CreateToolButton("下移 (↓)", 78, (s, e) => MoveSelectedRow(1));
            var btnDel = CreateToolButton("删除选中 (Del)", 105, OnDeleteSelectedClick);
            var btnRules = CreateToolButton("图框规则设置...", 108, OnOpenRulesClick);
            var lblHint = new Label
            {
                Text = "提示: 非模态窗口可直接操作CAD；双击纸张列换纸，双击序号列定位。",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                ForeColor = Color.DimGray,
                Margin = SP(6, 2, 0, 2)
            };

            flow.Controls.AddRange(new Control[]
            {
                _btnToggleCollapse, btnPrintPreview, btnPreview, btnTeach, btnCreateTable,
                btnManualAdd, btnSelectAll, btnInvert, btnUp, btnDown, btnDel, btnRules, lblHint
            });

            return flow;
        }

        private Control BuildSheetGridPanel()
        {
            _gridSheets = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = true,
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize
            };
            _gridSheets.RowTemplate.Height = S(26);

            _gridSheets.Columns.Add(new DataGridViewCheckBoxColumn
            {
                Name = "ColSelected",
                HeaderText = "选择",
                Width = S(48)
            });
            _gridSheets.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "ColIndex",
                HeaderText = "序号",
                Width = S(50),
                ReadOnly = true
            });
            _gridSheets.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "ColDrawingNo",
                HeaderText = "图号 (可改)",
                Width = S(110)
            });
            _gridSheets.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "ColDrawingName",
                HeaderText = "图名 (可改)",
                Width = S(175)
            });
            _gridSheets.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "ColFrameType",
                HeaderText = "图层 / 类型",
                Width = S(115),
                ReadOnly = true
            });
            _gridSheets.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "ColGeometry",
                HeaderText = "图框尺寸(图形单位)",
                Width = S(138),
                ReadOnly = true
            });
            _gridSheets.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "ColScale",
                HeaderText = "比例 (可改)",
                Width = S(125)
            });
            _gridSheets.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "ColPaper",
                HeaderText = "目标纸张 (双击可换)",
                Width = S(200),
                ReadOnly = true
            });
            _gridSheets.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "ColOutputFileName",
                HeaderText = "输出文件名 (可改)",
                Width = S(185)
            });
            _gridSheets.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "ColStatus",
                HeaderText = "状态 / 审核信息",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                MinimumWidth = S(180),
                ReadOnly = true
            });

            _gridSheets.CurrentCellDirtyStateChanged += OnGridCurrentCellDirtyStateChanged;
            _gridSheets.CellValueChanged += OnGridCellValueChanged;
            _gridSheets.CellDoubleClick += OnGridCellDoubleClick;

            var gridMenu = new ContextMenuStrip();
            gridMenu.Items.Add("👁️ 全屏打印预览 (BPPREVIEW)", null, OnPlotPreviewSelectedSheetClick);
            gridMenu.Items.Add("🔍 定位并缩放到图框", null, OnPreviewSelectedSheetClick);
            gridMenu.Items.Add(new ToolStripSeparator());
            gridMenu.Items.Add("🎯 点选学习图框 (BPTEACH)...", null, OnTeachFrameClick);
            gridMenu.Items.Add("📊 在 CAD 中生成图纸目录表 (BPTABLE)...", null, OnCreateDrawingTableClick);
            gridMenu.Items.Add(new ToolStripSeparator());
            gridMenu.Items.Add("🗑️ 从列表中移除选中图框 (Del)", null, OnDeleteSelectedClick);
            _gridSheets.ContextMenuStrip = gridMenu;

            return _gridSheets;
        }

        private Control BuildBottomBar()
        {
            var bottomLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 2,
                Padding = SP(2, 4, 2, 2)
            };
            bottomLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56F));
            bottomLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44F));
            bottomLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 58F));
            bottomLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 42F));

            var leftStatusFlow = CreateInlineFlow();
            _lblSummaryCounts = new Label
            {
                Text = "总张数: 0 | 待打印: 0 | 需确认: 0 | 成功: 0 | 失败: 0",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = SP(0, 2, 12, 2),
                Font = new Font(Font, FontStyle.Bold)
            };
            leftStatusFlow.Controls.Add(_lblSummaryCounts);

            var rightButtonsFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Margin = new Padding(0)
            };

            _btnClose = new Button
            {
                Text = "关闭",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = SS(84, 30),
                Padding = SP(6, 1, 6, 1),
                Anchor = AnchorStyles.Left,
                Margin = SP(6, 0, 0, 0)
            };
            _btnClose.Click += (s, e) => Close();

            _btnStartPlot = new Button
            {
                Text = "打印(P)",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = SS(110, 30),
                Padding = SP(8, 1, 8, 1),
                Anchor = AnchorStyles.Left,
                Margin = SP(6, 0, 0, 0),
                BackColor = Color.FromArgb(0, 120, 215),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font(Font, FontStyle.Bold)
            };
            _btnStartPlot.Click += OnStartPlotClick;

            _btnRetryFailed = new Button
            {
                Text = "重试失败项",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = SS(88, 30),
                Padding = SP(6, 1, 6, 1),
                Anchor = AnchorStyles.Left,
                Margin = SP(6, 0, 0, 0)
            };
            _btnRetryFailed.Click += OnRetryFailedClick;

            _btnPageStats = new Button
            {
                Text = "页数统计",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = SS(84, 30),
                Padding = SP(6, 1, 6, 1),
                Anchor = AnchorStyles.Left,
                Margin = SP(6, 0, 0, 0)
            };
            _btnPageStats.Click += OnPageStatsClick;

            _chkConfirmPageCount = new CheckBox
            {
                Text = "打印前确认页数",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = SP(0, 2, 6, 2)
            };

            rightButtonsFlow.Controls.AddRange(new Control[]
            {
                _btnClose, _btnStartPlot, _btnRetryFailed, _btnPageStats, _chkConfirmPageCount
            });

            _lblCurrentTaskStatus = new Label
            {
                Text = "就绪",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            };

            var progressAndLogTable = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(0)
            };
            progressAndLogTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55F));
            progressAndLogTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45F));

            _progressBar = new ProgressBar
            {
                Dock = DockStyle.Fill,
                Minimum = 0,
                Maximum = 100,
                Value = 0
            };

            _lnkOpenLog = new LinkLabel
            {
                Text = "查看打印日志",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight
            };
            _lnkOpenLog.LinkClicked += (s, e) => OpenLogFile();

            progressAndLogTable.Controls.Add(_progressBar, 0, 0);
            progressAndLogTable.Controls.Add(_lnkOpenLog, 1, 0);

            bottomLayout.Controls.Add(leftStatusFlow, 0, 0);
            bottomLayout.Controls.Add(rightButtonsFlow, 1, 0);
            bottomLayout.Controls.Add(_lblCurrentTaskStatus, 0, 1);
            bottomLayout.Controls.Add(progressAndLogTable, 1, 1);

            return bottomLayout;
        }

        private void OnFormKeyDown(object sender, KeyEventArgs e)
        {
            if (_gridSheets.IsCurrentCellInEditMode) return;

            if (e.Control && e.KeyCode == Keys.Up)
            {
                MoveSelectedRow(-1);
                e.Handled = true;
            }
            else if (e.Control && e.KeyCode == Keys.Down)
            {
                MoveSelectedRow(1);
                e.Handled = true;
            }
            else if (e.Control && e.KeyCode == Keys.A)
            {
                SetAllSelection(true);
                e.Handled = true;
            }
            else if (e.Control && e.KeyCode == Keys.Q)
            {
                InvertSelection();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Delete && _gridSheets.Focused)
            {
                OnDeleteSelectedClick(sender, EventArgs.Empty);
                e.Handled = true;
            }
        }

        private void InitializeEnvironmentAndScan()
        {
            _isUpdatingUi = true;
            try
            {
                // Frame recognition mode
                _rbModeManualPick.Checked = _settings.RecognitionMode == FrameRecognitionMode.ManualPick;
                _rbModeByBlock.Checked = _settings.RecognitionMode == FrameRecognitionMode.ByBlock;
                _rbModeByLayer.Checked = _settings.RecognitionMode == FrameRecognitionMode.ByLayer;
                _chkIncludeNonPlottable.Checked = _settings.IncludeNonPlottableLayers;
                _chkIncludePaperSpaceLayouts.Checked = _settings.IncludePaperSpaceLayouts;

                PopulateLayerAndBlockDropdowns();
                _cmbLayerList.Text = _settings.ActiveFrameLayer ?? "TK,BORDER,图框,PUB_TITLE";
                _cmbBlockList.Text = _settings.ActiveFrameBlockName ?? "";
                UpdateLayerBlockControlsState();

                // Sort order
                _rbSortRowFirst.Checked = _settings.SortOrderMode == SheetSortOrderMode.RowFirst;
                _rbSortColFirst.Checked = _settings.SortOrderMode == SheetSortOrderMode.ColumnFirst;
                _rbSortSelection.Checked = _settings.SortOrderMode == SheetSortOrderMode.SelectionOrder;
                _rbDirLeftToRight.Checked = !_settings.ReverseX;
                _rbDirRightToLeft.Checked = _settings.ReverseX;
                _rbDirTopToBottom.Checked = !_settings.ReverseY;
                _rbDirBottomToTop.Checked = _settings.ReverseY;
                UpdateSortOrderControlsState();
                UpdateSearchRegionUiState();

                // Plotter & style
                _nudCopies.Value = Math.Max(1, Math.Min(99, _settings.PlotCopies));
                _chkPrintLineweights.Checked = _settings.PrintLineweights;
                _chkUsePlotStyle.Checked = _settings.UsePlotStyle;
                _cmbColorMode.SelectedIndex = (int)_settings.DefaultColorMode;

                // Paper & scale
                _chkAutoMatchPaper.Checked = _settings.AutoMatchPaperSize;
                _chkFitToPaper.Checked = _settings.UseFitToPaper;
                _chkUseCustomScale.Checked = _settings.UseCustomFixedScale;
                _txtScaleNum.Text = (_settings.CustomScaleNumerator > 0 ? _settings.CustomScaleNumerator : 1.0).ToString("G", CultureInfo.InvariantCulture);
                _txtScaleDen.Text = (_settings.CustomScaleDenominator > 0 ? _settings.CustomScaleDenominator : (_settings.DefaultScaleValue > 0 ? _settings.DefaultScaleValue : 100.0)).ToString("G", CultureInfo.InvariantCulture);

                // Offset & orientation
                _chkCenterPlot.Checked = _settings.CenterPlot;
                _txtOffsetX.Text = _settings.PlotOffsetX.ToString("F2", CultureInfo.InvariantCulture);
                _txtOffsetY.Text = _settings.PlotOffsetY.ToString("F2", CultureInfo.InvariantCulture);
                _txtOffsetX.Enabled = !_settings.CenterPlot;
                _txtOffsetY.Enabled = !_settings.CenterPlot;

                _rbOrientAuto.Checked = _settings.OrientationPolicy == PlotOrientationPolicy.Auto;
                _rbOrientPortrait.Checked = _settings.OrientationPolicy == PlotOrientationPolicy.Portrait;
                _rbOrientLandscape.Checked = _settings.OrientationPolicy == PlotOrientationPolicy.Landscape;
                _chkUpsideDown.Checked = _settings.UpsideDown;

                // Output
                _rbSinglePdf.Checked = _settings.OutputMode == OutputMode.SinglePdfPerSheet;
                _rbMergedPdf.Checked = _settings.OutputMode == OutputMode.MergedMultiPagePdf;
                _chkSaveToDwgDir.Checked = _settings.SaveToDwgDirectory;
                _chkSaveToCustomDir.Checked = !_settings.SaveToDwgDirectory;
                _txtOutputDir.Text = _settings.OutputDirectory;
                _chkAppendPaperSize.Checked = _settings.AppendPaperSizeToFileName;
                _txtFileNameTemplate.Text = _settings.FileNameTemplate;
                _txtMergedFileName.Text = _namingService.FormatMergedFileName(_settings.MergedFileNameTemplate, _doc?.Name);
                _cmbDuplicatePolicy.SelectedIndex = (int)_settings.DuplicatePolicy;
                _chkAutoCropPdf.Checked = _settings.AutoCropNonStandardPdf;
                _txtCropMarginMm.Text = _settings.PdfCropMarginMm.ToString("G", CultureInfo.InvariantCulture);
                _chkConfirmPageCount.Checked = _settings.ConfirmPageCountBeforePlot;
                UpdateOutputDirControlsState();

                RefreshPlotStyleList();
                RefreshPc3MediaCatalog();
            }
            finally
            {
                _isUpdatingUi = false;
            }

            if (_settings.RecognitionMode != FrameRecognitionMode.ManualPick)
            {
                ScanModelSpaceFrames();
            }
        }

        private void PopulateLayerAndBlockDropdowns()
        {
            Document activeDoc = CoreApp.DocumentManager.MdiActiveDocument ?? _doc;
            if (activeDoc == null) return;
            _doc = activeDoc;
            try
            {
                string prevLayer = _cmbLayerList.Text;
                string prevBlock = _cmbBlockList.Text;

                List<string> layers;
                List<string> blocks;
                using (DocumentLock docLock = _doc.LockDocument())
                {
                    layers = FrameScanner.GetAllLayerNames(_doc.Database, _chkIncludeNonPlottable.Checked);
                    blocks = FrameScanner.GetModelSpaceBlockNames(_doc.Database);
                }

                _cmbLayerList.Items.Clear();
                _cmbLayerList.Items.Add("TK,BORDER,图框,PUB_TITLE");
                foreach (string l in layers)
                {
                    _cmbLayerList.Items.Add(l);
                }
                if (!string.IsNullOrWhiteSpace(prevLayer)) _cmbLayerList.Text = prevLayer;

                _cmbBlockList.Items.Clear();
                foreach (string b in blocks)
                {
                    _cmbBlockList.Items.Add(b);
                }
                if (!string.IsNullOrWhiteSpace(prevBlock)) _cmbBlockList.Text = prevBlock;
            }
            catch
            {
            }
        }

        private void UpdateLayerBlockControlsState()
        {
            bool byLayer = _rbModeByLayer.Checked;
            bool byBlock = _rbModeByBlock.Checked;
            _cmbLayerList.Enabled = byLayer;
            _cmbBlockList.Enabled = byBlock;

            if (_rbModeManualPick.Checked)
            {
                _btnPickLayerOrBlock.Text = "在图中点选/框选图框 <";
            }
            else if (byBlock)
            {
                _btnPickLayerOrBlock.Text = "点选图块 + 框选搜图范围 <";
            }
            else
            {
                _btnPickLayerOrBlock.Text = "点选图层 + 框选搜图范围 <";
            }
        }

        private void UpdateSortOrderControlsState()
        {
            bool isSpatialSort = !_rbSortSelection.Checked;
            _rbDirLeftToRight.Enabled = isSpatialSort;
            _rbDirRightToLeft.Enabled = isSpatialSort;
            _rbDirTopToBottom.Enabled = isSpatialSort;
            _rbDirBottomToTop.Enabled = isSpatialSort;

            SheetSortOrderMode mode = _rbSortColFirst.Checked
                ? SheetSortOrderMode.ColumnFirst
                : (_rbSortSelection.Checked ? SheetSortOrderMode.SelectionOrder : SheetSortOrderMode.RowFirst);

            bool revX = _rbDirRightToLeft.Checked;
            bool revY = _rbDirBottomToTop.Checked;

            _lblSortOrderDesc.Text = "当前顺序: " + mode.GetSortOrderDescription(revX, revY);
        }

        private void UpdateSearchRegionUiState()
        {
            if (_searchRegion.HasValue && _searchRegion.Value.Width > 1.0 && _searchRegion.Value.Height > 1.0)
            {
                _lblSearchRegionStatus.Text = string.Format(
                    "搜图范围: 已框选 ({0:F0}×{1:F0})",
                    _searchRegion.Value.Width,
                    _searchRegion.Value.Height);
                _lblSearchRegionStatus.ForeColor = Color.FromArgb(0, 120, 60);
                _btnFullRegion.Visible = true;
                _btnSelectSearchRegion.Text = "重选搜图范围 <";
            }
            else
            {
                _lblSearchRegionStatus.Text = "搜图范围: 全图模型空间";
                _lblSearchRegionStatus.ForeColor = Color.FromArgb(40, 90, 140);
                _btnFullRegion.Visible = false;
                _btnSelectSearchRegion.Text = "框选搜图范围 <";
            }
        }

        private void RefreshViewportOrderTransients()
        {
            if (_doc == null) return;
            try
            {
                _cadInteraction.UpdateSheetOrderTransients(_doc, _sheets, _searchRegion);
            }
            catch
            {
            }
        }

        private void UpdateOutputDirControlsState()
        {
            bool customDir = _chkSaveToCustomDir.Checked;
            _txtOutputDir.Enabled = customDir;
            _btnBrowseDir.Enabled = customDir;
        }

        private void OnRecognitionModeChanged(object sender, EventArgs e)
        {
            if (_isUpdatingUi) return;
            if (sender is RadioButton rb && !rb.Checked) return;

            if (_rbModeManualPick.Checked)
            {
                _settings.RecognitionMode = FrameRecognitionMode.ManualPick;
                UpdateLayerBlockControlsState();
                PromptPickFramesInCad();
            }
            else if (_rbModeByBlock.Checked)
            {
                _settings.RecognitionMode = FrameRecognitionMode.ByBlock;
                UpdateLayerBlockControlsState();
                ScanModelSpaceFrames();
            }
            else
            {
                _settings.RecognitionMode = FrameRecognitionMode.ByLayer;
                UpdateLayerBlockControlsState();
                ScanModelSpaceFrames();
            }
        }

        private void OnActiveLayerOrBlockChanged(object sender, EventArgs e)
        {
            if (_isUpdatingUi) return;
            _settings.ActiveFrameLayer = _cmbLayerList.Text.Trim();
            _settings.ActiveFrameBlockName = _cmbBlockList.Text.Trim();
            ScanModelSpaceFrames();
        }

        private void OnPickLayerOrBlockFromDrawingClick(object sender, EventArgs e)
        {
            Document activeDoc = CoreApp.DocumentManager.MdiActiveDocument ?? _doc;
            if (activeDoc == null) return;
            _doc = activeDoc;

            if (_rbModeManualPick.Checked)
            {
                PromptPickFramesInCad();
                return;
            }

            RunInCommandContext(() =>
            {
                FrameRecognitionMode currentMode = _rbModeByBlock.Checked
                    ? FrameRecognitionMode.ByBlock
                    : FrameRecognitionMode.ByLayer;

                if (_cadInteraction.PromptPickLayerOrBlockAndSearchRegion(
                    _doc,
                    currentMode,
                    _cmbLayerList.Text.Trim(),
                    _cmbBlockList.Text.Trim(),
                    out string pickedLayer,
                    out string pickedBlock,
                    out Extents2D? pickedRegion))
                {
                    _isUpdatingUi = true;
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(pickedLayer))
                        {
                            _cmbLayerList.Text = pickedLayer;
                            _settings.ActiveFrameLayer = pickedLayer;
                        }
                        if (!string.IsNullOrWhiteSpace(pickedBlock))
                        {
                            _cmbBlockList.Text = pickedBlock;
                            _settings.ActiveFrameBlockName = pickedBlock;
                        }

                        _searchRegion = pickedRegion;
                        UpdateLayerBlockControlsState();
                        UpdateSearchRegionUiState();
                    }
                    finally
                    {
                        _isUpdatingUi = false;
                    }

                    ScanModelSpaceFrames();
                }
            }, hideForm: true);
        }

        private void OnClearLayerBlockClick(object sender, EventArgs e)
        {
            _isUpdatingUi = true;
            try
            {
                _cmbLayerList.Text = "TK,BORDER,图框,PUB_TITLE";
                _cmbBlockList.Text = "";
                _settings.ActiveFrameLayer = _cmbLayerList.Text;
                _settings.ActiveFrameBlockName = "";
                _searchRegion = null;
                UpdateSearchRegionUiState();
            }
            finally
            {
                _isUpdatingUi = false;
            }
            ScanModelSpaceFrames();
        }

        private void OnSortOrderChanged(object sender, EventArgs e)
        {
            if (_isUpdatingUi) return;
            if (sender is RadioButton rb && !rb.Checked) return;

            if (_rbSortColFirst.Checked) _settings.SortOrderMode = SheetSortOrderMode.ColumnFirst;
            else if (_rbSortSelection.Checked) _settings.SortOrderMode = SheetSortOrderMode.SelectionOrder;
            else _settings.SortOrderMode = SheetSortOrderMode.RowFirst;

            _settings.ReverseX = _rbDirRightToLeft.Checked;
            _settings.ReverseY = _rbDirBottomToTop.Checked;

            UpdateSortOrderControlsState();
            SaveCurrentUiToSettings();

            if (_sheets != null && _sheets.Count > 0)
            {
                _sheets = _geometryAnalyzer.SortPrintSheets(
                    _sheets,
                    _settings.SortOrderMode,
                    _settings.ReverseX,
                    _settings.ReverseY);

                ReevaluateAllSheets(preserveCustomFileName: false);
                RefreshViewportOrderTransients();
                _lblCurrentTaskStatus.Text = string.Format(
                    "已按 [{0}] 重新排列 {1} 张图框顺序，并在 CAD 视图中更新序号。",
                    _settings.SortOrderMode.GetSortOrderDescription(_settings.ReverseX, _settings.ReverseY),
                    _sheets.Count);
            }
        }

        private void OnHighlightFramesClick(object sender, EventArgs e)
        {
            Document activeDoc = CoreApp.DocumentManager.MdiActiveDocument ?? _doc;
            if (activeDoc == null || _sheets.Count == 0)
            {
                MessageBox.Show(this, "当前没有可亮显的图框。", "亮显图框", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            _doc = activeDoc;

            _cadInteraction.HighlightSheetsInViewport(_doc, _sheets, _searchRegion);
            _lblCurrentTaskStatus.Text = string.Format("已在 CAD 视图中全览并亮显 {0} 张图框顺序（您可直接操作 CAD 视图或点击“折叠窗口 ▲”查看）。", _sheets.Count);
        }

        private void OnSelectSearchRegionClick(object sender, EventArgs e)
        {
            Document activeDoc = CoreApp.DocumentManager.MdiActiveDocument ?? _doc;
            if (activeDoc == null) return;
            _doc = activeDoc;

            RunInCommandContext(() =>
            {
                _searchRegion = _cadInteraction.PromptSelectSearchRegion(_doc);
                UpdateSearchRegionUiState();
                ScanModelSpaceFrames();
            }, hideForm: true);
        }

        private void PromptPickFramesInCad()
        {
            Document activeDoc = CoreApp.DocumentManager.MdiActiveDocument ?? _doc;
            if (activeDoc == null) return;
            _doc = activeDoc;

            RunInCommandContext(() =>
            {
                var pickedCandidates = _cadInteraction.PromptPickFrameEntities(_doc, _frameScanner, _settings);
                if (pickedCandidates != null && pickedCandidates.Count > 0)
                {
                    var sortedPicked = _geometryAnalyzer.DeduplicateAndRank(
                        pickedCandidates,
                        _settings.SortOrderMode,
                        _settings.ReverseX,
                        _settings.ReverseY);

                    DrawingUnits units;
                    using (DocumentLock docLock = _doc.LockDocument())
                    {
                        units = FrameScanner.ReadDatabaseDrawingUnits(_doc.Database);
                    }
                    _sheets = _sheetPrepService.CreatePrintSheets(
                        sortedPicked,
                        _doc.Name,
                        units,
                        _settings,
                        _availableMedia);

                    PopulateGridFromSheets();
                    UpdateSummaryCounts();
                    RefreshViewportOrderTransients();
                    _lblCurrentTaskStatus.Text = string.Format(
                        "点选图框完成：共识别到 {0} 个图框，已按 [{1}] 编号。",
                        _sheets.Count,
                        _settings.SortOrderMode.GetSortOrderDescription(_settings.ReverseX, _settings.ReverseY));
                }
            }, hideForm: true);
        }

        private void RefreshPlotStyleList()
        {
            Document activeDoc = CoreApp.DocumentManager.MdiActiveDocument ?? _doc;
            if (activeDoc != null) _doc = activeDoc;

            if (_doc != null)
            {
                using (DocumentLock docLock = _doc.LockDocument())
                {
                    _availablePlotStyles = _mediaCatalog.GetAvailablePlotStyleSheets(_doc.Database);
                }
            }
            else
            {
                _availablePlotStyles = _mediaCatalog.GetAvailablePlotStyleSheets(null);
            }

            _cmbPlotStyle.Items.Clear();
            _cmbPlotStyle.Items.Add("无");
            foreach (string style in _availablePlotStyles)
            {
                _cmbPlotStyle.Items.Add(style);
            }

            int idx = _availablePlotStyles.FindIndex(s =>
                string.Equals(s, _settings.DefaultPlotStyle, StringComparison.OrdinalIgnoreCase));
            _cmbPlotStyle.SelectedIndex = idx >= 0 ? idx + 1 : 0;

            _batchFilesPanel?.RefreshPlotStyles(_availablePlotStyles);
        }

        private void RefreshPc3MediaCatalog()
        {
            _availableMedia = _mediaCatalog.RefreshMediaList(out _deviceCheck);

            _cmbManualPaper.Items.Clear();
            foreach (var media in _availableMedia)
            {
                _cmbManualPaper.Items.Add(media);
            }

            if (_deviceCheck.IsAvailable && _availableMedia.Count > 0)
            {
                _lblDeviceStatus.Text = string.Format("{0} ({1}种纸)", _deviceCheck.MatchedDeviceName, _availableMedia.Count);
                _lblDeviceStatus.ForeColor = Color.FromArgb(16, 124, 16);
                _btnStartPlot.Enabled = true;

                int selectedIdx = _availableMedia.FindIndex(m =>
                    string.Equals(m.CanonicalName, _settings.ManualPaperCanonicalName, StringComparison.OrdinalIgnoreCase));
                if (selectedIdx < 0)
                {
                    selectedIdx = _availableMedia.FindIndex(m =>
                        m.LocaleName.IndexOf("full bleed A2", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        m.LocaleName.IndexOf("full bleed A1", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        m.LocaleName.IndexOf("A3", StringComparison.OrdinalIgnoreCase) >= 0);
                }
                if (selectedIdx < 0 && _availableMedia.Count > 0) selectedIdx = 0;

                if (selectedIdx >= 0)
                {
                    _cmbManualPaper.SelectedIndex = selectedIdx;
                }
            }
            else
            {
                _lblDeviceStatus.Text = _deviceCheck.DiagnosticMessage;
                _lblDeviceStatus.ForeColor = Color.Red;
                _btnStartPlot.Enabled = false;
                _logService.LogError("DeviceCheckFailed", _deviceCheck.DiagnosticMessage);
            }

            UpdatePaperControlsEnabledState();
        }

        private void RefreshPc3AndReevaluateSheets()
        {
            RefreshPc3MediaCatalog();
            ReevaluateAllSheets(preserveCustomFileName: true);
        }

        private void UpdatePaperControlsEnabledState()
        {
            bool autoMatch = _chkAutoMatchPaper.Checked;
            _cmbManualPaper.Enabled = _deviceCheck != null && _deviceCheck.IsAvailable;
            _btnApplyManualPaper.Enabled = _deviceCheck != null && _deviceCheck.IsAvailable;
            if (!autoMatch && _cmbManualPaper.SelectedItem is PaperMedia media)
            {
                _settings.ManualPaperCanonicalName = media.CanonicalName;
            }
        }

        private void ScanModelSpaceFrames()
        {
            Document activeDoc = CoreApp.DocumentManager.MdiActiveDocument ?? _doc;
            if (activeDoc == null) return;
            _doc = activeDoc;

            try
            {
                SaveCurrentUiToSettings();
                _lblCurrentTaskStatus.Text = "正在扫描模型空间图框...";
                using (DocumentLock docLock = _doc.LockDocument())
                {
                    var candidates = _frameScanner.ScanModelSpaceFrames(_doc.Database, _settings, _searchRegion);
                    DrawingUnits units = FrameScanner.ReadDatabaseDrawingUnits(_doc.Database);

                    _sheets = _sheetPrepService.CreatePrintSheets(
                        candidates,
                        _doc.Name,
                        units,
                        _settings,
                        _availableMedia);
                }

                PopulateGridFromSheets();
                UpdateSummaryCounts();
                RefreshViewportOrderTransients();
                _lblCurrentTaskStatus.Text = string.Format(
                    "扫描完成：共识别到 {0} 个有效图框{1}，当前按 [{2}] 排列。",
                    _sheets.Count,
                    _searchRegion.HasValue ? " (限定搜图区域内)" : "",
                    _settings.SortOrderMode.GetSortOrderDescription(_settings.ReverseX, _settings.ReverseY));
                _logService.LogInfo("FrameScanCompleted", string.Format("Scanned {0} frames in {1}", _sheets.Count, _doc.Name));
            }
            catch (Exception ex)
            {
                _lblCurrentTaskStatus.Text = "扫描图框异常: " + ex.Message;
                _logService.LogError("FrameScanFailed", ex.Message, ex);
                MessageBox.Show(this, "扫描模型空间图框时发生错误:\n" + ex.Message, "扫描错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void PopulateGridFromSheets()
        {
            _isUpdatingUi = true;
            try
            {
                _gridSheets.Rows.Clear();
                for (int i = 0; i < _sheets.Count; i++)
                {
                    var s = _sheets[i];
                    s.OrderIndex = i + 1;
                    int rowIdx = _gridSheets.Rows.Add();
                    DataGridViewRow row = _gridSheets.Rows[rowIdx];
                    row.Tag = s;
                    UpdateGridRowFromSheet(row, s);
                }
            }
            finally
            {
                _isUpdatingUi = false;
            }
        }

        private void UpdateGridRowFromSheet(DataGridViewRow row, PrintSheet s)
        {
            row.Cells["ColSelected"].Value = s.IsSelected;
            row.Cells["ColIndex"].Value = s.OrderIndex;
            row.Cells["ColDrawingNo"].Value = s.DrawingNumber ?? "";
            row.Cells["ColDrawingName"].Value = s.DrawingName ?? "";
            row.Cells["ColFrameType"].Value = FormatFrameType(s);
            row.Cells["ColGeometry"].Value = s.FormattedGeometryInfo;
            row.Cells["ColScale"].Value = s.FormattedScaleInfo;

            string paperLabel = string.IsNullOrWhiteSpace(s.TargetPaperCanonicalName)
                ? "(未匹配/需确认)"
                : string.Format("{0} ({1:F0}x{2:F0})", s.TargetPaperLocaleName, s.PaperWidthMm, s.PaperHeightMm);
            row.Cells["ColPaper"].Value = paperLabel;
            row.Cells["ColOutputFileName"].Value = s.OutputFileName ?? "";
            row.Cells["ColStatus"].Value = s.FormattedStatusSummary;

            if (s.Status == SheetStatus.Failed)
            {
                row.DefaultCellStyle.BackColor = Color.FromArgb(255, 228, 228);
            }
            else if (s.Status == SheetStatus.NeedsConfirmation)
            {
                row.DefaultCellStyle.BackColor = Color.FromArgb(255, 244, 214);
            }
            else if (s.Status == SheetStatus.Succeeded)
            {
                row.DefaultCellStyle.BackColor = Color.FromArgb(226, 246, 226);
            }
            else if (s.WarningMessages != null && s.WarningMessages.Count > 0)
            {
                row.DefaultCellStyle.BackColor = Color.FromArgb(252, 250, 235);
            }
            else
            {
                row.DefaultCellStyle.BackColor = Color.White;
            }
        }

        private static string FormatFrameType(PrintSheet s)
        {
            string spaceTag = (!string.IsNullOrWhiteSpace(s.Space) && s.Space != "模型空间")
                ? "[" + s.Space + "] "
                : "";
            string layerPrefix = string.IsNullOrWhiteSpace(s.LayerName) ? "" : s.LayerName + " / ";
            switch (s.FrameType)
            {
                case FrameType.BlockReference:
                    return spaceTag + layerPrefix + (string.IsNullOrWhiteSpace(s.BlockName) ? "图框块" : "块:" + s.BlockName);
                case FrameType.ManualWindow:
                    return spaceTag + "手动框选";
                case FrameType.LineLoop:
                    return spaceTag + layerPrefix + "直线闭合框";
                case FrameType.Polyline:
                default:
                    return spaceTag + layerPrefix + "多段线框";
            }
        }

        private void OnGridCurrentCellDirtyStateChanged(object sender, EventArgs e)
        {
            if (_gridSheets.IsCurrentCellDirty && _gridSheets.CurrentCell is DataGridViewCheckBoxCell)
            {
                _gridSheets.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        }

        private void OnGridCellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (_isUpdatingUi || e.RowIndex < 0 || e.RowIndex >= _gridSheets.Rows.Count) return;

            DataGridViewRow row = _gridSheets.Rows[e.RowIndex];
            if (!(row.Tag is PrintSheet sheet)) return;

            string colName = _gridSheets.Columns[e.ColumnIndex].Name;
            _isUpdatingUi = true;
            try
            {
                if (colName == "ColSelected")
                {
                    sheet.IsSelected = Convert.ToBoolean(row.Cells["ColSelected"].Value ?? false);
                }
                else if (colName == "ColDrawingNo")
                {
                    sheet.DrawingNumber = Convert.ToString(row.Cells["ColDrawingNo"].Value ?? "").Trim();
                    _sheetPrepService.RefreshSheetPaperAndStatus(sheet, _settings, _availableMedia, preserveCustomFileName: false);
                }
                else if (colName == "ColDrawingName")
                {
                    sheet.DrawingName = Convert.ToString(row.Cells["ColDrawingName"].Value ?? "").Trim();
                    _sheetPrepService.RefreshSheetPaperAndStatus(sheet, _settings, _availableMedia, preserveCustomFileName: false);
                }
                else if (colName == "ColScale")
                {
                    string rawScale = Convert.ToString(row.Cells["ColScale"].Value ?? "").Trim();
                    double? parsed = ScaleParser.TryParseScaleValue(rawScale);
                    if (parsed.HasValue && parsed.Value > 0)
                    {
                        sheet.FitToPaper = false;
                        sheet.ScaleValue = parsed.Value;
                        sheet.ScaleSource = ScaleSource.UserSpecified;
                        _sheetPrepService.RefreshSheetPaperAndStatus(sheet, _settings, _availableMedia, preserveCustomFileName: true);
                    }
                    else
                    {
                        MessageBox.Show(this, "无效的比例格式，请输入例如 1:100 或 100。", "比例输入提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
                else if (colName == "ColOutputFileName")
                {
                    string customName = Convert.ToString(row.Cells["ColOutputFileName"].Value ?? "").Trim();
                    sheet.OutputFileName = _namingService.EnsureValidPdfFileName(customName);
                }

                UpdateGridRowFromSheet(row, sheet);
                UpdateSummaryCounts();
            }
            finally
            {
                _isUpdatingUi = false;
            }
        }

        private void OnGridCellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _gridSheets.Rows.Count || e.ColumnIndex < 0) return;
            string colName = _gridSheets.Columns[e.ColumnIndex].Name;
            DataGridViewRow row = _gridSheets.Rows[e.RowIndex];
            if (!(row.Tag is PrintSheet sheet)) return;

            if (colName == "ColIndex" || colName == "ColFrameType" || colName == "ColGeometry")
            {
                _cadInteraction.ZoomToSheet(_doc, sheet);
                _lblCurrentTaskStatus.Text = string.Format("已在模型空间定位并亮显图框 #{0} ({1})", sheet.OrderIndex, sheet.FrameHandle);
                return;
            }

            if (colName != "ColPaper") return;

            if (_availableMedia == null || _availableMedia.Count == 0)
            {
                MessageBox.Show(this, "当前 DWG To PDF.pc3 纸张列表为空，请先检查绘图仪配置。", "纸张选择", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            PaperMedia chosen = PromptSelectPaperMediaDialog(sheet.TargetPaperCanonicalName);
            if (chosen != null)
            {
                ApplySelectedMediaToSingleSheet(sheet, chosen);
                UpdateGridRowFromSheet(row, sheet);
                UpdateSummaryCounts();
            }
        }

        private void OnFilterPaperClick(object sender, EventArgs e)
        {
            string current = (_cmbManualPaper.SelectedItem as PaperMedia)?.CanonicalName;
            PaperMedia chosen = PromptSelectPaperMediaDialog(current);
            if (chosen != null)
            {
                int idx = _availableMedia.FindIndex(m => string.Equals(m.CanonicalName, chosen.CanonicalName, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0) _cmbManualPaper.SelectedIndex = idx;
            }
        }

        private PaperMedia PromptSelectPaperMediaDialog(string currentCanonicalName)
        {
            using (var dlg = new Form())
            {
                dlg.AutoScaleMode = AutoScaleMode.None;
                dlg.Text = "从 DWG To PDF.pc3 搜索/过滤纸张尺寸";
                dlg.Size = SS(560, 460);
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.MaximizeBox = false;
                dlg.MinimizeBox = false;
                dlg.Font = Font;

                var txtFilter = new TextBox { Dock = DockStyle.Fill };
                var listBox = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
                var bottomPanel = new FlowLayoutPanel
                {
                    Dock = DockStyle.Bottom,
                    Height = S(46),
                    FlowDirection = FlowDirection.RightToLeft,
                    Padding = SP(6, 6, 6, 6)
                };

                var btnCancel = new Button
                {
                    Text = "取消",
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    MinimumSize = SS(84, 28),
                    Margin = SP(6, 0, 0, 0),
                    DialogResult = DialogResult.Cancel
                };
                var btnOk = new Button
                {
                    Text = "确定选择",
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    MinimumSize = SS(94, 28),
                    Margin = SP(6, 0, 0, 0),
                    DialogResult = DialogResult.OK
                };
                bottomPanel.Controls.Add(btnCancel);
                bottomPanel.Controls.Add(btnOk);

                void PopulateList(string keyword)
                {
                    listBox.Items.Clear();
                    foreach (var m in _availableMedia)
                    {
                        if (string.IsNullOrWhiteSpace(keyword) ||
                            m.DisplayLabel.IndexOf(keyword.Trim(), StringComparison.OrdinalIgnoreCase) >= 0 ||
                            m.CanonicalName.IndexOf(keyword.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            int idx = listBox.Items.Add(m);
                            if (string.Equals(m.CanonicalName, currentCanonicalName, StringComparison.OrdinalIgnoreCase))
                            {
                                listBox.SelectedIndex = idx;
                            }
                        }
                    }
                    if (listBox.SelectedIndex < 0 && listBox.Items.Count > 0)
                    {
                        listBox.SelectedIndex = 0;
                    }
                }

                txtFilter.TextChanged += (s, e) => PopulateList(txtFilter.Text);
                listBox.DoubleClick += (s, e) =>
                {
                    if (listBox.SelectedItem != null)
                    {
                        dlg.DialogResult = DialogResult.OK;
                        dlg.Close();
                    }
                };

                var topPanel = new Panel { Dock = DockStyle.Top, Height = S(38), Padding = SP(6, 6, 6, 4) };
                var lblSearch = new Label { Text = "关键字过滤:", Dock = DockStyle.Left, Width = S(86), TextAlign = ContentAlignment.MiddleLeft };
                topPanel.Controls.Add(txtFilter);
                topPanel.Controls.Add(lblSearch);

                dlg.Controls.Add(listBox);
                dlg.Controls.Add(topPanel);
                dlg.Controls.Add(bottomPanel);
                dlg.AcceptButton = btnOk;
                dlg.CancelButton = btnCancel;

                PopulateList("");

                if (dlg.ShowDialog(this) == DialogResult.OK && listBox.SelectedItem is PaperMedia media)
                {
                    return media;
                }
                return null;
            }
        }

        private void OnManualPickClick(object sender, EventArgs e)
        {
            Document activeDoc = CoreApp.DocumentManager.MdiActiveDocument ?? _doc;
            if (activeDoc == null) return;
            _doc = activeDoc;

            RunInCommandContext(() =>
            {
                var picked = _cadInteraction.PromptPickFrameEntities(_doc, _frameScanner, _settings);
                if (picked != null && picked.Count > 0)
                {
                    DrawingUnits units;
                    using (DocumentLock docLock = _doc.LockDocument())
                    {
                        units = FrameScanner.ReadDatabaseDrawingUnits(_doc.Database);
                    }
                    var addedList = _sheetPrepService.CreatePrintSheets(
                        picked,
                        _doc.Name,
                        units,
                        _settings,
                        _availableMedia);

                    foreach (var addedSheet in addedList)
                    {
                        addedSheet.OrderIndex = _sheets.Count + 1;
                        addedSheet.OutputFileName = _namingService.FormatSheetFileName(
                            _settings.FileNameTemplate,
                            _doc.Name,
                            addedSheet.OrderIndex,
                            addedSheet.DrawingNumber,
                            addedSheet.DrawingName);
                        _sheets.Add(addedSheet);
                    }

                    PopulateGridFromSheets();
                    UpdateSummaryCounts();
                    RefreshViewportOrderTransients();
                }
            }, hideForm: true);
        }

        private void SetAllSelection(bool selected)
        {
            foreach (var s in _sheets)
            {
                s.IsSelected = selected;
            }
            PopulateGridFromSheets();
            UpdateSummaryCounts();
            RefreshViewportOrderTransients();
        }

        private void InvertSelection()
        {
            foreach (var s in _sheets)
            {
                s.IsSelected = !s.IsSelected;
            }
            PopulateGridFromSheets();
            UpdateSummaryCounts();
            RefreshViewportOrderTransients();
        }

        private void OnDeleteSelectedClick(object sender, EventArgs e)
        {
            var toRemove = new HashSet<PrintSheet>();
            foreach (DataGridViewRow row in _gridSheets.SelectedRows)
            {
                if (row.Tag is PrintSheet s) toRemove.Add(s);
            }
            if (toRemove.Count == 0)
            {
                foreach (var s in _sheets.Where(x => x.IsSelected).ToList())
                {
                    toRemove.Add(s);
                }
            }

            if (toRemove.Count == 0) return;

            _sheets.RemoveAll(s => toRemove.Contains(s));
            PopulateGridFromSheets();
            UpdateSummaryCounts();
            RefreshViewportOrderTransients();
        }

        private void MoveSelectedRow(int delta)
        {
            if (_gridSheets.SelectedRows.Count == 0) return;
            int idx = _gridSheets.SelectedRows[0].Index;
            int newIdx = idx + delta;
            if (newIdx < 0 || newIdx >= _sheets.Count) return;

            PrintSheet item = _sheets[idx];
            _sheets.RemoveAt(idx);
            _sheets.Insert(newIdx, item);

            PopulateGridFromSheets();
            _gridSheets.ClearSelection();
            _gridSheets.Rows[newIdx].Selected = true;
            UpdateSummaryCounts();
            RefreshViewportOrderTransients();
        }

        private void OnPreviewSelectedSheetClick(object sender, EventArgs e)
        {
            PrintSheet target = GetPrimaryHighlightedSheet();
            if (target == null)
            {
                MessageBox.Show(this, "请先在清单中选中一张要定位预览的图框。", "定位预览", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _cadInteraction.ZoomToSheet(_doc, target);
            _lblCurrentTaskStatus.Text = string.Format("已在模型空间视图中定位并亮显图框 #{0} ({1})", target.OrderIndex, target.FrameHandle);
        }

        private void OnPlotPreviewSelectedSheetClick(object sender, EventArgs e)
        {
            PrintSheet target = GetPrimaryHighlightedSheet();
            if (target == null)
            {
                MessageBox.Show(this, "请先在清单中选中一张要全屏预览的图纸。", "打印预览", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            RunInCommandContext(() =>
            {
                try
                {
                    _lblCurrentTaskStatus.Text = string.Format("正在启动图纸 #{0} [{1}] 的全屏矢量打印预览...", target.OrderIndex, target.DrawingName);
                    _plotJobRunner.PreviewSingleSheet(_doc, target, _settings);
                    _lblCurrentTaskStatus.Text = string.Format("图纸 #{0} 打印预览已结束。", target.OrderIndex);
                }
                catch (Exception ex)
                {
                    _lblCurrentTaskStatus.Text = "打印预览失败: " + ex.Message;
                    MessageBox.Show(this, "打印预览失败: " + ex.Message, "预览错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            });
        }

        private void OnTeachFrameClick(object sender, EventArgs e)
        {
            _lblCurrentTaskStatus.Text = "请在 CAD 图纸中点选图框块...";
            RunInCommandContext(() =>
            {
                AcEd.Editor ed = _doc.Editor;
                var peo = new AcEd.PromptEntityOptions("\n请在图纸中点选一个图框块: ");
                peo.SetRejectMessage("\n请点选一个图框块参照 (BlockReference)。");
                peo.AddAllowedClass(typeof(AcDb.BlockReference), true);
                AcEd.PromptEntityResult per = ed.GetEntity(peo);
                if (per.Status != AcEd.PromptStatus.OK) return;

                if (_settings.FrameRules == null || _settings.FrameRules.Count == 0)
                {
                    _settings.FrameRules = BatchPdfSettings.CreateDefault().FrameRules;
                }
                var rule = _settings.FrameRules[0];

                string effectiveBlockName;
                string blockLayer;
                var numTags = new List<string>();
                var nameTags = new List<string>();
                var scaleTags = new List<string>();

                using (AcDb.Transaction tr = _doc.Database.TransactionManager.StartTransaction())
                {
                    var br = tr.GetObject(per.ObjectId, AcDb.OpenMode.ForRead) as AcDb.BlockReference;
                    if (br == null)
                    {
                        ed.WriteMessage("\n[BPTEACH] 所选图元不是图块。闭合多段线已默认支持。\n");
                        return;
                    }
                    effectiveBlockName = FrameScanner.GetEffectiveBlockName(tr, br);
                    blockLayer = (br.Layer ?? "").Trim();

                    foreach (AcDb.ObjectId attId in br.AttributeCollection)
                    {
                        var ar = tr.GetObject(attId, AcDb.OpenMode.ForRead) as AcDb.AttributeReference;
                        if (ar == null || string.IsNullOrWhiteSpace(ar.Tag)) continue;
                        string tag = ar.Tag.Trim();
                        string upper = tag.ToUpperInvariant();
                        if (tag.Contains("图号") || tag.Contains("编号") || upper.Contains("DWGNO") || upper.Contains("DWG_NO") || upper.Contains("SHEET_NO") || upper.Contains("DRAWING_NO") || upper == "NO")
                        {
                            if (!numTags.Contains(tag)) numTags.Add(tag);
                        }
                        else if (tag.Contains("图名") || tag.Contains("名称") || upper.Contains("TITLE") || upper.Contains("NAME"))
                        {
                            if (!nameTags.Contains(tag)) nameTags.Add(tag);
                        }
                        else if (tag.Contains("比例") || upper.Contains("SCALE") || upper == "BL")
                        {
                            if (!scaleTags.Contains(tag)) scaleTags.Add(tag);
                        }
                    }
                    tr.Commit();
                }

                if (!rule.BlockNames.Any(b => string.Equals(b, effectiveBlockName, StringComparison.OrdinalIgnoreCase)))
                {
                    rule.BlockNames.Add(effectiveBlockName);
                }
                if (!string.IsNullOrWhiteSpace(blockLayer) &&
                    !string.Equals(blockLayer, "0", StringComparison.OrdinalIgnoreCase) &&
                    !rule.AllowedLayers.Any(l => string.Equals(l, blockLayer, StringComparison.OrdinalIgnoreCase)))
                {
                    rule.AllowedLayers.Add(blockLayer);
                }
                foreach (var t in numTags) if (!rule.SheetNumberTags.Any(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase))) rule.SheetNumberTags.Add(t);
                foreach (var t in nameTags) if (!rule.SheetNameTags.Any(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase))) rule.SheetNameTags.Add(t);
                foreach (var t in scaleTags) if (!rule.ScaleTags.Any(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase))) rule.ScaleTags.Add(t);

                _isUpdatingUi = true;
                try
                {
                    _cmbBlockList.Text = effectiveBlockName;
                    _settings.ActiveFrameBlockName = effectiveBlockName;
                }
                finally
                {
                    _isUpdatingUi = false;
                }

                _configService.SaveSettings(_settings);
                ScanModelSpaceFrames();
                _lblCurrentTaskStatus.Text = string.Format("学习完成：已记住图框块 [{0}]，重新扫描图纸中。", effectiveBlockName);
                MessageBox.Show(this, string.Format("已成功学习图框块 [{0}]！\n\n图号属性: {1}\n图名属性: {2}\n出图比例: {3}\n\n已自动刷新图框列表。",
                    effectiveBlockName,
                    numTags.Count > 0 ? string.Join(", ", numTags) : "(未识别)",
                    nameTags.Count > 0 ? string.Join(", ", nameTags) : "(未识别)",
                    scaleTags.Count > 0 ? string.Join(", ", scaleTags) : "(未识别)"), "学习成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
            });
        }

        private void OnCreateDrawingTableClick(object sender, EventArgs e)
        {
            if (_sheets == null || _sheets.Count == 0)
            {
                MessageBox.Show(this, "当前清单中没有图纸，请先扫描图框。", "生成图纸目录", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _lblCurrentTaskStatus.Text = "请在 CAD 图纸中点击图纸目录表的左上角插入点...";
            RunInCommandContext(() =>
            {
                AcEd.Editor ed = _doc.Editor;
                var ppo = new AcEd.PromptPointOptions("\n请指定图纸目录左上角插入基点: ");
                AcEd.PromptPointResult ppr = ed.GetPoint(ppo);
                if (ppr.Status != AcEd.PromptStatus.OK) return;

                if (CadTableService.GenerateDrawingIndexTable(_doc, _sheets, _settings, ppr.Value, out string msg))
                {
                    _lblCurrentTaskStatus.Text = msg;
                    MessageBox.Show(this, msg, "图纸目录已生成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    _lblCurrentTaskStatus.Text = msg;
                    MessageBox.Show(this, msg, "生成失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            });
        }

        private void OnOpenRulesClick(object sender, EventArgs e)
        {
            using (var dlg = new FrameRulesForm(_settings))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    // Sync UI layer/block boxes if the user cleared or updated rules so stale single-item text doesn't override rules
                    if (_settings.FrameRules != null && _settings.FrameRules.Count > 0)
                    {
                        var rule = _settings.FrameRules[0];
                        _isUpdatingUi = true;
                        try
                        {
                            if (rule.BlockNames != null && rule.BlockNames.Count > 0 &&
                                !string.IsNullOrWhiteSpace(_cmbBlockList.Text) &&
                                !rule.BlockNames.Any(b => string.Equals(b, _cmbBlockList.Text.Trim(), StringComparison.OrdinalIgnoreCase)))
                            {
                                _cmbBlockList.Text = "";
                                _settings.ActiveFrameBlockName = "";
                            }
                        }
                        finally
                        {
                            _isUpdatingUi = false;
                        }
                    }

                    _configService.SaveSettings(_settings);
                    ScanModelSpaceFrames();
                }
            }
        }

        private void OnAutoMatchCheckedChanged(object sender, EventArgs e)
        {
            if (_isUpdatingUi) return;
            _settings.AutoMatchPaperSize = _chkAutoMatchPaper.Checked;
            UpdatePaperControlsEnabledState();
            ReevaluateAllSheets(preserveCustomFileName: true);
        }

        private void OnManualPaperSelectionChanged(object sender, EventArgs e)
        {
            if (_isUpdatingUi) return;
            if (_cmbManualPaper.SelectedItem is PaperMedia media)
            {
                _settings.ManualPaperCanonicalName = media.CanonicalName;
                if (!_settings.AutoMatchPaperSize)
                {
                    foreach (var s in _sheets)
                    {
                        s.TargetPaperCanonicalName = media.CanonicalName;
                    }
                    ReevaluateAllSheets(preserveCustomFileName: true);
                }
            }
        }

        private void OnApplyManualPaperToSelectedClick(object sender, EventArgs e)
        {
            if (!(_cmbManualPaper.SelectedItem is PaperMedia media)) return;

            var targets = GetHighlightedOrCheckedSheets();
            foreach (var s in targets)
            {
                ApplySelectedMediaToSingleSheet(s, media);
            }

            PopulateGridFromSheets();
            UpdateSummaryCounts();
        }

        private void ApplySelectedMediaToSingleSheet(PrintSheet s, PaperMedia media)
        {
            SheetPreparationService.ApplyMediaToSheet(s, media);
            PrintableAreaValidationResult val = _paperMatcher.ValidatePrintableArea(
                s.FrameWidthUnits,
                s.FrameHeightUnits,
                s.DrawingUnits,
                media,
                s.ScaleValue,
                s.FitToPaper);

            s.WarningMessages.RemoveAll(w =>
                w.StartsWith("PC3 ") ||
                w.StartsWith("识别为加长图幅") ||
                w.StartsWith("裁切风险") ||
                w.StartsWith("边距提示") ||
                w.StartsWith("未选择"));

            if (!val.IsValid)
            {
                s.WarningMessages.Add(val.WarningMessage);
                s.Status = SheetStatus.NeedsConfirmation;
            }
            else
            {
                if (val.HasClippingRisk && !string.IsNullOrWhiteSpace(val.WarningMessage))
                {
                    s.WarningMessages.Add(val.WarningMessage);
                }
                s.Status = (s.FitToPaper || (s.ScaleValue.HasValue && s.ScaleValue.Value > 0))
                    ? SheetStatus.Ready
                    : SheetStatus.NeedsConfirmation;
            }
        }

        private void OnScaleModeChanged(object sender, EventArgs e)
        {
            if (_isUpdatingUi) return;
            _settings.UseFitToPaper = _chkFitToPaper.Checked;
            _settings.UseCustomFixedScale = _chkUseCustomScale.Checked;

            double? customRatio = TryReadCustomScaleDenominatorFromUi();

            foreach (var s in _sheets)
            {
                s.FitToPaper = _settings.UseFitToPaper;
                if (!s.FitToPaper)
                {
                    if (_settings.UseCustomFixedScale && customRatio.HasValue && customRatio.Value > 0)
                    {
                        s.ScaleValue = customRatio.Value;
                        s.ScaleSource = ScaleSource.UserSpecified;
                    }
                    else if (!s.ScaleValue.HasValue || s.ScaleSource == ScaleSource.FitToPaper || s.ScaleSource == ScaleSource.UserSpecified)
                    {
                        if (ScaleParser.TryInferScaleFromAvailableMedia(
                                s.FrameWidthUnits, s.FrameHeightUnits, s.DrawingUnits, _availableMedia, _settings.PaperMatchToleranceMm,
                                out double inferred, out _))
                        {
                            s.ScaleValue = inferred;
                            s.ScaleSource = ScaleSource.InferredFromPaper;
                        }
                    }
                }
            }
            ReevaluateAllSheets(preserveCustomFileName: true);
        }

        private void OnApplyScaleToSelectedClick(object sender, EventArgs e)
        {
            double? scaleVal = TryReadCustomScaleDenominatorFromUi();
            if (!scaleVal.HasValue || scaleVal.Value <= 0)
            {
                MessageBox.Show(this, "请输入有效的比例数值（例如 [1] 毫米 = [100] 图形单位）。", "比例设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _settings.DefaultScaleValue = scaleVal.Value;
            _chkFitToPaper.Checked = false;
            _settings.UseFitToPaper = false;

            var targets = GetHighlightedOrCheckedSheets();
            foreach (var s in targets)
            {
                s.FitToPaper = false;
                s.ScaleValue = scaleVal.Value;
                s.ScaleSource = ScaleSource.UserSpecified;
                _sheetPrepService.RefreshSheetPaperAndStatus(s, _settings, _availableMedia, preserveCustomFileName: true);
            }

            PopulateGridFromSheets();
            UpdateSummaryCounts();
        }

        private double? TryReadCustomScaleDenominatorFromUi()
        {
            if (double.TryParse(_txtScaleNum.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double num) &&
                double.TryParse(_txtScaleDen.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double den) &&
                num > 0 && den > 0)
            {
                _settings.CustomScaleNumerator = num;
                _settings.CustomScaleDenominator = den;
                return den / num;
            }
            return ScaleParser.TryParseScaleValue(_txtScaleDen.Text.Trim());
        }

        private void OnPlotStyleSettingsChanged(object sender, EventArgs e)
        {
            if (_isUpdatingUi) return;
            SyncPlotStyleSettingsFromUi();
            foreach (var s in _sheets)
            {
                s.PlotStyle = _settings.DefaultPlotStyle;
                s.ColorMode = _settings.DefaultColorMode;
                s.PrintLineweights = _settings.PrintLineweights;
            }
        }

        private void OnNamingSettingsChanged(object sender, EventArgs e)
        {
            if (_isUpdatingUi) return;
            _settings.FileNameTemplate = _txtFileNameTemplate.Text.Trim();
            ReevaluateAllSheets(preserveCustomFileName: false);
        }

        private void OnSettingsChanged(object sender, EventArgs e)
        {
            if (_isUpdatingUi) return;
            SaveCurrentUiToSettings();
            ReevaluateAllSheets(preserveCustomFileName: true);
        }

        private void OnBrowseOutputDirClick(object sender, EventArgs e)
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "选择 PDF 批量输出目录";
                dlg.SelectedPath = _txtOutputDir.Text;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _txtOutputDir.Text = dlg.SelectedPath;
                    _settings.OutputDirectory = dlg.SelectedPath;
                }
            }
        }

        private void OnPageStatsClick(object sender, EventArgs e)
        {
            string stats = BuildPageCountStatisticsText();
            MessageBox.Show(this, stats, "图纸页数与图幅统计", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private string BuildPageCountStatisticsText()
        {
            var selected = _sheets.Where(s => s.IsSelected).ToList();
            if (selected.Count == 0)
            {
                return "当前未勾选任何图纸。";
            }

            var groups = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in selected)
            {
                string key;
                if (s.ScaleValue.HasValue && s.ScaleValue.Value > 0)
                {
                    var est = _paperMatcher.EstimateTargetPaper(s.FrameWidthUnits, s.FrameHeightUnits, s.DrawingUnits, s.ScaleValue);
                    key = ScaleParser.GetStandardPaperShortName(est.ExpectedWidthMm, est.ExpectedHeightMm);
                }
                else if (s.PaperWidthMm > 0 && s.PaperHeightMm > 0)
                {
                    key = ScaleParser.GetStandardPaperShortName(s.PaperWidthMm, s.PaperHeightMm);
                }
                else
                {
                    key = "未定图幅";
                }

                groups[key] = groups.TryGetValue(key, out int cnt) ? cnt + 1 : 1;
            }

            var lines = new List<string>
            {
                string.Format("已勾选打印总张数: {0} 张 (打印份数: {1} 份)", selected.Count, (int)_nudCopies.Value),
                "----------------------------------------",
                "按标准/加长图幅分类统计:"
            };

            foreach (var kvp in groups.OrderBy(k => k.Key))
            {
                lines.Add(string.Format("  • {0} : {1} 张", kvp.Key, kvp.Value));
            }

            return string.Join("\n", lines);
        }

        private void OnStartPlotClick(object sender, EventArgs e)
        {
            ExecutePlotBatch(onlyFailed: false);
        }

        private void OnRetryFailedClick(object sender, EventArgs e)
        {
            var failed = _sheets.Where(s => s.Status == SheetStatus.Failed).ToList();
            if (failed.Count == 0)
            {
                MessageBox.Show(this, "当前没有处于“失败”状态的图纸任务。", "重试失败项", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            foreach (var s in _sheets)
            {
                s.IsSelected = (s.Status == SheetStatus.Failed);
            }
            PopulateGridFromSheets();
            ExecutePlotBatch(onlyFailed: true);
        }

        private void ExecutePlotBatch(bool onlyFailed)
        {
            SaveCurrentUiToSettings();
            _configService.SaveSettings(_settings);

            RefreshPc3MediaCatalog();
            if (!_deviceCheck.IsAvailable)
            {
                MessageBox.Show(this, _deviceCheck.DiagnosticMessage, "缺少 DWG To PDF.pc3", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var selectedSheets = _sheets.Where(s => s.IsSelected).OrderBy(s => s.OrderIndex).ToList();
            if (selectedSheets.Count == 0)
            {
                MessageBox.Show(this, "请至少勾选一张需要打印的图框。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var unresolved = selectedSheets
                .Where(s => string.IsNullOrWhiteSpace(s.TargetPaperCanonicalName) ||
                            (!s.FitToPaper && (!s.ScaleValue.HasValue || s.ScaleValue.Value <= 0)))
                .ToList();

            if (unresolved.Count > 0)
            {
                string msg = string.Format(
                    "有 {0} 张已勾选图纸缺少匹配的 PC3 纸张或比例未知（例如第 {1} 张：{2}）。\n\n" +
                    "请先在清单中确认比例/纸张，或勾选“布满图纸(Fit)”。",
                    unresolved.Count,
                    unresolved[0].OrderIndex,
                    unresolved[0].FormattedStatusSummary);
                MessageBox.Show(this, msg, "存在待确认图纸", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (_settings.ConfirmPageCountBeforePlot)
            {
                string statsMsg = BuildPageCountStatisticsText() + "\n\n是否确认开始批量打印？";
                if (MessageBox.Show(this, statsMsg, "打印前确认页数", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                {
                    return;
                }
            }

            _cancelRequested = false;
            _btnStartPlot.Enabled = false;
            _progressBar.Value = 0;

            Document activeDoc = CoreApp.DocumentManager.MdiActiveDocument ?? _doc;
            if (activeDoc != null) _doc = activeDoc;
            string effectiveOutDir = PlotJobRunner.ResolveEffectiveOutputDirectory(_doc, _settings);

            RunInCommandContext(() =>
            {
                JobResult result;
                try
                {
                    if (_settings.OutputMode == OutputMode.SinglePdfPerSheet)
                    {
                        result = _plotJobRunner.PlotEachSheetToPdf(
                            _doc,
                            _sheets,
                            _settings,
                            OnPlotProgressUpdate,
                            () => _cancelRequested,
                            filePath => MessageBox.Show(
                                this,
                                string.Format("文件已存在：\n{0}\n是否覆盖？", filePath),
                                "重名覆盖确认",
                                MessageBoxButtons.YesNo,
                                MessageBoxIcon.Question) == DialogResult.Yes);
                    }
                    else
                    {
                        string desiredMergedName = _namingService.EnsureValidPdfFileName(_txtMergedFileName.Text);
                        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        string mergedPath = _namingService.ResolveOutputFilePath(
                            effectiveOutDir,
                            desiredMergedName,
                            _settings.DuplicatePolicy,
                            reserved,
                            filePath => MessageBox.Show(
                                this,
                                string.Format("合并 PDF 文件已存在：\n{0}\n是否覆盖？", filePath),
                                "重名覆盖确认",
                                MessageBoxButtons.YesNo,
                                MessageBoxIcon.Question) == DialogResult.Yes);

                        if (string.IsNullOrEmpty(mergedPath))
                        {
                            _lblCurrentTaskStatus.Text = "合并打印已取消（目标文件已存在且选择了不覆盖/跳过）。";
                            return;
                        }

                        _lastMergedOutputFilePath = mergedPath;

                        result = _plotJobRunner.PlotMultiSheetPdf(
                            _doc,
                            _sheets,
                            mergedPath,
                            _settings,
                            OnPlotProgressUpdate,
                            () => _cancelRequested,
                            prompt => MessageBox.Show(this, prompt, "合并预检提示", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes);
                    }

                    // If AutoCropNonStandardPdf was NOT checked, but there are non-standard/lengthened sheets in this batch,
                    // offer a post-plot option so the user can crop excess white borders with one click.
                    var nonStdPlotted = selectedSheets
                        .Where(s => s.Status == SheetStatus.Succeeded && PlotJobRunner.IsSheetNonStandardPaperMatch(s))
                        .ToList();

                    if (!_settings.AutoCropNonStandardPdf && nonStdPlotted.Count > 0)
                    {
                        string cropPrompt = string.Format(
                            "打印已完成！检测到本次打印包含 {0} 张非标/加长图框（周围存在多余白边，标准图框无需裁剪）。\n\n" +
                            "是否立即按当前留白距离（{1:G} mm）自动裁剪这些 PDF 的周围白边？",
                            nonStdPlotted.Count,
                            Math.Max(0.0, _settings.PdfCropMarginMm));

                        if (MessageBox.Show(this, cropPrompt, "非标图框 PDF 自动裁剪选项", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                        {
                            string mergedTarget = _settings.OutputMode == OutputMode.MergedMultiPagePdf ? _lastMergedOutputFilePath : null;
                            _plotJobRunner.CropPlottedSheets(_sheets, mergedTarget, _settings);
                        }
                    }

                    PopulateGridFromSheets();
                    UpdateSummaryCounts();
                    _progressBar.Value = 100;

                    int croppedSheetCount = selectedSheets.Count(s =>
                        s.WarningMessages != null &&
                        s.WarningMessages.Any(w => w != null && w.StartsWith("页面尺寸由 ", StringComparison.Ordinal)));

                    string summaryText = string.Format(
                        "打印完成：成功 {0} 张，失败 {1} 张，跳过 {2} 张{3}。",
                        result.SucceededCount,
                        result.FailedCount,
                        result.SkippedCount,
                        croppedSheetCount > 0
                            ? string.Format("（已自动裁剪 {0} 张非标PDF白边，留白 {1:G}mm；标准图框保持原样）", croppedSheetCount, Math.Max(0.0, _settings.PdfCropMarginMm))
                            : "");
                    if (!string.IsNullOrWhiteSpace(result.FatalError))
                    {
                        summaryText += " 错误: " + result.FatalError;
                    }
                    _lblCurrentTaskStatus.Text = summaryText;

                    MessageBox.Show(
                        this,
                        summaryText + "\n\n输出目录: " + result.OutputDirectory + "\n日志文件: " + _logService.CurrentLogFilePath,
                        "批量打印结果",
                        MessageBoxButtons.OK,
                        result.FailedCount == 0 && string.IsNullOrEmpty(result.FatalError) ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                }
                finally
                {
                    _btnStartPlot.Enabled = _deviceCheck != null && _deviceCheck.IsAvailable;
                }
            });
        }

        private void OnPlotProgressUpdate(int currentIndex, int totalCount, PrintSheet currentSheet)
        {
            int pct = totalCount > 0 ? Math.Min(100, (int)(((currentIndex - 1) * 100.0) / totalCount)) : 0;
            _progressBar.Value = pct;
            _lblCurrentTaskStatus.Text = string.Format(
                "正在打印 ({0}/{1}): 图框 #{2} [{3}] -> {4}",
                currentIndex,
                totalCount,
                currentSheet?.OrderIndex ?? currentIndex,
                currentSheet?.TargetPaperLocaleName ?? "",
                currentSheet?.OutputFileName ?? "");
            System.Windows.Forms.Application.DoEvents();
        }

        private void OnOpenOutputDirClick(object sender, EventArgs e)
        {
            SaveCurrentUiToSettings();
            string dir = PlotJobRunner.ResolveEffectiveOutputDirectory(_doc, _settings);
            if (string.IsNullOrWhiteSpace(dir)) return;
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                Process.Start("explorer.exe", dir);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "无法打开目录: " + ex.Message, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OnCropExistingPdfsClick(object sender, EventArgs e)
        {
            SaveCurrentUiToSettings();
            _configService.SaveSettings(_settings);

            string mergedTarget = null;
            if (_settings.OutputMode == OutputMode.MergedMultiPagePdf)
            {
                if (!string.IsNullOrWhiteSpace(_lastMergedOutputFilePath) && File.Exists(_lastMergedOutputFilePath))
                {
                    mergedTarget = _lastMergedOutputFilePath;
                }
                else
                {
                    string effectiveDir = PlotJobRunner.ResolveEffectiveOutputDirectory(_doc, _settings);
                    string candidate = Path.Combine(effectiveDir, _namingService.EnsureValidPdfFileName(_txtMergedFileName.Text));
                    if (File.Exists(candidate))
                    {
                        mergedTarget = candidate;
                    }
                }
            }

            PdfCropResult cropRes = _plotJobRunner.CropPlottedSheets(_sheets, mergedTarget, _settings);
            PopulateGridFromSheets();
            UpdateSummaryCounts();

            if (cropRes.TotalPages == 0)
            {
                MessageBox.Show(
                    this,
                    "未找到已生成的 PDF 文件，请先点击“打印(P)”生成 PDF，或确认输出目录中存在对应 PDF 文件。",
                    "裁剪已输出 PDF",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            string detailMsg = string.Format(
                "PDF 白边自动裁剪完成（默认留白距离：{0:G} mm）：\n\n" +
                "• 检测总页数：{1} 页\n" +
                "• 已裁剪非标/加长图幅：{2} 页\n" +
                "• 自动跳过标准图幅（无需裁剪）：{3} 页",
                Math.Max(0.0, _settings.PdfCropMarginMm),
                cropRes.TotalPages,
                cropRes.CroppedPages,
                cropRes.SkippedStandardPages);

            if (cropRes.PageDetails != null && cropRes.PageDetails.Count > 0)
            {
                detailMsg += "\n\n裁剪明细：\n  - " + string.Join("\n  - ", cropRes.PageDetails.Take(8));
            }

            _lblCurrentTaskStatus.Text = string.Format(
                "PDF裁剪完成：已裁剪 {0} 页非标图纸（留白 {1:G}mm），跳过 {2} 页标准图纸。",
                cropRes.CroppedPages,
                Math.Max(0.0, _settings.PdfCropMarginMm),
                cropRes.SkippedStandardPages);

            MessageBox.Show(this, detailMsg, "非标图框 PDF 自动裁剪结果", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void OpenLogFile()
        {
            try
            {
                string logPath = _logService.CurrentLogFilePath;
                if (File.Exists(logPath))
                {
                    Process.Start("notepad.exe", logPath);
                }
                else if (Directory.Exists(_logService.LogDirectory))
                {
                    Process.Start("explorer.exe", _logService.LogDirectory);
                }
            }
            catch
            {
            }
        }

        private void ReevaluateAllSheets(bool preserveCustomFileName)
        {
            for (int i = 0; i < _sheets.Count; i++)
            {
                _sheets[i].OrderIndex = i + 1;
                _sheetPrepService.RefreshSheetPaperAndStatus(_sheets[i], _settings, _availableMedia, preserveCustomFileName);
            }
            PopulateGridFromSheets();
            UpdateSummaryCounts();
        }

        private void UpdateSummaryCounts()
        {
            int total = _sheets.Count;
            int selectedReady = _sheets.Count(s => s.IsSelected && s.Status == SheetStatus.Ready);
            int needsConfirm = _sheets.Count(s => s.IsSelected && s.Status == SheetStatus.NeedsConfirmation);
            int succeeded = _sheets.Count(s => s.Status == SheetStatus.Succeeded);
            int failed = _sheets.Count(s => s.Status == SheetStatus.Failed);
            int skipped = _sheets.Count(s => s.Status == SheetStatus.Skipped || !s.IsSelected);

            _lblRecognizedCount.Text = string.Format("识别图框: {0}", total);
            _lblSummaryCounts.Text = string.Format(
                "总张数: {0} | 待打印: {1} | 需确认: {2} | 成功: {3} | 失败: {4} | 未选/跳过: {5}",
                total, selectedReady, needsConfirm, succeeded, failed, skipped);
        }

        private void SyncPlotStyleSettingsFromUi()
        {
            _settings.DefaultPlotStyle = _cmbPlotStyle.SelectedIndex > 0
                ? Convert.ToString(_cmbPlotStyle.SelectedItem)
                : "";
            _settings.DefaultColorMode = (ColorMode)Math.Max(0, _cmbColorMode.SelectedIndex);
            _settings.PrintLineweights = _chkPrintLineweights.Checked;
            _settings.UsePlotStyle = _chkUsePlotStyle.Checked;
        }

        private void SaveCurrentUiToSettings()
        {
            if (_rbModeManualPick.Checked) _settings.RecognitionMode = FrameRecognitionMode.ManualPick;
            else if (_rbModeByBlock.Checked) _settings.RecognitionMode = FrameRecognitionMode.ByBlock;
            else _settings.RecognitionMode = FrameRecognitionMode.ByLayer;

            _settings.IncludeNonPlottableLayers = _chkIncludeNonPlottable.Checked;
            _settings.IncludePaperSpaceLayouts = _chkIncludePaperSpaceLayouts.Checked;
            _settings.ActiveFrameLayer = _cmbLayerList.Text.Trim();
            _settings.ActiveFrameBlockName = _cmbBlockList.Text.Trim();

            if (_rbSortColFirst.Checked) _settings.SortOrderMode = SheetSortOrderMode.ColumnFirst;
            else if (_rbSortSelection.Checked) _settings.SortOrderMode = SheetSortOrderMode.SelectionOrder;
            else _settings.SortOrderMode = SheetSortOrderMode.RowFirst;

            _settings.ReverseX = _rbDirRightToLeft.Checked;
            _settings.ReverseY = _rbDirBottomToTop.Checked;

            _settings.PlotCopies = (int)_nudCopies.Value;
            SyncPlotStyleSettingsFromUi();

            _settings.AutoMatchPaperSize = _chkAutoMatchPaper.Checked;
            if (_cmbManualPaper.SelectedItem is PaperMedia media)
            {
                _settings.ManualPaperCanonicalName = media.CanonicalName;
            }
            _settings.UseFitToPaper = _chkFitToPaper.Checked;
            _settings.UseCustomFixedScale = _chkUseCustomScale.Checked;

            double? parsedScale = TryReadCustomScaleDenominatorFromUi();
            if (parsedScale.HasValue && parsedScale.Value > 0)
            {
                _settings.DefaultScaleValue = parsedScale.Value;
            }

            _settings.CenterPlot = _chkCenterPlot.Checked;
            if (double.TryParse(_txtOffsetX.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double ox))
                _settings.PlotOffsetX = ox;
            if (double.TryParse(_txtOffsetY.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double oy))
                _settings.PlotOffsetY = oy;

            if (_rbOrientPortrait.Checked) _settings.OrientationPolicy = PlotOrientationPolicy.Portrait;
            else if (_rbOrientLandscape.Checked) _settings.OrientationPolicy = PlotOrientationPolicy.Landscape;
            else _settings.OrientationPolicy = PlotOrientationPolicy.Auto;
            _settings.UpsideDown = _chkUpsideDown.Checked;

            _settings.OutputMode = _rbMergedPdf.Checked ? OutputMode.MergedMultiPagePdf : OutputMode.SinglePdfPerSheet;
            _settings.SaveToDwgDirectory = _chkSaveToDwgDir.Checked;
            _settings.OutputDirectory = _txtOutputDir.Text.Trim();
            _settings.AppendPaperSizeToFileName = _chkAppendPaperSize.Checked;
            _settings.FileNameTemplate = _txtFileNameTemplate.Text.Trim();
            _settings.DuplicatePolicy = (DuplicateFilePolicy)Math.Max(0, _cmbDuplicatePolicy.SelectedIndex);
            _settings.AutoCropNonStandardPdf = _chkAutoCropPdf.Checked;
            if (double.TryParse(_txtCropMarginMm.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double cropMargin) && cropMargin >= 0)
            {
                _settings.PdfCropMarginMm = cropMargin;
            }
            _settings.ConfirmPageCountBeforePlot = _chkConfirmPageCount.Checked;

            _configService.SaveSettings(_settings);
        }

        private PrintSheet GetPrimaryHighlightedSheet()
        {
            if (_gridSheets.SelectedRows.Count > 0 && _gridSheets.SelectedRows[0].Tag is PrintSheet s)
            {
                return s;
            }
            if (_gridSheets.CurrentRow != null && _gridSheets.CurrentRow.Tag is PrintSheet cs)
            {
                return cs;
            }
            return _sheets.FirstOrDefault(x => x.IsSelected);
        }

        private List<PrintSheet> GetHighlightedOrCheckedSheets()
        {
            var list = new List<PrintSheet>();
            foreach (DataGridViewRow row in _gridSheets.SelectedRows)
            {
                if (row.Tag is PrintSheet s) list.Add(s);
            }
            if (list.Count == 0)
            {
                list.AddRange(_sheets.Where(s => s.IsSelected));
            }
            return list;
        }

        private Button CreateToolButton(string text, int minWidth, EventHandler onClick)
        {
            var btn = new Button
            {
                Text = text,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = SS(minWidth, 28),
                Padding = SP(4, 1, 4, 1),
                Anchor = AnchorStyles.Left,
                Margin = SP(0, 0, 6, 0)
            };
            btn.Click += onClick;
            return btn;
        }

        private Button CreateInlineButton(string text, int minWidth, int minHeight, EventHandler onClick)
        {
            var btn = new Button
            {
                Text = text,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = SS(minWidth, minHeight),
                Padding = SP(4, 0, 4, 0),
                Anchor = AnchorStyles.Left
            };
            btn.Click += onClick;
            return btn;
        }

        private Label CreateInlineLabel(string text, int leftMargin, int rightMargin)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = SP(leftMargin, 2, rightMargin, 2)
            };
        }

        private static FlowLayoutPanel CreateInlineFlow()
        {
            return new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0)
            };
        }

        private void AddLabeledControl(TableLayoutPanel table, int row, int labelCol, string labelText, Control control)
        {
            var lbl = new Label
            {
                Text = labelText,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = SP(0, 2, 6, 2),
                TextAlign = ContentAlignment.MiddleLeft
            };
            control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            table.Controls.Add(lbl, labelCol, row);
            table.Controls.Add(control, labelCol + 1, row);
        }
    }
}
