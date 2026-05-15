using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;

namespace HalouSuite.Payload
{
    // 配置 / 清单刷新 / 功能调度执行（lisp / command / shell / placeholder）。
    internal sealed partial class HalouSuiteManager
    {
        public void RefreshManifest(bool manual)
        {
            EnsureInitialized();
            // 先尝试从 CDN 同步最新 manifest 到本地 bin/halou-plugin-manifest.json，
            // 这样以后只 push manifest（不发新 Payload）也能让客户面板看到新功能名/描述。
            TrySyncManifestFromCdn();
            _manifest = PluginManifest.Load(_configuration, _localManifestPath, _manifestCachePath, out _statusMessage);
            TryCheckLicense(silent: !manual);
            ApplyCommandAliases();
            UpdatePaletteView();

            if (manual)
            {
                WriteMessage(string.Format("{0} 已刷新：{1}", StatusPrefix, _statusMessage));
            }
        }

        // 根据 license.json 里的 payload_download_url 推导同目录下的
        // halou-plugin-manifest.json CDN 路径，下载覆盖本地 bin/。
        // 任何失败都吞掉，本地 fallback 仍可用。
        private void TrySyncManifestFromCdn()
        {
            try
            {
                string dllUrl = _latestDownloadUrl;
                if (string.IsNullOrWhiteSpace(dllUrl)) return;
                if (!dllUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !dllUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;

                int q = dllUrl.IndexOf('?');
                string baseUrl = q >= 0 ? dllUrl.Substring(0, q) : dllUrl;
                int slash = baseUrl.LastIndexOf('/');
                if (slash <= 0) return;
                string manifestUrl = baseUrl.Substring(0, slash + 1) + "halou-plugin-manifest.json"
                                     + (q >= 0 ? dllUrl.Substring(q) : string.Empty);

                string json = RobustHttp.DownloadString(manifestUrl, null, s =>
                {
                    string t = s == null ? "" : s.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
                    return t.Length > 0 && t[0] == '{';
                });
                if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(_localManifestPath)) return;

                // 校验是合法 JSON 且能反序列化为 manifest 再落盘，避免污染本地缓存
                if (PluginManifest.TryDeserialize(json) == null) return;
                File.WriteAllText(_localManifestPath, json, System.Text.Encoding.UTF8);
            }
            catch
            {
                // 网络/权限/解析失败都不影响本地 fallback
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

        // v2.0.17：原实现在 UI 线程上同步跑 RefreshManifest（含网络下载、最坏 5-30s），
        // 直接卡死 acad 主线程。现改为后台线程跑网络，完成后 BeginInvoke 回 UI 做轻量刷新。
        private int _refreshInFlight; // 0 = idle, 1 = running
        private void OnRefreshTimerTick(object sender, EventArgs e)
        {
            if (System.Threading.Interlocked.CompareExchange(ref _refreshInFlight, 1, 0) == 1) return;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    try { TrySyncManifestFromCdn(); } catch { }
                    try { TryCheckLicense(silent: true); } catch { }
                }
                finally
                {
                    MarshalToUi(() =>
                    {
                        try
                        {
                            _manifest = PluginManifest.Load(_configuration, _localManifestPath, _manifestCachePath, out _statusMessage);
                            ApplyCommandAliases();
                            UpdatePaletteView();
                        }
                        catch { }
                    });
                    System.Threading.Interlocked.Exchange(ref _refreshInFlight, 0);
                }
            });
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

            string loadExpression;
            if (!TryBuildLispLoadExpression(feature.LoadPath, true, out loadExpression))
            {
                string loadPath = ResolveManifestPath(feature.LoadPath);
                throw new FileNotFoundException("未找到 LISP 文件。", loadPath ?? feature.LoadPath);
            }

            doc.SendStringToExecute(loadExpression + " ", true, false, false);

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
