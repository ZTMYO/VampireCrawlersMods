# 吸血鬼爬行者 Mod 合集

这是一个为游戏《吸血鬼爬行者》(Vampire Crawlers) 制作的 BepInEx IL2CPP Mod 合集仓库。  
当前包含 3 个实用 Mod：敌人总血量显示 + 手牌排序 + 更多信息 Mod。

## Mod 一览

### 敌人总血量显示 Mod

![敌人总血量显示演示](docImg/img1.gif)

- 在战斗界面顶部显示所有敌人的总血量与百分比。
- 血条扣除带平滑动画，观感更自然。
- 受击后有短暂延迟再缩减，打击反馈更好。
- 采用 Harmony 补丁，避免每帧场景搜索，性能更稳。

### 手牌排序 Mod

![手牌滚轮排序演示](docImg/img2.gif)

- 鼠标触发：下滑升序，上滑降序。
- 手柄触发：`RT` 升序，`LT` 降序。
- 防误触：按下 `Esc`、暂停界面或设置面板打开时不触发。
- 防抖节流：滚轮触发有冷却，避免大幅滚动导致高频重复排序。
- 规则补充：`free` 牌按牌面值参与排序。

### 更多信息 Mod

![更多信息 Mod 演示](docImg/img3.png)

- 显示“可连击法力”辅助玩家快速找到可触发连击的牌。
- 悬停选中碎裂牌时，显示“可打出次数”辅助玩家确定裂纹牌的剩余打出次数。

## 🛠️ 安装方法

> 请先自行安装指定版本：`BepInEx-Unity.IL2CPP-*-6.0.0-be.755+3fab71a`。

1. 先下载并解压 BepInEx 压缩包（按你的系统选择）：
   - Windows x64：  
     [BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.755+3fab71a.zip](https://builds.bepinex.dev/projects/bepinex_be/755/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.755%2B3fab71a.zip)
   - macOS x64：  
     [BepInEx-Unity.IL2CPP-macos-x64-6.0.0-be.755+3fab71a.zip](https://builds.bepinex.dev/projects/bepinex_be/755/BepInEx-Unity.IL2CPP-macos-x64-6.0.0-be.755%2B3fab71a.zip)
   - 其他平台/版本总览：  
     [BepInEx Bleeding Edge 下载总站](https://builds.bepinex.dev/projects/bepinex_be)
2. 解压后你会得到一个外层文件夹（例如 `BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.755+3fab71a`）。
3. 打开这个外层文件夹，把“里面的所有文件和文件夹”复制到游戏根目录（不要把这个外层文件夹本身直接丢进游戏目录）。
4. 正确结果通常是游戏根目录同时有：`BepInEx/`、`dotnet/`、`winhttp.dll`、`doorstop_config.ini`、`.doorstop_version`。
5. 将本仓库 `plugins/` 目录下的 `*.dll` 复制到游戏目录的 `BepInEx/plugins/` 中。
   - 【关键提示】若下载后的 `dll` 文件名被单引号包裹，请先去掉单引号再放入 `plugins` 目录。
6. 启动游戏进入战斗即可生效。

> 首次启动说明：第一次启动游戏时，BepInEx IL2CPP 会自动下载 Unity 基础库并生成互操作文件，可能需要等待一段时间（通常几十秒到几分钟）。
> 期间日志出现 `Downloading unity base libraries`、`Extracting unity base libraries`、`Running Cpp2IL`、`Creating application model` 都是正常现象，请耐心等待完成，不要中途强退。

![游戏路径参考图](docImg/pathImg.png)
*安装参考：将文件放入上图所示的游戏根目录*

## 📂 项目结构

- `plugins/`：已编译的 Mod 插件（`ShowEnemyHpMod.dll`、`SortCardMod.dll`、`MoreInfoMod.dll`）。
- `源码/`：Mod C# 源码（`ShowEnemyHpMod.cs`、`SortCardMod.cs`、`MoreInfoMod.cs`）。
- `docImg/`：文档图片目录（`img1.gif`、`img2.gif`、`img3.png`、`pathImg.png`）。
- `【安装必看】使用本 Mod 必须先手动安装指定版 BepInEx 框架！.txt`：纯文本安装说明与常见问题。

## 👨‍💻 开发说明

- 项目面向 **Unity 6 (6000.x) + IL2CPP** 环境。
- 通过 Harmony 补丁和反射适配部分游戏内部类型，减少版本差异影响。
- 手牌排序 Mod 重点处理了 UI 与模型层同步，以及输入场景屏蔽（暂停/设置/ESC）问题。

## ❓ 常见问题 (Q&A)

**Q: 使用这些 Mod 会影响 Steam 成就吗？**  
A: 当前合集内 Mod 主要是 UI/交互增强，不涉及成就或存档校验逻辑，通常不会影响成就。

**Q: Steam Deck 可以使用吗？**  
A: 尚未完整实机测试。理论上 Proton 环境可正常加载 BepInEx IL2CPP 时即可运行，欢迎反馈兼容性结果。

## ⚠️ 免责声明

- 本项目仅用于单机环境下的界面与交互优化学习，不用于任何联机对抗或破坏公平性的用途。
- 本项目不提供绕过反作弊、破解付费内容、篡改联机数据等功能。
- 请遵守游戏 EULA 与相关平台规则；因使用本项目产生的风险与后果由使用者自行承担。
