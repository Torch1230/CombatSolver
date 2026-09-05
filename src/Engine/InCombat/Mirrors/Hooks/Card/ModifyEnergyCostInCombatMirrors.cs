using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.Common.Mirrors;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;

using Registry = MethodMirrorRegistry<AbstractModel, ModifyEnergyCostInCombatMirrorContext, decimal>;

// Mirrors the early and late passes of Hook.ModifyEnergyCostInCombat.
internal static class ModifyEnergyCostInCombatMirrors
{
    private static readonly MirrorMethodSpec ModifyEnergyCostInCombat = MirrorMethodSpec.Hook(
        nameof(AbstractModel.TryModifyEnergyCostInCombat),
        [typeof(CardModel), typeof(decimal), typeof(decimal).MakeByRefType()]);

    private static readonly MirrorMethodSpec ModifyEnergyCostInCombatLate = MirrorMethodSpec.Hook(
        nameof(AbstractModel.TryModifyEnergyCostInCombatLate),
        [typeof(CardModel), typeof(decimal), typeof(decimal).MakeByRefType()]);

    private static readonly Registry Registry = new(ModifyEnergyCostInCombat);
    private static readonly Registry LateRegistry = CreateLateRegistry();

    public static decimal Invoke(AbstractModel listener, ModifyEnergyCostInCombatMirrorContext context)
    {
        if (Registry.TryInvokeRegistered(listener, context, out var result))
        {
            return result.Value;
        }

        listener.TryModifyEnergyCostInCombat(context.Card.Preview, context.Cost, out var modifiedCost);
        return modifiedCost;
    }

    public static decimal InvokeLate(AbstractModel listener, ModifyEnergyCostInCombatMirrorContext context)
    {
        if (LateRegistry.TryInvokeRegistered(listener, context, out var result))
        {
            return result.Value;
        }

        listener.TryModifyEnergyCostInCombatLate(context.Card.Preview, context.Cost, out var modifiedCost);
        return modifiedCost;
    }

    private static Registry CreateLateRegistry()
    {
        var registry = new Registry(ModifyEnergyCostInCombatLate);

        registry.Register<FreeAttackPower>(HandleFreeAttackPower);
        registry.Register<FreePowerPower>(HandleFreePowerPower);
        registry.Register<FreeSkillPower>(HandleFreeSkillPower);
        registry.Register<VeilpiercerPower>(HandleVeilpiercerPower);
        registry.Register<VoidFormPower>(HandleVoidFormPower);

        registry.Register<BrilliantScarf>(HandleBrilliantScarf);

        return registry;
    }

    private static decimal HandleFreeAttackPower(FreeAttackPower power, ModifyEnergyCostInCombatMirrorContext context)
    {
        return HandleFreeCardPower(power, CardType.Attack, context);
    }

    private static decimal HandleFreePowerPower(FreePowerPower power, ModifyEnergyCostInCombatMirrorContext context)
    {
        return HandleFreeCardPower(power, CardType.Power, context);
    }

    private static decimal HandleFreeSkillPower(FreeSkillPower power, ModifyEnergyCostInCombatMirrorContext context)
    {
        return HandleFreeCardPower(power, CardType.Skill, context);
    }

    private static decimal HandleVeilpiercerPower(
        VeilpiercerPower power,
        ModifyEnergyCostInCombatMirrorContext context)
    {
        return context.Card.Preview.Owner.Creature == power.Owner &&
            context.Card.HasKeyword(context.State, CardKeyword.Ethereal) &&
            IsInPlayablePile(context) &&
            context.StateStore.GetPowerAmount(power).IsActive
                ? 0
                : context.Cost;
    }

    private static decimal HandleVoidFormPower(
        VoidFormPower power,
        ModifyEnergyCostInCombatMirrorContext context)
    {
        return context.Card.Preview.Owner.Creature == power.Owner &&
            IsInPlayablePile(context) &&
            context.StateStore.Get(power, static value => new VoidFormPredictionState(value)).CardsPlayedThisTurn <
            power.Amount
                ? 0
                : context.Cost;
    }

    private static decimal HandleBrilliantScarf(
        BrilliantScarf relic,
        ModifyEnergyCostInCombatMirrorContext context)
    {
        return context.Card.Preview.Owner == relic.Owner &&
            IsInPlayablePile(context) &&
            context.StateStore.Get(relic, static value => new CounterPredictionState(value._cardsPlayedThisTurn)).Value ==
            relic.DynamicVars.Cards.IntValue - 1
                ? 0
                : context.Cost;
    }

    private static decimal HandleFreeCardPower(
        PowerModel power,
        CardType type,
        ModifyEnergyCostInCombatMirrorContext context)
    {
        return context.Card.Preview.Owner.Creature == power.Owner &&
            context.Card.Preview.Type == type &&
            IsInPlayablePile(context) &&
            context.StateStore.GetPowerAmount(power).IsActive
                ? 0
                : context.Cost;
    }

    private static bool IsInPlayablePile(ModifyEnergyCostInCombatMirrorContext context)
    {
        return context.Card.GetPile(context.State)?.Type is PileType.Hand or PileType.Play;
    }
}

// 这个 context 在调用返回后没有任何持有者：本文件的每个 handler 只读它的属性（状态仓工厂
// 都是 static lambda，不捕获它），MethodMirrorRegistry 只是转交给 handler，回退路径交给游戏的
// 只是 CardModel 与 decimal。因此它可以按模拟器复用一份，Card/Cost 每次取用时重置。
internal sealed class ModifyEnergyCostInCombatMirrorContext : CombatMirrorContext
{
    public required PredictedCard Card { get; set; }

    public required decimal Cost { get; set; }
}
