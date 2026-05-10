# AutoCAD Clipboard Paste Plugin (AutoOn Test)

AutoCAD 剪贴板粘贴试验插件，验证"网页 JSON → 系统剪贴板 → AutoCAD 图元"的完整链路。

## 已生成内容

- `dist/JsqClipboardCadPlugin.dll`
  基础试验 DLL（手动 `JSQPASTE` 命令）
- `dist/JsqClipboardCadPlugin.AutoOn.dll`
  带 `Ctrl+V` 接管和自动开启的试验 DLL
- `dist/load-jsq-plugin.lsp`
  AutoCAD 自动加载脚本
- `src/JsqClipboardCadPlugin.cs`
  插件源码
- `demo/copy-demo.html`
  最小网页复制示例
- `demo/sample-payload.json`
  示例剪贴板数据
- `scripts/build-plugin.ps1`
  重新编译脚本
- `scripts/build-plugin.bat`
  编译入口（BAT）
- `manifest/halou-plugin-manifest.json`
  插件集合清单示例，会在编译后复制到 `dist/`

脚本结构调整（独立拆分）：

- `W/CAD Halou插件/` 为新的独立脚本目录（推荐从这里执行）
- 当前已迁移：build、copy-sample、open-generator、popup_jsqcad_generator.py、generate_sheet_jsqcad.py
- `autocad-clipboard-plugin-autoon-test/scripts/` 下保留兼容转发壳，旧命令仍可继续使用

## 命令

- `JSQPASTE` — 手动读取剪贴板并生成图元
- `JSQHOOKON` — 开启当前会话的 `PASTECLIP` 接管
- `JSQHOOKOFF` — 恢复 AutoCAD 原生 `PASTECLIP`
- `HALOU` — 打开插件集合面板
- `HALOUTOGGLE` — 切换插件集合面板显示
- `HALOUREFRESH` — 立即刷新插件清单
- `HALOUZK` — 直接执行 ZK 功能
- `HALOUKB` — 直接执行 KB 功能占位

AutoOn 版本在 `NETLOAD` 后默认自动开启接管。

统一插件壳在加载后还会自动完成以下动作：

- 注册默认热键 `Ctrl+Shift+~` 打开面板
- 在 AutoCAD 下方状态栏托盘区添加一个 `H` 图标，点击即可弹出/收起面板
- 按配置文件中的清单来源定时刷新功能列表

## 支持的图元

polyline、line、circle、arc、text

## 快速测试

1. 打开 AutoCAD 2021
2. `NETLOAD` → 选择 `dist/JsqClipboardCadPlugin.AutoOn.dll`
3. 用 `demo/copy-demo.html` 或 `scripts/copy-sample-payload.ps1` 复制示例数据
4. `JSQPASTE` 或 `Ctrl+V` 粘贴
5. 指定插入点（或回车用 0,0）

## JSON 格式

剪贴板文本支持两种写法：直接 JSON 或 `JSQCAD:` 前缀 + JSON

```json
{
  "format": "JSQCAD/1.0",
  "units": "mm",
  "basePoint": [0, 0],
  "layers": [
    { "name": "JSQ-OUTLINE", "colorIndex": 1 }
  ],
  "entities": [
    {
      "type": "polyline",
      "layer": "JSQ-OUTLINE",
      "closed": true,
      "vertices": [[0, 0], [50, 0], [50, 50], [0, 50]]
    }
  ]
}
```

## 说明

- 如果剪贴板内容不是 `JSQCAD:` 数据，接管后的 `PASTECLIP` 会自动回落到 AutoCAD 原生粘贴
- 只对当前 AutoCAD 会话生效
- 统一插件集合的本地配置保存在 `%APPDATA%\HalouCadSuite\config.json`
- 默认清单路径为 `dist/halou-plugin-manifest.json`，可以在面板里改成远端 URL，并填写请求头/凭证值
- 当前已内置 `ZK` 入口，`KB` 先保留占位；等 KB LSP 补上后，直接改清单即可接入
