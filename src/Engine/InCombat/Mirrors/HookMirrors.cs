using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Attack;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Block;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Damage;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Death;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Orb;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Resources;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnEnd;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver.Engine.InCombat.Mirrors;

// Simulation-facing facade for mirrored combat hooks, analogous to vanilla Hook. Callers pass
// ordinary hook arguments; this class owns mirror context construction, listener enumeration, and
// hook-level ordering while method-specific registries and contexts remain implementation details.
internal static class HookMirrors
{
    /// <summary>
    /// Mirrors <see cref="Hook.ModifyBlock"/>.
    /// </summary>
    public static decimal ModifyBlock(
        CombatPredictionSimulator simulator,
        Creature target,
        decimal block,
        ValueProp props,
        PredictedCard? cardSource,
        CardPlay? cardPlay,
        out List<AbstractModel> modifiers)
    {
        modifiers = [];

        var cardModel = cardSource?.Preview;
        if (cardModel?.Enchantment is { } enchantment)
        {
            block += enchantment.EnchantBlockAdditive(block);
            block *= enchantment.EnchantBlockMultiplicative(block);
        }

        foreach (var listener in IterateCombatHookListeners(simulator))
        {
            var additive = listener.ModifyBlockAdditive(target, block, props, cardModel, cardPlay);
            block += additive;
            if (additive != 0)
            {
                modifiers.Add(listener);
                if (cardSource != null
                    && simulator.IsRecordingActionRelicTriggers
                    && listener is RelicModel relic)
                {
                    simulator.RecordRelicTrigger(
                        relic,
                        $"：格挡{FormatSigned(additive)}");
                }
            }
        }

        var context = new ModifyBlockMultiplicativeMirrorContext
        {
            Simulator = simulator,
            Target = target,
            Amount = block,
            Props = props,
            CardSource = cardSource,
            CardPlay = cardPlay
        };

        foreach (var listener in IterateCombatHookListeners(simulator))
        {
            context.Amount = block;
            var multiplier = ModifyBlockMultiplicativeMirrors.Invoke(listener, context);
            block *= multiplier;
            if (multiplier != 1)
            {
                modifiers.Add(listener);
                if (cardSource != null
                    && simulator.IsRecordingActionRelicTriggers
                    && listener is RelicModel relic)
                {
                    simulator.RecordRelicTrigger(
                        relic,
                        $"：格挡×{FormatDecimal(multiplier)}");
                }
            }
        }

        return Math.Max(0, block);
    }

    /// <summary>
    /// Mirrors <see cref="Hook.AfterModifyingBlockAmount"/>.
    /// </summary>
    public static void AfterModifyingBlockAmount(
        CombatPredictionSimulator simulator,
        decimal modifiedBlock,
        PredictedCard? cardSource,
        CardPlay? cardPlay,
        IReadOnlyList<AbstractModel> modifiers)
    {
        var context = new AfterModifyingBlockAmountMirrorContext
        {
            Simulator = simulator,
            ModifiedBlock = modifiedBlock,
            CardSource = cardSource,
            CardPlay = cardPlay
        };

        foreach (var listener in IterateCombatHookListeners(simulator))
        {
            if (modifiers.Contains(listener))
            {
                AfterModifyingBlockAmountMirrors.Invoke(listener, context);
            }
        }
    }

    // Mirrors Hook.BeforeBlockGained.
    public static void BeforeBlockGained(
        CombatPredictionSimulator simulator,
        Creature creature,
        decimal amount,
        ValueProp props,
        PredictedCard? source)
    {
        var context = new BeforeBlockGainedMirrorContext
        {
            Simulator = simulator,
            Creature = creature,
            Amount = amount,
            Props = props,
            Source = source
        };

        foreach (var listener in IterateCombatHookListeners(simulator))
        {
            BeforeBlockGainedMirrors.Invoke(listener, context);
        }
    }

    // Mirrors Hook.AfterBlockGained.
    public static void AfterBlockGained(
        CombatPredictionSimulator simulator,
        Creature creature,
        decimal amount,
        ValueProp props,
        PredictedCard? source)
    {
        var context = new AfterBlockGainedMirrorContext
        {
            Simulator = simulator,
            Creature = creature,
            Amount = amount,
            Props = props,
            Source = source
        };

        foreach (var listener in IterateCombatHookListeners(simulator))
        {
            AfterBlockGainedMirrors.Invoke(listener, context);
        }
    }

