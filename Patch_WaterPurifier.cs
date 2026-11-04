using System;
using HarmonyLib;

namespace ChemPurifierCompat
{
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
    //     InvokeAllRed
    //   调用，任何一个订阅者返回 true 即放行。+= 一个检查函数既不会覆盖
    //   原有的 MODULE_TYPE_UNIVERSAL 放行逻辑，又能追加 chem_module 的
    //   放行条件，两者 OR 起来。
    // ========================================================================
    [HarmonyPatch(typeof(MachinePurifier), "WaterPurifier")]
    public static class Patch_WaterPurifier
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
                ChemLog.Log?.Warning($"[ChemPurifier] Patch_WaterPurifier failed: {e}");
            }
        }
    }
}
