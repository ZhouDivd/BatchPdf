using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BatchPdf.Installer
{
    public class InstallerForm : Form
    {
        private TextBox _txtTargetDir;
        private CheckBox _chkShortcut;
        private CheckBox _chkContextMenu;
        private CheckBox _chkCadAutoLoad;
        private TextBox _txtLogs;
        private Button _btnInstall;
        private Button _btnUninstall;
        private Label _lblStatus;
        private ProgressBar _progressBar;

        public InstallerForm()
        {
            InitializeComponent();
            CheckInstalledAutoCad();
        }

        private void InitializeComponent()
        {
            this.Text = "BatchPdf - AutoCAD 批量出图利器 安装程序 v1.2.0";
            this.Size = new Size(680, 560);
            this.MinimumSize = new Size(600, 500);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular);
            this.BackColor = Color.FromArgb(248, 249, 250);

            // 1. Header Banner
            var pnlHeader = new Panel
            {
                Dock = DockStyle.Top,
                Height = 85,
                BackColor = Color.FromArgb(24, 76, 120),
                Padding = new Padding(20, 15, 20, 15)
            };

            var lblTitle = new Label
            {
                Text = "BatchPdf 批量打印与智能识别插件",
                Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold),
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(20, 15)
            };

            var lblSubtitle = new Label
            {
                Text = "AutoCAD 2019 ~ 2024 一键部署 | 非标裁白边 | 免开CAD极速出图 | 智能双语图签识别",
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular),
                ForeColor = Color.FromArgb(210, 230, 250),
                AutoSize = true,
                Location = new Point(22, 48)
            };

            pnlHeader.Controls.Add(lblTitle);
            pnlHeader.Controls.Add(lblSubtitle);
            this.Controls.Add(pnlHeader);

            // 2. Main Content
            var pnlContent = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(25, 20, 25, 15)
            };

            int y = 100;

            // Target Dir Label & TextBox
            var lblDir = new Label
            {
                Text = "安装目标路径 (官方标准 ApplicationPlugins 目录):",
                Location = new Point(25, y),
                AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold)
            };
            this.Controls.Add(lblDir);
            y += 24;

            _txtTargetDir = new TextBox
            {
                Text = InstallerEngine.DefaultBundleDir,
                Location = new Point(25, y),
                Width = 510,
                ReadOnly = true,
                BackColor = Color.White
            };
            this.Controls.Add(_txtTargetDir);

            var btnBrowse = new Button
            {
                Text = "浏览...",
                Location = new Point(545, y - 2),
                Size = new Size(85, 27)
            };
            btnBrowse.Click += (s, e) =>
            {
                using (var fbd = new FolderBrowserDialog { Description = "选择插件安装目录", SelectedPath = _txtTargetDir.Text })
                {
                    if (fbd.ShowDialog() == DialogResult.OK)
                    {
                        _txtTargetDir.Text = fbd.SelectedPath;
                    }
                }
            };
            this.Controls.Add(btnBrowse);
            y += 38;

            // Options Checkboxes
            _chkShortcut = new CheckBox
            {
                Text = "创建桌面快捷方式「【BatchPdf】极速批量打印 (免开CAD)」",
                Checked = true,
                Location = new Point(25, y),
                AutoSize = true
            };
            this.Controls.Add(_chkShortcut);
            y += 24;

            _chkContextMenu = new CheckBox
            {
                Text = "添加 Windows 鼠标右键菜单「使用 BatchPdf 批量打印 (免开CAD)」",
                Checked = true,
                Location = new Point(25, y),
                AutoSize = true
            };
            this.Controls.Add(_chkContextMenu);
            y += 24;

            _chkCadAutoLoad = new CheckBox
            {
                Text = "配置 AutoCAD 注册表开机与按需自动加载命令 (BPDF / BPLOT / BPTABLE)",
                Checked = true,
                Location = new Point(25, y),
                AutoSize = true
            };
            this.Controls.Add(_chkCadAutoLoad);
            y += 32;

            // Log Console
            _txtLogs = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.FromArgb(220, 220, 220),
                Font = new Font("Consolas", 9F),
                Location = new Point(25, y),
                Size = new Size(610, 180),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };
            this.Controls.Add(_txtLogs);
            y += 190;

            // Progress bar & Status
            _progressBar = new ProgressBar
            {
                Location = new Point(25, y),
                Size = new Size(610, 12),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                Style = ProgressBarStyle.Continuous
            };
            this.Controls.Add(_progressBar);
            y += 20;

            _lblStatus = new Label
            {
                Text = "就绪。点击下方「一键安装到本机」即可完成自动配置。",
                Location = new Point(25, y),
                AutoSize = true,
                ForeColor = Color.FromArgb(80, 80, 80),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left
            };
            this.Controls.Add(_lblStatus);

            // Action Buttons
            _btnInstall = new Button
            {
                Text = "一键安装到本机",
                Location = new Point(365, y - 8),
                Size = new Size(150, 36),
                BackColor = Color.FromArgb(24, 120, 76),
                ForeColor = Color.White,
                Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
                FlatStyle = FlatStyle.Flat,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };
            _btnInstall.FlatAppearance.BorderSize = 0;
            _btnInstall.Click += OnInstallClick;
            this.Controls.Add(_btnInstall);

            _btnUninstall = new Button
            {
                Text = "卸载插件",
                Location = new Point(525, y - 8),
                Size = new Size(110, 36),
                BackColor = Color.FromArgb(220, 225, 230),
                ForeColor = Color.FromArgb(50, 50, 50),
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular),
                FlatStyle = FlatStyle.Flat,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };
            _btnUninstall.FlatAppearance.BorderSize = 0;
            _btnUninstall.Click += OnUninstallClick;
            this.Controls.Add(_btnUninstall);
        }

        private void CheckInstalledAutoCad()
        {
            var cadList = InstallerEngine.DetectAutoCADs();
            AppendLog("【系统检测】正在扫描本机已安装的 AutoCAD 版本...");
            if (cadList.Count > 0)
            {
                foreach (var cad in cadList)
                {
                    AppendLog("  [检测到] " + cad);
                }
            }
            else
            {
                AppendLog("  [提示] 未在默认注册表中检测到 AutoCAD，若安装在非标准路径，安装后仍可通过命令行或脚本直接使用。");
            }
            AppendLog("");
        }

        private async void OnInstallClick(object sender, EventArgs e)
        {
            if (InstallerEngine.IsAutoCadRunning())
            {
                var dr = MessageBox.Show(
                    "检测到 AutoCAD 正在运行中！\n如果插件旧文件被占用可能导致更新失败。建议点击「取消」先关闭 CAD 再安装。\n\n是否仍要强制尝试安装？",
                    "提示",
                    MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Warning);
                if (dr != DialogResult.OK) return;
            }

            SetControlsEnabled(false);
            _progressBar.Style = ProgressBarStyle.Marquee;
            _lblStatus.Text = "正在安装与部署插件，请稍候...";

            string targetDir = _txtTargetDir.Text.Trim();
            bool shortcut = _chkShortcut.Checked;
            bool contextMenu = _chkContextMenu.Checked;
            bool cadAutoLoad = _chkCadAutoLoad.Checked;

            try
            {
                await Task.Run(() =>
                {
                    InstallerEngine.Install(targetDir, shortcut, contextMenu, cadAutoLoad, AppendLog);
                });

                _lblStatus.Text = "安装成功！打开 AutoCAD 输入 BPDF 即可使用。";
                MessageBox.Show(
                    "BatchPdf 批量打印插件已成功安装部署完成！\n\n• 启动 AutoCAD 2019~2024 输入 BPDF 即可打开主界面\n• 可直接使用桌面快捷方式免开 CAD 批量出图",
                    "安装完成",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                AppendLog("[安装失败] " + ex.Message);
                _lblStatus.Text = "安装失败: " + ex.Message;
                MessageBox.Show("安装失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _progressBar.Style = ProgressBarStyle.Continuous;
                _progressBar.Value = 100;
                SetControlsEnabled(true);
            }
        }

        private async void OnUninstallClick(object sender, EventArgs e)
        {
            var dr = MessageBox.Show(
                "确定要从本机卸载 BatchPdf 插件以及相关桌面快捷方式和右键菜单吗？",
                "确认卸载",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (dr != DialogResult.Yes) return;

            SetControlsEnabled(false);
            _progressBar.Style = ProgressBarStyle.Marquee;
            _lblStatus.Text = "正在卸载插件，请稍候...";

            string targetDir = _txtTargetDir.Text.Trim();

            try
            {
                await Task.Run(() =>
                {
                    InstallerEngine.Uninstall(targetDir, AppendLog);
                });

                _lblStatus.Text = "卸载完成。";
                MessageBox.Show("BatchPdf 插件已成功卸载！", "卸载完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                AppendLog("[卸载失败] " + ex.Message);
                _lblStatus.Text = "卸载失败: " + ex.Message;
            }
            finally
            {
                _progressBar.Style = ProgressBarStyle.Continuous;
                _progressBar.Value = 0;
                SetControlsEnabled(true);
            }
        }

        private void SetControlsEnabled(bool enabled)
        {
            _btnInstall.Enabled = enabled;
            _btnUninstall.Enabled = enabled;
            _chkShortcut.Enabled = enabled;
            _chkContextMenu.Enabled = enabled;
            _chkCadAutoLoad.Enabled = enabled;
        }

        private void AppendLog(string text)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(AppendLog), text);
                return;
            }

            _txtLogs.AppendText(text + Environment.NewLine);
            _txtLogs.SelectionStart = _txtLogs.TextLength;
            _txtLogs.ScrollToCaret();
        }
    }
}