    // Mirrors Hook.AfterStarsGained.
    public static void AfterStarsGained(
        CombatPredictionSimulator simulator,
        int amount,
        Player gainer)
    {
        var context = new AfterStarsGainedMirrorContext
        {
            Simulator = simulator,
            Amount = amount,
            Gainer = gainer
        };

        foreach (var listener in IterateCombatHookListeners(simulator))
        {
            AfterStarsGainedMirrors.Invoke(listener, context);
        }
    }

    // Mirrors Hook.AfterBlockBroken. Vanilla deliberately iterates the combat state directly
    // so the hook still fires for a block-breaking hit that is also ending combat.
    public static void AfterBlockBroken(
        CombatPredictionSimulator simulator,
        Creature target,
        Creature? breaker)
    {
        var context = new AfterBlockBrokenMirrorContext
        {
            Simulator = simulator,
            Target = target,
            Breaker = breaker
        };

        foreach (var listener in context.State.IterateHookListeners())
        {
            AfterBlockBrokenMirrors.Invoke(listener, context);
        }
    }

    // Mirrors Hook.ShouldDraw with listener short-circuiting.
    public static bool ShouldDraw(
        CombatPredictionSimulator simulator,
        Player player,
        bool fromHandDraw,
        [NotNullWhen(false)] out AbstractModel? modifier)
    {
        var context = new ShouldDrawMirrorContext
        {
            Simulator = simulator,
            Player = player,
            FromHandDraw = fromHandDraw
        };

        foreach (var listener in IterateCombatHookListeners(simulator))
        {
            if (!ShouldDrawMirrors.Invoke(listener, context))
            {
                modifier = listener;
                return false;
            }
        }

        modifier = null;
        return true;
    }

    // Mirrors Hook.AfterCardDrawnEarly followed by Hook.AfterCardDrawn.
    public static void AfterCardDrawn(
        CombatPredictionSimulator simulator,
        PredictedCard card,
        bool fromHandDraw)
    {
        var context = new AfterCardDrawnMirrorContext
        {
            Simulator = simulator,
            Card = card,
            InitialCard = card.Preview,
            FromHandDraw = fromHandDraw
        };

        foreach (var listener in IterateCombatHookListeners(simulator))
        {
            AfterCardDrawnMirrors.InvokeEarly(listener, context);
        }

        HookListenerEnumerable listeners = IterateCombatHookListeners(simulator);
        foreach (var listener in listeners)
        {
            AfterCardDrawnMirrors.Invoke(listener, context);
        }
        AfterCardDrawnMirrors.Invoke(card.Preview, context);
    }

    // Mirrors Hook.AfterCardExhausted.
    public static void AfterCardExhausted(
        CombatPredictionSimulator simulator,
        PredictedCard card,
        bool causedByEthereal)
    {
        var context = new AfterCardExhaustedMirrorContext
        {
            Simulator = simulator,
            Card = card,
            CausedByEthereal = causedByEthereal
        };

        foreach (var listener in IterateCombatHookListeners(simulator))
        {
            AfterCardExhaustedMirrors.Invoke(listener, context);
        }
    }

    // Mirrors Hook.ModifyShuffleOrder.
    public static void ModifyShuffleOrder(
        CombatPredictionSimulator simulator,
        Player player,
        List<PredictedCard> cards,
        bool isInitialShuffle)
    {
        var context = new ModifyShuffleOrderMirrorContext
        {
            Simulator = simulator,
            Player = player,
            Cards = cards,
            IsInitialShuffle = isInitialShuffle
        };

        foreach (var listener in IterateCombatHookListeners(simulator))
        {
            ModifyShuffleOrderMirrors.Invoke(listener, context);
        }
    }

    // Mirrors Hook.AfterShuffle.
    public static void AfterShuffle(CombatPredictionSimulator simulator, Player player)
    {
        var context = new AfterShuffleMirrorContext { Simulator = simulator, Player = player };

        foreach (var listener in IterateCombatHookListeners(simulator))
        {
            if (simulator.HasPendingChoice)
                break;
            AfterShuffleMirrors.Invoke(listener, context);
            if (simulator.HasPendingChoice)
                break;
        }
    }

    // Mirrors Hook.AfterCardDiscarded.
    public static void AfterCardDiscarded(CombatPredictionSimulator simulator, PredictedCard card)
    {
        var context = new AfterCardDiscardedMirrorContext { Simulator = simulator, Card = card };

        foreach (var listener in IterateCombatHookListeners(simulator))
        {
            AfterCardDiscardedMirrors.Invoke(listener, context);
        }
    }

