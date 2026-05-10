using System;
using System.Collections.Generic;
using System.Drawing;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Windows;

namespace JsqClipboardCadPlugin
{
    // Palette / TrayItem / HotKey 注册与回调。
    internal sealed partial class HalouSuiteManager
    {
        public void ShowPalette()
        {
            EnsureInitialized();
            _paletteSet.Visible = true;
        }

        public void TogglePalette()
        {
            EnsureInitialized();
            _paletteSet.Visible = !_paletteSet.Visible;
        }

        public void RefreshPaletteView()
        {
            UpdatePaletteView();
        }

        private void UpdatePaletteView()
        {
            if (_paletteControl == null)
            {
                return;
            }

            _paletteControl.UpdateView(_configuration, _manifest, _allowedFeatures, _statusMessage);
            _paletteControl.UpdateLicenseView(_licenseStatus, _licenseMessage, CurrentVersion, _latestVersion, _latestDownloadUrl, _releaseNotes);
        }

        private void EnsurePalette()
        {
            if (_paletteSet != null)
            {
                return;
            }

            _paletteSet = new PaletteSet(PaletteTitle, _paletteId)
            {
                Style = PaletteSetStyles.ShowAutoHideButton | PaletteSetStyles.ShowCloseButton | PaletteSetStyles.ShowPropertiesMenu,
                DockEnabled = DockSides.Left | DockSides.Right,
                MinimumSize = new Size(420, 520),
                Size = new Size(460, 620),
                KeepFocus = false,
                Icon = _suiteIcon,
                Visible = false
            };

            _paletteControl = new SuitePaletteControl(this);
            _paletteSet.Add("功能中心", _paletteControl);
        }

        private void EnsureTrayItem()
        {
            if (_trayItem != null)
            {
                return;
            }

            StatusBar statusBar = Application.StatusBar;
            if (statusBar == null)
            {
                return;
            }

            _trayItem = new TrayItem
            {
                ToolTipText = "Halou 插件集合",
                Icon = _suiteIcon,
                Visible = true,
                Enabled = true
            };
            _trayItem.MouseDown += OnTrayItemMouseDown;
            _trayItem.Deleted += OnTrayItemDeleted;

            if (!statusBar.TrayItems.Contains(_trayItem))
            {
                statusBar.TrayItems.Add(_trayItem);
                statusBar.Update();
            }
        }

        private void EnsureHotKeyWindow()
        {
            if (_hotKeyWindow != null)
            {
                return;
            }

            _hotKeyWindow = new HotKeyWindow();
            _hotKeyWindow.HotKeyPressed += OnHotKeyPressed;
        }

        private void ApplyHotKeyRegistration()
        {
            if (_hotKeyWindow == null)
            {
                return;
            }            _hotKeyWindow.UnregisterAll();
            _hotKeyFeatureMap = new Dictionary<int, string>();

            // 1. 面板弹出热键
            HotkeyBinding palette = HotkeyBinding.Parse(_configuration.Hotkey);
            bool registered = _hotKeyWindow.Register(HotKeyWindow.PaletteHotKeyId, palette.Modifiers, palette.Key);
            if (!registered)
            {
                _statusMessage = string.Format("热键注册失败，当前保留命令 HALOU。配置值：{0}", _configuration.Hotkey);
            }

            // 2. 功能级热键（可选）
            if (_configuration.FeatureHotkeys != null)
            {
                int nextId = HotKeyWindow.FeatureHotKeyStart;
                List<string> conflicts = new List<string>();
                foreach (KeyValuePair<string, string> kv in _configuration.FeatureHotkeys)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key) || string.IsNullOrWhiteSpace(kv.Value)) continue;
                    HotkeyBinding binding = HotkeyBinding.TryParse(kv.Value);
                    if (binding == null) continue;
                    int id = nextId++;
                    if (_hotKeyWindow.Register(id, binding.Modifiers, binding.Key))
                    {
                        _hotKeyFeatureMap[id] = kv.Key;
                    }
                    else
                    {
                        conflicts.Add(string.Format("{0}({1})", kv.Key, kv.Value));
                    }
                }
                if (conflicts.Count > 0)
                {
                    _statusMessage = "以下功能热键注册失败（可能已被占用）：" + string.Join("，", conflicts.ToArray());
                }
            }
        }

        private void OnTrayItemMouseDown(object sender, StatusBarMouseDownEventArgs e)
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left)
            {
                TogglePalette();
            }
        }

        private void OnTrayItemDeleted(object sender, EventArgs e)
        {
            _trayItem = null;
            EnsureTrayItem();
        }

        private void OnHotKeyPressed(object sender, HotKeyPressedEventArgs e)
        {
            if (e.Id == HotKeyWindow.PaletteHotKeyId)
            {
                TogglePalette();
                return;
            }

            string featureId;
            if (_hotKeyFeatureMap != null && _hotKeyFeatureMap.TryGetValue(e.Id, out featureId))
            {
                RunFeatureById(featureId);
            }
        }
    }
}
