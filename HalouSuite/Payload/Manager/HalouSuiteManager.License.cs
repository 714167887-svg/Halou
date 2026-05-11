using System;
using System.Collections.Generic;
using System.Linq;

namespace HalouSuite.Payload
{
    // 授权检查 / 账号白名单 / 功能权限。
    // 字段：_licenseStatus / _licenseMessage / _allowedFeatures / _latestVersion / _latestDownloadUrl / _releaseNotes
    // 见 HalouSuiteManager.cs（字段集中）。
    internal sealed partial class HalouSuiteManager
    {
        public LicenseStatus LicenseStatus { get { return _licenseStatus; } }
        public string LicenseMessage { get { return _licenseMessage; } }
        public string LatestVersion { get { return _latestVersion; } }
        public string LatestDownloadUrl { get { return _latestDownloadUrl; } }

        public bool IsFeatureAllowed(string featureId)
        {
            // 未检查 / 离线 / 默认许可 等未设置 _allowedFeatures 的场景，一律放行
            if (_allowedFeatures == null || _allowedFeatures.Count == 0) return true;
            if (string.IsNullOrWhiteSpace(featureId)) return true;
            if (_allowedFeatures.Contains("*")) return true;
            return _allowedFeatures.Contains(featureId.Trim());
        }

        public void TryCheckLicense(bool silent)
        {
            string endpoint = _configuration != null ? _configuration.LicenseEndpoint : null;
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                _licenseStatus = LicenseStatus.NotConfigured;
                _licenseMessage = "未配置授权端点";
                return;
            }

            string accountName = _configuration != null ? _configuration.AccountName : null;
            try
            {
                Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                // 注意：不发 Authorization 头 —— raw.githubusercontent.com 会把带
                // Authorization:Bearer 的请求当 API 调用，非法 token 返回 404。
                // token 改用自定义头 X-Halou-Token。
                if (!string.IsNullOrWhiteSpace(_configuration.AccountToken))
                {
                    headers["X-Halou-Token"] = _configuration.AccountToken.Trim();
                }
                if (!string.IsNullOrWhiteSpace(accountName))
                {
                    headers["X-Halou-Account"] = accountName.Trim();
                }
                headers["X-Halou-Client"] = "HalouSuite/" + CurrentVersion;

                // 加时间戳防 CDN 缓存
                string url = endpoint + (endpoint.Contains("?") ? "&" : "?") + "_t=" + DateTime.UtcNow.Ticks;
                // 内容必须以 '{' 起手，否则就是被中间链路劫持成 HTML 错误页 → 自动换镜像/通道
                string json = RobustHttp.DownloadString(url, headers, s =>
                {
                    string t = s == null ? "" : s.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
                    return t.Length > 0 && t[0] == '{';
                });
                LicenseInfo info = LicenseInfo.Parse(json);
                ApplyLicense(info, accountName);
            }
            catch (System.Exception ex)
            {
                _licenseStatus = LicenseStatus.Unknown;
                _licenseMessage = "无法联网校验：" + ex.Message;
                if (!silent)
                {
                    WriteMessage(string.Format("{0} 授权校验失败：{1}", StatusPrefix, ex.Message));
                }
            }
        }

