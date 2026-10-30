// ============================================================================
// ChemPurifier Compat
// ----------------------------------------------------------------------------
// 让《深空当铺》的净水器（water_purifier）可以安装化学设备模组（chem_module），
// 并按已安装的 chem_module 数量分级豁免污染物：
//
//   1 个 chem_module：酸、碱、酒精、脂合剂             —— 不被净水器移除
//   2 个 chem_module：再追加还原剂、稳定剂、N-羟甲基碱 —— 七种全部不被移除
//
// 整体设计要点：
//
//   1. 安装入口（WaterPurifierPatch）
//      chem_module 由 ModuleHelper.InitModuleItem 注册时只启用
//      MODULE_TYPE_CHEM 标签，而净水器模块槽只放行 MODULE_TYPE_UNIVERSAL。
//      两者不匹配，玩家无法把 chem_module 装进净水器。这个补丁在
//      WaterPurifier() 的 Postfix 里给模块槽追加一个"放行 chem_module"
//      的委托，既保留原检查，又追加新规则。
//
//   2. 污染豁免（PurifyContainerPatch）
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

using HarmonyLib;
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
    public class ChemPurifierMod : IMod
    {
        public void Init(ModManifest manifest)
        {
            ChemLog.Log = new ModLog(manifest);
            ChemLog.Log.Log("ChemPurifier Compat initialized.");
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

            int count = PurifyContainerPatch.CountChemModules(item);
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
    }

    // ========================================================================
    // 补丁 1：让净水器模块槽接受 chem_module
    // ========================================================================
    // 目标方法：MachinePurifier.WaterPurifier()
    //   —— 在玩家制造一台新净水器时调用，负责搭好窗口、模块槽、回调。
    //
    // 为什么需要这个补丁：
    //   WaterPurifier() 内部调用
    //     ContainerHelper.AllowOnlyTaggedItemsOr(moduleInv,
    //         new[] { "MODULE_TYPE_UNIVERSAL" }, true);
    //   而 chem_module 由 InitModuleItem 注册时只启用 MODULE_TYPE_CHEM
    //   （ModuleHelper.InitModuleItem 只 EnableTag 一个 moduleType）。
    //   结果：chem_module 没有 MODULE_TYPE_UNIVERSAL，装不进净水器。
    //
    // 为什么用 += 追加而不是替换：
    //   mayInventoryAddItemFunc 是多播委托，游戏用
    //     InvokeAllReduce(..., BoolReduceOR)
    //   调用，任何一个订阅者返回 true 即放行。+= 一个检查函数既不会覆盖
    //   原有的 MODULE_TYPE_UNIVERSAL 放行逻辑，又能追加 chem_module 的
    //   放行条件，两者 OR 起来。
    // ========================================================================
    [HarmonyPatch(typeof(MachinePurifier), "WaterPurifier")]
    public static class WaterPurifierPatch
    {
        [HarmonyPostfix]
        public static void Postfix(GameItem __result)
        {
            if (__result == null) return;

            try
            {
                // 索引说明：净水器面板顺序为 [Power, Module, Container, Note]。
                // MachineHelper.GetBodyStorage(machine, 0) 是电池槽；
                // MachineHelper.GetModuleInv(machine) 内部固定取索引 1，
                // 正好是模块槽。不要用 0，那是电池槽。
                GameGridInventory moduleInv = MachineHelper.GetModuleInv(__result);
                if (moduleInv == null)
                {
                    ChemLog.Log?.Warning("[ChemPurifier] module inventory not found.");
                    return;
                }

                // 追加放行条件：
                //   - item 非空
                //   - 标识符为 chem_module
                //   - 物品归属玩家（与游戏原有的 GeneralHelper.IsItemOwned 检查一致）
                moduleInv.mayInventoryAddItemFunc += (item, inv) =>
                    item != null &&
                    "chem_module".Equals(item.identifier) &&
                    GeneralHelper.IsItemOwned(item);

                ChemLog.Log?.Log("[ChemPurifier] water_purifier now accepts chem_module.");
            }
            catch (Exception e)
            {
                // 补丁里抛异常会冒泡到游戏的调用栈，用 try/catch 包起来避免
                // 拖垮机器创建流程。
                ChemLog.Log?.Warning($"[ChemPurifier] WaterPurifierPatch failed: {e}");
            }
        }
    }

    // ========================================================================
    // 补丁 2：按 chem_module 数量分级豁免污染物（水等价替换式）
    // ========================================================================
    // 目标方法：MachinePurifier.PurifyContainer(machine, waterContainer, ...)
    //   —— 每个净水周期调用一次，内部会调用 RemoveLargestFirst 移除污染物。
    //
    // 需求语义：
    //   "这七种物质在净水计算期间完全等价于水"——它们占用水的位置，
    //   但不被净水器移除；计算结束后恢复原状，原封不动留在容器里。
    //
    // 实现原理：
    //   1. Prefix：把豁免液体的 tag 清零，把等量加到 water 的 tag 上。
    //      此后 PurifyContainer 内部所有计算（totalVolume、water、
    //      RemoveLargestFirst 的遍历）看到的都是"这些物质不存在，
    //      对应的量是水"，与"容器里真的只有水"完全等价。
    //   2. Postfix：把 water 里对应的量扣回，恢复豁免液体的原始 tag。
    //      净效果是豁免液体数量不变，非豁免污染物按原规则被移除。
    //
    // 为什么这样能保证"完全等价于水"：
    //   - totalVolume 前后守恒（豁免量从各自 tag 挪到 water tag，总量不变）。
    //   - water 变大，所以 (totalVolume - water) 变小，非豁免污染物的
    //     可移除预算相应减少——这正是"水占用了位置"的真实物理效果。
    //   - RemoveLargestFirst 遍历时豁免液体的 tag 为 0，直接被跳过。
    //   - 如果容器里本来就没有非豁免污染物，removedAmount = 0，
    //     onCycleEndSlotItemFunc 会提前 return，不消耗电、不触发机器效果，
    //     与"容器里只有水"的行为一致。
    // ========================================================================
    [HarmonyPatch(typeof(MachinePurifier), "PurifyContainer")]
    public static class PurifyContainerPatch
    {
        // --------------------------------------------------------------------
        // 分级的液体 identifier，来自 Liquid.InitLiquid() 的 SetIdentifier。
        //   acid              = 酸
        //   base              = 碱（不是 alkali）
        //   alcohol           = 酒精
        //   lipobond_agent    = 脂合剂
        //   reducing_agent    = 还原剂
        //   stabilizing_agent = 稳定剂
        //   methylol          = N-羟甲基碱
        // 如果游戏更新后改了这些 ID，F8 控制台会看到豁免不生效，
        // 回到 dnSpy 的 Liquid.InitLiquid 重新核对即可。
        // --------------------------------------------------------------------
        private static readonly string[] Tier1Ids =
        {
            "acid", "base", "alcohol", "lipobond_agent"
        };

        private static readonly string[] Tier2Ids =
        {
            "reducing_agent", "stabilizing_agent", "methylol"
        };

        /// <summary>
        /// Prefix：把豁免液体临时"变成水"。
        /// __state 保存每种豁免液体的原始数量；如果没有任何豁免液体，
        /// __state 为 null，Postfix 会直接跳过。
        /// </summary>
        [HarmonyPrefix]
        public static void Prefix(GameItem machine, GameItem waterContainer,
            out Dictionary<string, int> __state)
        {
            __state = null;
            if (waterContainer == null) return;

            int count = CountChemModules(machine);
            if (count <= 0) return;

            // 按模块数量决定本次豁免哪些物质
            var exemptions = new List<string>(Tier1Ids);
            if (count >= 2) exemptions.AddRange(Tier2Ids);

            string waterPart = Liquid.GetCurrentPartFromId("water");
            if (string.IsNullOrEmpty(waterPart)) return;

            var originalAmounts = new Dictionary<string, int>();
            int totalMoved = 0;

            foreach (string id in exemptions)
            {
                Liquid liquid = Liquid.GetLiquidFromId(id);
                if (liquid == null) continue;

                int amount = waterContainer.GetTagReadonly(liquid.currentPart).GetInt();
                if (amount <= 0) continue;

                originalAmounts[id] = amount;
                totalMoved += amount;

                // 清零：让净水逻辑完全看不到它
                waterContainer.ModifyTag(liquid.currentPart,
                    state => state.Enable().SetInt(0), false);
            }

            // 容器里没有豁免液体：不做任何修改，让原方法按原状态执行
            if (totalMoved <= 0) return;

            // 等量加到 water 上，保持 totalVolume 守恒
            waterContainer.ModifyTag(waterPart,
                state => state.Enable().SetInt(state.GetInt() + totalMoved),
                false);

            __state = originalAmounts;
        }

        /// <summary>
        /// Postfix：把豁免液体从 water 里"变回来"，恢复原始 tag。
        /// 然后重算一次水特征——PurifyContainer 内部那次 UpdateWaterFeature
        /// 是在"豁免物质视为水"的状态下算的，需要按真实状态覆盖一次，
        /// 让纯度、毒性等反映容器里实际存在的物质。
        /// </summary>
        [HarmonyPostfix]
        public static void Postfix(GameItem waterContainer,
            Dictionary<string, int> __state)
        {
            if (__state == null || __state.Count == 0) return;
            if (waterContainer == null) return;

            string waterPart = Liquid.GetCurrentPartFromId("water");
            if (string.IsNullOrEmpty(waterPart)) return;

            int totalRestore = 0;

            foreach (var kv in __state)
            {
                Liquid liquid = Liquid.GetLiquidFromId(kv.Key);
                if (liquid == null) continue;

                // 恢复到 Prefix 前的原始数量。
                // 用局部变量而不是直接捕获 kv.Value，便于阅读也避免闭包
                // 捕获迭代变量带来的歧义（C# 5+ 已经安全，这里只是更清晰）。
                int restoreAmount = kv.Value;
                waterContainer.ModifyTag(liquid.currentPart,
                    state => state.Enable().SetInt(restoreAmount), false);

                totalRestore += restoreAmount;
            }

            // 从 water 扣回等量。用 Math.Max(0, ...) 兜底，防止异常路径
            // （比如原方法中途抛异常）导致 water 变成负数。
            waterContainer.ModifyTag(waterPart, state =>
            {
                int newWater = Math.Max(0, state.GetInt() - totalRestore);
                state.Enable().SetInt(newWater);
            }, false);

            // PurifyContainer 内部已调用过一次 UpdateWaterFeature，
            // 但那是在"豁免物质 = 水"的状态下算的。这里按真实状态重算，
            // 让纯度、毒性、蒸发点等反映容器里实际存在的物质。
            WaterFeatureHelper.UpdateWaterFeature(waterContainer, false);
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