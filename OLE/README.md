# CAD 批量 OLE 图片导入

这个目录是独立于 halou wode 的单独脚本目录。

## 当前推荐方案

优先使用 LSP 方案：

- 入口文件: oleimgdir.lsp
- 辅助脚本: oleimgdir-clipboard.ps1

## LSP 方案功能

- 命令名: OLEIMGDIR
- 简短别名: OI
- 自检命令: OLEIMGCHECK
- 作用: 扫描当前图纸所在目录的全部图片文件
- 导入方式: 逐张写入 Windows 剪贴板后调用 AutoCAD 的 PASTECLIP
- 结果: 图片以 OLE 方式插入当前 CAD 图纸
- 尺寸规则: 每张图片最大不超过 2500mm x 2500mm，超出自动等比缩放到该范围内
- 排版方式: 横向依次放置（左下角基点起）
- 图片间隔: 固定 150mm

## 支持格式

- bmp
- dib
- png
- jpg
- jpeg
- gif
- tif
- tiff

## 使用方法

1. 把 oleimgdir.lsp 和 oleimgdir-clipboard.ps1 放在同一个文件夹。
2. 在 AutoCAD 中执行 APPLOAD。
3. 加载 oleimgdir.lsp。
4. 在命令行输入 OLEIMGDIR。
5. 如果图纸已保存，脚本自动扫描当前 DWG 所在目录。
6. 如果图纸未保存，脚本会要求手动输入目录。
7. 指定排版左下角基点，直接回车默认 0,0。

## 发给别人用

1. 不要只发 oleimgdir.lsp，必须连 oleimgdir-clipboard.ps1 一起发。
2. 两个文件必须放在同一个文件夹里。
3. 对方电脑必须是 Windows，并且能调用 powershell.exe。
4. 如果图片在共享盘或网络路径，对方电脑也必须能访问同一路径。
5. 对方加载后直接运行 OLEIMGDIR。

## 说明

- 目前只扫描当前目录，不递归子目录。
- 这个方案没有走 INSERTOBJ 对话框，因为那个流程不适合稳定批量脚本化。
- 这个方案依赖桌面 AutoCAD 和 Windows 剪贴板，不适用于 accoreconsole 这类无界面场景。
- 如果脚本目录里缺少 oleimgdir-clipboard.ps1，OLE 导入会直接失败。
- 目录中保留了之前的 C# 工程，作为备用实现，不影响 LSP 方案使用。