    // Mirrors Hook.AfterCardGeneratedForCombat.
    public static void AfterCardGeneratedForCombat(
        CombatPredictionSimulator simulator,
        PredictedCard card,
        Player? creator)
    {
        var context = new AfterCardGeneratedForCombatMirrorContext
        {
            Simulator = simulator,
            Card = card,
            Creator = creator
        };

        // Card-pile insertion registers prediction-local cards before this later hook phase.
        foreach (var listener in IterateCombatHookListeners(simulator))
        {
            AfterCardGeneratedForCombatMirrors.Invoke(listener, context);
        }
    }

    /// <summary>
    /// Mirrors <see cref="Hook.ShouldPlay"/>.
    /// </summary>
    public static bool ShouldPlay(
        CombatPredictionSimulator simulator,
        PredictedCard card,
        [NotNullWhen(false)] out AbstractModel? preventer,
        AutoPlayType autoPlayType)
    {
        var context = new ShouldPlayMirrorContext
        {
            Simulator = simulator,
            Card = card,
            AutoPlayType = autoPlayType
        };

        foreach (var listener in IterateCombatHookListeners(simulator))
        {
            if (!ShouldPlayMirrors.Invoke(listener, context))
            {
                preventer = listener;
                return false;
            }
        }

        preventer = null;
        return true;
    }

    /// <summary>
    /// Mirrors <see cref="Hook.ModifyEnergyCostInCombat"/>.
    /// </summary>
    public static decimal ModifyEnergyCostInCombat(
        CombatPredictionSimulator simulator,
        PredictedCard card,
        decimal originalCost)
    {
        if (originalCost < 0)
        {
            return originalCost;
        }

        // 费用查询是最热的 hook 之一（每次可玩性判定都要为手里每张牌跑一遍）。这个 context
        // 调用返回后无人持有，按模拟器复用一份即可；绑在模拟器上是因为 Simulator 是 required
        // init，复用范围不能跨模拟器。取用时先摘空槽位，万一某个游戏侧 hook 递归回到这里，
        // 内层会自建一份，两层互不干扰。
        ModifyEnergyCostInCombatMirrorContext context;
        if (simulator.EnergyCostMirrorScratch is { } scratch)
        {
            simulator.EnergyCostMirrorScratch = null;
            scratch.Card = card;
            scratch.Cost = originalCost;
            context = scratch;
        }
        else
        {
            context = new ModifyEnergyCostInCombatMirrorContext
            {
                Simulator = simulator,
                Card = card,
                Cost = originalCost
            };
        }

        foreach (var listener in IterateCombatHookListeners(simulator))
        {
            context.Cost = ModifyEnergyCostInCombatMirrors.Invoke(listener, context);
        }

        foreach (var listener in IterateCombatHookListeners(simulator))
        {
            context.Cost = ModifyEnergyCostInCombatMirrors.InvokeLate(listener, context);
        }

        if (!card.Preview.IsCanonical
            && simulator.State.CombatState is ICombatPredictionPlayerCardRules rules
            && rules.AreCardsFree(card.Preview.Owner))
        {
            context.Cost = 0m;
        }

        decimal cost = context.Cost;
        simulator.EnergyCostMirrorScratch = context;
        return cost;
    }

    /// <summary>
    /// Mirrors <see cref="Hook.ModifyStarCost"/>.
    /// </summary>
    public static decimal ModifyStarCost(
        CombatPredictionSimulator simulator,
        PredictedCard card,
        decimal originalCost)
    {
        if (originalCost < 0)
        {
            return originalCost;
        }

        var context = new ModifyStarCostMirrorContext
        {
            Simulator = simulator,
            Card = card,
            Cost = originalCost
        };
        foreach (var listener in IterateCombatHookListeners(simulator))
        {
            context.Cost = ModifyStarCostMirrors.Invoke(listener, context);
        }

        return context.Cost;
    }

    /// <summary>
    /// Mirrors <see cref="Hook.ModifyCardPlayCount"/>.
    /// </summary>
    public static int ModifyCardPlayCount(
        CombatPredictionSimulator simulator,
        PredictedCard card,
        int originalPlayCount,
        Creature? target,
        out List<AbstractModel> modifiers)
    {
        var context = new ModifyCardPlayCountMirrorContext
        {
            Simulator = simulator,
            Card = card,
            Target = target,
            PlayCount = originalPlayCount
        };
        modifiers = [];

        foreach (var listener in IterateCombatHookListeners(simulator))
        {
            var previousPlayCount = context.PlayCount;
            context.PlayCount = ModifyCardPlayCountMirrors.Invoke(listener, context);
            if (context.PlayCount != previousPlayCount)
            {
                modifiers.Add(listener);
            }
        }

        return context.PlayCount;
    }

