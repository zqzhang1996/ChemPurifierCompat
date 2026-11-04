// ============================================================================
// ChemPurifier Compat
// ----------------------------------------------------------------------------
// 让《深空当铺》的净水器（water_purifier）可以安装化学设备模组（chem_module），
// 并按已安装的 chem_module 数量分级豁免污染物：
//
//   1 个 chem_module：酸、碱、酒精、脂合剂             —— 不被净水器移除
//   2 个 chem_module：再追加还原剂、稳定剂、N-羟甲基碱 —— 七种全部不被移除
//
// 文件划分：
//
//   ModEntry.cs              —— 模组入口、静态日志、提示框、模块计数
//   Patch_WaterPurifier.cs   —— 让净水器模块槽接受 chem_module
//   Patch_PurifyContainer.cs —— 按 chem_module 数量分级豁免污染物
//   Patch_ChemBayFilter.cs   —— 让化学师储藏区（Chemist Storage Bay）接受带 FILTER 标签的物品
//
// 整体设计要点：
//
//   1. 安装入口（Patch_WaterPurifier）
//      chem_module 由 ModuleHelper.InitModuleItem 注册时只启用
//      MODULE_TYPE_CHEM 标签，而净水器模块槽只放行 MODULE_TYPE_UNIVERSAL。
//      两者不匹配，玩家无法把 chem_module 装进净水器。这个补丁在
//      WaterPurifier() 的 Postfix 里给模块槽追加一个"放行 chem_module"
//      的委托，既保留原检查，又追加新规则。
//
//   2. 污染豁免（Patch_PurifyContainer）
//      需求是"这七种物质在净水计算期间完全等价于水"——它们占用水的位置，
//      但不被净水器移除；计算结束后恢复原状、原封不动留在容器里。
//      做法是在 PurifyContainer 的 Prefix 把豁免液体 tag 清零、把等量加到
//      water 的 tag 上，让净水逻辑"看到"的容器就是一个没有这些物质、
//      但水量更多的容器；Postfix 再把 water 扣回、恢复原始 tag。
//
//   3. 分级依据
//      CountChemModules 统计净水器模块槽中 chem_module 的总数（用 unitCount
//      累加，兼容多实例和堆叠两种情况）。每个净化周期重新统计，所以玩家
//      中途插拔模块后下一个周期自动切换等级，不需要监听增删事件。
//
//   4. 提示文本
//      走游戏自带的 Localization/*.csv + ModHelper.GetLocalized，未提供的
//      语言自动回退到 en，再回退到键名本身。文案按等级分两条键。
//
// 兼容性：
//   - 游戏自己的 ModLoader 会扫描所有 [HarmonyPatch] 类并自动应用，
//     本模组不手动调用 Harmony.PatchAll。
//   - 只反射游戏自身类型（typeof / nameof），不涉及外部程序集反射，
//     因此不需要在 manifest 里声明任何 <Capabilities>。
// ============================================================================

using System;
using System.Collections.Generic;

namespace ChemPurifierCompat
{
    // ========================================================================
    // 静态日志入口
    // ========================================================================
    // Harmony 补丁类必须是 static（游戏扫描程序集时要求），拿不到 IMod 实例。
    // 用静态字段把 ModLog 暴露给补丁类使用。
    // ModLog 的输出会带上 manifest.xml 里 <Name> 的前缀，例如：
    //   [ChemPurifier Compat]: ...
    // ========================================================================
    public static class ChemLog
    {
        public static ModLog Log;
    }

    // ========================================================================
    // 模组入口
    // ========================================================================
    // 实现游戏的 IMod 接口。注意：
    //   - 不要手动调用 Harmony.PatchAll()，游戏会扫描并应用所有 [HarmonyPatch]
    //     类。
    //   - Init/OnEnable 运行在主菜单，此时商店容器、存档状态都不存在，
    //     所以这里只做日志和钩子订阅，不生成物品、不访问 PlayerStore。
    //   - OnEnable 里不能调用 ModHelper.GetLocalized，本地化文件此时尚未加载；
    //     只有在钩子（钩子在游戏过程中触发）里调用才是安全的。
    // ========================================================================
    public class ModEntry : IMod
    {
        public void Init(ModManifest manifest)
        {
            ChemLog.Log = new ModLog(manifest);
            ChemLog.Log.Log("ChemPurifier Compat initialized.");

            // 补丁的库存缓存以场景实例为键，重新载入时必须清空，
            // 否则会残留上一局已销毁的 GameInventory 引用。
            Patch_ChemBayFilter.ClearCache();
        }