        private void ApplyLicense(LicenseInfo info, string accountName)
        {
            // 默认不限制功能；若命中某个有功能白名单的账号再覆盖
            _allowedFeatures = null;

            if (info == null)
            {
                _licenseStatus = LicenseStatus.Unknown;
                _licenseMessage = "授权信息解析失败";
                return;
            }

            // Phase 2：优先使用 Payload 字段（latest_payload_version / payload_download_url）；
            // 没填则回退到 host 字段（latest_version / download_url）—— 这条 fallback 仅为 1.x 旧客户端保留，
            // 在 Phase 2 host 下点"下载新版本"，IsHalouPayloadDll 会拒绝 host DLL 并给出明确提示。
            string lv = !string.IsNullOrWhiteSpace(info.LatestPayloadVersion)
                ? info.LatestPayloadVersion.Trim()
                : (!string.IsNullOrWhiteSpace(info.LatestVersion) ? info.LatestVersion.Trim() : CurrentVersion);

            // 多 SDK 分发：按本进程加载的 acmgd 大版本号选 tag
            // acmgd 24 → arx24（AutoCAD 2021/2022/2023）；25 → arx25（2024/2025）
            string preferredTag = DetectArxTagForCurrentProcess();
            string ldu = null;
            if (!string.IsNullOrWhiteSpace(preferredTag)
                && info.PayloadDownloadUrls != null
                && info.PayloadDownloadUrls.Count > 0)
            {
                string urlForTag;
                if (info.PayloadDownloadUrls.TryGetValue(preferredTag, out urlForTag)
                    && !string.IsNullOrWhiteSpace(urlForTag))
                {
                    ldu = urlForTag.Trim();
                }
            }
            if (string.IsNullOrWhiteSpace(ldu))
            {
                ldu = !string.IsNullOrWhiteSpace(info.PayloadDownloadUrl)
                    ? info.PayloadDownloadUrl.Trim()
                    : info.DownloadUrl;
            }
            _latestVersion = lv;
            _latestDownloadUrl = ldu;
            _releaseNotes = info.ReleaseNotes;

            // 全局封杀：kill_switch = true 时无视账号直接禁用
            if (info.KillSwitch)
            {
                _licenseStatus = LicenseStatus.Denied;
                _licenseMessage = !string.IsNullOrWhiteSpace(info.KillReason) ? info.KillReason : "授权方已全局停用。";
                return;
            }

            if (string.IsNullOrWhiteSpace(accountName))
            {
                _licenseStatus = LicenseStatus.NotConfigured;
                _licenseMessage = "请到「账号」页填写账号名后重新检查。";
                return;
            }

            LicenseAccountInfo acct;
            if (info.Accounts != null && info.Accounts.TryGetValue(accountName.Trim(), out acct) && acct != null)
            {
                if (acct.Allowed)
                {
                    _licenseStatus = LicenseStatus.Allowed;
                    _licenseMessage = !string.IsNullOrWhiteSpace(acct.Note)
                        ? string.Format("✔ 账号「{0}」已授权（{1}）", accountName, acct.Note)
                        : string.Format("✔ 账号「{0}」已授权", accountName);

                    // 功能白名单：null / 空 / 含 "*" 视为全开；否则只允许列出的功能 Id
                    if (acct.Features != null && acct.Features.Count > 0
                        && !acct.Features.Any(f => f == "*"))
                    {
                        _allowedFeatures = new HashSet<string>(acct.Features, StringComparer.OrdinalIgnoreCase);
                        _licenseMessage += string.Format("，限定功能：{0}", string.Join("、", acct.Features.ToArray()));
                    }
                }
                else
                {
                    _licenseStatus = LicenseStatus.Denied;
                    _licenseMessage = !string.IsNullOrWhiteSpace(acct.Reason)
                        ? string.Format("✖ 账号「{0}」已被停用：{1}", accountName, acct.Reason)
                        : string.Format("✖ 账号「{0}」已被停用", accountName);
                }
                return;
            }

            if (info.DefaultAllowed)
            {
                _licenseStatus = LicenseStatus.Allowed;
                _licenseMessage = string.Format("✔ 账号「{0}」未在清单，按默认许可放行", accountName);
            }
            else
            {
                _licenseStatus = LicenseStatus.Denied;
                _licenseMessage = string.Format("✖ 账号「{0}」不在授权清单", accountName);
            }
        }

        // 探测本进程加载的 acmgd 的 AssemblyVersion 大版本号，映射到 ARX SDK 标签：
        //   24.x → arx24（AutoCAD 2021/2022/2023）
        //   25.x → arx25（AutoCAD 2024/2025）
        // 失败 / 找不到 / 不识别 → 返回 null（调用方走 PayloadDownloadUrl 兜底）
        private static string DetectArxTagForCurrentProcess()
        {
            try
            {
                System.Reflection.Assembly[] all = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < all.Length; i++)
                {
                    System.Reflection.Assembly a = all[i];
                    if (a == null) continue;
                    string name;
                    try { name = a.GetName().Name; }
                    catch { continue; }
                    if (string.IsNullOrEmpty(name)) continue;
                    if (!string.Equals(name, "acmgd", StringComparison.OrdinalIgnoreCase)) continue;
                    Version v = a.GetName().Version;
                    if (v == null) return null;
                    if (v.Major == 24) return "arx24";
                    if (v.Major == 25) return "arx25";
                    return "arx" + v.Major.ToString();
                }
            }
            catch
            {
                // 忽略：探测失败就走默认 url
            }
            return null;
        }
    }
}
