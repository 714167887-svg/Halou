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

        // 用 ReflectionOnlyLoadFrom 检查 DLL 是不是 Phase 2 的 Halou Payload 格式
        private static bool IsHalouPayloadDll(string path, out string errMsg)
        {
            errMsg = null;
            try
            {
                // 读字节再 ReflectionOnlyLoad，避免锁文件影响后续 Move
                byte[] bytes = File.ReadAllBytes(path);
                System.Reflection.Assembly asm = System.Reflection.Assembly.ReflectionOnlyLoad(bytes);
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (System.Reflection.ReflectionTypeLoadException rt) { types = rt.Types; }
                if (types == null) { errMsg = "无法读取类型表"; return false; }
                foreach (Type t in types)
                {
                    if (t == null) continue;
                    if (t.FullName == "HalouSuite.Payload.PayloadEntry") return true;
                }
                errMsg = "缺少 HalouSuite.Payload.PayloadEntry 类型";
                return false;
            }
            catch (Exception ex)
            {
                errMsg = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
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
