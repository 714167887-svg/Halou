CAD 插件独立脚本目录（W 根目录）

用途：
- 将 autocad-clipboard-plugin-autoon-test 的常用脚本从子项目中独立出来，统一放在 W 根目录下。
- 原子项目 scripts 目录仍保留兼容入口，会转发到此目录。

脚本：
- build-autocad-plugin.ps1      编译插件并复制 manifest 到 dist
- copy-sample-payload.ps1       复制示例 JSQCAD 负载到剪贴板
- open-jsqcad-generator.bat      打开参数化生成器
