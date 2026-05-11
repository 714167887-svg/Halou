using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;
using Clipboard = System.Windows.Forms.Clipboard;
using DrawingColor = System.Drawing.Color;
using DrawingFont = System.Drawing.Font;
using Keys = System.Windows.Forms.Keys;

namespace HalouSuite.Payload
{
    internal sealed class SuitePaletteControl : System.Windows.Forms.UserControl
    {
        private readonly HalouSuiteManager _manager;
        // 功能 tab：FlowLayoutPanel 承载每行 [功能名 + 自动加载 toggle]，替代旧的 ListBox
        private readonly System.Windows.Forms.FlowLayoutPanel _featureRowsPanel;
        private string _selectedFeatureId;
        private readonly Dictionary<string, System.Windows.Forms.Panel> _featureRowPanels
            = new Dictionary<string, System.Windows.Forms.Panel>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CadPluginFeature> _featureRowFeatures
            = new Dictionary<string, CadPluginFeature>(StringComparer.OrdinalIgnoreCase);
        private readonly System.Windows.Forms.TextBox _manifestSourceTextBox;
        private readonly System.Windows.Forms.TextBox _credentialHeaderTextBox;
        private readonly System.Windows.Forms.TextBox _credentialValueTextBox;
        private readonly HotkeyCaptureTextBox _hotkeyTextBox;
        private readonly System.Windows.Forms.NumericUpDown _refreshSecondsInput;
        private readonly System.Windows.Forms.Label _statusLabel;
        private readonly System.Windows.Forms.Label _featureDescriptionLabel;
        private readonly System.Windows.Forms.TextBox _accountNameTextBox;
        private readonly System.Windows.Forms.TextBox _accountTokenTextBox;
        private readonly System.Windows.Forms.TextBox _accountEndpointTextBox;
        private readonly System.Windows.Forms.Label _accountStatusLabel;
        private readonly System.Windows.Forms.CheckBox _showTokenCheckBox;
        private readonly System.Windows.Forms.Label _autoStartStatusLabel;
        private readonly System.Windows.Forms.Label _licenseStatusLabel;
        private readonly System.Windows.Forms.Label _versionStatusLabel;
        private readonly System.Windows.Forms.TextBox _licenseEndpointTextBox;
        // 快捷键 tab：承载每个功能的热键捕获控件（改用 FlowLayoutPanel 避免 TLP 错位）
        private readonly System.Windows.Forms.FlowLayoutPanel _hotkeyListPanel;
        private readonly Dictionary<string, HotkeyCaptureTextBox> _featureHotkeyBoxes
            = new Dictionary<string, HotkeyCaptureTextBox>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, System.Windows.Forms.TextBox> _featureCommandBoxes
            = new Dictionary<string, System.Windows.Forms.TextBox>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, System.Windows.Forms.CheckBox> _featureAutoLoadBoxes
            = new Dictionary<string, System.Windows.Forms.CheckBox>(StringComparer.OrdinalIgnoreCase);

