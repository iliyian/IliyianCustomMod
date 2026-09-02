# Iliyian Custom Mod (RimWorld 1.6)

iliyian 的个人 RimWorld 功能合集 mod，基于 Harmony。

## 功能

1. **深钻井资源覆盖层** — 选中深钻井时显示深层资源绿网格 + 鼠标悬停显示资源图标和剩余数量。原版 1.6 要求地图上有正在运行的地基穿透扫描仪才显示，本 mod 移除该限制。
2. **炮塔射击捕食者** — 捕食者进入捕食状态（`PredatorHunt`）且猎物属于玩家阵营（小人或动物）时，射程内的炮塔会开火。原版机制中捕食者生成时是中立的，永远不会进入炮塔的目标缓存 `AttackTargetsCache`。需要视线，不隔墙射击。
3. **头部装备/头发显示模式** — mod 设置页三选一：只显示装备（原版默认）/ 隐藏装备显示头发 / 装备头发同时显示。原版通过 `PawnRenderTree.AdjustParms` 的 `skipFlags` 隐藏头发导致"光头"，本 mod 清除该标记。设置更改后立即刷新所有小人缓存（`GlobalTextureAtlasManager.TryMarkPawnFrameSetDirty`），无需重新读档。
4. **机械师带宽可编辑** — 选中机械师后 gizmo 栏新增「编辑机械带宽」按钮，弹窗直接设置带宽（按 pawn 持久化到存档，恢复原版值即自动清除覆盖）。

## 安装

- 依赖 [Harmony](https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077)，加载顺序在其后。
- 复制整个文件夹到 `RimWorld/Mods/`，游戏内启用。

## 构建

需要 .NET SDK（net472），游戏目录与 Harmony mod 路径见 csproj 中的 HintPath：

```bash
cd Source && dotnet build -c Release
```

产物在 `Assemblies/`。

## 源码结构

- `Source/IliyianCustomMod.cs` — 全部 Harmony 补丁、设置页、带宽编辑窗口
- `About/About.xml` — mod 元数据与描述