    /// <summary>
    /// Mirrors <see cref="Hook.AfterModifyingCardPlayCount"/>.
    /// </summary>
    public static void AfterModifyingCardPlayCount(
        CombatPredictionSimulator simulator,
        PredictedCard card,
        IReadOnlyList<AbstractModel> modifiers)
    {
        var context = new AfterModifyingCardPlayCountMirrorContext
        {
            Simulator = simulator,
            Card = card
        };

        foreach (var listener in IterateCombatHookListeners(simulator))
        {
            if (modifiers.Contains(listener))
            {
                ModifyCardPlayCountMirrors.InvokeAfter(listener, context);
            }
        }
    }

    /// <summary>
    /// Mirrors <see cref="Hook.ModifyCardPlayResultLocation"/>.
    /// </summary>
    public static CardLocation ModifyCardPlayResultLocation(
        CombatPredictionSimulator simulator,
        PredictedCard card,
        bool isAutoPlay,
        ResourceInfo resources,
        CardLocation originalLocation,
        out List<AbstractModel> modifiers)
    {
        var context = new ModifyCardPlayResultLocationMirrorContext
        {
            Simulator = simulator,
            Card = card,
            IsAutoPlay = isAutoPlay,
            Resources = resources,
            Location = originalLocation
        };
        modifiers = [];

        foreach (var listener in IterateCombatHookListeners(simulator))
        {
            var previousLocation = context.Location;
            context.Location = ModifyCardPlayResultLocationMirrors.Invoke(listener, context);
            if (context.Location != previousLocation)
            {
                modifiers.Add(listener);
            }
        }

        return context.Location;
    }

    // Vanilla Hook has no facade for this step. Mirrors CardModel.OnPlayWrapper's direct
    // iteration over the modifier list returned by Hook.ModifyCardPlayResultLocation.
    public static void AfterModifyingCardPlayResultLocation(
        CombatPredictionSimulator simulator,
        PredictedCard card,
        CardLocation location,
        IReadOnlyList<AbstractModel> modifiers)
    {
        var context = new AfterModifyingCardPlayResultLocationMirrorContext
        {
            Simulator = simulator,
            Card = card,
            Location = location
        };

        foreach (var modifier in modifiers)
        {
            ModifyCardPlayResultLocationMirrors.InvokeAfter(modifier, context);
        }
    }

    // Mirrors Hook.BeforeCardPlayed. Unlike the two after phases, vanilla suppresses this
    // guarded dispatch when combat was already over or ending at dispatch start.
    public static void BeforeCardPlayed(
        CombatPredictionSimulator simulator,
        PredictedCard card,
        CardPlay cardPlay)
    {
        var context = new BeforeCardPlayedMirrorContext
        {
            Simulator = simulator,
            Card = card,
            CardPlay = cardPlay
        };

        foreach (var listener in IterateCombatHookListeners(simulator))
        {
            BeforeCardPlayedMirrors.Invoke(listener, context);
        }
    }

    public static void AbortCardPlayed(CombatPredictionSimulator simulator, CardPlay cardPlay)
    {
        BeforeCardPlayedMirrors.CompleteOrAbort(simulator, cardPlay);
        AfterCardPlayedMirrors.CompleteOrAbort(simulator, cardPlay, completed: false);
    }

    // Mirrors Hook.AfterCardPlayed's ordinary pass followed by a fresh full late pass. Vanilla
    // deliberately iterates the combat state directly so a killing card can finish resolving.
    public static void AfterCardPlayed(
        CombatPredictionSimulator simulator,
        PredictedCard card,
        CardPlay cardPlay)
    {
        var context = new AfterCardPlayedMirrorContext
        {
            Simulator = simulator,
            Card = card,
            CardPlay = cardPlay
        };

        foreach (var listener in context.State.IterateHookListeners())
        {
            AfterCardPlayedMirrors.Invoke(listener, context);
        }

        foreach (var listener in context.State.IterateHookListeners())
        {
            AfterCardPlayedMirrors.InvokeLate(listener, context);
        }

        // A paired listener can be removed by the card being resolved, so it no longer appears in
        // either AfterCardPlayed pass. The pairing belongs to this completed CardPlay transaction.
        BeforeCardPlayedMirrors.CompleteOrAbort(simulator, cardPlay);
        AfterCardPlayedMirrors.CompleteOrAbort(simulator, cardPlay, completed: true);
    }

