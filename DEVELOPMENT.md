# 开发说明 (DEVELOPMENT)

本文档记录本 mod 的技术实现、原版机制分析（基于 RimWorld 1.6.4871 反编译源码）、以及新增功能的开发流程。

## 环境与工具链

| 工具 | 用途 |
|---|---|
| .NET SDK (`dotnet build -c Release`) | 编译，目标 net472 |
| `ilspycmd`（`dotnet tool install -g ilspycmd`，需匹配已装 .NET runtime 版本） | 反编译 `Assembly-CSharp.dll` |
| `gh` CLI | GitHub 仓库管理 |

### 反编译流程（排查原版机制的起手式）

```bash
# 主程序集 → /tmp/rwsrc（-p 输出按命名空间分目录的 .cs 项目）
ilspycmd "C:/Program Files (x86)/Steam/steamapps/common/RimWorld/RimWorldWin64_Data/Managed/Assembly-CSharp.dll" -p -o /tmp/rwsrc

# 任意 mod DLL 同理，例如 HAR：
ilspycmd ".../workshop/content/294100/839005762/1.6/Assemblies/AlienRace.dll" -p -o /tmp/har
```

注意：游戏目录自带 `Source/` 文件夹**不完整**，不能代替反编译。

### 扫描 mod 引用了哪些原版内部类（查冲突用）

对启用 mod 的 DLL 直接按字节 grep 字符串（比逐个反编译快几个数量级）：

```python
# 每个 DLL 读进内存，检查 b"DeepResourceGrid" 之类关键类型名是否出现
data = open(p, "rb").read()
hits = [w for w in [b"DeepResourceGrid", b"MarkForDraw"] if w in data]
```

命中 ≠ 破坏：还要反编译确认是 Harmony patch（危险）还是只读引用（无害）。

## 各功能的技术细节

### 1. 深钻井资源覆盖层

**原版机制**（`Verse/DeepResourceGrid.cs`）：
- 绿色网格由 `CellBoolDrawer` 绘制，数据在 `defGrid`/`countGrid`，**只有地脉扫描仪发现过才有数据**（`GetCellBool = CountAt > 0`），任何 mod 都画不出未扫描的格子。
- `MarkForDraw()` 是唯一绘制开关，原版仅三处调用：`PlaceWorker_ShowDeepResources.DrawGhost`（放置预览）、`CompDeepScanner.PostDrawExtraSelectionOverlays`、调试选项 `DebugViewSettings.drawDeepResources`（汉化名"绘制地下资源"）。
- 门禁 `AnyActiveDeepScannersOnMap()` 遍历 `listerBuildings.allBuildingsColonist` 找 `CompDeepScanner` 且通电。**第三方种族/扫描仪 mod 自制的扫描仪不是 `CompDeepScanner`** → 门永远 false → 悬停标签、绿网格全消失。这是本功能要绕过的核心。

**补丁点**：
| 目标 | 类型 | 作用 |
|---|---|---|
| `ThingWithComps.DrawExtraSelectionOverlays` | postfix | 选中带 `CompDeepDrill` 的建筑时调 `MarkForDraw()` |
| `DeepResourceGrid.DeepResourcesOnGUI` | postfix | 单选钻井且门禁失败时，反射调私有 `RenderMouseAttachments()` 补悬停标签 |

调用链依据：`SelectionDrawer.cs:52` 每帧对选中物调 `DrawExtraSelectionOverlays`；`MapInterface.cs:150` 每帧调 `DeepResourceGridUpdate`（`CellBoolDrawerUpdate` 真正画格子）。

### 2. 炮塔射击捕猎中的捕食者

**原版机制**（`RimWorld/AttackTargetsCache.cs` + `RimWorld/GenHostility.cs`）：
- 目标缓存 `RegisterTarget` 在 Pawn **生成时**按当时敌对关系注册。狼/熊生成时中立（`Faction.OfAnimals`）→ 永不进玩家敌对列表。
- 捕猎后 `GenHostility.IsPredatorHostileTo`（`GenHostility.cs:186`）确实动态判定敌对，但**不触发缓存更新通知**，`GetPotentialTargetsFor` 看不到它 → 炮塔瞎。小人 drafted 的索敌走实时计算所以能打。
- "捕食中"判定 = `CurJob.def == JobDefOf.PredatorHunt` 且 `curDriver` 未结束且目标(TargetIndex.A)是对方阵营 Pawn（镜像 `GetPreyOfMyFaction`，`GenHostility.cs:217`）。

