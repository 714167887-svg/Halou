using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;

namespace JsqClipboardCadPlugin
{
    // 配置 / 清单刷新 / 功能调度执行（lisp / command / shell / placeholder）。
    internal sealed partial class HalouSuiteManager
    {
        public void RefreshManifest(bool manual)
        {
            EnsureInitialized();
            _manifest = PluginManifest.Load(_configuration, _localManifestPath, _manifestCachePath, out _statusMessage);
            TryCheckLicense(silent: !manual);
            ApplyCommandAliases();
            UpdatePaletteView();

            if (manual)
            {
                WriteMessage(string.Format("{0} 已刷新：{1}", StatusPrefix, _statusMessage));
            }
        }

        public void RunFeatureById(string featureId)
        {
            EnsureInitialized();
            if (_licenseStatus == LicenseStatus.Denied)
            {
                ShowInfo("授权被禁用", string.Format("{0}\n\n如需恢复，请联系授权方。", _licenseMessage ?? "当前账号已被停用。"), isError: true);
                return;
            }

            if (!IsFeatureAllowed(featureId))
            {
                ShowInfo("功能未授权",
                    string.Format("当前账号没有「{0}」功能的使用权限。\n\n如需开通，请联系作者。", featureId ?? ""),
                    isError: true);
                return;
            }

            CadPluginFeature feature = FindFeature(featureId);
            if (feature == null)
            {
                ShowInfo("功能不存在", "当前清单中没有找到对应功能。", isError: true);
                return;
            }

            RunFeature(feature);
        }

        public void SaveConfiguration(SuiteConfiguration configuration)
        {
            EnsureInitialized();
            _configuration = configuration ?? SuiteConfiguration.CreateDefault(_localManifestPath);
            _configuration.Save(_configPath);
            ApplyHotKeyRegistration();
            ApplyCommandAliases();
            EnsureRefreshTimer();
            RefreshManifest(manual: false);
            WriteMessage(string.Format("{0} 配置已保存。", StatusPrefix));
        }

        public void OpenConfigFolder()
        {
            EnsureInitialized();
            Process.Start(new ProcessStartInfo
            {
                FileName = _configDirectory,
                UseShellExecute = true
            });
        }

        private void EnsureRefreshTimer()
        {
            if (_refreshTimer == null)
            {
                _refreshTimer = new System.Windows.Forms.Timer();
                _refreshTimer.Tick += OnRefreshTimerTick;
            }

            int seconds = Math.Max(_configuration.AutoRefreshSeconds, MinimumRefreshSeconds);
            _refreshTimer.Interval = seconds * 1000;
            _refreshTimer.Stop();
            _refreshTimer.Start();
        }

        private void OnRefreshTimerTick(object sender, EventArgs e)
        {
            RefreshManifest(manual: false);
        }

        // ===== 功能查找与分发执行 =====

        private CadPluginFeature FindFeature(string featureId)
        {
            if (_manifest == null || _manifest.Features == null)
            {
                return null;
            }

            return _manifest.Features.FirstOrDefault(feature =>
                feature != null &&
                string.Equals(feature.Id, featureId, StringComparison.OrdinalIgnoreCase));
        }

        private void RunFeature(CadPluginFeature feature)
        {
            try
            {
                if (feature == null || !feature.Enabled)
                {
                    ShowInfo("功能不可用", "该功能当前未启用。", isError: true);
                    return;
                }

                string kind = (feature.Kind ?? string.Empty).Trim().ToLowerInvariant();
                switch (kind)
                {
                    case "lisp":
                        RunLispFeature(feature);
                        break;
                    case "command":
                        RunCommandFeature(feature);
                        break;
                    case "shell":
                    case "script":
                        RunShellFeature(feature);
                        break;
                    case "placeholder":
                    default:
                        ShowInfo(feature.Title ?? "功能占位", feature.Message ?? "该功能尚未接入。", isError: false);
                        break;
                }
            }
            catch (System.Exception ex)
            {
                ShowInfo(feature != null ? feature.Title : "执行失败", ex.Message, isError: true);
            }
        }

        private void RunLispFeature(CadPluginFeature feature)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                throw new InvalidOperationException("当前没有活动文档。请先打开图纸。");
            }

            string loadPath = ResolveManifestPath(feature.LoadPath);
            if (string.IsNullOrWhiteSpace(loadPath) || !File.Exists(loadPath))
            {
                throw new FileNotFoundException("未找到 LISP 文件。", loadPath ?? feature.LoadPath);
            }

            string normalizedPath = loadPath.Replace('\\', '/');
            string escapedPath = normalizedPath.Replace("\"", "\\\"");
            doc.SendStringToExecute(string.Format("(progn (load \"{0}\") (princ)) ", escapedPath), true, false, false);

            if (!string.IsNullOrWhiteSpace(feature.Command))
            {
                doc.SendStringToExecute(feature.Command.Trim() + " ", true, false, false);
            }

            WriteMessage(string.Format("{0} 已执行 {1}。", StatusPrefix, feature.Title));
        }

        private void RunCommandFeature(CadPluginFeature feature)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                throw new InvalidOperationException("当前没有活动文档。请先打开图纸。");
            }

            if (string.IsNullOrWhiteSpace(feature.Command))
            {
                throw new InvalidOperationException("未配置 CAD 命令。\n");
            }

            doc.SendStringToExecute(feature.Command.Trim() + " ", true, false, false);
            WriteMessage(string.Format("{0} 已执行命令 {1}。", StatusPrefix, feature.Command.Trim()));
        }

        private void RunShellFeature(CadPluginFeature feature)
        {
            string executablePath = ResolveManifestPath(feature.ExecutablePath);
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            {
                throw new FileNotFoundException("未找到可执行文件。", executablePath ?? feature.ExecutablePath);
            }

            string workingDirectory = ResolveManifestPath(feature.WorkingDirectory);
            if (string.IsNullOrWhiteSpace(workingDirectory))
            {
                workingDirectory = Path.GetDirectoryName(executablePath);
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = feature.Arguments ?? string.Empty,
                WorkingDirectory = workingDirectory,
                UseShellExecute = true
            });

            WriteMessage(string.Format("{0} 已启动 {1}。", StatusPrefix, feature.Title));
        }
    }
}