    // Mirrors Hook.AfterCurrentHpChanged.
    public static void AfterCurrentHpChanged(
        CombatPredictionSimulator simulator,
        Creature creature,
        decimal delta)
    {
        var context = new AfterCurrentHpChangedMirrorContext
        {
            Simulator = simulator,
            Creature = creature,
            Delta = delta
        };

        foreach (var listener in IterateRunHookListeners(simulator))
        {
            AfterCurrentHpChangedMirrors.Invoke(listener, context);
        }
    }

    /// <summary>
    /// Mirrors <see cref="Hook.ModifyDamage"/>.
    /// </summary>
    public static decimal ModifyDamage(
        CombatPredictionSimulator simulator,
        Creature? target,
        Creature? dealer,
        decimal damage,
        ValueProp props,
        PredictedCard? cardSource,
        CardPlay? cardPlay)
    {
        var cardModel = cardSource?.Preview;
        if (cardModel?.Enchantment is { } enchantment)
        {
            damage += enchantment.EnchantDamageAdditive(damage, props);
            damage *= enchantment.EnchantDamageMultiplicative(damage, props);
        }

        var context = new ModifyDamageMirrorContext
        {
            Simulator = simulator,
            Target = target,
            Dealer = dealer,
            Amount = damage,
            Props = props,
            CardSource = cardSource,
            CardPlay = cardPlay
        };
        foreach (var listener in IterateRunHookListeners(simulator))
        {
            context.Amount = damage;
            decimal additive = ModifyDamageMirrors.InvokeAdditive(listener, context);
            damage += additive;
            if (additive != 0
                && cardSource != null
                && simulator.IsRecordingActionRelicTriggers
                && listener is RelicModel relic)
            {
                simulator.RecordRelicTrigger(relic, $"：伤害{FormatSigned(additive)}");
            }
        }

        foreach (var listener in IterateRunHookListeners(simulator))
        {
            context.Amount = damage;
            decimal multiplier = ModifyDamageMirrors.InvokeMultiplicative(listener, context);
            damage *= multiplier;
            if (multiplier != 1
                && cardSource != null
                && simulator.IsRecordingActionRelicTriggers
                && listener is RelicModel relic)
            {
                simulator.RecordRelicTrigger(
                    relic,
                    relic is PenNib
                        ? $"×{FormatDecimal(multiplier)}"
                        : $"：伤害×{FormatDecimal(multiplier)}");
            }
        }

        var cap = decimal.MaxValue;
        foreach (var listener in IterateRunHookListeners(simulator))
        {
            cap = Math.Min(cap, listener.ModifyDamageCap(target, props, dealer, cardModel, cardPlay));
        }

        return Math.Max(0, Math.Min(damage, cap));
    }

    private static string FormatSigned(decimal value)
        => value >= 0
            ? $"+{FormatDecimal(value)}"
            : FormatDecimal(value);

    private static string FormatDecimal(decimal value)
        => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>
    /// Mirrors <see cref="Hook.ModifyHpLost"/>.
    /// </summary>
    public static decimal ModifyHpLost(
        CombatPredictionSimulator simulator,
        Creature target,
        decimal amount,
        ValueProp props,
        Creature? dealer,
        PredictedCard? cardSource,
        HpLossHookPhase phases,
        out List<AbstractModel> modifiers)
    {
        var context = new ModifyHpLostMirrorContext
        {
            Simulator = simulator,
            Target = target,
            Amount = amount,
            Props = props,
            Dealer = dealer,
            CardSource = cardSource
        };
        modifiers = [];

        if (phases.HasFlag(HpLossHookPhase.BeforeOsty))
        {
            foreach (var listener in IterateRunHookListeners(simulator))
            {
                var previousAmount = context.Amount;
                context.Amount = ModifyHpLostMirrors.InvokeBeforeOsty(listener, context);
                if (decimal.Truncate(previousAmount) != decimal.Truncate(context.Amount))
                {
                    modifiers.Add(listener);
                }
            }

            foreach (var listener in IterateRunHookListeners(simulator))
            {
                var previousAmount = context.Amount;
                context.Amount = ModifyHpLostMirrors.InvokeBeforeOstyLate(listener, context);
                if (decimal.Truncate(previousAmount) != decimal.Truncate(context.Amount))
                {
                    modifiers.Add(listener);
                }
            }
        }

        if (phases.HasFlag(HpLossHookPhase.AfterOsty))
        {
            foreach (var listener in IterateRunHookListeners(simulator))
            {
                var previousAmount = context.Amount;
                context.Amount = ModifyHpLostMirrors.InvokeAfterOsty(listener, context);
                if (decimal.Truncate(previousAmount) != decimal.Truncate(context.Amount))
                {
                    modifiers.Add(listener);
                }
            }

            foreach (var listener in IterateRunHookListeners(simulator))
            {
                var previousAmount = context.Amount;
                context.Amount = ModifyHpLostMirrors.InvokeAfterOstyLate(listener, context);
                if (decimal.Truncate(previousAmount) != decimal.Truncate(context.Amount))
                {
                    modifiers.Add(listener);
                }
            }
        }

        return context.Amount;
    }