        public SuitePaletteControl(HalouSuiteManager manager)
        {
            _manager = manager;
            Dock = System.Windows.Forms.DockStyle.Fill;

            System.Windows.Forms.TableLayoutPanel root = new System.Windows.Forms.TableLayoutPanel
            {
                Dock = System.Windows.Forms.DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new System.Windows.Forms.Padding(12, 10, 12, 10),
                BackColor = UiBg
            };
            root.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.AutoSize));
            root.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 100f));
            root.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.AutoSize));
            Controls.Add(root);

            System.Windows.Forms.Label titleLabel = new System.Windows.Forms.Label
            {
                Text = "Halou 插件集合",
                Dock = System.Windows.Forms.DockStyle.Top,
                AutoSize = true,
                Font = new DrawingFont("Microsoft YaHei UI", 13.5f, FontStyle.Bold),
                ForeColor = UiText,
                Padding = new System.Windows.Forms.Padding(2, 0, 0, 8)
            };
            root.Controls.Add(titleLabel, 0, 0);

            System.Windows.Forms.TabControl tabs = new System.Windows.Forms.TabControl
            {
                Dock = System.Windows.Forms.DockStyle.Fill,
                Font = new DrawingFont("Microsoft YaHei UI", 9.5f),
                SizeMode = System.Windows.Forms.TabSizeMode.Fixed,
                ItemSize = new Size(88, 32),
                Padding = new Point(16, 6),
                Appearance = System.Windows.Forms.TabAppearance.Normal
            };
            root.Controls.Add(tabs, 0, 1);

            // ============== Tab 1: 功能 ==============
            System.Windows.Forms.TabPage featureTab = new System.Windows.Forms.TabPage("功能")
            {
                Padding = new System.Windows.Forms.Padding(14, 12, 14, 12),
                BackColor = UiCardBg
            };
            tabs.TabPages.Add(featureTab);

            System.Windows.Forms.TableLayoutPanel featurePanel = new System.Windows.Forms.TableLayoutPanel
            {
                Dock = System.Windows.Forms.DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4
            };
            featurePanel.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.AutoSize));
            featurePanel.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 100f));
            featurePanel.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.AutoSize));
            featurePanel.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.AutoSize));
            featureTab.Controls.Add(featurePanel);

            featurePanel.Controls.Add(new System.Windows.Forms.Label
            {
                Text = "功能清单（双击运行；右侧开关 = 启动 CAD 时自动加载）",
                AutoSize = true,
                Dock = System.Windows.Forms.DockStyle.Top,
                Font = new DrawingFont("Microsoft YaHei UI", 10f, FontStyle.Bold),
                ForeColor = UiText,
                Padding = new System.Windows.Forms.Padding(0, 0, 0, 8)
            }, 0, 0);

            _featureRowsPanel = new System.Windows.Forms.FlowLayoutPanel
            {
                Dock = System.Windows.Forms.DockStyle.Fill,
                FlowDirection = System.Windows.Forms.FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle,
                BackColor = UiCardBg,
                Padding = new System.Windows.Forms.Padding(2)
            };
            featurePanel.Controls.Add(_featureRowsPanel, 0, 1);

            _featureDescriptionLabel = new System.Windows.Forms.Label
            {
                AutoSize = false,
                Dock = System.Windows.Forms.DockStyle.Fill,
                Height = 64,
                Font = new DrawingFont("Microsoft YaHei UI", 9f),
                ForeColor = UiTextMuted,
                Padding = new System.Windows.Forms.Padding(4, 8, 4, 4)
            };
            featurePanel.Controls.Add(_featureDescriptionLabel, 0, 2);

            System.Windows.Forms.FlowLayoutPanel featureButtons = new System.Windows.Forms.FlowLayoutPanel
            {
                Dock = System.Windows.Forms.DockStyle.Fill,
                FlowDirection = System.Windows.Forms.FlowDirection.LeftToRight,
                AutoSize = true,
                WrapContents = false,
                Padding = new System.Windows.Forms.Padding(0, 6, 0, 0)
            };
            featurePanel.Controls.Add(featureButtons, 0, 3);

            featureButtons.Controls.Add(CreateButton("▶ 运行选中", delegate { RunSelectedFeature(); }));
            featureButtons.Controls.Add(CreateSecondaryButton("⟳ 刷新清单", delegate { _manager.RefreshManifest(manual: true); }));

            // ============== Tab 2: 快捷键 ==============
            System.Windows.Forms.TabPage hotkeyTab = new System.Windows.Forms.TabPage("快捷键")
            {
                Padding = new System.Windows.Forms.Padding(14, 12, 14, 12),
                BackColor = UiCardBg
            };
            tabs.TabPages.Add(hotkeyTab);

            System.Windows.Forms.TableLayoutPanel hotkeyRoot = new System.Windows.Forms.TableLayoutPanel
            {
                Dock = System.Windows.Forms.DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4
            };
            hotkeyRoot.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.AutoSize));
            hotkeyRoot.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.AutoSize));
            hotkeyRoot.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 100f));
            hotkeyRoot.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.AutoSize));
            hotkeyTab.Controls.Add(hotkeyRoot);

            hotkeyRoot.Controls.Add(new System.Windows.Forms.Label
            {
                Text = "每个功能可两选一：按下组合键登记全局热键，或直接输入 AutoCAD 命令名（如 MYZK）。Esc 清空。",
                AutoSize = false,
                Dock = System.Windows.Forms.DockStyle.Top,
                Height = 22,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new DrawingFont("Microsoft YaHei UI", 8.8f),
                ForeColor = UiTextMuted,
                Padding = new System.Windows.Forms.Padding(2, 0, 2, 4)
            }, 0, 0);

            // 面板弹出热键单独一行
            System.Windows.Forms.TableLayoutPanel paletteHotkeyRow = new System.Windows.Forms.TableLayoutPanel
            {
                Dock = System.Windows.Forms.DockStyle.Top,
                ColumnCount = 3,
                RowCount = 1,
                Height = 34,
                Padding = new System.Windows.Forms.Padding(0, 0, 0, 8)
            };
            paletteHotkeyRow.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 34f));
            paletteHotkeyRow.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 66f));
            paletteHotkeyRow.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.AutoSize));
            hotkeyRoot.Controls.Add(paletteHotkeyRow, 0, 1);

            paletteHotkeyRow.Controls.Add(new System.Windows.Forms.Label
            {
                Text = "面板弹出 / 关闭",
                AutoSize = false,
                Dock = System.Windows.Forms.DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new DrawingFont("Microsoft YaHei UI", 9.2f, FontStyle.Bold),
                ForeColor = UiText
            }, 0, 0);

            _hotkeyTextBox = new HotkeyCaptureTextBox();
            _hotkeyTextBox.Dock = System.Windows.Forms.DockStyle.Fill;
            _hotkeyTextBox.BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle;
            _hotkeyTextBox.Font = new DrawingFont("Microsoft YaHei UI", 9.2f);
            _hotkeyTextBox.Margin = new System.Windows.Forms.Padding(0, 4, 0, 4);
            paletteHotkeyRow.Controls.Add(_hotkeyTextBox, 1, 0);

            paletteHotkeyRow.Controls.Add(CreateSmallButton("×", delegate { _hotkeyTextBox.Text = string.Empty; }), 2, 0);

            // 功能级热键（动态填充）- FlowLayoutPanel 垂直堆叠，每行一个自管理 Panel
            _hotkeyListPanel = new System.Windows.Forms.FlowLayoutPanel
            {
                Dock = System.Windows.Forms.DockStyle.Fill,
                FlowDirection = System.Windows.Forms.FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Padding = new System.Windows.Forms.Padding(0, 8, 0, 0)
            };
            hotkeyRoot.Controls.Add(_hotkeyListPanel, 0, 2);

            System.Windows.Forms.FlowLayoutPanel hotkeyBottomButtons = new System.Windows.Forms.FlowLayoutPanel
            {
                Dock = System.Windows.Forms.DockStyle.Top,
                FlowDirection = System.Windows.Forms.FlowDirection.LeftToRight,
                AutoSize = true,
                WrapContents = true,
                Padding = new System.Windows.Forms.Padding(0, 12, 0, 0)
            };
            hotkeyRoot.Controls.Add(hotkeyBottomButtons, 0, 3);
            hotkeyBottomButtons.Controls.Add(CreateButton("保存", OnSaveConfigClicked));
            hotkeyBottomButtons.Controls.Add(CreateSecondaryButton("全部清除", delegate
            {
                _hotkeyTextBox.Text = string.Empty;
                foreach (HotkeyCaptureTextBox b in _featureHotkeyBoxes.Values) b.Text = string.Empty;
                foreach (System.Windows.Forms.TextBox b in _featureCommandBoxes.Values) b.Text = string.Empty;
            }));

            // ============== Tab 3: 账号 ==============
            System.Windows.Forms.TabPage accountTab = new System.Windows.Forms.TabPage("账号")
            {
                Padding = new System.Windows.Forms.Padding(8),
                BackColor = DrawingColor.White
            };
            tabs.TabPages.Add(accountTab);

            System.Windows.Forms.TableLayoutPanel accountPanel = new System.Windows.Forms.TableLayoutPanel
            {
                Dock = System.Windows.Forms.DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 6,
                Font = new DrawingFont("Microsoft YaHei UI", 9f)
            };
            accountPanel.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 82f));
            accountPanel.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 100f));
            accountPanel.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.AutoSize));
            accountPanel.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 30f));
            accountPanel.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 30f));
            accountPanel.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 30f));
            accountPanel.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.AutoSize));
            accountPanel.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.AutoSize));
            accountPanel.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 100f));
            accountTab.Controls.Add(accountPanel);

            accountPanel.Controls.Add(CreateFieldLabel("账号名"), 0, 0);
            _accountNameTextBox = CreateTextBox();
            accountPanel.Controls.Add(_accountNameTextBox, 1, 0);
            accountPanel.SetColumnSpan(_accountNameTextBox, 2);

            accountPanel.Controls.Add(CreateFieldLabel("登录令牌"), 0, 1);
            _accountTokenTextBox = CreateTextBox();
            _accountTokenTextBox.UseSystemPasswordChar = true;
            accountPanel.Controls.Add(_accountTokenTextBox, 1, 1);

            _showTokenCheckBox = new System.Windows.Forms.CheckBox
            {
                Text = "显示",
                AutoSize = true,
                Dock = System.Windows.Forms.DockStyle.Fill,
                Font = new DrawingFont("Microsoft YaHei UI", 8.8f)
            };
            _showTokenCheckBox.CheckedChanged += delegate
            {
                _accountTokenTextBox.UseSystemPasswordChar = !_showTokenCheckBox.Checked;
                _credentialValueTextBox.UseSystemPasswordChar = !_showTokenCheckBox.Checked;
            };
            accountPanel.Controls.Add(_showTokenCheckBox, 2, 1);

            accountPanel.Controls.Add(CreateFieldLabel("登录地址"), 0, 2);
            _accountEndpointTextBox = CreateTextBox();
            accountPanel.Controls.Add(_accountEndpointTextBox, 1, 2);
            accountPanel.SetColumnSpan(_accountEndpointTextBox, 2);

            System.Windows.Forms.FlowLayoutPanel accountButtons = new System.Windows.Forms.FlowLayoutPanel
            {
                Dock = System.Windows.Forms.DockStyle.Fill,
                FlowDirection = System.Windows.Forms.FlowDirection.LeftToRight,
                AutoSize = true,
                WrapContents = true,
                Padding = new System.Windows.Forms.Padding(0, 8, 0, 4)
            };
            accountPanel.Controls.Add(accountButtons, 0, 3);
            accountPanel.SetColumnSpan(accountButtons, 3);

            accountButtons.Controls.Add(CreateButton("🔐 登录 / 验证", OnAccountSignInClicked));

            _accountStatusLabel = new System.Windows.Forms.Label
            {
                AutoSize = true,
                Dock = System.Windows.Forms.DockStyle.Fill,
                Font = new DrawingFont("Microsoft YaHei UI", 9f, FontStyle.Bold),
                ForeColor = UiText,
                Padding = new System.Windows.Forms.Padding(2, 6, 0, 0),
                Text = "未登录"
            };
            accountPanel.Controls.Add(_accountStatusLabel, 0, 4);
            accountPanel.SetColumnSpan(_accountStatusLabel, 3);

            System.Windows.Forms.Label accountHintLabel = new System.Windows.Forms.Label
            {
                AutoSize = false,
                Dock = System.Windows.Forms.DockStyle.Fill,
                AutoEllipsis = true,
                Font = new DrawingFont("Microsoft YaHei UI", 8.4f),
                ForeColor = UiTextMuted,
                Padding = new System.Windows.Forms.Padding(2, 10, 2, 0),
                Text = "提示：登录地址可留空（仅本地保存）。填了 http(s) 才会做一次联网校验。"
            };
            accountPanel.Controls.Add(accountHintLabel, 0, 5);
            accountPanel.SetColumnSpan(accountHintLabel, 3);

            // 授权状态显示与账号状态合并共用同一个 Label
            _licenseStatusLabel = _accountStatusLabel;

            // ============== Tab 3: 设置 ==============
            System.Windows.Forms.TabPage settingsTab = new System.Windows.Forms.TabPage("设置")
            {
                Padding = new System.Windows.Forms.Padding(8),
                BackColor = DrawingColor.White,
                AutoScroll = true
            };
            tabs.TabPages.Add(settingsTab);

            System.Windows.Forms.TableLayoutPanel configPanel = new System.Windows.Forms.TableLayoutPanel
            {
                Dock = System.Windows.Forms.DockStyle.Top,
                ColumnCount = 2,
                RowCount = 4,
                AutoSize = true,
                AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink,
                Font = new DrawingFont("Microsoft YaHei UI", 9f)
            };
            configPanel.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 92f));
            configPanel.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 100f));
            for (int i = 0; i < 4; i++)
            {
                configPanel.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 30f));
            }
            settingsTab.Controls.Add(configPanel);

            // 清单来源字段保留实例（用于加载/保存配置），但不在 UI 中显示，避免暴露内部诊断路径给最终用户。
            _manifestSourceTextBox = CreateTextBox();
            _manifestSourceTextBox.Visible = false;

            configPanel.Controls.Add(CreateFieldLabel("授权端点"), 0, 0);
            _licenseEndpointTextBox = CreateTextBox();
            configPanel.Controls.Add(_licenseEndpointTextBox, 1, 0);

            configPanel.Controls.Add(CreateFieldLabel("凭证头"), 0, 1);
            _credentialHeaderTextBox = CreateTextBox();
            configPanel.Controls.Add(_credentialHeaderTextBox, 1, 1);

            configPanel.Controls.Add(CreateFieldLabel("凭证值"), 0, 2);
            _credentialValueTextBox = CreateTextBox();
            _credentialValueTextBox.UseSystemPasswordChar = true;
            configPanel.Controls.Add(_credentialValueTextBox, 1, 2);

            configPanel.Controls.Add(CreateFieldLabel("刷新秒数"), 0, 3);
            _refreshSecondsInput = new System.Windows.Forms.NumericUpDown
            {
                Dock = System.Windows.Forms.DockStyle.Fill,
                Minimum = 60,
                Maximum = 3600,
                Value = 300,
                Font = new DrawingFont("Microsoft YaHei UI", 9f)
            };
            configPanel.Controls.Add(_refreshSecondsInput, 1, 3);

            // 版本 / 更新分组
            System.Windows.Forms.GroupBox versionGroup = new System.Windows.Forms.GroupBox
            {
                Text = "版本与网络更新",
                Dock = System.Windows.Forms.DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink,
                Font = new DrawingFont("Microsoft YaHei UI", 9.2f, FontStyle.Bold),
                ForeColor = UiTextMuted,
                Padding = new System.Windows.Forms.Padding(10, 8, 10, 10),
                Margin = new System.Windows.Forms.Padding(0, 12, 0, 0)
            };
            settingsTab.Controls.Add(versionGroup);

            System.Windows.Forms.TableLayoutPanel versionPanel = new System.Windows.Forms.TableLayoutPanel
            {
                Dock = System.Windows.Forms.DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                AutoSize = true,
                AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink,
                Font = new DrawingFont("Microsoft YaHei UI", 9f)
            };
            versionGroup.Controls.Add(versionPanel);

            _versionStatusLabel = new System.Windows.Forms.Label
            {
                AutoSize = true,
                Dock = System.Windows.Forms.DockStyle.Top,
                Font = new DrawingFont("Microsoft YaHei UI", 9f),
                ForeColor = UiText,
                Text = "当前版本：-",
                Padding = new System.Windows.Forms.Padding(0, 2, 0, 6)
            };
            versionPanel.Controls.Add(_versionStatusLabel, 0, 0);

            System.Windows.Forms.FlowLayoutPanel versionButtons = new System.Windows.Forms.FlowLayoutPanel
            {
                Dock = System.Windows.Forms.DockStyle.Fill,
                FlowDirection = System.Windows.Forms.FlowDirection.LeftToRight,
                AutoSize = true,
                WrapContents = true
            };
            versionPanel.Controls.Add(versionButtons, 0, 1);

            versionButtons.Controls.Add(CreateSecondaryButton("检查更新", delegate
            {
                _manager.TryCheckLicense(silent: false);
                _manager.RefreshPaletteView();
            }));
            versionButtons.Controls.Add(CreateButton("⬇ 下载新版本", OnDownloadUpdateClicked));

            // 自启动分组
            System.Windows.Forms.GroupBox autoStartGroup = new System.Windows.Forms.GroupBox
            {
                Text = "AutoCAD 启动集成",
                Dock = System.Windows.Forms.DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink,
                Font = new DrawingFont("Microsoft YaHei UI", 9.2f, FontStyle.Bold),
                ForeColor = UiTextMuted,
                Padding = new System.Windows.Forms.Padding(10, 8, 10, 10),
                Margin = new System.Windows.Forms.Padding(0, 12, 0, 0)
            };
            settingsTab.Controls.Add(autoStartGroup);
            autoStartGroup.BringToFront();

            System.Windows.Forms.TableLayoutPanel autoStartPanel = new System.Windows.Forms.TableLayoutPanel
            {
                Dock = System.Windows.Forms.DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                AutoSize = true,
                AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink,
                Font = new DrawingFont("Microsoft YaHei UI", 9f, FontStyle.Regular)
            };
            autoStartGroup.Controls.Add(autoStartPanel);

            _autoStartStatusLabel = new System.Windows.Forms.Label
            {
                AutoSize = true,
                Dock = System.Windows.Forms.DockStyle.Top,
                Font = new DrawingFont("Microsoft YaHei UI", 9f),
                ForeColor = UiText,
                Text = "状态：未检测",
                Padding = new System.Windows.Forms.Padding(0, 2, 0, 4)
            };
            autoStartPanel.Controls.Add(_autoStartStatusLabel, 0, 0);

            System.Windows.Forms.FlowLayoutPanel autoStartButtons = new System.Windows.Forms.FlowLayoutPanel
            {
                Dock = System.Windows.Forms.DockStyle.Fill,
                FlowDirection = System.Windows.Forms.FlowDirection.LeftToRight,
                AutoSize = true,
                WrapContents = true,
                Padding = new System.Windows.Forms.Padding(0, 4, 0, 0)
            };
            autoStartPanel.Controls.Add(autoStartButtons, 0, 1);

            autoStartButtons.Controls.Add(CreateButton("启用自启动", OnInstallAutoStartClicked));
            autoStartButtons.Controls.Add(CreateSecondaryButton("停用自启动", OnUninstallAutoStartClicked));
            autoStartButtons.Controls.Add(CreateSecondaryButton("重新检测", delegate { RefreshAutoStartStatus(); }));

            System.Windows.Forms.Label autoStartHint = new System.Windows.Forms.Label
            {
                AutoSize = false,
                Dock = System.Windows.Forms.DockStyle.Fill,
                Height = 36,
                Font = new DrawingFont("Microsoft YaHei UI", 8.4f),
                ForeColor = UiTextMuted,
                Padding = new System.Windows.Forms.Padding(0, 6, 0, 0),
                Text = "启用后，下次启动 AutoCAD 会自动加载当前 DLL；不再需要每次 NETLOAD。"
            };
            autoStartPanel.Controls.Add(autoStartHint, 0, 2);

            // 设置页底部按钮
            System.Windows.Forms.FlowLayoutPanel settingsButtons = new System.Windows.Forms.FlowLayoutPanel
            {
                Dock = System.Windows.Forms.DockStyle.Top,
                FlowDirection = System.Windows.Forms.FlowDirection.LeftToRight,
                AutoSize = true,
                WrapContents = true,
                Padding = new System.Windows.Forms.Padding(0, 10, 0, 0),
                Margin = new System.Windows.Forms.Padding(0, 4, 0, 0)
            };
            settingsTab.Controls.Add(settingsButtons);
            settingsButtons.BringToFront();

            settingsButtons.Controls.Add(CreateButton("✔ 保存配置", OnSaveConfigClicked));
            settingsButtons.Controls.Add(CreateSecondaryButton("打开配置目录", delegate { _manager.OpenConfigFolder(); }));

            _statusLabel = new System.Windows.Forms.Label
            {
                Dock = System.Windows.Forms.DockStyle.Top,
                AutoSize = false,
                Height = 44,
                AutoEllipsis = true,
                Font = new DrawingFont("Microsoft YaHei UI", 8.6f),
                ForeColor = UiTextMuted,
                Padding = new System.Windows.Forms.Padding(4, 6, 4, 2)
            };
            root.Controls.Add(_statusLabel, 0, 2);

            // 暗色主题统一注入：递归把所有内嵌 Panel/Tab/Label/TextBox 染成深色
            ApplyDarkTheme(this);

            RefreshAutoStartStatus();
        }

        /// <summary>递归对控件树注入暗色主题。仅修改未显式偏离主题的控件。</summary>
        private static void ApplyDarkTheme(System.Windows.Forms.Control root)
        {
            foreach (System.Windows.Forms.Control c in root.Controls)
            {
                if (c is System.Windows.Forms.TabControl)
                {
                    c.BackColor = UiBg;
                    c.ForeColor = UiText;
                }
                else if (c is System.Windows.Forms.TabPage)
                {
                    System.Windows.Forms.TabPage tp = (System.Windows.Forms.TabPage)c;
                    tp.BackColor = UiCardBg;
                    tp.ForeColor = UiText;
                    tp.UseVisualStyleBackColor = false;
                }
                else if (c is System.Windows.Forms.TableLayoutPanel
                      || c is System.Windows.Forms.FlowLayoutPanel
                      || c is System.Windows.Forms.Panel)
                {
                    if (c.BackColor == System.Drawing.SystemColors.Control || c.BackColor == DrawingColor.Empty)
                    {
                        c.BackColor = UiCardBg;
                    }
                    c.ForeColor = UiText;
                }
                else if (c is System.Windows.Forms.Label)
                {
                    System.Windows.Forms.Label lbl = (System.Windows.Forms.Label)c;
                    if (lbl.ForeColor == System.Drawing.SystemColors.ControlText)
                    {
                        lbl.ForeColor = UiText;
                    }
                    lbl.BackColor = DrawingColor.Transparent;
                }
                else if (c is System.Windows.Forms.TextBox)
                {
                    System.Windows.Forms.TextBox tb = (System.Windows.Forms.TextBox)c;
                    tb.BackColor = UiCardBgHover;
                    tb.ForeColor = UiText;
                    tb.BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle;
                }
                else if (c is System.Windows.Forms.NumericUpDown)
                {
                    System.Windows.Forms.NumericUpDown nud = (System.Windows.Forms.NumericUpDown)c;
                    nud.BackColor = UiCardBgHover;
                    nud.ForeColor = UiText;
                    nud.BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle;
                }
                else if (c is System.Windows.Forms.CheckBox)
                {
                    System.Windows.Forms.CheckBox cb = (System.Windows.Forms.CheckBox)c;
                    if (cb.Visible)
                    {
                        cb.ForeColor = UiText;
                        cb.BackColor = DrawingColor.Transparent;
                    }
                }

                if (c.HasChildren) ApplyDarkTheme(c);
            }
        }

        public void UpdateView(SuiteConfiguration configuration, PluginManifest manifest, HashSet<string> allowedFeatureIds, string statusMessage)
        {
            _manifestSourceTextBox.Text = configuration.ManifestSource ?? string.Empty;
            _credentialHeaderTextBox.Text = configuration.CredentialHeader ?? string.Empty;
            _credentialValueTextBox.Text = configuration.CredentialValue ?? string.Empty;
            _hotkeyTextBox.Text = configuration.Hotkey ?? string.Empty;
            _refreshSecondsInput.Value = Math.Max(_refreshSecondsInput.Minimum, Math.Min(_refreshSecondsInput.Maximum, configuration.AutoRefreshSeconds));
            _licenseEndpointTextBox.Text = configuration.LicenseEndpoint ?? string.Empty;

            _accountNameTextBox.Text = configuration.AccountName ?? string.Empty;
            _accountTokenTextBox.Text = configuration.AccountToken ?? string.Empty;
            _accountEndpointTextBox.Text = configuration.AccountEndpoint ?? string.Empty;
            RefreshAccountStatusLabel(configuration);

            RebuildFeatureRows(configuration, manifest, allowedFeatureIds);

            RebuildHotkeyList(configuration, manifest, allowedFeatureIds);

            _statusLabel.Text = string.Format(
                "状态：{0}   内置命令：HALOU / HALOUZK / HALOUKB",
                statusMessage ?? "待命");
        }

        private static bool IsFeatureVisible(CadPluginFeature feature, HashSet<string> allowedFeatureIds)
        {
            if (feature == null) return false;
            if (allowedFeatureIds == null || allowedFeatureIds.Count == 0) return true;
            if (allowedFeatureIds.Contains("*")) return true;
            string id = (feature.Id ?? string.Empty).Trim();
            if (id.Length == 0) return false;
            return allowedFeatureIds.Contains(id);
        }

        private void RebuildHotkeyList(SuiteConfiguration configuration, PluginManifest manifest, HashSet<string> allowedFeatureIds)
        {
            if (_hotkeyListPanel == null) return;
            _hotkeyListPanel.SuspendLayout();
            while (_hotkeyListPanel.Controls.Count > 0)
            {
                System.Windows.Forms.Control c = _hotkeyListPanel.Controls[0];
                _hotkeyListPanel.Controls.RemoveAt(0);
                c.Dispose();
            }
            _featureHotkeyBoxes.Clear();
            _featureCommandBoxes.Clear();
            // 注：_featureAutoLoadBoxes 由"功能" tab 的 RebuildFeatureRows 维护，此处不清空

            if (manifest == null || manifest.Features == null || manifest.Features.Count == 0)
            {
                _hotkeyListPanel.Controls.Add(new System.Windows.Forms.Label
                {
                    Text = "（清单为空，刷新后显示）",
                    AutoSize = true,
                    ForeColor = DrawingColor.FromArgb(140, 140, 140),
                    Font = new DrawingFont("Microsoft YaHei UI", 8.8f),
                    Padding = new System.Windows.Forms.Padding(2, 8, 0, 0)
                });
                _hotkeyListPanel.ResumeLayout();
                return;
            }

            // 表头行
            _hotkeyListPanel.Controls.Add(BuildHotkeyHeaderRow());

            for (int i = 0; i < manifest.Features.Count; i++)
            {
                CadPluginFeature f = manifest.Features[i];
                if (f == null || string.IsNullOrWhiteSpace(f.Id)) continue;
                if (!IsFeatureVisible(f, allowedFeatureIds)) continue;

                string initial = null;
                if (configuration != null)
                {
                    string currentHotkey;
                    string currentCmd;
                    if (configuration.FeatureHotkeys != null
                        && configuration.FeatureHotkeys.TryGetValue(f.Id, out currentHotkey)
                        && !string.IsNullOrWhiteSpace(currentHotkey))
                    {
                        initial = currentHotkey;
                    }
                    else if (configuration.FeatureCommands != null
                        && configuration.FeatureCommands.TryGetValue(f.Id, out currentCmd)
                        && !string.IsNullOrWhiteSpace(currentCmd))
                    {
                        initial = currentCmd;
                    }
                }

                _hotkeyListPanel.Controls.Add(BuildHotkeyFeatureRow(f, initial));
                // AutoLoad 开关已移到"功能" tab
            }

            _hotkeyListPanel.ResumeLayout(true);
            _hotkeyListPanel.PerformLayout();
        }

        /// <summary>构建快捷键列表的一行：左侧标题 + 输入框 + 清除按钮。AutoLoad 开关已移到"功能" tab。</summary>
        private System.Windows.Forms.Panel BuildHotkeyFeatureRow(CadPluginFeature f, string initialText)
        {
            System.Windows.Forms.Panel row = new System.Windows.Forms.Panel
            {
                Width = Math.Max(1, _hotkeyListPanel.ClientSize.Width - 4),
                Height = 30,
                Margin = new System.Windows.Forms.Padding(0, 0, 0, 2),
                Anchor = System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right | System.Windows.Forms.AnchorStyles.Top
            };
            // 让行随 FlowLayoutPanel 宽度变化
            row.SizeChanged += delegate { LayoutHotkeyRow(row); };
            _hotkeyListPanel.SizeChanged += delegate
            {
                if (row.IsDisposed) return;
                row.Width = Math.Max(1, _hotkeyListPanel.ClientSize.Width - 4);
            };

            System.Windows.Forms.Label title = new System.Windows.Forms.Label
            {
                Text = f.Title ?? f.Id,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new DrawingFont("Microsoft YaHei UI", 9.2f),
                ForeColor = UiText,
                Tag = "title"
            };
            row.Controls.Add(title);

            HotkeyCaptureTextBox box = new HotkeyCaptureTextBox
            {
                BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle,
                Font = new DrawingFont("Microsoft YaHei UI", 9.2f),
                AllowPlainText = true,
                Tag = "box"
            };
            if (!string.IsNullOrEmpty(initialText)) box.Text = initialText;
            row.Controls.Add(box);
            _featureHotkeyBoxes[f.Id] = box;

            string featureIdCopy = f.Id;
            System.Windows.Forms.Button clearBtn = CreateSmallButton("×", delegate
            {
                HotkeyCaptureTextBox b;
                if (_featureHotkeyBoxes.TryGetValue(featureIdCopy, out b)) b.Text = string.Empty;
            });
            clearBtn.Tag = "clear";
            row.Controls.Add(clearBtn);

            LayoutHotkeyRow(row);
            return row;
        }

        /// <summary>表头行（“功能” / “全局热键 / AutoCAD 命令名”）。</summary>
        private System.Windows.Forms.Panel BuildHotkeyHeaderRow()
        {
            System.Windows.Forms.Panel row = new System.Windows.Forms.Panel
            {
                Width = Math.Max(1, _hotkeyListPanel.ClientSize.Width - 4),
                Height = 26,
                Margin = new System.Windows.Forms.Padding(0, 0, 0, 2)
            };
            _hotkeyListPanel.SizeChanged += delegate
            {
                if (row.IsDisposed) return;
                row.Width = Math.Max(1, _hotkeyListPanel.ClientSize.Width - 4);
            };
            row.SizeChanged += delegate { LayoutHotkeyRow(row); };

            System.Windows.Forms.Label left = new System.Windows.Forms.Label
            {
                Text = "功能",
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new DrawingFont("Microsoft YaHei UI", 8.4f, FontStyle.Bold),
                ForeColor = UiTextSubtle,
                Tag = "title"
            };
            row.Controls.Add(left);

            System.Windows.Forms.Label right = new System.Windows.Forms.Label
            {
                Text = "全局热键 / AutoCAD 命令名",
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new DrawingFont("Microsoft YaHei UI", 8.4f, FontStyle.Bold),
                ForeColor = UiTextSubtle,
                Tag = "box"
            };
            row.Controls.Add(right);

            LayoutHotkeyRow(row);
            return row;
        }

        /// <summary>按 36% 标题 / 弹性输入框 / 32px 清除布局。</summary>
        private static void LayoutHotkeyRow(System.Windows.Forms.Panel row)
        {
            int w = row.ClientSize.Width;
            int h = row.ClientSize.Height;
            const int clearBtnW = 32;
            int titleW = (int)(w * 0.36);
            int boxW = w - titleW - clearBtnW - 8;
            if (boxW < 40) boxW = 40;

            System.Windows.Forms.Control title = null, box = null, clear = null;
            foreach (System.Windows.Forms.Control c in row.Controls)
            {
                string tag = c.Tag as string;
                if (tag == "title") title = c;
                else if (tag == "box") box = c;
                else if (tag == "clear") clear = c;
            }

            int x = 0;
            if (title != null)
            {
                title.SetBounds(x, 0, titleW, h);
                x += titleW + 4;
            }
            if (box != null)
            {
                int boxH = 22;
                box.SetBounds(x, (h - boxH) / 2, boxW, boxH);
                x += boxW + 4;
            }
            if (clear != null)
            {
                clear.SetBounds(x, (h - 24) / 2, clearBtnW - 4, 24);
            }
        }

        // ========== 深蓝白色调（2026-05 UI v3：deep navy + soft white） ==========
        private static readonly DrawingColor UiBg = DrawingColor.FromArgb(15, 23, 42);          // 根背景：深海军蓝 #0F172A
        private static readonly DrawingColor UiCardBg = DrawingColor.FromArgb(30, 41, 59);      // 卡片/Tab 内容：石板蓝 #1E293B
        private static readonly DrawingColor UiCardBgHover = DrawingColor.FromArgb(45, 60, 84); // hover/选中行 #2D3C54
        private static readonly DrawingColor UiPrimary = DrawingColor.FromArgb(56, 132, 255);   // 主色：明亮蓝 #3884FF
        private static readonly DrawingColor UiPrimaryHover = DrawingColor.FromArgb(96, 165, 255);
        private static readonly DrawingColor UiPrimaryDown = DrawingColor.FromArgb(37, 99, 235);
        private static readonly DrawingColor UiSecondaryBg = DrawingColor.FromArgb(51, 65, 90);
        private static readonly DrawingColor UiSecondaryHover = DrawingColor.FromArgb(71, 85, 110);
        private static readonly DrawingColor UiBorder = DrawingColor.FromArgb(59, 75, 102);
        private static readonly DrawingColor UiBorderFocus = DrawingColor.FromArgb(96, 165, 255);
        private static readonly DrawingColor UiText = DrawingColor.FromArgb(241, 245, 249);     // 主文本：近白 #F1F5F9
        private static readonly DrawingColor UiTextMuted = DrawingColor.FromArgb(203, 213, 225); // 次级 #CBD5E1
        private static readonly DrawingColor UiTextSubtle = DrawingColor.FromArgb(165, 180, 200); // 弱化 #A5B4C8

        private static System.Windows.Forms.Label CreateFieldLabel(string text)
        {
            return new System.Windows.Forms.Label
            {
                Text = text,
                AutoSize = false,
                Dock = System.Windows.Forms.DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new DrawingFont("Microsoft YaHei UI", 9f),
                ForeColor = UiText,
                Padding = new System.Windows.Forms.Padding(0, 0, 8, 0)
            };
        }

        private static System.Windows.Forms.TextBox CreateTextBox()
        {
            System.Windows.Forms.TextBox tb = new System.Windows.Forms.TextBox
            {
                Dock = System.Windows.Forms.DockStyle.Fill,
                Font = new DrawingFont("Microsoft YaHei UI", 9.2f),
                BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle,
                BackColor = UiCardBg,
                ForeColor = UiText,
                Margin = new System.Windows.Forms.Padding(0, 4, 0, 4)
            };
            tb.GotFocus += delegate { tb.BackColor = UiCardBgHover; };
            tb.LostFocus += delegate { tb.BackColor = UiCardBg; };
            return tb;
        }

        /// <summary>主要按钮（蓝色填充），用于核心动作：保存、运行。</summary>
        private static System.Windows.Forms.Button CreateButton(string text, Action onClick)
        {
            return CreateStyledButton(text, onClick, primary: true);
        }

        /// <summary>次级按钮（淡灰），用于辅助动作。</summary>
        private static System.Windows.Forms.Button CreateSecondaryButton(string text, Action onClick)
        {
            return CreateStyledButton(text, onClick, primary: false);
        }

        /// <summary>行内小按钮，用于"清除"这类细小动作。</summary>
        private static System.Windows.Forms.Button CreateSmallButton(string text, Action onClick)
        {
            System.Windows.Forms.Button btn = new System.Windows.Forms.Button
            {
                Text = text,
                AutoSize = false,
                Size = new Size(28, 24),
                Font = new DrawingFont("Microsoft YaHei UI", 10f),
                Margin = new System.Windows.Forms.Padding(4, 3, 0, 3),
                FlatStyle = System.Windows.Forms.FlatStyle.Flat,
                BackColor = UiSecondaryBg,
                ForeColor = UiTextMuted,
                Cursor = System.Windows.Forms.Cursors.Hand,
                TabStop = false
            };
            btn.FlatAppearance.BorderSize = 0;
            btn.FlatAppearance.MouseOverBackColor = UiSecondaryHover;
            btn.FlatAppearance.MouseDownBackColor = UiBorder;
            btn.Click += delegate { onClick(); };
            return btn;
        }

        private static System.Windows.Forms.Button CreateStyledButton(string text, Action onClick, bool primary)
        {
            System.Windows.Forms.Button btn = new System.Windows.Forms.Button
            {
                Text = text,
                AutoSize = true,
                AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(96, 32),
                Font = new DrawingFont("Microsoft YaHei UI", 9.2f, primary ? FontStyle.Bold : FontStyle.Regular),
                Margin = new System.Windows.Forms.Padding(0, 2, 8, 2),
                Padding = new System.Windows.Forms.Padding(14, 4, 14, 4),
                FlatStyle = System.Windows.Forms.FlatStyle.Flat,
                Cursor = System.Windows.Forms.Cursors.Hand,
                UseVisualStyleBackColor = false
            };
            if (primary)
            {
                btn.BackColor = UiPrimary;
                btn.ForeColor = DrawingColor.White;
                btn.FlatAppearance.BorderSize = 0;
                btn.FlatAppearance.MouseOverBackColor = UiPrimaryHover;
                btn.FlatAppearance.MouseDownBackColor = UiPrimaryDown;
            }
            else
            {
                btn.BackColor = UiSecondaryBg;
                btn.ForeColor = UiText;
                btn.FlatAppearance.BorderSize = 1;
                btn.FlatAppearance.BorderColor = UiBorder;
                btn.FlatAppearance.MouseOverBackColor = UiSecondaryHover;
                btn.FlatAppearance.MouseDownBackColor = UiBorder;
            }
            btn.Click += delegate { onClick(); };
            return btn;
        }

        private void OnSaveConfigClicked()
        {
            SuiteConfiguration configuration = BuildConfigurationFromUi();
            _manager.SaveConfiguration(configuration);
        }

        private SuiteConfiguration BuildConfigurationFromUi()
        {
            Dictionary<string, string> featureHotkeys = new Dictionary<string, string>();
            Dictionary<string, string> featureCommands = new Dictionary<string, string>();
            Dictionary<string, bool> autoLoadFeatures = new Dictionary<string, bool>();
            foreach (KeyValuePair<string, System.Windows.Forms.CheckBox> kv in _featureAutoLoadBoxes)
            {
                if (kv.Value != null && kv.Value.Checked) autoLoadFeatures[kv.Key] = true;
            }
            foreach (KeyValuePair<string, HotkeyCaptureTextBox> kv in _featureHotkeyBoxes)
            {
                string v = (kv.Value.Text ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(v)) continue;
                // 包含 + 即认为组合热键；其它为 AutoCAD 命令名
                if (v.IndexOf('+') >= 0)
                {
                    featureHotkeys[kv.Key] = v;
                }
                else
                {
                    featureCommands[kv.Key] = v.ToUpperInvariant();
                }
            }

            return new SuiteConfiguration
            {
                ManifestSource = _manifestSourceTextBox.Text.Trim(),
                CredentialHeader = _credentialHeaderTextBox.Text.Trim(),
                CredentialValue = _credentialValueTextBox.Text.Trim(),
                Hotkey = _hotkeyTextBox.Text.Trim(),
                AutoRefreshSeconds = Decimal.ToInt32(_refreshSecondsInput.Value),
                AccountName = _accountNameTextBox.Text.Trim(),
                AccountToken = _accountTokenTextBox.Text.Trim(),
                AccountEndpoint = _accountEndpointTextBox.Text.Trim(),
                LicenseEndpoint = _licenseEndpointTextBox.Text.Trim(),
                FeatureHotkeys = featureHotkeys,
                FeatureCommands = featureCommands,
                AutoLoadFeatures = autoLoadFeatures
            };
        }

        private void OnAccountSignInClicked()
        {
            string name = _accountNameTextBox.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                System.Windows.Forms.MessageBox.Show(
                    "请先填写账号名。",
                    "账号凭证",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Warning);
                return;
            }

            SuiteConfiguration cfg = BuildConfigurationFromUi();
            _manager.SaveConfiguration(cfg);

            // 直接向授权端点查询：通过 → 绿灯；禁用 → 红灯；网络异常 → 橙灯
            _manager.TryCheckLicense(silent: false);
            _manager.RefreshPaletteView();
        }

        private void OnAccountClearClicked()
        {
            _accountNameTextBox.Text = string.Empty;
            _accountTokenTextBox.Text = string.Empty;
            _accountEndpointTextBox.Text = string.Empty;
            SuiteConfiguration cfg = BuildConfigurationFromUi();
            _manager.SaveConfiguration(cfg);
            _accountStatusLabel.Text = "未登录";
            _accountStatusLabel.ForeColor = DrawingColor.FromArgb(70, 70, 70);
        }

        private void OnAccountApplyToHeaderClicked()
        {
            string token = _accountTokenTextBox.Text.Trim();
            if (string.IsNullOrEmpty(token))
            {
                System.Windows.Forms.MessageBox.Show(
                    "当前没有登录令牌，无法写入凭证头。",
                    "账号凭证",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Information);
                return;
            }

            _credentialHeaderTextBox.Text = "Authorization";
            _credentialValueTextBox.Text = "Bearer " + token;
            _accountStatusLabel.Text = "已把账号令牌套用到清单请求头";
            _accountStatusLabel.ForeColor = DrawingColor.FromArgb(40, 90, 150);
        }

        private void RefreshAccountStatusLabel(SuiteConfiguration configuration)
        {
            if (configuration == null || string.IsNullOrWhiteSpace(configuration.AccountName))
            {
                _accountStatusLabel.Text = "未登录";
                _accountStatusLabel.ForeColor = DrawingColor.FromArgb(70, 70, 70);
                return;
            }

            _accountStatusLabel.Text = string.Format("● 当前账号：{0}", configuration.AccountName);
            _accountStatusLabel.ForeColor = DrawingColor.FromArgb(40, 90, 150);
        }

        private void RefreshAutoStartStatus()
        {
            try
            {
                bool installed = _manager.IsAutoStartInstalled();
                if (installed)
                {
                    _autoStartStatusLabel.Text = "状态：✔ 已启用（下次启动 AutoCAD 自动加载）";
                    _autoStartStatusLabel.ForeColor = DrawingColor.FromArgb(40, 120, 40);
                }
                else
                {
                    _autoStartStatusLabel.Text = "状态：○ 未启用";
                    _autoStartStatusLabel.ForeColor = DrawingColor.FromArgb(70, 70, 70);
                }
            }
            catch (System.Exception ex)
            {
                _autoStartStatusLabel.Text = "状态：检测失败 - " + ex.Message;
                _autoStartStatusLabel.ForeColor = DrawingColor.FromArgb(180, 40, 40);
            }
        }

        private void OnInstallAutoStartClicked()
        {
            try
            {
                string detail;
                int n = _manager.InstallAutoStart(out detail);
                if (n == 0)
                {
                    System.Windows.Forms.MessageBox.Show(
                        "未在注册表找到 AutoCAD 安装项。请确认已安装 AutoCAD 并至少启动过一次。",
                        "自启动",
                        System.Windows.Forms.MessageBoxButtons.OK,
                        System.Windows.Forms.MessageBoxIcon.Warning);
                }
                else
                {
                    System.Windows.Forms.MessageBox.Show(
                        string.Format("已写入 {0} 个 AutoCAD 版本/产品。\n下次启动 AutoCAD 将自动加载。\n\n{1}", n, detail),
                        "自启动",
                        System.Windows.Forms.MessageBoxButtons.OK,
                        System.Windows.Forms.MessageBoxIcon.Information);
                }
            }
            catch (System.Exception ex)
            {
                System.Windows.Forms.MessageBox.Show(
                    "启用失败：" + ex.Message,
                    "自启动",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Error);
            }

            RefreshAutoStartStatus();
        }

        private void OnUninstallAutoStartClicked()
        {
            try
            {
                string detail;
                int n = _manager.UninstallAutoStart(out detail);
                System.Windows.Forms.MessageBox.Show(
                    n == 0
                        ? "没有检测到已安装的自启动项。"
                        : string.Format("已从 {0} 个 AutoCAD 版本/产品移除。\n\n{1}", n, detail),
                    "自启动",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Information);
            }
            catch (System.Exception ex)
            {
                System.Windows.Forms.MessageBox.Show(
                    "停用失败：" + ex.Message,
                    "自启动",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Error);
            }

            RefreshAutoStartStatus();
        }

        public void UpdateLicenseView(LicenseStatus status, string message, string currentVersion, string latestVersion, string downloadUrl, string releaseNotes)
        {
            if (_licenseStatusLabel != null)
            {
                switch (status)
                {
                    case LicenseStatus.Allowed:
                        _licenseStatusLabel.Text = message ?? "✔ 已授权";
                        _licenseStatusLabel.ForeColor = DrawingColor.FromArgb(40, 120, 40);
                        break;
                    case LicenseStatus.Denied:
                        _licenseStatusLabel.Text = message ?? "✖ 已被停用";
                        _licenseStatusLabel.ForeColor = DrawingColor.FromArgb(200, 40, 40);
                        break;
                    case LicenseStatus.NotConfigured:
                        _licenseStatusLabel.Text = message ?? "● 未配置账号";
                        _licenseStatusLabel.ForeColor = DrawingColor.FromArgb(150, 100, 20);
                        break;
                    default:
                        _licenseStatusLabel.Text = message ?? "○ 未检查";
                        _licenseStatusLabel.ForeColor = DrawingColor.FromArgb(100, 100, 100);
                        break;
                }
            }

            if (_versionStatusLabel != null)
            {
                string versionLine = string.Format("当前版本：v{0}   |   最新版本：v{1}", currentVersion ?? "-", latestVersion ?? "-");
                bool hasUpdate = _manager.IsUpdateAvailable();
                if (hasUpdate)
                {
                    versionLine += "   【有新版本可用】";
                    _versionStatusLabel.ForeColor = DrawingColor.FromArgb(200, 80, 20);
                }
                else
                {
                    _versionStatusLabel.ForeColor = DrawingColor.FromArgb(70, 70, 70);
                }

                if (!string.IsNullOrWhiteSpace(releaseNotes))
                {
                    versionLine += "\n更新说明：" + releaseNotes;
                }

                _versionStatusLabel.Text = versionLine;
            }
        }

        private void OnDownloadUpdateClicked()
        {
            if (!_manager.IsUpdateAvailable())
            {
                System.Windows.Forms.MessageBox.Show(
                    string.Format("当前已是最新版本 v{0}。", HalouSuiteManager.CurrentVersion),
                    "网络更新",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Information);
                return;
            }

            // 在线热更新：先把进度框显示出来，等 Shown 事件再启动下载，
            // 否则下载若太快，进度事件会在 Form 句柄创建前丢失。
            HotUpdateProgressForm dlg = new HotUpdateProgressForm(
                "下载新版本 v" + _manager.LatestVersion);
            string[] resultMsg = new string[1];
            bool[] resultOk = new bool[1];

            dlg.Shown += delegate
            {
                _manager.StartHotUpdate(
                    delegate(int pct, long got, long total) { dlg.ReportProgress(pct, got, total); },
                    delegate(bool ok, string msg)
                    {
                        resultOk[0] = ok;
                        resultMsg[0] = msg;
                        dlg.SetStatus(ok ? "更新完成" : "更新失败");
                        dlg.MarkFinished(ok);
                    });
            };
            dlg.ShowDialog(this);

            // 对话框关闭后才弹结果框（避免两个模态打架）
            string finalMsg = resultMsg[0] ?? (resultOk[0] ? "更新已应用" : "未知错误");
            System.Windows.Forms.MessageBox.Show(
                finalMsg,
                "网络更新",
                System.Windows.Forms.MessageBoxButtons.OK,
                resultOk[0] ? System.Windows.Forms.MessageBoxIcon.Information
                             : System.Windows.Forms.MessageBoxIcon.Error);
        }

        private void OnFeatureSelected(string featureId)
        {
            CadPluginFeature feature;
            if (!_featureRowFeatures.TryGetValue(featureId ?? string.Empty, out feature) || feature == null)
            {
                _featureDescriptionLabel.Text = string.Empty;
                return;
            }

            _featureDescriptionLabel.Text = string.Format(
                "{0}\n类型：{1}\n命令：{2}",
                feature.Description ?? "无说明",
                feature.Kind ?? "placeholder",
                feature.Command ?? "未配置");
        }

        private void RunSelectedFeature()
        {
            if (string.IsNullOrEmpty(_selectedFeatureId)) return;
            CadPluginFeature feature;
            if (_featureRowFeatures.TryGetValue(_selectedFeatureId, out feature) && feature != null)
            {
                _manager.RunFeatureById(feature.Id);
            }
        }

        private void SelectFeatureRow(string featureId)
        {
            _selectedFeatureId = featureId;
            // 高亮选中行 / 还原其它行
            foreach (KeyValuePair<string, System.Windows.Forms.Panel> kv in _featureRowPanels)
            {
                if (kv.Value == null || kv.Value.IsDisposed) continue;
                bool isSel = string.Equals(kv.Key, featureId, StringComparison.OrdinalIgnoreCase);
                kv.Value.BackColor = isSel ? UiCardBgHover : UiCardBg;
            }
            OnFeatureSelected(featureId);
        }

        private void RebuildFeatureRows(SuiteConfiguration configuration, PluginManifest manifest, HashSet<string> allowedFeatureIds)
        {
            if (_featureRowsPanel == null) return;
            _featureRowsPanel.SuspendLayout();
            while (_featureRowsPanel.Controls.Count > 0)
            {
                System.Windows.Forms.Control c = _featureRowsPanel.Controls[0];
                _featureRowsPanel.Controls.RemoveAt(0);
                c.Dispose();
            }
            _featureRowPanels.Clear();
            _featureRowFeatures.Clear();
            _featureAutoLoadBoxes.Clear();

            string firstId = null;
            string keepSelectedId = _selectedFeatureId;

            if (manifest == null || manifest.Features == null || manifest.Features.Count == 0)
            {
                _featureRowsPanel.Controls.Add(new System.Windows.Forms.Label
                {
                    Text = "（清单为空，刷新后显示）",
                    AutoSize = true,
                    ForeColor = UiTextMuted,
                    Font = new DrawingFont("Microsoft YaHei UI", 8.8f),
                    Padding = new System.Windows.Forms.Padding(2, 8, 0, 0)
                });
                _featureRowsPanel.ResumeLayout();
                _selectedFeatureId = null;
                _featureDescriptionLabel.Text = string.Empty;
                return;
            }

            foreach (CadPluginFeature feature in manifest.Features)
            {
                if (!IsFeatureVisible(feature, allowedFeatureIds)) continue;
                if (feature == null || string.IsNullOrWhiteSpace(feature.Id)) continue;

                bool autoLoad = false;
                if (configuration != null && configuration.AutoLoadFeatures != null)
                {
                    bool al;
                    if (configuration.AutoLoadFeatures.TryGetValue(feature.Id, out al)) autoLoad = al;
                }

                System.Windows.Forms.Panel row = BuildFeatureRow(feature, autoLoad);
                _featureRowsPanel.Controls.Add(row);
                _featureRowPanels[feature.Id] = row;
                _featureRowFeatures[feature.Id] = feature;

                if (firstId == null) firstId = feature.Id;
            }

            _featureRowsPanel.ResumeLayout(true);
            _featureRowsPanel.PerformLayout();

            // 维持原有选中（如果还存在），否则选第一个
            if (keepSelectedId != null && _featureRowPanels.ContainsKey(keepSelectedId))
            {
                SelectFeatureRow(keepSelectedId);
            }
            else if (firstId != null)
            {
                SelectFeatureRow(firstId);
            }
            else
            {
                _selectedFeatureId = null;
                _featureDescriptionLabel.Text = string.Empty;
            }
        }

        /// <summary>构建功能行：[功能名 Label][自动加载 Toggle 按钮]。点击行=选中；双击行=运行；toggle 不冒泡。</summary>
        private System.Windows.Forms.Panel BuildFeatureRow(CadPluginFeature f, bool autoLoad)
        {
            string featureIdCopy = f.Id;
            string suffix = f.Enabled ? string.Empty : " [停用]";
            string displayText = (f.Title ?? f.Id ?? "未命名功能") + suffix;

            System.Windows.Forms.Panel row = new System.Windows.Forms.Panel
            {
                Width = Math.Max(1, _featureRowsPanel.ClientSize.Width - 6),
                Height = 32,
                Margin = new System.Windows.Forms.Padding(0, 0, 0, 2),
                BackColor = UiCardBg,
                Cursor = System.Windows.Forms.Cursors.Hand
            };
            // 行宽随 FlowLayoutPanel 变化
            _featureRowsPanel.SizeChanged += delegate
            {
                if (row.IsDisposed) return;
                row.Width = Math.Max(1, _featureRowsPanel.ClientSize.Width - 6);
            };

            System.Windows.Forms.Label title = new System.Windows.Forms.Label
            {
                Text = displayText,
                AutoSize = false,
                Dock = System.Windows.Forms.DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new DrawingFont("Microsoft YaHei UI", 10f),
                ForeColor = UiText,
                Padding = new System.Windows.Forms.Padding(8, 0, 4, 0),
                Cursor = System.Windows.Forms.Cursors.Hand
            };

            // 隐藏的 CheckBox：保持 _featureAutoLoadBoxes 字典契约不变（保存逻辑零改动）
            System.Windows.Forms.CheckBox hiddenBox = new System.Windows.Forms.CheckBox
            {
                Visible = false,
                Checked = autoLoad
            };
            row.Controls.Add(hiddenBox);
            _featureAutoLoadBoxes[f.Id] = hiddenBox;

            ToggleSwitch toggle = new ToggleSwitch
            {
                Checked = autoLoad,
                Dock = System.Windows.Forms.DockStyle.Right,
                Width = 44,
                Margin = new System.Windows.Forms.Padding(4)
            };
            toggle.CheckedChanged += delegate
            {
                hiddenBox.Checked = toggle.Checked;
            };
            try
            {
                System.Windows.Forms.ToolTip tt = new System.Windows.Forms.ToolTip();
                tt.SetToolTip(toggle, "自动加载：开启后每次新建/打开图纸时自动加载该 LSP（仅 LSP 类型生效）");
            }
            catch { }

            row.Controls.Add(title);
            row.Controls.Add(toggle);
            // WinForms Dock 规则：Right 控件优先于 Fill 计算，需要 toggle 在 z-order 顶部
            row.Controls.SetChildIndex(toggle, 0);

            // 行级点击=选中；双击=运行
            System.EventHandler selectHandler = delegate { SelectFeatureRow(featureIdCopy); };
            System.EventHandler runHandler = delegate { _manager.RunFeatureById(featureIdCopy); };
            row.Click += selectHandler;
            title.Click += selectHandler;
            row.DoubleClick += runHandler;
            title.DoubleClick += runHandler;

            return row;
        }

        /// <summary>iOS 风格滑块开关：圆角轨道 + 圆形把手，开启=蓝、关闭=灰。</summary>
        private sealed class ToggleSwitch : System.Windows.Forms.Control
        {
            private bool _checked;
            public event EventHandler CheckedChanged;

            public bool Checked
            {
                get { return _checked; }
                set
                {
                    if (_checked == value) return;
                    _checked = value;
                    Invalidate();
                    if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
                }
            }

            public ToggleSwitch()
            {
                SetStyle(System.Windows.Forms.ControlStyles.AllPaintingInWmPaint
                    | System.Windows.Forms.ControlStyles.OptimizedDoubleBuffer
                    | System.Windows.Forms.ControlStyles.UserPaint
                    | System.Windows.Forms.ControlStyles.ResizeRedraw
                    | System.Windows.Forms.ControlStyles.SupportsTransparentBackColor, true);
                BackColor = DrawingColor.Transparent;
                Cursor = System.Windows.Forms.Cursors.Hand;
                Size = new Size(44, 22);
                TabStop = false;
            }

            protected override void OnClick(EventArgs e)
            {
                Checked = !Checked;
                base.OnClick(e);
            }

            protected override void OnPaint(System.Windows.Forms.PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

                // 轨道：固定 22px 高度居中绘制（不被父容器拉伸）
                int trackH = 22;
                int trackW = Math.Min(Width - 2, 44);
                int padX = (Width - trackW) / 2;
                int padY = (Height - trackH) / 2;
                int knobD = trackH - 4;

                // 轨道
                DrawingColor trackColor = _checked ? UiPrimary : DrawingColor.FromArgb(80, 84, 90);
                using (var path = RoundedRect(padX, padY, trackW, trackH, trackH / 2))
                using (var br = new SolidBrush(trackColor))
                {
                    g.FillPath(br, path);
                }

                // 把手
                int knobX = _checked ? padX + trackW - knobD - 2 : padX + 2;
                int knobY = padY + 2;
                using (var br = new SolidBrush(DrawingColor.White))
                {
                    g.FillEllipse(br, knobX, knobY, knobD, knobD);
                }
                using (var pen = new Pen(DrawingColor.FromArgb(60, 0, 0, 0), 1f))
                {
                    g.DrawEllipse(pen, knobX, knobY, knobD, knobD);
                }
            }

            private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(int x, int y, int w, int h, int r)
            {
                var path = new System.Drawing.Drawing2D.GraphicsPath();
                int d = r * 2;
                path.AddArc(x, y, d, d, 180, 90);
                path.AddArc(x + w - d, y, d, d, 270, 90);
                path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
                path.AddArc(x, y + h - d, d, d, 90, 90);
                path.CloseFigure();
                return path;
            }
        }
    }
}
