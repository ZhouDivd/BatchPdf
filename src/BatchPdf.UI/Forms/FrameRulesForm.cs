using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using BatchPdf.Core.Configuration;

namespace BatchPdf.UI.Forms
{
    public class FrameRulesForm : Form
    {
        private readonly BatchPdfSettings _settings;
        private float _uiScale = 1.0f;

        private TextBox _txtRuleName;
        private TextBox _txtAllowedLayers;
        private TextBox _txtBlockNames;
        private TextBox _txtSheetNumberTags;
        private TextBox _txtSheetNameTags;
        private TextBox _txtScaleTags;
        private NumericUpDown _numMinWidth;
        private NumericUpDown _numMinHeight;
        private NumericUpDown _numToleranceMm;

        public FrameRulesForm(BatchPdfSettings settings)
        {
            _settings = settings ?? BatchPdfSettings.CreateDefault();
            InitializeComponents();
            LoadFromSettings();
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
                float maxScaleW = (workArea.Width * 0.90f) / 660.0f;
                float maxScaleH = (workArea.Height * 0.90f) / 500.0f;
                maxScreenScale = Math.Max(0.85f, Math.Min(maxScaleW, maxScaleH));
            }

            _uiScale = Math.Min(Math.Max(1.0f, dpiScale), maxScreenScale);
            Font = new Font("Microsoft YaHei UI", 12.0f * _uiScale, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        private void InitializeComponents()
        {
            SuspendLayout();
            InitDpiScale();

            Text = "图框识别与属性映射规则设置 (BPDFSET)";
            Size = SS(660, 500);
            MinimumSize = SS(600, 460);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = SP(14, 14, 14, 14),
                ColumnCount = 2,
                RowCount = 10
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

            _txtRuleName = CreateTextBox();
            _txtAllowedLayers = CreateTextBox();
            _txtBlockNames = CreateTextBox();
            _txtSheetNumberTags = CreateTextBox();
            _txtSheetNameTags = CreateTextBox();
            _txtScaleTags = CreateTextBox();

            _numMinWidth = new NumericUpDown { Minimum = 10, Maximum = 100000, DecimalPlaces = 0, Width = S(140), Anchor = AnchorStyles.Left };
            _numMinHeight = new NumericUpDown { Minimum = 10, Maximum = 100000, DecimalPlaces = 0, Width = S(140), Anchor = AnchorStyles.Left };
            _numToleranceMm = new NumericUpDown { Minimum = 1, Maximum = 50, DecimalPlaces = 1, Width = S(140), Anchor = AnchorStyles.Left };

            AddRow(layout, 0, "规则名称:", _txtRuleName);
            AddRow(layout, 1, "限定图框图层 (逗号分隔,留空不限):", _txtAllowedLayers);
            AddRow(layout, 2, "图框块名通配 (逗号分隔):", _txtBlockNames);
            AddRow(layout, 3, "【图号】属性标签映射:", _txtSheetNumberTags);
            AddRow(layout, 4, "【图名】属性标签映射:", _txtSheetNameTags);
            AddRow(layout, 5, "【比例】属性标签映射:", _txtScaleTags);
            AddRow(layout, 6, "最小图框宽度 (绘图单位):", _numMinWidth);
            AddRow(layout, 7, "最小图框高度 (绘图单位):", _numMinHeight);
            AddRow(layout, 8, "纸张自动匹配容差 (mm):", _numToleranceMm);

            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            var btnPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = SP(0, 8, 0, 0),
                WrapContents = false
            };

            var btnCancel = CreateDialogButton("取消", 90, DialogResult.Cancel);
            var btnSave = CreateDialogButton("保存设置", 104, DialogResult.None);
            var btnReset = CreateDialogButton("恢复默认", 98, DialogResult.None);

            btnSave.Click += OnSaveClick;
            btnReset.Click += OnResetClick;

            btnPanel.Controls.Add(btnCancel);
            btnPanel.Controls.Add(btnSave);
            btnPanel.Controls.Add(btnReset);

            layout.Controls.Add(btnPanel, 0, 9);
            layout.SetColumnSpan(btnPanel, 2);

            Controls.Add(layout);
            AcceptButton = btnSave;
            CancelButton = btnCancel;
            ResumeLayout(true);
        }