**补丁点**：`Building_TurretGun.TryFindNewTarget` postfix —— 原版结果无效时，扫 `mapPawns.AllPawnsSpawned` 找射程内"捕猎我方 Pawn 的捕食者"，取最近者；`GenSight.LineOfSight` 防隔墙射击（抛射物能过头顶则豁免）。

### 3. 头部装备/头发显示模式

**原版机制**（`Verse/PawnRenderTree.cs` AdjustParms ~273 行起 + `Verse/PawnRenderNodeWorker.cs:28`）：
- 装备覆盖 `BodyPartGroupDefOf.UpperHead` → `parms.skipFlags |= Hair`；覆盖 `FullHead` → 额外 `Beard` + `Eyes`。这就是"戴帽子变光头"的来源。
- `skipFlags` 在每个节点的 `PawnRenderNodeWorker.CanDrawNow` 里消费（`parms.skipFlags.HasFlag(node.Props.skipFlag)` → 不画）。
- 头部装备图形本身由 `PawnRenderNodeWorker_Apparel_Head.CanDrawNow` 控制。

**补丁点**：
| 目标 | 类型 | 作用 |
|---|---|---|
| `PawnRenderTree.AdjustParms` | postfix | 非默认模式时清 `Hair`（HideHeadgear 再清 `Beard`/`Eyes`） |
| `PawnRenderNodeWorker_Apparel_Head.CanDrawNow` | postfix | HideHeadgear 模式下头部装备一律不画 |

**关键坑：双缓存**。远镜头（`CameraDriver.ZoomRootSize > 18f`）下小人不是实时渲染，走 `GlobalTextureAtlasManager` 的帧图集缓存（`PawnRenderer.cs:340` 的 `useCached` 判定）；检视栏/殖民者栏头像走另一套 `PortraitsCache`。改设置后必须两个都刷新：

```csharp
GlobalTextureAtlasManager.TryMarkPawnFrameSetDirty(pawn);  // 每个在场 pawn
PortraitsCache.Clear();                                     // 头像整体清空
```

这就是"设置改了没反应/头像没刷新"问题的解法。设置页在模式变化时调用 `RefreshAllPawnCaches()`。

已知限制：HAR 种族若用自建渲染节点（不走 `PawnRenderNode_Hair`），"同时显示"可能对特定 race 无效 —— 需反编译对应 mod 的 `AlienRenderTreePatches` 补对它的 patch。

### 4. 机械师带宽可编辑

**原版机制**（`RimWorld/Pawn_MechanitorTracker.cs:71`）：
```csharp
public int TotalBandwidth => (int)pawn.GetStatValue(StatDefOf.MechBandwidth);
```
就是Stat 取值，patch getter 即可全局生效（带宽 gizmo、造机械体检查、超载逻辑全部走这个属性）。
- 注意：`tracker.pawn` 字段是 **private**，要用公开属性 `tracker.Pawn`。
- gizmo：`MechanitorBandwidthGizmo.Visible => 单选即显示`（无征召门，原版代码里没有；实测征召才显示疑似其他 QoL mod 过滤，未深究）。
- 覆盖值按 `pawn.ThingID` 存在 `GameComponent`（`BandwidthOverridesComponent`），随存档持久化；设回原版值即删除覆盖。
- 悬停标签私有方法：`DeepResourceGrid.RenderMouseAttachments`，用 `AccessTools.Method` + 反射调用。

## 新增功能的标准流程

1. **反编译定位**：在 `/tmp/rwsrc` 里 grep 关键词找到原版类，读懂门禁/缓存/调用链。
2. **写 patch**：优先 postfix 改 `__result` / 参数；改 `ref` 参数（如 `PawnDrawParms`）用 `ref` 签名。私有成员一律 `AccessTools`。
3. **编译**：`cd Source && dotnet build -c Release`（缺 Unity 模块引用会报 CS0012，往 csproj 补 `UnityEngine.XxxModule.dll`）。
4. **部署**：`cp -r ../About ../Assemblies "C:/Program Files (x86)/Steam/steamapps/common/RimWorld/Mods/IliyianCustomMod/"`
5. **验证**：游戏内看日志 `[IliyianCustomMod] loaded: ...`；Harmony 冲突会在这里报红。
6. **提交**：`git add -A && git commit && git push`（repo: `iliyian/IliyianCustomMod`，public）。

## 已知未决项

- 机械师编辑 gizmo 未征召时不显示（原版无此门，疑似与其他 mod 交互；备选方案：给 `MechanitorBandwidthGizmo.GizmoOnGUI` 加 postfix 让点击带宽条直接开编辑窗 —— 该 gizmo 不消费点击，安全）。
- mod 设置页打开时游戏暂停，暂停期间场景不重绘属于正常现象；缓存刷新逻辑已让恢复运行后立刻生效。