    // Mirrors Hook.AfterDamageGiven.
    public static void AfterDamageGiven(
        CombatPredictionSimulator simulator,
        Creature target,
        DamageResult result,
        ValueProp props,
        Creature? dealer,
        PredictedCard? source)
    {
        var context = new AfterDamageGivenMirrorContext
        {
            Simulator = simulator,
            Target = target,
            Result = result,
            Props = props,
            Dealer = dealer,
            Source = source
        };

        foreach (var listener in IterateRunHookListeners(simulator))
        {
            AfterDamageGivenMirrors.Invoke(listener, context);
        }
    }

    /// <summary>
    /// Mirrors <see cref="Hook.AfterModifyingHpLostAfterOsty"/>.
    /// </summary>
    public static void AfterModifyingHpLostAfterOsty(
        CombatPredictionSimulator simulator,
        IReadOnlyList<AbstractModel> modifiers)
    {
        var context = new AfterModifyingHpLostMirrorContext { Simulator = simulator };

        foreach (var modifier in IterateRunHookListeners(simulator))
        {
            if (modifiers.Contains(modifier))
            {
                AfterModifyingHpLostAfterOstyMirrors.Invoke(modifier, context);
            }
        }
    }

    // Mirrors Hook.BeforeDamageReceived.
    public static void BeforeDamageReceived(
        CombatPredictionSimulator simulator,
        Creature target,
        decimal amount,
        ValueProp props,
        Creature? dealer,
        PredictedCard? source)
    {
        var context = new BeforeDamageReceivedMirrorContext
        {
            Simulator = simulator,
            Target = target,
            Amount = amount,
            Props = props,
            Dealer = dealer,
            Source = source
        };

        foreach (var listener in IterateRunHookListeners(simulator))
        {
            BeforeDamageReceivedMirrors.Invoke(listener, context);
        }
    }

    // Mirrors Hook.AfterDamageReceived followed by Hook.AfterDamageReceivedLate.
    public static void AfterDamageReceived(
        CombatPredictionSimulator simulator,
        Creature target,
        DamageResult result,
        ValueProp props,
        Creature? dealer,
        PredictedCard? source)
    {
        var context = new AfterDamageReceivedMirrorContext
        {
            Simulator = simulator,
            Target = target,
            Result = result,
            Props = props,
            Dealer = dealer,
            Source = source
        };

        foreach (var listener in IterateRunHookListeners(simulator))
        {
            AfterDamageReceivedMirrors.Invoke(listener, context);
        }

        foreach (var listener in IterateRunHookListeners(simulator))
        {
            AfterDamageReceivedMirrors.InvokeLate(listener, context);
        }
    }

    // Mirrors Hook.BeforeAttack.
    public static void BeforeAttack(CombatPredictionSimulator simulator, AttackCommand command)
    {
        var context = new BeforeAttackMirrorContext { Simulator = simulator, Command = command };

        foreach (var listener in IterateCombatHookListeners(simulator))
        {
            BeforeAttackMirrors.Invoke(listener, context);
        }
    }

    // Mirrors Hook.ModifyAttackHitCount with listener-to-listener result chaining.
    public static int ModifyAttackHitCount(
        CombatPredictionSimulator simulator,
        AttackCommand command,
        int originalHitCount)
    {
        var context = new ModifyAttackHitCountMirrorContext
        {
            Simulator = simulator,
            Command = command,
            HitCount = originalHitCount
        };

        foreach (var listener in IterateCombatHookListeners(simulator))
        {
            context.HitCount = ModifyAttackHitCountMirrors.Invoke(listener, context);
        }

        return context.HitCount;
    }

