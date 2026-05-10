# Halou Suite —— Host + Payload 热更架构

> v2.0 引入。目标：用户安装新版本无需退出 AutoCAD。

## 目录

```
HalouSuite/
├─ Contract/   HalouContract.dll  —— 接口契约（IPayload / IHostServices）
├─ Host/       HalouHost.dll       —— 永驻外壳，独占 [CommandMethod] / [LispFunction]
├─ Payload/    HalouPayload.<ver>.dll —— 真正的业务逻辑，可热替换
├─ build-all.ps1   —— 三个 DLL 一键编译
└─ README.md
```

## 设计要点

1. **Host 永不热更**：包含所有 `[CommandMethod]` / `[LispFunction]` 属性。AutoCAD 一次注册命令表后无法被覆盖，所以这些声明必须放在不变的 DLL 里。
2. **Payload 用 `Assembly.Load(File.ReadAllBytes())` 加载**，避免文件锁；新版本 DLL 命名为 `HalouPayload.<ver>.dll`，文件名唯一以避开 Assembly identity 缓存。
3. **Host 通过 IPayload 反射调用 Payload**。Payload 里**禁止**有 `[CommandMethod]` / `[LispFunction]` 属性。
4. **Contract.dll 不引用 AutoCAD**，保持纯净；LISP 入参在 Host 层解析后以 string/int 等基础类型传给 Payload。
5. **热更流程**：下载新 payload → `currentPayload.Dispose()` → `Assembly.Load(byte[])` → 反射创建 → `Activate()` → 托盘气泡通知。

## 兼容性

旧版（< 2.0）单 DLL 安装方式由 `autocad-clipboard-plugin-autoon-test/` 维护，2.0 上线后 `install.ps1` 自动清理旧路径。
