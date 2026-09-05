using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors;
using CombatSolver.Engine.InCombat.Mirrors.Hooks;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal static class CorePowerSupport
{
    internal const int TheHuntLongTermResourceValue = 30;

    public static bool ApplyCardPowers(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        PredictedCard playedCard,
        CardPlay cardPlay,
        Creature? target,
        int ownerBlockBefore,
        decimal cardBlockGained,
        int historyEntryStart,
        ISet<uint> processedEnemyDeaths)
    {
        CardModel card = playedCard.Preview;
        Creature owner = playedCard.Preview.Owner.Creature;
        bool targetDeathTriggersFatal = target == null
            || combat.EffectivePowers()
                .Where(power => power.Owner == target)
                .All(power => power.ShouldOwnerDeathTriggerFatal());
        MonologuePower[] pendingMonologues = combat.CapturePendingMonologues(owner);
        CardOnPlaySupport.Apply(
            simulator,
            combat,
            playedCard,
            cardPlay,
            target,
            processedEnemyDeaths);
        if (simulator.HasPendingChoice)
            return false;
        if (!ApplyEnemyDeathPowers(
                simulator,
                combat,
                combat.KnownEnemies,
                processedEnemyDeaths))
        {
            return false;
        }
        combat.ResolveMonologues(owner, pendingMonologues);
        combat.SynchronizePanacheState(simulator, owner);
        if (card is Armaments or IronWave or Taunt)
        {
            SimCreatureState ownerState = simulator.State.GetCreature(owner);
            if (ownerState.Block <= ownerBlockBefore)
            {
                simulator.GainBlock(owner, card.DynamicVars.Block, playedCard, cardPlay);
                if (simulator.HasPendingChoice)
                    return false;
            }
            if (card is Armaments && card.IsUpgraded)
            {
                SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(card.Owner);
                foreach (PredictedCard handCard in playerState.Hand.Cards.Where(item => item.Preview.IsUpgradable))
                    handCard.Upgrade();
            }
        }
        switch (card)
        {
            case BlightStrike when target != null:
            {
                int damage = 0;
                foreach (CombatPredictionHistoryEntry historyEntry in
                         simulator.History.EntriesFrom(historyEntryStart))
                {
                    if (historyEntry is CombatPredictionDamageReceivedEntry entry
                        && ReferenceEquals(entry.CardSource?.Original, playedCard.Original)
                        && ReferenceEquals(entry.Receiver, target))
                    {
                        damage = checked(damage + entry.Result.TotalDamage);
                    }
                }
                if (damage > 0)
                    combat.Apply<DoomPower>(target, damage, owner);
                break;
            }
            case Fisticuffs:
            {
                int block = 0;
                foreach (CombatPredictionHistoryEntry historyEntry in
                         simulator.History.EntriesFrom(historyEntryStart))
                {
                    if (historyEntry is CombatPredictionDamageReceivedEntry entry
                        && ReferenceEquals(entry.CardSource?.Original, playedCard.Original))
                    {
                        int contribution = entry.Result.TotalDamage + entry.Result.OverkillDamage;
                        block = checked(block + contribution);
                    }
                }
                if (block > 0)
                    simulator.GainBlock(owner, block, ValueProp.Move, playedCard, cardPlay);
                break;
            }
            case BeatIntoShape when target != null:
            {
                int currentHits = 0;
                foreach (CombatPredictionHistoryEntry historyEntry in
                         simulator.History.EntriesFrom(historyEntryStart))
                {
                    if (historyEntry is CombatPredictionDamageReceivedEntry entry
                        && entry.Dealer == owner
                        && entry.Receiver == target
                        && entry.Result.Props.IsPoweredAttack())
                    {
                        currentHits = checked(currentHits + 1);
                    }
                }
                int priorHits = Math.Max(
                    0,
                    combat.GetPoweredAttackHitsThisTurn(owner, target) - currentHits);
                int forge = card.DynamicVars.CalculationBase.IntValue
                    + card.DynamicVars.CalculationExtra.IntValue * priorHits;
                PersistentPowerSupport.Forge(simulator, card.Owner, forge);
                break;
            }
            case Feed when target != null && WasFatalKill(
                targetDeathTriggersFatal,
                simulator,
                playedCard,
                target,
                historyEntryStart):
            {
                int maxHpGain = card.DynamicVars.MaxHp.IntValue;
                SimCreatureState ownerState = simulator.State.GetCreature(owner);
                ownerState.SetMaxHp(ownerState.MaxHp + maxHpGain);
                simulator.Heal(owner, maxHpGain);
                break;
            }
            case HandOfGreed when target != null && WasFatalKill(
                targetDeathTriggersFatal,
                simulator,
                playedCard,
                target,
                historyEntryStart):
            {
                int gold = card.DynamicVars["Gold"].IntValue;
                combat.GainPlayerGold(card.Owner, gold);
                combat.RecordLongTermResource(gold);
                break;
            }
            case KnockoutBlow when target != null && WasCardKill(simulator, playedCard, target, historyEntryStart):
                simulator.GainStars(card.Owner, card.DynamicVars.Stars.IntValue);
                break;
            case Sunder when target != null && WasCardKill(simulator, playedCard, target, historyEntryStart):
                simulator.GainEnergy(card.Owner, card.DynamicVars.Energy.IntValue);
                break;
            case TheHunt when target != null:
            {
                if (WasFatalKill(
                        targetDeathTriggersFatal,
                        simulator,
                        playedCard,
                        target,
                        historyEntryStart))
                {
                    combat.Apply<TheHuntPower>(owner, 1, owner);
                    combat.RecordLongTermResource(TheHuntLongTermResourceValue);
                }
                break;
            }
            case ToricToughness:
            {
                ToricToughnessPower power = combat.AddPowerInstance<ToricToughnessPower>(
                    owner,
                    card.DynamicVars["Turns"].IntValue,
                    owner);
                power.SetBlock(cardBlockGained);
                break;
            }
            case Accelerant:
                combat.Apply<AccelerantPower>(owner, card.DynamicVars["Accelerant"].IntValue, owner);
                break;
            case Accuracy:
                combat.Apply<AccuracyPower>(owner, card.DynamicVars["AccuracyPower"].IntValue, owner);
                break;
            case Afterimage:
                combat.Apply<AfterimagePower>(owner, card.DynamicVars["AfterimagePower"].IntValue, owner);
                break;
            case Aggression:
                combat.Apply<AggressionPower>(owner, 1, owner);
                break;
            case Anticipate:
                combat.ApplyAnticipate(owner, card.DynamicVars.Dexterity.IntValue, owner);
                break;
            case Barricade:
                combat.Apply<BarricadePower>(owner, 1, owner);
                break;
            case BiasedCognition:
                combat.Apply<FocusPower>(owner, card.DynamicVars["FocusPower"].IntValue, owner);
                combat.Apply<BiasedCognitionPower>(
                    owner,
                    card.DynamicVars["BiasedCognitionPower"].IntValue,
                    owner);
                break;
            case MegaCrit.Sts2.Core.Models.Cards.Buffer:
                combat.Apply<BufferPower>(owner, card.DynamicVars["BufferPower"].IntValue, owner);
                break;
            case Caltrops:
                combat.Apply<ThornsPower>(owner, card.DynamicVars["ThornsPower"].IntValue, owner);
                break;
            case Capacitor:
                simulator.AddOrbSlots(card.Owner, card.DynamicVars.Repeat.IntValue);
                break;
            case Corruption:
                combat.Apply<CorruptionPower>(owner, card.DynamicVars["Power"].IntValue, owner);
                break;
            case CreativeAi:
                combat.Apply<CreativeAiPower>(owner, card.DynamicVars["CreativeAi"].IntValue, owner);
                break;
            case DarkEmbrace:
                combat.Apply<DarkEmbracePower>(owner, 1, owner);
                break;
            case Defragment:
                combat.Apply<FocusPower>(owner, card.DynamicVars["FocusPower"].IntValue, owner);
                break;
            case DodgeAndRoll:
                int blockGained = Math.Max(0, simulator.State.GetCreature(owner).Block - ownerBlockBefore);
                if (blockGained > 0)
                    combat.Apply<BlockNextTurnPower>(owner, blockGained, owner);
                break;
            case DemonForm:
                combat.Apply<DemonFormPower>(owner, card.DynamicVars["StrengthPower"].IntValue, owner);
                break;
            case EchoForm:
                combat.Apply<EchoFormPower>(owner, card.DynamicVars["EchoForm"].IntValue, owner);
                break;
            case Envenom:
                combat.Apply<EnvenomPower>(owner, card.DynamicVars["EnvenomPower"].IntValue, owner);
                break;
            case FeelNoPain:
                combat.Apply<FeelNoPainPower>(owner, card.DynamicVars["Power"].IntValue, owner);
                break;
            case Furnace:
                combat.Apply<FurnacePower>(owner, card.DynamicVars.Forge.IntValue, owner);
                break;
            case Anger:
                simulator.AddGeneratedCardToCombat(
                    playedCard.CreateClone(),
                    PileType.Discard,
                    card.Owner,
                    CardPilePosition.Bottom,
                    CardGenerationResultKind.Fixed);
                if (simulator.HasPendingChoice)
                    return false;
                combat.RecordAngerCopyGenerated();
                break;
            case BattleTrance:
                combat.Apply<NoDrawPower>(owner, 1, owner);
                break;
            case CrimsonMantle:
                combat.IncrementCrimsonMantle(owner, card.DynamicVars["CrimsonMantlePower"].IntValue);
                break;
            case Footwork:
                combat.Apply<DexterityPower>(owner, card.DynamicVars.Dexterity.IntValue, owner);
                break;
            case Inflame:
                combat.Apply<StrengthPower>(owner, card.DynamicVars["StrengthPower"].IntValue, owner);
                break;
            case Prowess:
                combat.Apply<StrengthPower>(owner, card.DynamicVars.Strength.IntValue, owner);
                combat.Apply<DexterityPower>(owner, card.DynamicVars.Dexterity.IntValue, owner);
                break;
            case Friendship:
                combat.Apply<StrengthPower>(owner, -card.DynamicVars["StrengthPower"].IntValue, owner);
                combat.Apply<FriendshipPower>(owner, card.DynamicVars.Energy.IntValue, owner);
                break;
            case Hang when target != null:
            {
                int current = combat.GetAmount<HangPower>(target);
                combat.Apply<HangPower>(target, Math.Max(2, current), owner);
                break;
            }
            case WraithForm or Apparition:
                combat.Apply<IntangiblePower>(owner, card.DynamicVars["IntangiblePower"].IntValue, owner);
                if (card is WraithForm)
                    combat.Apply<WraithFormPower>(owner, card.DynamicVars["WraithFormPower"].IntValue, owner);
                break;
            case Abrasive:
                combat.Apply<DexterityPower>(owner, card.DynamicVars.Dexterity.IntValue, owner);
                combat.Apply<ThornsPower>(owner, card.DynamicVars["ThornsPower"].IntValue, owner);
                break;
            case BulkUp:
                simulator.State.GetPlayerCombatState(card.Owner).OrbQueue.RemoveCapacity(
                    card.DynamicVars["OrbSlots"].IntValue);
                combat.Apply<StrengthPower>(owner, card.DynamicVars.Strength.IntValue, owner);
                combat.Apply<DexterityPower>(owner, card.DynamicVars.Dexterity.IntValue, owner);
                break;
            case Resonance:
                combat.Apply<StrengthPower>(owner, card.DynamicVars["StrengthPower"].IntValue, owner);
                foreach (Creature enemy in combat.HittableEnemies)
                    combat.Apply<StrengthPower>(enemy, -1, owner);
                break;
            case SharedFate when target != null:
                combat.Apply<StrengthPower>(owner, -card.DynamicVars["PlayerStrengthLoss"].IntValue, owner);
                combat.Apply<StrengthPower>(target, -card.DynamicVars["EnemyStrengthLoss"].IntValue, owner);
                break;
            case FightMe when target != null:
                combat.Apply<StrengthPower>(owner, card.DynamicVars["StrengthPower"].IntValue, owner);
                combat.Apply<StrengthPower>(target, card.DynamicVars["EnemyStrength"].IntValue, owner);
                break;
            case Dominate:
                if (target != null)
                {
                    combat.Apply<VulnerablePower>(
                        target,
                        card.DynamicVars["VulnerablePower"].IntValue,
                        owner);
                    // Dominate awaits the Vulnerable application before it reads the resulting
                    // amount. Vicious and other AfterPowerAmountChanged listeners therefore
                    // finish (including any nested draws/auto-plays) before Strength is gained.
                    PowerLifecycleSupport.ResolvePowerAmountChanges(simulator, combat);
                    if (simulator.HasPendingChoice)
                        return false;
                    combat.Apply<StrengthPower>(owner, combat.GetAmount<VulnerablePower>(target), owner);
                }
                break;
            case Malaise when target != null:
                int malaise = Hook.ModifyXValue(
                    combat,
                    card,
                    card.EnergyCost.CapturedXValue) + (card.IsUpgraded ? 1 : 0);
                combat.Apply<StrengthPower>(target, -malaise, owner);
                combat.Apply<WeakPower>(target, malaise, owner);
                break;
            case PoisonedStab when target != null:
                combat.Apply<PoisonPower>(target, card.DynamicVars.Poison.IntValue, owner);
                break;
            case LegSweep or SuckerPunch or Null or Suppress when target != null:
                combat.Apply<WeakPower>(target, card.DynamicVars.Weak.IntValue, owner);
                break;
            case Bash or BeamCell or Break or Assassinate or Tremble or Fear or Squash or Taunt when target != null:
                combat.Apply<VulnerablePower>(target, card.DynamicVars.Vulnerable.IntValue, owner);
                break;
            case KnowThyPlace or GammaBlast or FallingStar when target != null:
                combat.Apply<WeakPower>(target, card.DynamicVars.Weak.IntValue, owner);
                combat.Apply<VulnerablePower>(target, card.DynamicVars.Vulnerable.IntValue, owner);
                break;
            case Putrefy when target != null:
                int amount = card.DynamicVars["Power"].IntValue;
                combat.Apply<WeakPower>(target, amount, owner);
                combat.Apply<VulnerablePower>(target, amount, owner);
                break;
            case Uppercut when target != null:
                int uppercut = card.DynamicVars["Power"].IntValue;
                combat.Apply<WeakPower>(target, uppercut, owner);
                combat.Apply<VulnerablePower>(target, uppercut, owner);
                break;
            case Comet when target != null:
                combat.Apply<WeakPower>(target, card.DynamicVars.Weak.IntValue, owner);
                combat.Apply<VulnerablePower>(target, card.DynamicVars.Vulnerable.IntValue, owner);
                break;
            case Thunderclap or HighFive:
                ApplyAll<VulnerablePower>(combat, card, card.DynamicVars.Vulnerable.IntValue);
                break;
            case Shockwave:
                ApplyAll<WeakPower>(combat, card, card.DynamicVars["Power"].IntValue);
                ApplyAll<VulnerablePower>(combat, card, card.DynamicVars["Power"].IntValue);
                break;
            case MeteorShower:
                ApplyAll<WeakPower>(combat, card, card.DynamicVars.Weak.IntValue);
                ApplyAll<VulnerablePower>(combat, card, card.DynamicVars.Vulnerable.IntValue);
                break;
            case PiercingWail:
                foreach (Creature enemy in combat.HittableEnemies)
                    combat.ApplyPiercingWail(enemy, card.DynamicVars["StrengthLoss"].IntValue, owner);
                break;
            case RefineBlade:
                PersistentPowerSupport.Forge(simulator, card.Owner, card.DynamicVars.Forge.IntValue);
                combat.AddEnergyNextTurn(card.Owner, card.DynamicVars.Energy.IntValue);
                break;
            case Sidestep:
                combat.AddEnergyNextTurn(card.Owner, card.DynamicVars.Energy.IntValue);
                break;
        }
        if (simulator.HasPendingChoice)
            return false;
        PowerLifecycleSupport.AfterCardPlayed(
            simulator,
            combat,
            playedCard,
            historyEntryStart);
        if (simulator.HasPendingChoice)
            return false;
        PowerLifecycleSupport.ResolvePowerAmountChanges(simulator, combat);
        if (simulator.HasPendingChoice)
            return false;
        combat.NormalizeCardAfflictions(simulator);
        if (simulator.HasPendingChoice)
            return false;
        TriggeredPowerSupport.CompensateHistorySince(simulator, combat, historyEntryStart);
        if (simulator.HasPendingChoice)
            return false;
        simulator.SynchronizePowerAmountPredictionStates();
        return true;
    }

    private static bool WasFatalKill(
        bool targetDeathTriggersFatal,
        CombatPredictionSimulator simulator,
        PredictedCard playedCard,
        Creature target,
        int historyEntryStart)
        => targetDeathTriggersFatal
           && WasCardKill(simulator, playedCard, target, historyEntryStart);

    private static bool WasCardKill(
        CombatPredictionSimulator simulator,
        PredictedCard playedCard,
        Creature target,
        int historyEntryStart)
    {
        foreach (CombatPredictionHistoryEntry historyEntry in
                 simulator.History.EntriesFrom(historyEntryStart))
        {
            if (historyEntry is CombatPredictionDamageReceivedEntry entry
                && ReferenceEquals(entry.CardSource?.Original, playedCard.Original)
                && entry.Receiver == target
                && entry.Result.WasTargetKilled)
            {
                return true;
            }
        }
        return false;
    }

    public static bool TriggerPoison(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        IEnumerable<Creature> creatures)
    {
        foreach (Creature creature in creatures)
        {
            int poison = combat.GetAmount<PoisonPower>(creature);
            if (poison <= 0 || simulator.State.GetCreature(creature).IsDead)
                continue;
            int accelerant = 0;
            foreach (Creature opponent in combat.GetOpponentsOf(creature))
            {
                if (simulator.State.GetCreature(opponent).IsAlive)
                    accelerant += combat.GetAmount<AccelerantPower>(opponent);
            }
            int triggerCount = Math.Min(poison, 1 + accelerant);
            for (int trigger = 0; trigger < triggerCount; trigger++)
            {
                int current = combat.GetAmount<PoisonPower>(creature);
                using (simulator.PushDamageSource(
                    CombatDamageSource.For(CombatDamageSourceKind.Poison, nameof(PoisonPower))))
                {
                    simulator.Damage(creature, current, ValueProp.Unblockable | ValueProp.Unpowered, null);
                }
                if (simulator.HasPendingChoice)
                    return false;
                if (simulator.State.GetCreature(creature).IsDead)
                    break;
                combat.SetAmount<PoisonPower>(creature, current - 1);
            }
        }
        return true;
    }

    public static bool TriggerPlayerSideTurnEndEffects(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        IReadOnlyList<Creature> players,
        int etherealExhaustCount = 0)
    {
        combat.RestoreTemporaryStrength(players);
        combat.RestoreTemporaryDexterity();
        combat.RestoreTemporaryFocus();
        foreach (Creature player in players)
        {
            int constrict = combat.GetAmount<ConstrictPower>(player);
            if (constrict > 0 && simulator.State.GetCreature(player).IsAlive)
                simulator.Damage(player, constrict, ValueProp.Unpowered, player);
            if (simulator.HasPendingChoice)
                return false;
            if (combat.GetAmount<TangledPower>(player) > 0)
                combat.SetAmount<TangledPower>(player, 0);
            if (combat.GetAmount<RingingPower>(player) > 0)
                combat.SetAmount<RingingPower>(player, 0);
            Tick<DoubleDamagePower>(combat, player);
            PersistentPowerSupport.TriggerRitual(combat, player);
        }
        if (!EndTurnPowerSupport.TriggerRegular(
                simulator,
                combat,
                CombatSide.Player,
                players,
                etherealExhaustCount))
        {
            return false;
        }
        PowerLifecycleSupport.ResolvePowerAmountChanges(simulator, combat);
        if (simulator.HasPendingChoice)
            return false;
        TriggerTransientSideTurnEndPowers(simulator, combat, CombatSide.Player, players);
        if (!EndTurnPowerSupport.TriggerLate(simulator, combat, players))
            return false;
        combat.NormalizeCardAfflictions(simulator);
        return true;
    }

    public static void CompletePlayerEarlySideTurnEndEffects(
        SimulatedCombatState combat,
        IEnumerable<Creature> players)
    {
        foreach (Creature player in players)
        {
            int regen = combat.GetAmount<RegenPower>(player);
            if (regen > 0)
                combat.SetAmount<RegenPower>(player, regen - 1);
        }
    }

    public static bool TriggerEnemySideTurnEndEffects(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        IReadOnlyList<Creature> enemies)
    {
        EndTurnPowerSupport.TriggerVeryEarly(combat, enemies);
        EndTurnPowerSupport.TriggerEnemyDoom(simulator, combat, enemies);
        if (simulator.HasPendingChoice)
            return false;
        foreach (Creature enemy in enemies)
        {
            if (simulator.State.GetCreature(enemy).IsAlive)
            {
                int regen = combat.GetAmount<RegenPower>(enemy);
                if (regen > 0)
                {
                    simulator.Heal(enemy, regen);
                    combat.SetAmount<RegenPower>(enemy, regen - 1);
                }
                int plating = combat.GetAmount<PlatingPower>(enemy);
                if (plating > 0)
                    simulator.GainBlock(enemy, plating, ValueProp.Unpowered);
            }
            Tick<DoubleDamagePower>(combat, enemy);
            PersistentPowerSupport.TriggerRitual(combat, enemy);
            if (simulator.HasPendingChoice)
                return false;
        }
        if (!EndTurnPowerSupport.TriggerRegular(simulator, combat, CombatSide.Enemy, enemies))
            return false;
        PowerLifecycleSupport.ResolvePowerAmountChanges(simulator, combat);
        if (simulator.HasPendingChoice)
            return false;
        TriggerTransientSideTurnEndPowers(simulator, combat, CombatSide.Enemy, enemies);
        combat.RestoreTemporaryStrength(enemies);
        TickDurations(combat);
        return EndTurnPowerSupport.TriggerLate(simulator, combat, enemies);
    }

    public static bool TriggerAfterBlockCleared(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Creature owner)
    {
        int blockNextTurn = combat.GetAmount<BlockNextTurnPower>(owner);
        if (blockNextTurn > 0)
        {
            simulator.GainBlock(owner, blockNextTurn, ValueProp.Unpowered);
            if (simulator.HasPendingChoice)
                return false;
            combat.SetAmount<BlockNextTurnPower>(owner, 0);
        }

        foreach (PowerModel power in combat.EffectivePowers().ToArray())
        {
            if (power.Amount <= 0 || !ReferenceEquals(power.Owner, owner))
                continue;
            switch (power)
            {
                case SelfFormingClayPower:
                    simulator.GainBlock(owner, power.Amount, ValueProp.Unpowered);
                    if (simulator.HasPendingChoice)
                        return false;
                    combat.SetPowerAmount(power, 0);
                    break;
                case ToricToughnessPower toric:
                    simulator.GainBlock(owner, toric.DynamicVars.Block.BaseValue, ValueProp.Unpowered);
                    if (simulator.HasPendingChoice)
                        return false;
                    combat.SetPowerAmount(power, power.Amount - 1);
                    break;
            }
        }
        combat.TriggerRelicsAfterBlockCleared(simulator, owner);
        return !simulator.HasPendingChoice;
    }

    public static bool ApplyEnemyDeathPowers(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        IReadOnlyList<Creature> enemies,
        ISet<uint> processedDeaths)
    {
        List<Creature>? newlyDead = null;
        foreach (Creature enemy in enemies)
        {
            if (enemy.CombatId is not uint combatId
                || processedDeaths.Contains(combatId)
                || simulator.State.GetCreature(enemy).IsAlive)
            {
                continue;
            }
            bool steamEruptionTriggered = combat.TryTriggerSteamEruptionDeath(simulator, enemy);
            if (simulator.HasPendingChoice)
                return false;
            if (steamEruptionTriggered)
                continue;
            (newlyDead ??= []).Add(enemy);
        }
        if (newlyDead != null)
        {
            foreach (Creature dead in newlyDead)
            {
                if (dead.CombatId is uint combatId)
                    processedDeaths.Add(combatId);
                if (!DeathPowerSupport.Trigger(simulator, combat, dead))
                    return false;
                foreach (Creature player in combat.PlayerCreatures)
                {
                    ConstrictPower? constrict = combat.GetPower<ConstrictPower>(player);
                    if (constrict?.Applier == dead)
                        combat.SetAmount<ConstrictPower>(player, 0);
                    HexPower? hex = combat.GetPower<HexPower>(player);
                    if (hex?.Applier == dead)
                        combat.RemoveHexPower(simulator, player);
                    ShrinkPower? shrink = combat.GetPower<ShrinkPower>(player);
                    if (shrink?.Applier == dead)
                        combat.SetAmount<ShrinkPower>(player, 0);
                }
                foreach (MagicBombPower bomb in combat.EffectivePowers()
                             .OfType<MagicBombPower>()
                             .Where(power => ReferenceEquals(power.Applier, dead))
                             .ToArray())
                {
                    combat.SetPowerAmount(bomb, 0);
                }
                if (simulator.HasPendingChoice)
                    return false;
            }
        }
        PowerLifecycleSupport.ResolvePowerAmountChanges(simulator, combat);
        return !simulator.HasPendingChoice;
    }

    public static void FlushPlayerHandAtTurnEnd(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Player player)
    {
        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(player);
        EnchantmentLifecycleSupport.BeforeFlush(simulator, player);
        List<PredictedCard>? toFlush = null;
        if (PersistentRelicSupport.ShouldFlush(combat, player))
        {
            foreach (PredictedCard card in playerState.Hand)
            {
                if (!card.Preview.ShouldRetainThisTurn)
                    (toFlush ??= []).Add(card);
            }
        }
        if (toFlush != null)
            simulator.AddToPile(toFlush, PileType.Discard);
        combat.TriggerBookmarkAfterFlush(simulator, player);

        foreach (PredictedCard card in playerState.AllCards)
        {
            if (PredictionUtils.NeedsEndOfTurnCleanup(card.Preview))
                card.MutablePreview.EndOfTurnCleanup();
        }
    }

    public static void TickDurations(SimulatedCombatState combat)
    {
        foreach (Creature creature in combat.Creatures)
        {
            Tick<WeakPower>(combat, creature);
            Tick<VulnerablePower>(combat, creature);
            Tick<FrailPower>(combat, creature);
            Tick<IntangiblePower>(combat, creature);
            int noBlock = combat.GetAmount<NoBlockPower>(creature);
            if (noBlock > 0)
                combat.SetAmount<NoBlockPower>(creature, noBlock - 1);
        }
    }

    public static int AdjustForecastAttack(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Creature attacker,
        Creature defender,
        int baseDamage)
    {
        decimal damage = HookMirrors.ModifyDamage(
            simulator,
            defender,
            attacker,
            baseDamage,
            ValueProp.Move,
            null,
            null);
        return Math.Max(0, (int)Math.Floor(damage));
    }

    private static void ApplyAll<T>(SimulatedCombatState combat, CardModel card, int amount)
        where T : PowerModel
    {
        foreach (Creature enemy in combat.HittableEnemies)
            combat.Apply<T>(enemy, amount, card.Owner.Creature);
    }

    private static void Tick<T>(SimulatedCombatState combat, Creature creature) where T : PowerModel
        => combat.TickDuration<T>(creature);

    private static void TriggerTransientSideTurnEndPowers(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        CombatSide side,
        IEnumerable<Creature> participants)
    {
        HashSet<Creature> participantSet = participants.ToHashSet();
        foreach (Creature creature in combat.Creatures)
        {
            if (creature.Side != side && combat.GetAmount<FlameBarrierPower>(creature) > 0)
                combat.SetAmount<FlameBarrierPower>(creature, 0);
            if (!participantSet.Contains(creature))
                continue;

            Remove<BorrowedTimePower>(simulator, combat, creature);
            Remove<BurstPower>(simulator, combat, creature);
            Remove<DuplicationPower>(simulator, combat, creature);
            Remove<NoDrawPower>(simulator, combat, creature);
            Remove<NoEnergyGainPower>(simulator, combat, creature);
            Remove<OneTwoPunchPower>(simulator, combat, creature);
            Remove<RagePower>(simulator, combat, creature);
            Remove<ReboundPower>(simulator, combat, creature);
            Remove<ShadowmeldPower>(simulator, combat, creature);
            Decrement<ConquerorPower>(combat, creature);
            Decrement<RetainHandPower>(combat, creature);
        }
    }

    private static void Remove<T>(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Creature creature)
        where T : PowerModel
    {
        T? power = combat.GetPower<T>(creature);
        if (power == null || power.Amount == 0)
            return;
        simulator.StateStore.GetPowerAmount(power).Consume();
        combat.SetPowerAmount(power, 0);
    }

    private static void Decrement<T>(SimulatedCombatState combat, Creature creature) where T : PowerModel
    {
        int amount = combat.GetAmount<T>(creature);
        if (amount > 0)
            combat.SetAmount<T>(creature, amount - 1);
    }
}
