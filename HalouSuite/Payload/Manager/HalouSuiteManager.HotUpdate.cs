using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Threading;
using Autodesk.AutoCAD.ApplicationServices;

namespace HalouSuite.Payload
{
    // 在线热更新：下载新版 HalouPayload.<ver>.dll 到 payloads 目录，
    // 然后触发 HALOURELOAD 让 Host 切换到新 Payload —— 全程不退出 acad。
    internal sealed partial class HalouSuiteManager
    {
        // payloads 目录：与 install-host-smoketest.ps1 / PayloadLoader 保持一致
        private static string GetPayloadsDirectory()
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string dir = Path.Combine(root, "HalouSuite", "payloads");
            try { if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); } catch { }
            return dir;
        }

        /// <summary>
        /// 异步热更新：下载新版 payload + 触发 HALOURELOAD。
        /// onProgress(percent, bytesReceived, totalBytes) 在下载线程回调。
        /// onCompleted(success, message) 在 UI 线程回调。
        /// </summary>
        public void StartHotUpdate(
            Action<int, long, long> onProgress,
            Action<bool, string> onCompleted)
        {
            if (!IsUpdateAvailable())
            {
                if (onCompleted != null) onCompleted(false, "没有可用更新");
                return;
            }

            string url = _latestDownloadUrl;
            string targetVersion = _latestVersion;
            string payloadsDir = GetPayloadsDirectory();
            string finalName = "HalouPayload." + targetVersion + ".dll";
            string finalPath = Path.Combine(payloadsDir, finalName);
            string tmpPath = finalPath + ".downloading";

            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }

            WebClient wc = new WebClient();
            wc.Headers["User-Agent"] = "HalouSuite/" + CurrentVersion;

            DownloadProgressChangedEventHandler progHandler = null;
            AsyncCompletedEventHandler doneHandler = null;

            progHandler = delegate(object s, DownloadProgressChangedEventArgs e)
            {
                if (onProgress != null)
                {
                    try { onProgress(e.ProgressPercentage, e.BytesReceived, e.TotalBytesToReceive); } catch { }
                }
            };
            doneHandler = delegate(object s, AsyncCompletedEventArgs e)
            {
                wc.DownloadProgressChanged -= progHandler;
                wc.DownloadFileCompleted -= doneHandler;
                try { wc.Dispose(); } catch { }

                if (e.Cancelled)
                {
                    SafeDelete(tmpPath);
                    PostBack(onCompleted, false, "已取消");
                    return;
                }
                if (e.Error != null)
                {
                    SafeDelete(tmpPath);
                    PostBack(onCompleted, false, "下载失败：" + e.Error.Message);
                    return;
                }

                // 尺寸校验：低于 50KB 多半是劫持/限流页
                long len = 0;
                try { len = new FileInfo(tmpPath).Length; } catch { }
                if (len < 50 * 1024)
                {
                    SafeDelete(tmpPath);
                    PostBack(onCompleted, false, "文件尺寸过小（可能是网络劫持），仅 " + len + " 字节");
                    return;
                }

                // 格式校验：必须是 Halou 新架构（Phase 2）的 Payload DLL，
                // 即包含 HalouSuite.Payload.PayloadEntry 类型。
                // 如果远端 download_url 还指向 1.1.74 旧版 JsqClipboardCadPlugin.dll，
                // 这里必须拒绝，否则会让用户以为更新成功但实际没换。
                string formatErr;
                if (!IsHalouPayloadDll(tmpPath, out formatErr))
                {
                    SafeDelete(tmpPath);
                    PostBack(onCompleted, false,
                        "下载到的不是新架构 Payload（" + formatErr + "）。\n" +
                        "服务器端 license.json 的 download_url 还指向旧版 JsqClipboardCadPlugin.dll，\n" +
                        "请发布新格式的 HalouPayload.<版本>.dll 后再试。");
                    return;
                }

                // 替换：把临时文件改成最终名
                try
                {
                    if (File.Exists(finalPath))
                    {
                        try { File.Delete(finalPath); }
                        catch
                        {
                            // 文件被旧 payload 锁住的可能性：用唯一名兜底
                            finalName = "HalouPayload." + targetVersion + "." +
                                         DateTime.Now.ToString("HHmmss") + ".dll";
                            finalPath = Path.Combine(payloadsDir, finalName);
                        }
                    }
                    File.Move(tmpPath, finalPath);
                }
                catch (Exception mx)
                {
                    PostBack(onCompleted, false, "落盘失败：" + mx.Message);
                    return;
                }

                // 触发 HALOURELOAD —— 必须在 acad 主线程
                TriggerReloadOnUiThread(onCompleted, targetVersion);
            };

            wc.DownloadProgressChanged += progHandler;
            wc.DownloadFileCompleted += doneHandler;

            try
            {
                wc.DownloadFileAsync(new Uri(url), tmpPath);
            }
            catch (Exception ex)
            {
                wc.DownloadProgressChanged -= progHandler;
                wc.DownloadFileCompleted -= doneHandler;
                try { wc.Dispose(); } catch { }
                if (onCompleted != null) onCompleted(false, "启动下载失败：" + ex.Message);
            }
        }

        private static void SafeDelete(string p)
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { }
        }

        // 检查 DLL 是不是 Phase 2 的 Halou Payload 格式。
        //
        // 历史教训：之前用 ReflectionOnlyLoad + GetTypes 判断，
        // 但 PayloadEntry 实现了 HalouContract 接口，ReflectionOnly 上下文里
        // 没注册解析器时，GetTypes 抛 ReflectionTypeLoadException，
        // 而 rt.Types 里 PayloadEntry 这个类型就是 null（因为接口引用没解析）→ 误判。
        // 这导致所有 Phase 2 客户端「下载新版 Payload」都会被自己拒绝（鸡生蛋）。
        //
        // 现在改用字节扫描：直接在 DLL bytes 里搜 "HalouSuite.Payload" 和
        // "PayloadEntry" 这两段字符串。CLR metadata #Strings heap 把 namespace
        // 和 type name 分开存（NUL 分隔的 UTF-8），所以不能搜整串"HalouSuite.Payload.PayloadEntry"，
        // 必须分别搜两段。旧 JsqClipboardCadPlugin.dll 完全没有 "HalouSuite.Payload" 字符串，绝不会误判。
        private static bool IsHalouPayloadDll(string path, out string errMsg)
        {
            errMsg = null;
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                byte[] ns = System.Text.Encoding.UTF8.GetBytes("HalouSuite.Payload");
                byte[] tn = System.Text.Encoding.UTF8.GetBytes("PayloadEntry");
                if (ContainsBytes(bytes, ns) && ContainsBytes(bytes, tn)) return true;
                errMsg = "未在 DLL 元数据中找到 HalouSuite.Payload / PayloadEntry";
                return false;
            }
            catch (Exception ex)
            {
                errMsg = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        private static bool ContainsBytes(byte[] hay, byte[] needle)
        {
            if (needle == null || needle.Length == 0) return false;
            if (hay == null || hay.Length < needle.Length) return false;
            int last = hay.Length - needle.Length;
            byte n0 = needle[0];
            for (int i = 0; i <= last; i++)
            {
                if (hay[i] != n0) continue;
                bool ok = true;
                for (int j = 1; j < needle.Length; j++)
                {
                    if (hay[i + j] != needle[j]) { ok = false; break; }
                }
                if (ok) return true;
            }
            return false;
        }

        private static void PostBack(Action<bool, string> cb, bool ok, string msg)
        {
            if (cb == null) return;
            try { cb(ok, msg); } catch { }
        }

        // 把 HALOURELOAD 投递到 acad 主线程执行（SendStringToExecute 必须在文档线程）
        private static void TriggerReloadOnUiThread(
            Action<bool, string> onCompleted, string targetVersion)
        {
            try
            {
                Document doc = Application.DocumentManager.MdiActiveDocument;
                if (doc == null)
                {
                    PostBack(onCompleted, true,
                        "新版本 v" + targetVersion + " 已下载到 payloads 目录。\n请打开任意图纸后输入 HALOURELOAD 应用。");
                    return;
                }

                // 通过文档队列执行 HALOURELOAD —— 不退出 acad
                doc.SendStringToExecute("HALOURELOAD ", true, false, false);

                // 给 Host 一点时间完成 reload 再回调（避免 UI 同时弹窗 + reload 抢占）
                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    Thread.Sleep(1500);
                    PostBack(onCompleted, true,
                        "新版本 v" + targetVersion + " 已应用。\n（Host 已热重载 Payload，无需重启 AutoCAD）");
                });
            }
            catch (Exception ex)
            {
                PostBack(onCompleted, false, "触发 HALOURELOAD 失败：" + ex.Message +
                    "\n您可以手动输入 HALOURELOAD 命令应用新版本。");
            }
        }
    }
}
