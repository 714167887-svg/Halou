# Parametric Generator

当前脚本主要支持两类中柱输出：

- `constructive-profile`
  - 默认模型
  - 对应转轴中柱
  - 按作图法生成完整 `26` 点轮廓
  - 支持 `91°` 到 `179°`

- `profile`
  - 插值版外轮廓
  - 同样对应转轴中柱
  - 支持 `91°` 到 `179°`

- `legacy-full`
  - 旧版 `26` 点兼容模型
  - 支持 `91°` 到 `144°`

- `right-middle-profile-v4`
  - 对应挡水中柱
  - 用于 `8317` 款式中柱
  - 按 `91 / 100 / 110 / 135 / 160 / 170 / 179` 这组样板建立
  - 先生成外链 `14` 点，再做 `0.8` 内偏移，输出完整 `28` 点
  - 支持 `91°` 到 `179°`

## 双中柱模式

如果同时输入两个角度：

- 左侧角度走转轴中柱 `constructive-profile-v2`
- 右侧角度走挡水中柱 `right-middle-profile-v4`
- 这两个配件目前都属于 `8317` 款式中柱
- 程序会自动横向排布，默认间距 `50mm`

```powershell
python ./scripts/generate_sheet_jsqcad.py 129 --right-angle 135 --gap 50 --prefix
```

## 弹窗工具

```text
./scripts/open-jsqcad-generator.bat
```

弹窗中的两个输入框已改名为：

- 转轴中柱角度
- 挡水中柱角度

## 备注

- 上面的路径写法默认从 `autocad-clipboard-plugin-autoon-test/` 目录执行；如果从别处启动，请先切换到仓库根目录再运行。

- 转轴中柱是左侧那条轮廓
- 挡水中柱是右侧那条轮廓
- 导出 `JSQCAD` 的 `meta` 已经改用这两个名称