    // The simulator marks combat as ending as soon as a lethal hit resolves, earlier than the live
    // AttackCommand lifecycle. Dispatch directly so paired BeforeAttack state still completes.
    public static void AfterAttack(CombatPredictionSimulator simulator, AttackCommand command)
    {
        var context = new AfterAttackMirrorContext { Simulator = simulator, Command = command };

        foreach (var listener in simulator.State.IterateHookListeners())
        {
            AfterAttackMirrors.Invoke(listener, context);
        }
    }

    // Mirrors Hook.ShouldDie followed by Hook.ShouldDieLate, including first-preventer short-circuiting.
    public static bool ShouldDie(
        CombatPredictionSimulator simulator,
        Creature creature,
        [NotNullWhen(false)] out AbstractModel? preventer)
    {
        var context = new ShouldDieMirrorContext { Simulator = simulator, Creature = creature };

        foreach (var listener in IterateRunHookListeners(simulator))
        {
            if (!ShouldDieMirrors.Invoke(listener, context))
            {
                preventer = listener;
                return false;
            }
        }

        foreach (var listener in IterateRunHookListeners(simulator))
        {
            if (!ShouldDieMirrors.InvokeLate(listener, context))
            {
                preventer = listener;
                return false;
            }
        }

        preventer = null;
        return true;
    }

    // Mirrors Hook.AfterPreventingDeath's only-preventer dispatch.
    public static void AfterPreventingDeath(
        CombatPredictionSimulator simulator,
        AbstractModel preventer,
        Creature creature)
    {
        var context = new AfterPreventingDeathMirrorContext
        {
            Simulator = simulator,
            Creature = creature
        };

        if (IterateRunHookListeners(simulator).Contains(preventer))
        {
            AfterPreventingDeathMirrors.Invoke(preventer, context);
        }
    }

    // Mirrors Hook.BeforeDeath.
    public static void BeforeDeath(CombatPredictionSimulator simulator, Creature creature)
    {
        var context = new BeforeDeathMirrorContext { Simulator = simulator, Creature = creature };

        foreach (var listener in IterateRunHookListeners(simulator))
        {
            BeforeDeathMirrors.Invoke(listener, context);
        }
    }

    // Mirrors Hook.AfterDeath.
    public static void AfterDeath(
        CombatPredictionSimulator simulator,
        Creature creature,
        bool wasRemovalPrevented)
    {
        var context = new AfterDeathMirrorContext
        {
            Simulator = simulator,
            Creature = creature,
            WasRemovalPrevented = wasRemovalPrevented
        };

        foreach (var listener in IterateRunHookListeners(simulator))
        {
            AfterDeathMirrors.Invoke(listener, context);
        }
    }

    // Mirrors Hook.ModifyOrbPassiveTriggerCount.
    public static int ModifyOrbPassiveTriggerCount(
        CombatPredictionSimulator simulator,
        OrbModel orb,
        int triggerCount,
        out List<AbstractModel> modifiers)
    {
        var context = new ModifyOrbPassiveTriggerCountMirrorContext
        {
            Simulator = simulator,
            Orb = orb,
            TriggerCount = triggerCount
        };
        modifiers = [];

        foreach (var listener in IterateCombatHookListeners(simulator))
        {
            var newTriggerCount = ModifyOrbPassiveTriggerCountMirrors.Invoke(listener, context);
            if (newTriggerCount != context.TriggerCount)
            {
                context.TriggerCount = newTriggerCount;
                modifiers.Add(listener);
            }
        }

        return context.TriggerCount;
    }

    // Mirrors Hook.AfterOrbChanneled.
    public static void AfterOrbChanneled(CombatPredictionSimulator simulator, Player player, OrbModel orb)
    {
        var context = new AfterOrbChanneledMirrorContext
        {
            Simulator = simulator,
            Player = player,
            Orb = orb
        };

        foreach (var listener in IterateCombatHookListeners(simulator))
        {
            AfterOrbChanneledMirrors.Invoke(listener, context);
        }
    }

    // Mirrors Hook.AfterOrbEvoked.
    public static void AfterOrbEvoked(
        CombatPredictionSimulator simulator,
        OrbModel orb,
        IReadOnlyList<Creature> targets)
    {
        var context = new AfterOrbEvokedMirrorContext
        {
            Simulator = simulator,
            Orb = orb,
            Targets = targets
        };

        foreach (var listener in IterateCombatHookListeners(simulator))
        {
            AfterOrbEvokedMirrors.Invoke(listener, context);
        }
    }

