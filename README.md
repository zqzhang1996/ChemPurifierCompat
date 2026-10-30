# ChemPurifier Compat

《深空当铺》（Probably Stolen）的兼容性模组。允许**净水器**安装**化学设备模组**，并按已安装的化学模块数量分级豁免污染物。

## 功能

| 化学模块数量 | 不再视为污染物的物质 |
|---|---|
| 1 个 | 酸、碱、酒精、脂合剂 |
| 2 个 | 追加：N-羟甲基碱、稳定剂、还原剂 |

豁免物质在净化计算中**完全等价于水**——它们占用水的位置，但不被移除；

## 安装

将 `ChemPurifierCompat` 文件夹放入游戏目录下的 `Mods/`，在游戏内"模组"菜单中启用并重启。

依赖游戏自带的 ModLoader 与 Harmony，无需额外安装。

## 实现原理

原版净水器只放行 `MODULE_TYPE_UNIVERSAL` 模块，而 `chem_module` 带的是 `MODULE_TYPE_CHEM`，两者不匹配。本模组通过两个 Harmony 补丁解决：

- **`WaterPurifierPatch`**：在 `MachinePurifier.WaterPurifier` 的 Postfix 中，给模块槽追加放行 `chem_module` 的委托（`+=`，不覆盖原有检查）。
- **`PurifyContainerPatch`**：在 `MachinePurifier.PurifyContainer` 的 Prefix 中，把豁免液体的 tag 清零、等量加到 water 的 tag 上；Postfix 再扣回 water、恢复原始 tag。净水逻辑全程"看到"这些物质等于水，因此不会移除它们。

## 兼容性

- 只反射游戏自身类型，`manifest.xml` 中 `<Capabilities>` 为空，不需要声明任何能力。
- 若游戏更新后物质 identifier 或机器方法名发生变化，需要重新核对后调整。

## 目录结构

```
ChemPurifierCompat/
├── manifest.xml
├── ChemPurifierCompat.dll
└── Localization/
    ├── en.csv
    └── zh.csv
```

## 许可

MIT
