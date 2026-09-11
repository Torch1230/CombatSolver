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

        foreach (var listener in IterateCombatHookListeners(simulator, MirroredHookMask.ModifyBlockAdditive))
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

        foreach (var listener in IterateCombatHookListeners(simulator, MirroredHookMask.ModifyBlockMultiplicative))
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

        foreach (var listener in IterateCombatHookListeners(simulator, MirroredHookMask.AfterModifyingBlockAmount))
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

        foreach (var listener in IterateCombatHookListeners(simulator, MirroredHookMask.BeforeBlockGained))
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

        foreach (var listener in IterateCombatHookListeners(simulator, MirroredHookMask.AfterBlockGained))
        {
            AfterBlockGainedMirrors.Invoke(listener, context);
            if (simulator.HasPendingChoice)
                return;
        }
    }

    // Mirrors Hook.AfterStarsGained.
    public static bool AfterStarsGained(
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

        foreach (var listener in IterateCombatHookListeners(simulator, MirroredHookMask.AfterStarsGained))
        {
            AfterStarsGainedMirrors.Invoke(listener, context);
            if (simulator.HasPendingChoice)
                return false;
        }
        return true;
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
            if (simulator.HasPendingChoice)
                return;
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

        foreach (var listener in IterateCombatHookListeners(simulator, MirroredHookMask.ShouldDraw))
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

        foreach (var listener in IterateCombatHookListeners(simulator, MirroredHookMask.AfterCardDrawnEarly))
        {
            AfterCardDrawnMirrors.InvokeEarly(listener, context);
            if (simulator.HasPendingChoice)
                return;
        }

        HookListenerEnumerable listeners = IterateCombatHookListeners(simulator, MirroredHookMask.AfterCardDrawn);
        foreach (var listener in listeners)
        {
            AfterCardDrawnMirrors.Invoke(listener, context);
            if (simulator.HasPendingChoice)
                return;
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

        foreach (var listener in IterateCombatHookListeners(simulator, MirroredHookMask.AfterCardExhausted))
        {
            AfterCardExhaustedMirrors.Invoke(listener, context);
            if (simulator.HasPendingChoice)
                return;
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

        foreach (var listener in IterateCombatHookListeners(simulator, MirroredHookMask.ModifyShuffleOrder))
        {
            ModifyShuffleOrderMirrors.Invoke(listener, context);
        }
    }

    // Mirrors Hook.AfterShuffle.
    public static void AfterShuffle(CombatPredictionSimulator simulator, Player player)
    {
        var context = new AfterShuffleMirrorContext { Simulator = simulator, Player = player };

        foreach (var listener in IterateCombatHookListeners(simulator, MirroredHookMask.AfterShuffle))
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

        foreach (var listener in IterateCombatHookListeners(simulator, MirroredHookMask.AfterCardDiscarded))
        {
            AfterCardDiscardedMirrors.Invoke(listener, context);
            if (simulator.HasPendingChoice)
                return;
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
        foreach (var listener in IterateCombatHookListeners(simulator, MirroredHookMask.AfterCardGeneratedForCombat))
        {
            AfterCardGeneratedForCombatMirrors.Invoke(listener, context);
            if (simulator.HasPendingChoice)
                return;
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

        foreach (var listener in IterateCombatHookListeners(simulator, MirroredHookMask.ShouldPlay))
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

        foreach (var listener in IterateCombatHookListeners(simulator, MirroredHookMask.TryModifyEnergyCostInCombat, allowPendingRead: true))
        {
            context.Cost = ModifyEnergyCostInCombatMirrors.Invoke(listener, context);
        }

        foreach (var listener in IterateCombatHookListeners(simulator, MirroredHookMask.TryModifyEnergyCostInCombatLate, allowPendingRead: true))
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
        foreach (var listener in IterateCombatHookListeners(simulator, MirroredHookMask.TryModifyStarCost))
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

        foreach (var listener in IterateCombatHookListeners(simulator, MirroredHookMask.ModifyCardPlayCount))
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

        foreach (var listener in IterateCombatHookListeners(simulator, MirroredHookMask.AfterModifyingCardPlayCount))
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

        foreach (var listener in IterateCombatHookListeners(simulator, MirroredHookMask.ModifyCardPlayResultLocation))
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

        foreach (var listener in IterateCombatHookListeners(simulator, MirroredHookMask.BeforeCardPlayed))
        {
            BeforeCardPlayedMirrors.Invoke(listener, context);
            if (simulator.HasPendingChoice)
                return;
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
            if (simulator.HasPendingChoice)
                return;
        }

        foreach (var listener in context.State.IterateHookListeners())
        {
            AfterCardPlayedMirrors.InvokeLate(listener, context);
            if (simulator.HasPendingChoice)
                return;
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

        foreach (var listener in IterateRunHookListeners(simulator, MirroredHookMask.AfterCurrentHpChanged))
        {
            AfterCurrentHpChangedMirrors.Invoke(listener, context);
            if (simulator.HasPendingChoice)
                return;
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
        foreach (var listener in IterateRunHookListeners(simulator, MirroredHookMask.ModifyDamageAdditive))
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

        foreach (var listener in IterateRunHookListeners(simulator, MirroredHookMask.ModifyDamageMultiplicative))
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
        foreach (var listener in IterateRunHookListeners(simulator, MirroredHookMask.ModifyDamageCap))
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
    /// Mirrors <see cref="Hook.ModifyUnblockedDamageTarget"/>.
    /// </summary>
    public static Creature ModifyUnblockedDamageTarget(CombatPredictionSimulator simulator, Creature target,
        decimal amount, ValueProp props, Creature? dealer)
    {
        var context = new ModifyUnblockedDamageTargetMirrorContext
            { Simulator = simulator, Target = target, Amount = amount, Props = props, Dealer = dealer };
        // Native damage redirection bypasses the ending guard and chains each listener's
        // returned recipient into the next. No Power instance is removed or recreated.
        var listeners = simulator.State.CombatState is ICombatPredictionHookListenerSource source
            ? source.MirroredHookListeners : simulator.State.IterateHookListeners();
        foreach (var listener in new HookListenerEnumerable(simulator, listeners, MirroredHookMask.ModifyUnblockedDamageTarget))
            context.Target = ModifyUnblockedDamageTargetMirrors.Invoke(listener, context);
        return context.Target;
    }

    /// <summary>Mirrors <see cref="Hook.ModifyHpLost"/>.</summary>
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
            foreach (var listener in IterateRunHookListeners(simulator, MirroredHookMask.ModifyHpLostBeforeOsty))
            {
                var previousAmount = context.Amount;
                context.Amount = ModifyHpLostMirrors.InvokeBeforeOsty(listener, context);
                if (decimal.Truncate(previousAmount) != decimal.Truncate(context.Amount))
                {
                    modifiers.Add(listener);
                }
            }

            foreach (var listener in IterateRunHookListeners(simulator, MirroredHookMask.ModifyHpLostBeforeOstyLate))
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
            foreach (var listener in IterateRunHookListeners(simulator, MirroredHookMask.ModifyHpLostAfterOsty))
            {
                var previousAmount = context.Amount;
                context.Amount = ModifyHpLostMirrors.InvokeAfterOsty(listener, context);
                if (decimal.Truncate(previousAmount) != decimal.Truncate(context.Amount))
                {
                    modifiers.Add(listener);
                }
            }

            foreach (var listener in IterateRunHookListeners(simulator, MirroredHookMask.ModifyHpLostAfterOstyLate))
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

        foreach (var listener in IterateRunHookListeners(simulator, MirroredHookMask.AfterDamageGiven))
        {
            AfterDamageGivenMirrors.Invoke(listener, context);
            if (simulator.HasPendingChoice)
                return;
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

        foreach (var listener in IterateRunHookListeners(simulator, MirroredHookMask.BeforeDamageReceived))
        {
            BeforeDamageReceivedMirrors.Invoke(listener, context);
            if (simulator.HasPendingChoice)
                return;
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

        foreach (var listener in IterateRunHookListeners(simulator, MirroredHookMask.AfterDamageReceived))
        {
            AfterDamageReceivedMirrors.Invoke(listener, context);
            if (simulator.HasPendingChoice)
                return;
        }

        foreach (var listener in IterateRunHookListeners(simulator, MirroredHookMask.AfterDamageReceivedLate))
        {
            AfterDamageReceivedMirrors.InvokeLate(listener, context);
            if (simulator.HasPendingChoice)
                return;
        }
    }

    // Mirrors Hook.BeforeAttack.
    public static void BeforeAttack(CombatPredictionSimulator simulator, AttackCommand command)
    {
        var context = new BeforeAttackMirrorContext { Simulator = simulator, Command = command };

        foreach (var listener in IterateCombatHookListeners(simulator, MirroredHookMask.BeforeAttack))
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

        foreach (var listener in IterateCombatHookListeners(simulator, MirroredHookMask.ModifyAttackHitCount))
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
        IReadOnlyList<AbstractModel> listeners = simulator.State.IterateHookListeners();
        bool completed = false;

        try
        {
            foreach (var listener in listeners)
            {
                AfterAttackMirrors.Invoke(listener, context);
                if (simulator.HasPendingChoice)
                    return;
            }
            completed = true;
        }
        finally
        {
            foreach (AbstractModel listener in listeners)
                AfterAttackMirrors.CompleteOrAbortPairedState(listener, context, completed);
        }
    }

    // Clears command-scoped BeforeAttack bookkeeping when the containing action
    // suspends. This deliberately does not record an attack, invoke ordinary
    // AfterAttack effects, or consume one-shot attack powers; replay starts again
    // from the whole-action snapshot.
    public static void AbortAttack(CombatPredictionSimulator simulator, AttackCommand command)
    {
        var context = new AfterAttackMirrorContext { Simulator = simulator, Command = command };
        foreach (AbstractModel listener in simulator.State.IterateHookListeners())
            AfterAttackMirrors.CompleteOrAbortPairedState(listener, context, completed: false);
    }

    // Mirrors Hook.ShouldDie followed by Hook.ShouldDieLate, including first-preventer short-circuiting.
    public static bool ShouldDie(
        CombatPredictionSimulator simulator,
        Creature creature,
        [NotNullWhen(false)] out AbstractModel? preventer)
    {
        var context = new ShouldDieMirrorContext { Simulator = simulator, Creature = creature };

        foreach (var listener in IterateRunHookListeners(simulator, MirroredHookMask.ShouldDie))
        {
            if (!ShouldDieMirrors.Invoke(listener, context))
            {
                preventer = listener;
                return false;
            }
        }

        foreach (var listener in IterateRunHookListeners(simulator, MirroredHookMask.ShouldDieLate))
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

        foreach (var listener in IterateRunHookListeners(simulator, MirroredHookMask.BeforeDeath))
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

        foreach (var listener in IterateRunHookListeners(simulator, MirroredHookMask.AfterDeath))
        {
            AfterDeathMirrors.Invoke(listener, context);
            if (simulator.HasPendingChoice)
                return;
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

        foreach (var listener in IterateCombatHookListeners(simulator, MirroredHookMask.AfterOrbChanneled))
        {
            AfterOrbChanneledMirrors.Invoke(listener, context);
            if (simulator.HasPendingChoice)
                return;
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

        foreach (var listener in IterateCombatHookListeners(simulator, MirroredHookMask.AfterOrbEvoked))
        {
            AfterOrbEvokedMirrors.Invoke(listener, context);
            if (simulator.HasPendingChoice)
                return;
        }
    }

    // Mirrors Hook.AfterAutoPostPlayPhaseEntered.
    public static void AfterAutoPostPlayPhaseEntered(CombatPredictionSimulator simulator, Player player)
    {
        var context = new AfterAutoPostPlayMirrorContext { Simulator = simulator, Player = player };

        foreach (var listener in IterateCombatHookListeners(simulator, MirroredHookMask.AfterAutoPostPlayPhaseEntered))
        {
            AfterAutoPostPlayPhaseEnteredMirrors.Invoke(listener, context);
            if (simulator.HasPendingChoice)
                return;
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

        foreach (var listener in IterateCombatHookListeners(simulator, MirroredHookMask.BeforeSideTurnEndVeryEarly))
        {
            BeforeSideTurnEndMirrors.InvokeVeryEarly(listener, context);
            if (simulator.HasPendingChoice)
                return;
        }

        foreach (var listener in IterateCombatHookListeners(simulator, MirroredHookMask.BeforeSideTurnEndEarly))
        {
            BeforeSideTurnEndMirrors.InvokeEarly(listener, context);
            if (simulator.HasPendingChoice)
                return;
        }

        foreach (var listener in IterateCombatHookListeners(simulator, MirroredHookMask.BeforeSideTurnEnd))
        {
            BeforeSideTurnEndMirrors.Invoke(listener, context);
            if (simulator.HasPendingChoice)
                return;
        }
    }

    /// <summary>
    /// Mirrors <see cref="Hook.IterateCombatHookListeners"/>.
    /// </summary>
    private static HookListenerEnumerable IterateCombatHookListeners(
        CombatPredictionSimulator simulator,
        MirroredHookMask mask = MirroredHookMask.All,
        bool allowPendingRead = false)
    {
        IReadOnlyList<AbstractModel> listeners = simulator.IsOverOrEnding
            ? Array.Empty<AbstractModel>()
            : simulator.State.CombatState is ICombatPredictionHookListenerSource source
                ? source.MirroredHookListeners
                : simulator.State.IterateHookListeners();
        return new HookListenerEnumerable(simulator, listeners, mask, allowPendingRead);
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

        private readonly CombatPredictionSimulator _simulator;
        private readonly MirroredHookMask _mask;
        private readonly bool _allowPendingRead;

        public HookListenerEnumerable(CombatPredictionSimulator simulator, IReadOnlyList<AbstractModel> listeners,
            MirroredHookMask mask = MirroredHookMask.All, bool allowPendingRead = false)
        {
            _simulator = simulator;
            _mask = mask;
            _allowPendingRead = allowPendingRead;
            if (listeners is MirroredHookListenerSnapshot snapshot && !snapshot.HasAny(mask))
                listeners = Array.Empty<AbstractModel>();
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
            => new(_simulator, _first, _second, _mask, _allowPendingRead);

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
            CombatPredictionSimulator simulator,
            IReadOnlyList<AbstractModel> first,
            IReadOnlyList<AbstractModel>? second,
            MirroredHookMask mask,
            bool allowPendingRead)
        {
            private IReadOnlyList<AbstractModel> _segment = first;
            private IReadOnlyList<AbstractModel>? _pending = second;
            private int _index = -1;
            private MirroredHookListenerSnapshot? _filtered = first as MirroredHookListenerSnapshot;

            public AbstractModel Current => _segment[_index];

            public bool MoveNext()
            {
                // Mirrored listeners are synchronous projections of async vanilla hooks. A
                // nested card choice is their suspension boundary: no later listener or later
                // hook phase may run until the containing action is replayed with that choice.
                // Explicit read-only cost queries still evaluate the visible pending state;
                // native card selectors do not suspend those synchronous queries.
                if (!allowPendingRead && simulator.HasPendingChoice)
                    return false;
                int next = _index + 1;
                if (_filtered is { } filtered)
                {
                    while (next < filtered.Layout.Entries.Length)
                    {
                        if ((filtered.Layout.Entries[next].Mask & mask) != 0)
                        {
                            _index = next;
                            return true;
                        }
                        next++;
                    }
                }
                else if (next < _segment.Count)
                {
                    _index = next;
                    return true;
                }
                if (_pending is null)
                    return false;
                _segment = _pending;
                _filtered = _segment as MirroredHookListenerSnapshot;
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
        CombatPredictionSimulator simulator,
        MirroredHookMask mask = MirroredHookMask.All)
    {
        var combatState = simulator.State.CombatState;
        if (combatState is ICombatPredictionHookListenerSource source)
            return new HookListenerEnumerable(simulator, source.MirroredRunHookListeners, mask);
        return new HookListenerEnumerable(
            simulator,
            combatState.RunState.IterateHookListeners(combatState).ToArray());
    }
}