    // Mirrors Hook.AfterAutoPostPlayPhaseEntered.
    public static void AfterAutoPostPlayPhaseEntered(CombatPredictionSimulator simulator, Player player)
    {
        var context = new AfterAutoPostPlayMirrorContext { Simulator = simulator, Player = player };

        foreach (var listener in IterateCombatHookListeners(simulator))
        {
            AfterAutoPostPlayPhaseEnteredMirrors.Invoke(listener, context);
        }
    }

    // Mirrors Hook.BeforeSideTurnEnd.
    public static void BeforeSideTurnEnd(
        CombatPredictionSimulator simulator,
        CombatSide side,
        IReadOnlyList<Creature> participants)
    {
        var context = new BeforeSideTurnEndMirrorContext
        {
            Simulator = simulator,
            Side = side,
            Participants = participants
        };

        foreach (var listener in IterateCombatHookListeners(simulator))
        {
            BeforeSideTurnEndMirrors.InvokeVeryEarly(listener, context);
        }

        foreach (var listener in IterateCombatHookListeners(simulator))
        {
            BeforeSideTurnEndMirrors.InvokeEarly(listener, context);
        }

        foreach (var listener in IterateCombatHookListeners(simulator))
        {
            BeforeSideTurnEndMirrors.Invoke(listener, context);
        }
    }

    /// <summary>
    /// Mirrors <see cref="Hook.IterateCombatHookListeners"/>.
    /// </summary>
    private static HookListenerEnumerable IterateCombatHookListeners(
        CombatPredictionSimulator simulator)
    {
        IReadOnlyList<AbstractModel> listeners = simulator.IsOverOrEnding
            ? Array.Empty<AbstractModel>()
            : simulator.State.IterateHookListeners();
        return new HookListenerEnumerable(listeners);
    }

    // IReadOnlyList<T>.GetEnumerator returns an interface enumerator and boxes List/array
    // enumerators. Hook dispatch is frequent enough for those tiny objects to become a visible
    // search allocation source, so iterate the immutable listener snapshot by index instead.
    private readonly struct HookListenerEnumerable
    {
        // 运行级监听表可能是"根牌组前缀 + 战斗监听表"的拼接视图。走视图自己的索引器意味着
        // 每个元素两次接口调用外加一次分支；这里拆成两段各自按下标推进，元素与顺序不变。
        private readonly IReadOnlyList<AbstractModel> _first;
        private readonly IReadOnlyList<AbstractModel>? _second;

        public HookListenerEnumerable(IReadOnlyList<AbstractModel> listeners)
        {
            if (listeners is ISegmentedModelList segmented)
            {
                _first = segmented.Prefix;
                _second = segmented.Suffix;
            }
            else
            {
                _first = listeners;
                _second = null;
            }
        }

        public Enumerator GetEnumerator()
            => new(_first, _second);

        public bool Contains(AbstractModel candidate)
        {
            for (int index = 0; index < _first.Count; index++)
            {
                if (EqualityComparer<AbstractModel>.Default.Equals(_first[index], candidate))
                    return true;
            }
            if (_second is null)
                return false;
            for (int index = 0; index < _second.Count; index++)
            {
                if (EqualityComparer<AbstractModel>.Default.Equals(_second[index], candidate))
                    return true;
            }
            return false;
        }

        internal struct Enumerator(
            IReadOnlyList<AbstractModel> first,
            IReadOnlyList<AbstractModel>? second)
        {
            private IReadOnlyList<AbstractModel> _segment = first;
            private IReadOnlyList<AbstractModel>? _pending = second;
            private int _index = -1;

            public AbstractModel Current => _segment[_index];

            public bool MoveNext()
            {
                int next = _index + 1;
                if (next < _segment.Count)
                {
                    _index = next;
                    return true;
                }
                if (_pending is null)
                    return false;
                _segment = _pending;
                _pending = null;
                _index = -1;
                return MoveNext();
            }
        }
    }

    /// <summary>
    /// Mirrors <see cref="MegaCrit.Sts2.Core.Runs.IRunState.IterateHookListeners"/> with the simulator's combat state.
    /// </summary>
    private static HookListenerEnumerable IterateRunHookListeners(
        CombatPredictionSimulator simulator)
    {
        var combatState = simulator.State.CombatState;
        if (combatState is ICombatPredictionHookListenerSource source)
            return new HookListenerEnumerable(source.RunHookListeners);
        return new HookListenerEnumerable(
            combatState.RunState.IterateHookListeners(combatState).ToArray());
    }
}