        private Button CreateDialogButton(string text, int minWidth, DialogResult dialogResult)
        {
            return new Button
            {
                Text = text,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = SS(minWidth, 30),
                Padding = SP(6, 2, 6, 2),
                Margin = SP(6, 2, 0, 2),
                DialogResult = dialogResult
            };
        }

        private TextBox CreateTextBox()
        {
            return new TextBox
            {
                Dock = DockStyle.Fill,
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Margin = SP(3, 4, 3, 4)
            };
        }

        private void AddRow(TableLayoutPanel panel, int row, string labelText, Control editor)
        {
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, SF(38F)));
            var lbl = new Label
            {
                Text = labelText,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = SP(0, 4, 10, 4)
            };
            panel.Controls.Add(lbl, 0, row);
            panel.Controls.Add(editor, 1, row);
        }

        private void LoadFromSettings()
        {
            FrameRule rule = (_settings.FrameRules != null && _settings.FrameRules.Count > 0)
                ? _settings.FrameRules[0]
                : BatchPdfSettings.CreateDefault().FrameRules[0];

            _txtRuleName.Text = rule.RuleName ?? "DefaultFrameRule";
            _txtAllowedLayers.Text = string.Join(", ", rule.AllowedLayers ?? new List<string>());
            _txtBlockNames.Text = string.Join(", ", rule.BlockNames ?? new List<string>());
            _txtSheetNumberTags.Text = string.Join(", ", rule.SheetNumberTags ?? new List<string>());
            _txtSheetNameTags.Text = string.Join(", ", rule.SheetNameTags ?? new List<string>());
            _txtScaleTags.Text = string.Join(", ", rule.ScaleTags ?? new List<string>());

            _numMinWidth.Value = ClampDecimal(rule.MinFrameWidth, _numMinWidth.Minimum, _numMinWidth.Maximum);
            _numMinHeight.Value = ClampDecimal(rule.MinFrameHeight, _numMinHeight.Minimum, _numMinHeight.Maximum);
            _numToleranceMm.Value = ClampDecimal(_settings.PaperMatchToleranceMm, _numToleranceMm.Minimum, _numToleranceMm.Maximum);
        }

        private void OnResetClick(object sender, EventArgs e)
        {
            var def = BatchPdfSettings.CreateDefault();
            _settings.FrameRules = def.FrameRules;
            _settings.PaperMatchToleranceMm = def.PaperMatchToleranceMm;
            LoadFromSettings();
        }

        private void OnSaveClick(object sender, EventArgs e)
        {
            var rule = new FrameRule
            {
                RuleName = string.IsNullOrWhiteSpace(_txtRuleName.Text) ? "DefaultFrameRule" : _txtRuleName.Text.Trim(),
                AllowedLayers = ParseCommaList(_txtAllowedLayers.Text),
                BlockNames = ParseCommaList(_txtBlockNames.Text),
                SheetNumberTags = ParseCommaList(_txtSheetNumberTags.Text),
                SheetNameTags = ParseCommaList(_txtSheetNameTags.Text),
                ScaleTags = ParseCommaList(_txtScaleTags.Text),
                MinFrameWidth = (double)_numMinWidth.Value,
                MinFrameHeight = (double)_numMinHeight.Value
            };

            _settings.FrameRules = new List<FrameRule> { rule };
            _settings.PaperMatchToleranceMm = (double)_numToleranceMm.Value;

            DialogResult = DialogResult.OK;
            Close();
        }

        private static List<string> ParseCommaList(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return new List<string>();
            return input
                .Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();
        }

        private static decimal ClampDecimal(double val, decimal min, decimal max)
        {
            decimal d = (decimal)val;
            if (d < min) return min;
            if (d > max) return max;
            return d;
        }
    }
}
