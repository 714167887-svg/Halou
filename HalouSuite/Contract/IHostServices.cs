using System;

namespace HalouSuite.Contract
{
    /// <summary>
    /// Payload 反向调用 Host 的能力面。
    /// 主要用途：触发热重载、写入 acad 命令行、读取 Host 已知的版本信息等。
    /// 任何 Payload 想长期持有的「跨更新状态」（许可缓存、配置）也应通过 Host 转储到磁盘，避免随 Payload 销毁。
    /// </summary>
    public interface IHostServices
    {
        /// <summary>
        /// Host 提供的接口能力级别。每次给 IHostServices/IPayload 增加新成员或破坏性修改时 +1。
        /// Payload 在自己的 RequiredHostApiLevel 里声明最低需要的等级；Host 加载时拒绝不兼容的 Payload。
        /// 当前级别 = 1（首版：版本协商 + LKG 回滚）。
        /// </summary>
        int HostApiLevel { get; }

        /// <summary>Host 程序集版本号（架构版本，与 Payload 业务版本独立）。</summary>
        string HostVersion { get; }

        /// <summary>Host 安装目录（HalouHost.dll 所在目录），固定路径，可用于查找资源。</summary>
        string HostDirectory { get; }

        /// <summary>Payload 仓库目录（%LOCALAPPDATA%\HalouSuite\payloads\），存放历次下载的 HalouPayload.*.dll。</summary>
        string PayloadDirectory { get; }

        /// <summary>用户配置目录（%AppData%\HalouCadSuite），跨更新持久化。</summary>
        string ConfigDirectory { get; }

        /// <summary>请求 Host 在下次安全时刻把当前 Payload 卸载并加载指定文件的新 Payload（路径必须在 PayloadDirectory 下）。</summary>
        void RequestReload(string newPayloadDllPath, string reasonForBubble);

        /// <summary>写一行到 AutoCAD 命令行（出错降级到 Trace）。</summary>
        void WriteLine(string message);
    }
}
