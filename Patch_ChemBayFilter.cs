using System;
using System.Collections.Generic;
using HarmonyLib;

namespace ChemPurifierCompat
{
    // ========================================================================
    // 补丁 3：让化学师储藏区（Chemist Storage Bay）接受带 FILTER 标签的物品
    // ========================================================================
    // 目标方法：GameGridInventory.MayHaveValidInventorySlot(item)
    //   —— 物品放入或移动前，由库存判断是否存在能容纳它的格子。
    //
    // 为什么需要这个补丁：
    //   原版只按物品自身的形状/标签判断能否放进格子，化学师储藏区
    //   （storage_bay_chem）不会额外放行带 FILTER 标签的物品，导致过滤器
    //   类物品存不进去。这个补丁只在原方法返回 false 时补一次判断：
    //   当前库存确实是 storage_bay_chem 的内容库存、且物品带 FILTER 标签，
    //   就把结果改成 true。原本返回 true 的情况完全不干预，所以不会改变
    //   任何原版已允许的放入行为。
    //
    // 为什么缓存判定结果：
    //   MayHaveValidInventorySlot 在拖拽预览时被高频调用，每次都向上查
    //   GetParentItem() 代价不低。用两个集合分别记住"是/不是化学师储藏区"
    //   的库存实例，每个库存只查一次。
    //   GameInventory 实例与场景绑定，重新载入存档后指针会换成新的，因此
    //   由 ModEntry.Init 调用 ClearCache() 丢弃上一局的缓存。
    // ========================================================================
    [HarmonyPatch(typeof(GameGridInventory), nameof(GameGridInventory.MayHaveValidInventorySlot))]
    public static class Patch_ChemBayFilter
    {
        // 化学师储藏区（Chemist Storage Bay）物品的 identifier（GameItem.identifier）。
        private const string ChemBayId = "storage_bay_chem";

        // 允许放入化学师储藏区的物品标签，与游戏里过滤器物品使用的 tag 一致。
        private const string FilterTag = "FILTER";

        // 已确认为化学师储藏区的库存，避免重复查找父物品
        private static readonly HashSet<GameInventory> ChemBays = new HashSet<GameInventory>();

        // 已确认不是化学师储藏区的库存，避免在普通库存上反复调用 GetParentItem
        private static readonly HashSet<GameInventory> NonChemBays = new HashSet<GameInventory>();

        /// <summary>
        /// 由 ModEntry.Init 在载入存档前调用，避免旧场景实例残留。
        /// </summary>
        public static void ClearCache()
        {
            ChemBays.Clear();
            NonChemBays.Clear();
        }

        [HarmonyPostfix]
        public static void Postfix(GameGridInventory __instance, GameItem item, ref bool __result)
        {
            // 原版已允许，或物品为空：不干预
            if (__result || item == null) return;

            try
            {
                // 已确认不是化学师储藏区：不干预
                if (NonChemBays.Contains(__instance)) return;

                // 第一次遇到这个库存：判断它是不是 storage_bay_chem 的内容库存
                if (!ChemBays.Contains(__instance))
                {
                    GameItem parent = __instance.GetParentItem();
                    if (parent == null || !ChemBayId.Equals(parent.identifier))
                    {
                        NonChemBays.Add(__instance);
                        return;
                    }

                    ChemBays.Add(__instance);
                    ChemLog.Log?.Log($"[ChemPurifier] chemist storage bay recognized: {ChemBayId}.");
                }

                // 是化学师储藏区，且物品带 FILTER 标签：强制允许
                if (item.IsTag(FilterTag))
                {
                    __result = true;
                }
            }
            catch (Exception e)
            {
                // 补丁里抛异常会冒泡到游戏的拖拽逻辑，用 try/catch 包起来避免
                // 影响物品移动；同时记入非化学师储藏区，避免每帧重复报错。
                if (__instance != null) NonChemBays.Add(__instance);
                ChemLog.Log?.Warning($"[ChemPurifier] Patch_ChemBayFilter failed: {e}");
            }
        }
    }
}