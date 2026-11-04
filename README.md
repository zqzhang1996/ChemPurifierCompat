# ChemPurifier Compat

《深空当铺》（Probably Stolen）的兼容性模组。允许**净水器**安装**化学设备模组**，并按已安装的化学模块数量分级豁免污染物；同时让**化学师储藏区**（Chemist Storage Bay）可以直接存放带 `FILTER` 标签的物品。

## 功能

| 化学模块数量 | 不再视为污染物的物质 |
|---|---|
| 1 个 | 酸、碱、酒精、脂合剂 |
| 2 个 | 追加：N-羟甲基碱、稳定剂、还原剂 |

豁免物质在净化计算中**完全等价于水**——它们占用水的位置，但不被移除；

此外，化学师储藏区（Chemist Storage Bay，`storage_bay_chem`）会额外放行带 `FILTER` 标签的物品，无需先取出过滤器再转移。

## 安装

将 `ChemPurifierCompat` 文件夹放入游戏目录下的 `Mods/`，在游戏内"模组"菜单中启用并重启。

依赖游戏自带的 ModLoader 与 Harmony，无需额外安装。

## 本地部署（开发用）

`deploy.ps1` 位于项目根目录，所有路径相对脚本自身解析，项目名取自同级的 `.csproj`：

```powershell
.\deploy.ps1                              # 编译 Release 并部署
.\deploy.ps1 -Configuration Debug         # 编译 Debug，附带 pdb
.\deploy.ps1 -NoBuild                     # 跳过编译，直接复制现有产物
.\deploy.ps1 -DestinationRoot 'D:\Mods'   # 自定义 Mods 根目录
```

默认部署到 `..\..\Mods\<项目名>`，即游戏根目录下的 `Mods/`；复制 `dll`、`manifest.xml` 和 `Localization/`。
游戏运行时会锁定已加载的 dll，需先退出游戏再部署。

## 实现原理

原版净水器只放行 `MODULE_TYPE_UNIVERSAL` 模块，而 `chem_module` 带的是 `MODULE_TYPE_CHEM`，两者不匹配；化学师储藏区也不会额外放行带 `FILTER` 标签的物品。本模组通过三个 Harmony 补丁解决：

- **`Patch_WaterPurifier`**：在 `MachinePurifier.WaterPurifier` 的 Postfix 中，给模块槽追加放行 `chem_module` 的委托（`+=`，不覆盖原有检查）。
- **`Patch_PurifyContainer`**：在 `MachinePurifier.PurifyContainer` 的 Prefix 中，把豁免液体的 tag 清零、等量加到 water 的 tag 上；Postfix 再扣回 water、恢复原始 tag。净水逻辑全程"看到"这些物质等于水，因此不会移除它们。
- **`Patch_ChemBayFilter`**：在 `GameGridInventory.MayHaveValidInventorySlot` 的 Postfix 中，仅当原方法返回 `false` 时补一次判断——若当前库存的父物品是化学师储藏区（Chemist Storage Bay，`storage_bay_chem`）且物品带 `FILTER` 标签，则把结果改为 `true`。原本允许的放入行为不受影响。

`Patch_ChemBayFilter` 用两个 `HashSet` 缓存"是 / 不是化学师储藏区"的判定，避免在高频调用的拖拽预览中反复向上查找父物品；缓存以库存实例为键，因此 `ModEntry.Init` 会调用 `ClearCache()` 丢弃上一局的残留引用。

## 兼容性

- 只反射游戏自身类型，`manifest.xml` 中 `<Capabilities>` 为空，不需要声明任何能力。
- 若游戏更新后物质 identifier、机器方法名、`storage_bay_chem` 的 identifier 或 `FILTER` 标签名发生变化，需要重新核对后调整。
- `Patch_ChemBayFilter` 只会**放宽**放入条件（在 `false` 上补 `true`），不会阻止任何原版允许的操作。

## 目录结构

```
ChemPurifierCompat/
├── manifest.xml
├── ChemPurifierCompat.dll
└── Localization/
    ├── en.csv
    └── zh.csv
```

源码结构：

```
ChemPurifierCompat/
├── ModEntry.cs              # IMod 入口、日志、提示框、模块计数
├── Patch_WaterPurifier.cs   # 模块槽放行 chem_module
├── Patch_PurifyContainer.cs # 分级豁免污染物
├── Patch_ChemBayFilter.cs   # 化学师储藏区放行 FILTER 物品
├── deploy.ps1               # 编译并部署到 Mods
├── ChemPurifierCompat.csproj
├── manifest.xml
└── Localization/
    ├── en.csv
    └── zh.csv
```

## 更新记录

### v1.02

- 新增 `Patch_ChemBayFilter`：化学师储藏区（Chemist Storage Bay，`storage_bay_chem`）可以存放带 `FILTER` 标签的物品（如韧棉织物 / Kotton Fabric）。
- 源码拆分为 `ModEntry.cs` + 每个补丁一个 `Patch_*.cs`。
- 新增 `deploy.ps1`：编译后直接把产物部署到 `Mods/`。

## 许可

MIT
