using System;
using System.Collections.Generic;
using HarmonyLib;

namespace ChemPurifierCompat
{
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
    public static class Patch_PurifyContainer
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

            int count = ModEntry.CountChemModules(machine);
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
    }
}