        public void OnEnable()
        {
            ChemLog.Log?.Log("ChemPurifier Compat enabled.");

            // OnCreateTooltipLate 在游戏构建物品提示框时触发。
            // 此时本地化文件已加载、游戏系统可用，是追加提示行的正确时机。
            ModHook.OnCreateTooltipLate += OnTooltip;
        }

        public void OnDisable()
        {
            // 退订必须和订阅成对出现，否则热重载或禁用模组时会残留处理函数。
            ModHook.OnCreateTooltipLate -= OnTooltip;
            ChemLog.Log?.Log("ChemPurifier Compat disabled.");
        }

        // --------------------------------------------------------------------
        // 在净水器提示框末尾追加"当前豁免列表"说明
        // --------------------------------------------------------------------
        // (x1) / (x2) 指的是"第 N 个化学模块带来的效果"，不是当前总数：
        //   - 装 1 个：只显示 (x1) 行
        //   - 装 2 个及以上：追加 (x2) 行
        //   - 装 3 个及以上：仍只显示这两行（第 3 个模块无额外效果）
        // 分两行显示避免单行过长，也便于玩家一眼看出两档效果的区别。
        // --------------------------------------------------------------------
        private static void OnTooltip(RichTextBuilder builder, GameItem item)
        {
            // 只处理净水器。item.identifier 在游戏里是 "water_purifier"。
            if (item == null || !item.identifier.Equals("water_purifier")) return;

            int count = CountChemModules(item);
            if (count <= 0) return; // 没装化学模块就不显示提示

            var color = RenderHandler.ColorPalette.LightCyan;

            // 第 1 个模块的豁免效果，装 1 个即显示
            string line1 = ModHelper.GetLocalized("ChemPurifierCompat", "chempurifier_tooltip_tier1");
            builder.AddLine(
                line1,
                true, // bold
                color,
                false, false, false, false,
                RenderHandler.ColorPalette.None,
                RenderHandler.ColorPalette.None,
                RenderHandler.ColorPalette.None);

            // 第 2 个模块的豁免效果，装 2 个及以上时追加一行
            if (count >= 2)
            {
                string line2 = ModHelper.GetLocalized("ChemPurifierCompat", "chempurifier_tooltip_tier2");
                builder.AddLine(
                    line2,
                    true,
                    color,
                    false, false, false, false,
                    RenderHandler.ColorPalette.None,
                    RenderHandler.ColorPalette.None,
                    RenderHandler.ColorPalette.None);
            }
        }

        /// <summary>
        /// 统计净水器模块槽中 chem_module 的总数量。
        /// 用 unitCount 累加，同时兼容"多个独立物品"和"单个堆叠物品"：
        ///   - 多个独立 1x1 物品 → 遍历到多个，各加 1
        ///   - 假设未来 chem_module 可堆叠，一个物品 unitCount = 3 → 加 3
        /// </summary>
        internal static int CountChemModules(GameItem machine)
        {
            if (machine == null) return 0;

            try
            {
                // MachineHelper.GetModules 内部取的是模块槽 (索引 1) 的
                // childItems，不会误扫到电池槽、水容器槽或便签槽。
                List<GameItem> modules = MachineHelper.GetModules(machine);
                if (modules == null) return 0;

                int count = 0;
                foreach (GameItem m in modules)
                {
                    if (m != null && "chem_module".Equals(m.identifier))
                        count += m.unitCount;
                }
                return count;
            }
            catch (Exception e)
            {
                ChemLog.Log?.Warning($"[ChemPurifier] CountChemModules failed: {e}");
                return 0;
            }
        }
    }
}
