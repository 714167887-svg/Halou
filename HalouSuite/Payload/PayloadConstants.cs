namespace HalouSuite.Payload
{
    /// <summary>
    /// Payload 内部共享常量。Phase 2 迁移过程中，从 1.1.74 HalouSuiteManager 静态常量
    /// 拆出来的不依赖 acmgd 的常量集中放这里，避免循环依赖。
    /// </summary>
    internal static class PayloadConstants
    {
        // 注意：raw.githubusercontent.com 有 ~5 分钟 CDN 缓存，且忽略 query string 强刷。
        // 改用 jsDelivr：commit 推送后基本秒级可见，业务上等同 git HEAD。
        // RobustHttp 仍保留 raw 作为 fallback。
        public const string DefaultLicenseEndpoint =
            "https://cdn.jsdelivr.net/gh/714167887-svg/halou-release@main/license.json";

        public const int MinimumRefreshSeconds = 60;

        /// <summary>命令行/状态消息前缀，老版本一直用 "[HalouSuite]"。</summary>
        public const string StatusPrefix = "[HalouSuite]";
    }
}
