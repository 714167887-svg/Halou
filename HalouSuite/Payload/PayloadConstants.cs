namespace HalouSuite.Payload
{
    /// <summary>
    /// Payload 内部共享常量。Phase 2 迁移过程中，从 1.1.74 HalouSuiteManager 静态常量
    /// 拆出来的不依赖 acmgd 的常量集中放这里，避免循环依赖。
    /// </summary>
    internal static class PayloadConstants
    {
        // v2.0.44：改回 raw.githubusercontent.com 作为主路径。
        // 原因：jsDelivr 对 @main 的缓存是 12h，purge 接口对同一文件有 ~46 分钟 throttle，
        // 短时间内连发数版会卡住客户端拿不到新 license，造成假死锁（v2.0.41~v2.0.43 踩坑）。
        // raw 的 CDN 缓存仅 ~5 分钟且 ?_t= cache-buster 可强刷。
        // RobustHttp.EnumerateUrls 会自动把 raw 转 jsDelivr 镜像作为 fallback（断网/封路时仍可用）。
        public const string DefaultLicenseEndpoint =
            "https://raw.githubusercontent.com/714167887-svg/halou-release/main/license.json";

        public const int MinimumRefreshSeconds = 60;

        /// <summary>命令行/状态消息前缀，老版本一直用 "[HalouSuite]"。</summary>
        public const string StatusPrefix = "[HalouSuite]";
    }
}
