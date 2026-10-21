using System.Reflection;
using System.Reflection.Emit;
using BaseLib.Hooks;
using BaseLib.Utils;
using BaseLib.Utils.Patching;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace BaseLib.Patches.Hooks;

/// <summary>
/// IHealAmountModifier.ModifyHealAdditive() -> IHealAmountModifier.ModifyHealMultiplicative()
/// </summary>
[HarmonyPatch(typeof(CreatureCmd), nameof(CreatureCmd.Heal), MethodType.Async)]
public static class ModifyHealAmountPatches
{
//amount = Hook.ModifyHealAmount(creature.Player?.RunState ?? creature.CombatState?.RunState ?? NullRunState.Instance, creature.CombatState, creature, amount);
    [HarmonyTranspiler]
    static List<CodeInstruction> Patch(IEnumerable<CodeInstruction> code)
    {
        return new InstructionPatcher(code)
            .Match(new InstructionMatcher()
                .ldarg_0()
                .ldfld(null)
                .ldfld(null).PredicateMatch(op => op is FieldInfo field && field.Name.Contains("creature"))
            )
            .CopyMatch(out var loadCreature)
            .Match(new InstructionMatcher()
                .ldfld(null).PredicateMatch(op => op is FieldInfo field && field.Name.Equals("amount"))
            )
            .Step(-1)
            .GetOperand(out var amountField)
            .Insert(CodeInstruction.LoadArgument(0)) //Load arg 0 for the stfld to amountField later
            .Step(1)
            .Insert(loadCreature)
            .Insert([
                //Stack is statemachine - amount - creature
                CodeInstruction.Call(typeof(ModifyHealAmountPatches), nameof(ModifyHealAmountPatches.ModifyHeal)),
                //Stack is statemachine - amount
                new CodeInstruction(OpCodes.Stfld, amountField), //Store in statemachine amount field
                CodeInstruction.LoadArgument(0),
                new CodeInstruction(OpCodes.Ldfld, amountField)
            ]);
    }

    /// <summary>
    /// Applies every heal amount modifier to <paramref name="amount"/> for <paramref name="creature"/>.
    /// A null <paramref name="creature"/> skips the hooks and returns the clamped amount, which is exactly
    /// what a heal with no listeners produces.
    /// </summary>
    /// <param name="amount">Heal amount before modifiers.</param>
    /// <param name="creature">Creature being healed.</param>
    public static decimal ModifyHeal(decimal amount, Creature creature)
    {
        //The transpiler calls this before the engine's own `creature == null` check (IL_00a5), so the argument is
        //not validated by the engine at this point. Vanilla currently dereferences `creature` even earlier
        //(IL_004c/IL_0064), so a null creature never actually reaches this hook today; the guard keeps the hook
        //total across engine reordering and direct calls. Returning Math.Max(0m, amount) mirrors
        //ModifyMultiplicative's final clamp, i.e. the result of a heal that has no IHealAmountModifier listeners.
        if (creature == null)
        {
            BaseLibMain.Logger.Warn("ModifyHeal called with a null creature; heal amount modifiers were skipped.");
            return Math.Max(0m, amount);
        }

        var combatState = BetaMainCompatibility.Creature_.CombatState.Get(creature);
        var runState = creature.Player?.RunState ?? (combatState == null ? NullRunState.Instance : new CombatStateWrapper(combatState).RunState);

        ModifyAdditive(runState, combatState, creature, ref amount);
        ModifyMultiplicative(runState, combatState, creature, ref amount);
        
        return amount;
    }
    
    static void ModifyAdditive(IRunState runState, object? combatState, Creature creature, ref decimal amount)
    {
        decimal num = amount;

        foreach (var item in BetaMainCompatibility.RunState.IterateHookListeners.Invoke<IEnumerable<AbstractModel>>(runState, combatState) ?? [])
        {
            if (item is IHealAmountModifier mod)
                num += mod.ModifyHealAdditive(creature, amount); // pass the amount before any addition
        }

        amount = num;
    }

    static void ModifyMultiplicative(IRunState runState, object? combatState, Creature creature, ref decimal __result)
    {
        decimal num = __result;

        foreach (var item in BetaMainCompatibility.RunState.IterateHookListeners.Invoke<IEnumerable<AbstractModel>>(runState, combatState) ?? [])
        {
            if (item is IHealAmountModifier mod)
                num *= mod.ModifyHealMultiplicative(creature, __result); // pass the amount before any multiplication
        }

        __result = Math.Max(0m, num);
    }
}