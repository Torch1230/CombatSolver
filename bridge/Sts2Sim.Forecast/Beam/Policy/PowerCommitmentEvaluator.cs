using System.Numerics;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Creatures;
using Sts2Sim.Core.Entities.Orbs;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Entities.Powers;
using Sts2Sim.Core.Hooks;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Cards;
using Sts2Sim.Core.Models.Orbs;
using Sts2Sim.Core.Models.Powers;
using Sts2Sim.Core.MonsterMoves.Intents;

namespace CombatSolver.Beam.Policy;

internal static class PowerCommitmentEvaluator
{
    internal static PowerCommitment? Update(
        CombatState childState,
        SearchNode? parent,
        PlannedAction? action,
        IReadOnlyList<ObservedCardPlay>? playedCards,
        StateEvaluator evaluator,
        int childBattleHpLost,
        int childProjectedHp,
        int childReachableHandValue,
        int childZeroCostPlayableCount,
        int childPersistentBuffValue,
        int childFutureResourceValue,
        int childLatentSetupValue)
    {
        if (parent is null)
            return null;

        PowerCommitment? commitment = parent.PowerCommitment;
        if (!evaluator.HasReachableRegisteredPower && commitment is null)
            return null;

        if (!childState.Players[0].Creature.IsAlive || childState.Engine?.IsInProgress == false)
            return null;

        int parentTurn = parent.Turn;
        int childTurn = childState.Players[0].PlayerCombatState?.TurnNumber ?? parentTurn;
        int rootTurn = evaluator.RootTurnNumber;
        int parentBattleHpLost = evaluator.BattleHpLost(parent);
        int hpDelta = Math.Max(0, childBattleHpLost - parentBattleHpLost);

        int setupGain = ComputeSetupGain(
            parent,
            childPersistentBuffValue,
            childFutureResourceValue,
            childLatentSetupValue);
        int progressEvidence = commitment is null
            ? 0
            : ComputeProgressEvidence(commitment, parent.State, childState);
        int realizedEvidence = commitment is null
            ? 0
            : ComputeRealizedEvidence(
                commitment,
                parent,
                childState,
                action,
                playedCards,
                childProjectedHp,
                childReachableHandValue,
                childZeroCostPlayableCount);

        bool attachedPower = false;
        bool appliedPriorEvidence = false;
        int playCount = playedCards?.Count ?? 0;

        if (playCount > 0)
        {
            for (int i = 0; i < playCount; i++)
            {
                ObservedCardPlay play = playedCards![i];
                if (!PowerCardValuationModels.Registry.TryGetCommitmentDescriptor(play.CardId, out PowerCommitmentDescriptor descriptor))
                    continue;
                int spentEnergy = play.IsAutoPlay ? 0 : play.EnergySpent;
                commitment = TryAttachPower(
                    commitment,
                    descriptor,
                    play.IsAutoPlay,
                    spentEnergy,
                    hpDelta,
                    setupGain,
                    progressEvidence,
                    realizedEvidence,
                    parent,
                    childState,
                    evaluator,
                    rootTurn,
                    childTurn,
                    ref appliedPriorEvidence,
                    ref attachedPower);
                setupGain = 0;
            }
        }
        else if (action is { Kind: "PlayCard", CardId: { } actionCardId }
            && PowerCardValuationModels.Registry.TryGetCommitmentDescriptor(actionCardId, out PowerCommitmentDescriptor directDescriptor))
        {
            PlayerCombatState? parentPcs = parent.State.Players[0].PlayerCombatState;
            CardModel? card = parentPcs is null
                ? null
                : ActionExecutor.FindHandCardByOccurrence(parentPcs.Hand.Cards, actionCardId, action.CardOccurrence);
            int spentEnergy = card is null
                ? 1
                : (card.CostsXEnergy ? Math.Max(0, parentPcs!.Energy) : Math.Max(0, card.EnergyCost));
            commitment = TryAttachPower(
                commitment,
                directDescriptor,
                isAutoPlay: false,
                spentEnergy,
                hpDelta,
                setupGain,
                progressEvidence,
                realizedEvidence,
                parent,
                childState,
                evaluator,
                rootTurn,
                childTurn,
                ref appliedPriorEvidence,
                ref attachedPower);
        }

        if (attachedPower)
            return commitment;
        if (commitment is null)
            return null;

        int maxTransitions = evaluator.AggressivePowerCommitment ? 3 : 2;
        return PowerCommitmentLifecycle.Advance(
            commitment,
            parentTurn,
            childTurn,
            maxTransitions,
            progressEvidence,
            realizedEvidence,
            terminal: false).Commitment;
    }

    private static PowerCommitment? TryAttachPower(
        PowerCommitment? commitment,
        in PowerCommitmentDescriptor descriptor,
        bool isAutoPlay,
        int spentEnergy,
        int hpDelta,
        int setupGain,
        int progressEvidence,
        int realizedEvidence,
        SearchNode parent,
        CombatState childState,
        StateEvaluator evaluator,
        int rootTurn,
        int childTurn,
        ref bool appliedPriorEvidence,
        ref bool attachedPower)
    {
        int investment = ComputeEnergyInvestment(
            spentEnergy,
            evaluator.TotalFloor,
            Math.Max(0, childTurn - rootTurn));
        if (!isAutoPlay && hpDelta > 0)
            investment = (int)Math.Min(int.MaxValue, (long)investment + hpDelta);

        int projectedPotential = ComputeOpeningProjectionPotential(descriptor.CardId, parent.State, childState);
        int triggerFloor = ComputeTriggerProjectionFloor(descriptor, parent.State, childState);
        bool hasTriggerEvidence = HasTriggerEvidence(descriptor, parent.State, childState);
        int immediateDefenseGain = Math.Max(0, childState.Players[0].Creature.Block - parent.State.Players[0].Creature.Block);
        int remainingEnergy = childState.Players[0].PlayerCombatState?.Energy ?? 0;

        PowerRouteAdmissionResult admission = PowerRouteAdmission.Evaluate(
            new PowerRouteAdmissionInput(
                descriptor.CardId,
                isAutoPlay,
                spentEnergy,
                remainingEnergy,
                hasTriggerEvidence,
                immediateDefenseGain,
                setupGain,
                projectedPotential,
                triggerFloor,
                investment),
            descriptor.Admission);
        if (!admission.Admitted)
            return commitment;

        attachedPower = true;
        if (commitment is null)
        {
            return PowerCommitmentLifecycle.Create(
                descriptor,
                childTurn,
                parent.Depth + 1,
                parent.State.SemanticHistory.CardsExhaustedThisCombat,
                investment,
                admission.Potential);
        }

        PowerCommitment updated = PowerCommitmentLifecycle.AddPower(
            commitment,
            descriptor,
            investment,
            admission.Potential,
            appliedPriorEvidence ? 0 : progressEvidence,
            appliedPriorEvidence ? 0 : realizedEvidence,
            childTurn);
        appliedPriorEvidence = true;
        return updated;
    }

    private static int ComputeEnergyInvestment(int spentEnergy, int totalFloor, int combatTurnOffset)
    {
        int investment = Math.Max(0, spentEnergy) * 8;
        if (combatTurnOffset > 1 || investment == 0)
            return investment;
        int numerator = totalFloor switch
        {
            <= 15 => 8,
            <= 30 => 5,
            _ => 3,
        };
        return (investment * numerator + 7) / 8;
    }

    private static int ComputeSetupGain(
        SearchNode parent,
        int childPersistentBuff,
        int childFutureResource,
        int childLatentSetup)
    {
        long gain = Math.Max(0, childPersistentBuff - parent.PersistentBuffValue);
        gain += Math.Max(0, childFutureResource - parent.FutureResourceValue);
        gain += Math.Max(0, childLatentSetup - parent.LatentSetupValue);
        return (int)Math.Min(int.MaxValue, gain);
    }

    private static int ComputeProgressEvidence(
        PowerCommitment commitment,
        CombatState parentState,
        CombatState childState)
    {
        if (!commitment.HasCard("MASTER_PLANNER"))
            return 0;
        return Math.Max(0, ComputeSlySkillValue(childState) - ComputeSlySkillValue(parentState));
    }

    private static int ComputeRealizedEvidence(
        PowerCommitment commitment,
        SearchNode parent,
        CombatState childState,
        PlannedAction? action,
        IReadOnlyList<ObservedCardPlay>? playedCards,
        int childProjectedHp,
        int childReachableHandValue,
        int childZeroCostPlayableCount)
    {
        CombatState parentState = parent.State;
        Player player = childState.Players[0];
        int parentTurn = parent.Turn;
        int childTurn = player.PlayerCombatState?.TurnNumber ?? parentTurn;

        long gain = 0;
        bool hasSpecialized = false;

        if (commitment.HasCard("AFTERIMAGE"))
        {
            hasSpecialized = true;
            if ((playedCards is { Count: > 0 } || action?.Kind == "PlayCard"))
                gain += Math.Max(1, player.Creature.Block - parentState.Players[0].Creature.Block);
        }
        if (commitment.HasCard("FOOTWORK"))
        {
            hasSpecialized = true;
            if (PlayedBlockSkill(playedCards, action, childState))
            {
                gain += Math.Max(1, player.Creature.Block - parentState.Players[0].Creature.Block);
                gain += Math.Max(0, childProjectedHp - parent.ProjectedHp);
            }
        }
        if (commitment.HasCard("WELL_LAID_PLANS"))
        {
            hasSpecialized = true;
            if (childTurn > parentTurn)
                gain += Math.Max(1, childReachableHandValue);
        }
        if (commitment.HasCard("MASTER_PLANNER"))
        {
            hasSpecialized = true;
            if (playedCards is { Count: > 0 })
            {
                for (int i = 0; i < playedCards.Count; i++)
                {
                    ObservedCardPlay play = playedCards[i];
                    if (play.IsAutoPlay && play.CardType == CardType.Skill)
                        gain += Math.Max(1, play.EnergySpent + 3);
                }
            }
        }
        if (commitment.HasCard("SPEEDSTER"))
        {
            hasSpecialized = true;
            int childOutsideDraws = childState.SemanticHistory.CountCardsDrawnOutsideHandDrawThisTurn(childState, player);
            int parentOutsideDraws = childTurn == parentTurn
                ? parentState.SemanticHistory.CountCardsDrawnOutsideHandDrawThisTurn(parentState, parentState.Players[0])
                : 0;
            int drawDelta = Math.Max(0, childOutsideDraws - parentOutsideDraws);
            if (drawDelta > 0)
            {
                int aliveEnemies = CountAliveEnemies(childState);
                gain += (long)drawDelta * 2 * Math.Max(0, aliveEnemies);
            }
        }
        if (commitment.HasCard("TOOLS_OF_THE_TRADE"))
        {
            hasSpecialized = true;
            if (childTurn > parentTurn)
                gain += Math.Max(1, childReachableHandValue);
        }

        if (!hasSpecialized)
        {
            int enemyDamage = Math.Max(0, SumAliveEnemyHp(parentState) - SumAliveEnemyHp(childState));
            gain += enemyDamage;
            gain += Math.Max(0, childReachableHandValue - parent.ReachableHandValue);
            gain += Math.Max(0, childZeroCostPlayableCount - parent.ZeroCostPlayableCount) * 4L;
            gain += Math.Max(0, childProjectedHp - parent.ProjectedHp);
        }

        return (int)Math.Min(int.MaxValue, gain);
    }

    private static bool PlayedBlockSkill(
        IReadOnlyList<ObservedCardPlay>? playedCards,
        PlannedAction? action,
        CombatState state)
    {
        if (playedCards is { Count: > 0 })
        {
            for (int i = 0; i < playedCards.Count; i++)
            {
                if (playedCards[i].CardType == CardType.Skill && IsBlockSkillId(state, playedCards[i].CardId))
                    return true;
            }
            return false;
        }
        return action is { Kind: "PlayCard", CardId: { } cardId } && IsBlockSkillId(state, cardId);
    }

    private static bool IsBlockSkillId(CombatState state, string cardId)
    {
        PlayerCombatState? pcs = state.Players[0].PlayerCombatState;
        if (pcs is null)
            return false;
        return ContainsBlockSkill(pcs.Hand.Cards, cardId)
            || ContainsBlockSkill(pcs.DrawPile.Cards, cardId)
            || ContainsBlockSkill(pcs.DiscardPile.Cards, cardId)
            || ContainsBlockSkill(pcs.ExhaustPile.Cards, cardId);

        static bool ContainsBlockSkill(IReadOnlyList<CardModel> cards, string id)
        {
            for (int i = 0; i < cards.Count; i++)
            {
                CardModel card = cards[i];
                if (card.Type == CardType.Skill && card.GainsBlock && string.Equals(card.Id.Entry, id, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
    }

    private static bool HasTriggerEvidence(
        in PowerCommitmentDescriptor descriptor,
        CombatState parentState,
        CombatState childState)
    {
        Player player = childState.Players[0];
        PlayerCombatState? pcs = player.PlayerCombatState;
        if (pcs is null)
            return false;

        int remainingTurns = EstimateRemainingTurns(childState, player, pcs, out _);
        int aliveEnemies = CountAliveEnemies(childState);
        int attackCount = CountCardType(pcs, CardType.Attack);
        int skillCount = CountCardType(pcs, CardType.Skill);
        bool hasAttack = attackCount > 0;
        bool hasBlockSkill = HasBlockSkillCard(pcs);

        return descriptor.Pool switch
        {
            PowerCardPool.Silent => SilentTriggerEvidence(descriptor.CardId, parentState, childState, player, pcs, remainingTurns, aliveEnemies, hasAttack, hasBlockSkill),
            PowerCardPool.Ironclad => IroncladTriggerEvidence(descriptor.CardId, childState, pcs, remainingTurns, aliveEnemies, attackCount, skillCount, hasAttack, hasBlockSkill),
            PowerCardPool.Defect => DefectTriggerEvidence(descriptor.CardId, childState, player, pcs, remainingTurns, hasAttack, hasBlockSkill),
            PowerCardPool.Regent => RegentTriggerEvidence(descriptor.CardId, pcs, remainingTurns, aliveEnemies, hasAttack),
            PowerCardPool.Necrobinder => NecrobinderTriggerEvidence(descriptor.CardId, childState, player, pcs, remainingTurns, aliveEnemies, hasAttack),
            PowerCardPool.Colorless => ColorlessTriggerEvidence(descriptor.CardId, childState, pcs, remainingTurns, aliveEnemies, hasAttack, skillCount > 0, hasBlockSkill),
            PowerCardPool.Fool => FoolTriggerEvidence(descriptor.CardId, childState, player, pcs, remainingTurns, aliveEnemies),
            _ => false,
        };
    }

    private static bool SilentTriggerEvidence(
        string cardId,
        CombatState parentState,
        CombatState childState,
        Player player,
        PlayerCombatState pcs,
        int remainingTurns,
        int aliveEnemies,
        bool hasAttack,
        bool hasBlockSkill)
    {
        return cardId switch
        {
            "ABRASIVE" => hasBlockSkill || childState.Players[0].Creature.CurrentHp < parentState.Players[0].Creature.CurrentHp,
            "ACCELERANT" => HasEnemyPower<PoisonPower>(childState) || HasCardInIds(pcs, "DEADLY_POISON", "BOUNCING_FLASK", "NOXIOUS_FUMES", "CORPSE_EXPLOSION", "CRIPPLING_CLOUD", "ENVENOM", "POISONED_STAB"),
            "ACCURACY" => HasShivSource(pcs),
            "AFTERIMAGE" => ConservativeReachableCardPlays(childState, player, pcs, remainingTurns) >= 5,
            "ENVENOM" => hasAttack && remainingTurns > 1,
            "FAN_OF_KNIVES" => aliveEnemies > 0,
            "FOOTWORK" => hasBlockSkill,
            "INFINITE_BLADES" => remainingTurns > 1,
            "MASTER_PLANNER" => true,
            "NOXIOUS_FUMES" => remainingTurns > 1 && aliveEnemies > 0,
            "PHANTOM_BLADES" => HasShivSource(pcs),
            "SERPENT_FORM" => ConservativeReachableCardPlays(childState, player, pcs, remainingTurns) > 0,
            "SPEEDSTER" => CountDrawTriggers(pcs) > 0,
            "TOOLS_OF_THE_TRADE" => remainingTurns > 1,
            "TRACKING" => (HasEnemyPower<WeakPower>(childState) || HasCardInIds(pcs, "NEUTRALIZE", "SUCKER_PUNCH", "LEG_SWEEP", "CRIPPLING_CLOUD")) && hasAttack,
            "WELL_LAID_PLANS" => remainingTurns > 1 && CountLiveCards(pcs) > 0,
            "WRAITH_FORM" => WraithTimingEvidence(parentState, childState, remainingTurns),
            _ => false,
        };
    }

    private static bool IroncladTriggerEvidence(
        string cardId,
        CombatState childState,
        PlayerCombatState pcs,
        int remainingTurns,
        int aliveEnemies,
        int attackCount,
        int skillCount,
        bool hasAttack,
        bool hasBlockSkill)
    {
        bool hasExhaust = CountKeywordCards(pcs, CardKeyword.Exhaust) > 0;
        bool hasVuln = HasEnemyPower<VulnerablePower>(childState) || HasCardInIds(pcs, "BASH", "SHOCKWAVE", "THUNDERCLAP", "UPPERCUT");
        return cardId switch
        {
            "AGGRESSION" => hasAttack && remainingTurns > 1,
            "BARRICADE" => hasBlockSkill,
            "CORRUPTION" => skillCount > 0,
            "CRIMSON_MANTLE" => remainingTurns > 1,
            "CRUELTY" => hasVuln && hasAttack,
            "DARK_EMBRACE" => hasExhaust && remainingTurns > 1,
            "DEMON_FORM" => hasAttack && remainingTurns > 1,
            "FEEL_NO_PAIN" => hasExhaust,
            "HELLRAISER" => HasTagCard(pcs, CardTag.Strike),
            "INFERNO" => remainingTurns > 1 && aliveEnemies > 0,
            "INFLAME" => hasAttack,
            "JUGGERNAUT" => hasBlockSkill && aliveEnemies > 0,
            "JUGGLING" => attackCount >= 3 || (hasAttack && remainingTurns > 1),
            "PYRE" => remainingTurns > 1,
            "RUPTURE" => HasCardInIds(pcs, "BLOODLETTING", "HEMOKINESIS", "BRUTALITY", "OFFERING", "COMBUST", "INFERNO"),
            "STAMPEDE" => hasAttack && remainingTurns > 1,
            "STONE_ARMOR" => remainingTurns > 1,
            "UNMOVABLE" => hasBlockSkill,
            "VICIOUS" => hasVuln && remainingTurns > 1,
            _ => false,
        };
    }

    private static bool DefectTriggerEvidence(
        string cardId,
        CombatState childState,
        Player player,
        PlayerCombatState pcs,
        int remainingTurns,
        bool hasAttack,
        bool hasBlockSkill)
    {
        int orbCount = pcs.OrbQueue.Orbs.Count;
        bool hasChannel = orbCount > 0 || HasCardInIds(pcs, "ZAP", "BALL_LIGHTNING", "COLD_SNAP", "DARKNESS", "CHAOS", "RAINBOW", "GLACIER", "COOLHEADED", "DOOM_AND_GLOOM", "TEMPEST", "FUSION", "ELECTRODYNAMICS");
        bool hasStatus = HasCardType(pcs.Hand.Cards, CardType.Status) || HasCardType(pcs.Hand.Cards, CardType.Curse) || HasCardInIds(pcs, "TURBO", "OVERCLOCK", "WILD_STRIKE", "BURN");
        return cardId switch
        {
            "BIASED_COGNITION" => orbCount > 0 || hasChannel,
            "BUFFER" => EstimateIncomingHits(childState, player.Creature) > 0,
            "BULK_UP" => hasAttack && hasBlockSkill,
            "CAPACITOR" => orbCount > 0 || hasChannel,
            "CONSUMING_SHADOW" => orbCount > 0,
            "COOLANT" => CountDistinctOrbs(pcs.OrbQueue.Orbs) > 0,
            "CREATIVE_AI" => remainingTurns > 1,
            "DEFRAGMENT" => orbCount > 0 || hasChannel,
            "ECHO_FORM" => remainingTurns > 1 && (hasAttack || hasBlockSkill),
            "FERAL" => HasZeroCostAttack(pcs),
            "HAILSTORM" => CountOrbType<FrostOrb>(pcs.OrbQueue.Orbs) > 0,
            "ITERATION" => hasStatus,
            "LOOP" => orbCount > 0,
            "MACHINE_LEARNING" => remainingTurns > 1,
            "SMOKESTACK" => hasStatus,
            "SPINNER" => remainingTurns > 1,
            "STORM" => CountCardType(pcs, CardType.Power) > 0,
            "SUBROUTINE" => CountCardType(pcs, CardType.Power) > 0,
            "THUNDER" => CountOrbType<LightningOrb>(pcs.OrbQueue.Orbs) > 0,
            "TRASH_TO_TREASURE" => hasStatus,
            _ => false,
        };
    }

    private static bool RegentTriggerEvidence(
        string cardId,
        PlayerCombatState pcs,
        int remainingTurns,
        int aliveEnemies,
        bool hasAttack)
    {
        bool hasStarCost = HasStarCostCard(pcs);
        bool hasForge = HasCardInIds(pcs, "FORGE", "SOVEREIGN_BLADE", "SOLAR_STRIKE", "BEAT_INTO_SHAPE", "SUMMON_FORTH");
        return cardId switch
        {
            "ARSENAL" => CountLiveCards(pcs) > 0 && remainingTurns > 1,
            "BLACK_HOLE" => (hasStarCost || hasForge) && aliveEnemies > 0,
            "CHILD_OF_THE_STARS" => hasStarCost,
            "FURNACE" => hasForge || remainingTurns > 1,
            "GENESIS" => remainingTurns > 1,
            "MONARCHS_GAZE" => hasAttack && aliveEnemies > 0,
            "NEUTRON_AEGIS" => remainingTurns > 1,
            "ORBIT" => remainingTurns > 1,
            "PALE_BLUE_DOT" => remainingTurns > 1 && pcs.Hand.Cards.Count > 0,
            "PARRY" => hasForge || HasCardInIds(pcs, "SOVEREIGN_BLADE"),
            "PILLAR_OF_CREATION" => CountLiveCards(pcs) > 0,
            "SEEKING_EDGE" => hasForge || remainingTurns > 1,
            "SPECTRUM_SHIFT" => remainingTurns > 1,
            "SWORD_SAGE" => hasForge || HasCardInIds(pcs, "SOVEREIGN_BLADE"),
            "THE_SEALED_THRONE" => remainingTurns > 1 && CountLiveCards(pcs) > 0,
            "TYRANNY" => remainingTurns > 1 && pcs.Hand.Cards.Count > 0,
            "VOID_FORM" => remainingTurns > 1,
            _ => false,
        };
    }

    private static bool NecrobinderTriggerEvidence(
        string cardId,
        CombatState childState,
        Player player,
        PlayerCombatState pcs,
        int remainingTurns,
        int aliveEnemies,
        bool hasAttack)
    {
        bool hasOsty = player.Osty is { IsAlive: true };
        bool hasEthereal = CountKeywordCards(pcs, CardKeyword.Ethereal) > 0;
        bool hasSoul = HasCardInIds(pcs, "SOUL", "GRAVE_WARDEN", "BONE_SHARDS", "REAP", "UNLEASH");
        return cardId switch
        {
            "CALCIFY" => hasOsty,
            "CALL_OF_THE_VOID" => remainingTurns > 1,
            "COUNTDOWN" => aliveEnemies > 0 && remainingTurns > 1,
            "DANSE_MACABRE" => remainingTurns > 1 && HasEnergyCostAtLeast(pcs, 2),
            "DEMESNE" => remainingTurns > 1,
            "DEVOUR_LIFE" => hasSoul || hasOsty,
            "FRIENDSHIP" => remainingTurns > 1 && hasAttack,
            "HAUNT" => hasSoul,
            "LETHALITY" => hasAttack,
            "NECRO_MASTERY" => hasOsty,
            "NEUROSURGE" => hasAttack || remainingTurns > 1,
            "PAGESTORM" => hasEthereal,
            "REAPER_FORM" => hasAttack,
            "SENTRY_MODE" => hasOsty || remainingTurns > 1,
            "SHROUD" => HasEnemyPower<DoomPower>(childState) || HasCardInIds(pcs, "DOOMSDAY", "DEATHS_DOOR", "CONDEMN", "BLIGHT_STRIKE"),
            "SLEIGHT_OF_FLESH" => hasAttack || HasEnemyPower<DoomPower>(childState) || HasEnemyPower<WeakPower>(childState),
            "SPIRIT_OF_ASH" => hasEthereal,
            _ => false,
        };
    }

    private static bool ColorlessTriggerEvidence(
        string cardId,
        CombatState childState,
        PlayerCombatState pcs,
        int remainingTurns,
        int aliveEnemies,
        bool hasAttack,
        bool hasSkill,
        bool hasBlockSkill)
    {
        return cardId switch
        {
            "AUTOMATION" => remainingTurns > 1,
            "CALAMITY" => hasAttack,
            "ENTROPY" => remainingTurns > 1 && pcs.Hand.Cards.Count > 0,
            "ETERNAL_ARMOR" => remainingTurns > 1,
            "FASTEN" => HasTagCard(pcs, CardTag.Defend),
            "MAYHEM" => remainingTurns > 1 && pcs.DrawPile.Cards.Count > 0,
            "NOSTALGIA" => hasAttack || hasSkill,
            "PANACHE" => pcs.Hand.Cards.Count > 0,
            "PREP_TIME" => hasAttack,
            "PROWESS" => hasAttack || hasBlockSkill,
            "ROLLING_BOULDER" => aliveEnemies > 0 && remainingTurns > 1,
            "STRATAGEM" => remainingTurns > 1,
            _ => false,
        };
    }

    private static bool FoolTriggerEvidence(
        string cardId,
        CombatState childState,
        Player player,
        PlayerCombatState pcs,
        int remainingTurns,
        int aliveEnemies)
    {
        int shoots = CountTagCards(pcs, CardTag.Shoot);
        bool ennuiActive = GetPlayerPowerAmount<EnnuiPower>(player.Creature) > 0;
        return cardId switch
        {
            "ALL_IS_WELL" => remainingTurns > 1,
            "ARCHERY" => shoots > 0 && remainingTurns > 2,
            "ECSTASY" => ennuiActive || HasCardInIds(pcs, "SADNESS"),
            "ENNUI" => remainingTurns > 1 && (remainingTurns <= 5 || GetPlayerPowerAmount<EcstasyPower>(player.Creature) > 0),
            "EXPLOSIVE_ARROWS" => shoots > 0 && aliveEnemies >= 2,
            "HIDDEN_ROOTS" => remainingTurns > 1,
            "HONING" => shoots > 0,
            "LOST_LUTE" => CountMelodyCards(pcs) >= 2,
            "LULLABY" => HasTagCard(pcs, CardTag.Wah) && HasTagCard(pcs, CardTag.Cho) && HasTagCard(pcs, CardTag.Ki),
            "MOTIF" => CountMelodyCards(pcs) >= 2,
            "MYTHIC_FORM" => remainingTurns > 2,
            "QUIVER" => shoots >= 2 && remainingTurns > 1,
            "SERRATED_ARROWS" => shoots > 0 && CountNonShootAttacks(pcs) >= 2,
            "SIMPLETON" => remainingTurns > 1 && pcs.Hand.Cards.Count <= 3,
            "SONG_PATH" => CountTagCards(pcs, CardTag.Cho) >= 2,
            "TUNE" => remainingTurns > 1 && HasTagCard(pcs, CardTag.Ki),
            "VIOLENT_CHORD" => CountTagCards(pcs, CardTag.Wah) is var wah && (wah >= 2 || (wah >= 1 && aliveEnemies >= 2)),
            "YAH" => shoots > 0 && remainingTurns > 1,
            _ => false,
        };
    }

    private static int ComputeTriggerProjectionFloor(
        in PowerCommitmentDescriptor descriptor,
        CombatState parentState,
        CombatState childState)
    {
        _ = parentState;
        Player player = childState.Players[0];
        PlayerCombatState pcs = player.PlayerCombatState!;
        int turns = Math.Max(1, EstimateRemainingTurns(childState, player, pcs, out _) - 1);
        int aliveEnemies = Math.Max(1, CountAliveEnemies(childState));
        int attacks = Math.Max(1, CountCardType(pcs, CardType.Attack));

        return descriptor.CardId switch
        {
            "FAN_OF_KNIVES" or "NOXIOUS_FUMES" => aliveEnemies,
            "FOOTWORK" or "INFINITE_BLADES" or "SERPENT_FORM" or "SPEEDSTER" or "TOOLS_OF_THE_TRADE"
                or "WELL_LAID_PLANS" or "BARRICADE" or "CORRUPTION" or "CRIMSON_MANTLE" or "DARK_EMBRACE"
                or "INFERNO" or "UNMOVABLE" or "CAPACITOR" or "CONSUMING_SHADOW" or "COOLANT"
                or "CREATIVE_AI" or "ECHO_FORM" or "LOOP" or "MACHINE_LEARNING" or "SPINNER"
                or "HAILSTORM" or "ARSENAL" or "SEEKING_EDGE" or "SPECTRUM_SHIFT" or "TYRANNY"
                or "VOID_FORM" or "CALL_OF_THE_VOID" or "DEMESNE" or "FRIENDSHIP" or "NECRO_MASTERY"
                or "NEUROSURGE" or "REAPER_FORM" or "SENTRY_MODE" or "MAYHEM" => 1,
            "DEMON_FORM" => 4 * attacks,
            "INFLAME" => 3 * attacks,
            "FEEL_NO_PAIN" => 4 * Math.Max(1, CountKeywordCards(pcs, CardKeyword.Exhaust)),
            "JUGGERNAUT" => 8 * Math.Max(1, CountBlockSkills(pcs)),
            "PYRE" => 2 * turns,
            "STONE_ARMOR" => 6 * turns,
            "BIASED_COGNITION" or "DEFRAGMENT" => Math.Max(1, GetPlayerPowerAmount<FocusPower>(player.Creature)) * Math.Max(1, pcs.OrbQueue.Orbs.Count),
            "BULK_UP" => attacks * 3,
            "FURNACE" => Math.Max(1, GetPlayerPowerAmount<FurnacePower>(player.Creature)) * turns,
            "GENESIS" => Math.Max(1, GetPlayerPowerAmount<GenesisPower>(player.Creature)) * turns,
            "NEUTRON_AEGIS" => Math.Max(1, GetPlayerPowerAmount<PlatingPower>(player.Creature)),
            "COUNTDOWN" => Math.Max(1, GetPlayerPowerAmount<CountdownPower>(player.Creature)) * turns,
            "LETHALITY" => 12,
            "ETERNAL_ARMOR" => Math.Max(1, GetPlayerPowerAmount<PlatingPower>(player.Creature)) * turns,
            "PROWESS" => Math.Max(1, GetPlayerPowerAmount<StrengthPower>(player.Creature)) * attacks,
            "ROLLING_BOULDER" => Math.Max(1, GetPlayerPowerAmount<RollingBoulderPower>(player.Creature)) * turns,
            "LULLABY" => Math.Max(1, GetPlayerPowerAmount<LullabyPower>(player.Creature)),
            _ => 0,
        };
    }

    private static int ComputeOpeningProjectionPotential(
        string cardId,
        CombatState parentState,
        CombatState childState)
    {
        Player player = childState.Players[0];
        PlayerCombatState pcs = player.PlayerCombatState!;
        int remainingTurns = EstimateRemainingTurns(childState, player, pcs, out int baseDraw);
        int futureTurns = Math.Max(0, remainingTurns - 1);
        int aliveEnemies = Math.Max(1, CountAliveEnemies(childState));
        int attacks = CountCardType(pcs, CardType.Attack);
        int shivs = CountTagCards(pcs, CardTag.Shiv);
        int skills = CountCardType(pcs, CardType.Skill);
        int blockSkills = CountBlockSkills(pcs);
        int exhausts = CountKeywordCards(pcs, CardKeyword.Exhaust);
        int buffGain = Math.Max(0, CountPositiveBuffStacks(childState) - CountPositiveBuffStacks(parentState));

        return cardId switch
        {
            "AFTERIMAGE" => ConservativeReachableCardPlays(childState, player, pcs, remainingTurns) * Math.Max(1, buffGain),
            "FOOTWORK" => Math.Max(1, blockSkills) * 2,
            "ACCURACY" => Math.Max(1, shivs) * 4,
            "FAN_OF_KNIVES" => 4 * aliveEnemies * 4,
            "INFINITE_BLADES" => futureTurns * 4,
            "PHANTOM_BLADES" => Math.Max(1, Math.Min(remainingTurns, Math.Max(1, shivs))) * 6,
            "NOXIOUS_FUMES" => futureTurns * aliveEnemies * 3,
            "ACCELERANT" => futureTurns * 4,
            "ENVENOM" => attacks * 2,
            "SERPENT_FORM" => ConservativeReachableCardPlays(childState, player, pcs, remainingTurns) * 3,
            "SPEEDSTER" => Math.Max(1, CountDrawTriggers(pcs)) * 2 * aliveEnemies,
            "TOOLS_OF_THE_TRADE" => futureTurns * Math.Max(1, baseDraw),
            "WELL_LAID_PLANS" => futureTurns * Math.Max(1, pcs.Hand.Cards.Count),
            "WRAITH_FORM" => WraithTimingEvidence(parentState, childState, remainingTurns) ? EstimateIncomingDamage(childState, player.Creature) : 0,
            "DEMON_FORM" => Math.Max(1, attacks) * futureTurns * 2,
            "INFLAME" => Math.Max(1, attacks) * 2,
            "BARRICADE" => Math.Max(1, blockSkills) * futureTurns * 3,
            "CORRUPTION" => Math.Max(1, skills) * 4,
            "DARK_EMBRACE" => Math.Max(1, exhausts) * 4,
            "FEEL_NO_PAIN" => Math.Max(1, exhausts) * 3,
            "CRUELTY" or "VICIOUS" => Math.Max(1, attacks) * 3,
            "DEFRAGMENT" or "BIASED_COGNITION" => Math.Max(1, pcs.OrbQueue.Orbs.Count) * Math.Max(1, futureTurns) * 3,
            "ECHO_FORM" => Math.Max(1, futureTurns) * 10,
            "CREATIVE_AI" or "MACHINE_LEARNING" => Math.Max(1, futureTurns) * 4,
            "ORBIT" or "AUTOMATION" or "DEMESNE" or "GENESIS" or "VOID_FORM" => Math.Max(1, futureTurns) * 6,
            _ => buffGain * 4 + futureTurns * 2,
        };
    }

    private static bool WraithTimingEvidence(CombatState parentState, CombatState childState, int remainingTurns)
    {
        int gain = GetPlayerPowerAmount<IntangiblePower>(childState.Players[0].Creature)
            - GetPlayerPowerAmount<IntangiblePower>(parentState.Players[0].Creature);
        if (gain <= 0)
            return false;
        int incoming = EstimateIncomingDamage(parentState, parentState.Players[0].Creature);
        int projectedHpBefore = parentState.Players[0].Creature.CurrentHp + parentState.Players[0].Creature.Block - incoming;
        return projectedHpBefore <= 0 || remainingTurns <= gain + 1;
    }

    private static int EstimateRemainingTurns(CombatState state, Player player, PlayerCombatState pcs, out int baseDraw)
    {
        int enemyHp = SumAliveEnemyHp(state);
        int attackValue = SumAttackValue(pcs);
        int liveCards = CountLiveCards(pcs);
        int deckSize = Math.Max(1, liveCards);
        int cycles = (int)Math.Ceiling((double)Math.Max(1, enemyHp) / Math.Max(1, attackValue));
        baseDraw = Math.Max(1, (int)Hook.ModifyHandDraw(state, player, 5m));
        return Math.Clamp(cycles * Math.Max(1, (int)Math.Ceiling(deckSize / 5d)), 1, 8);
    }

    private static int ConservativeReachableCardPlays(CombatState state, Player player, PlayerCombatState pcs, int remainingTurns)
    {
        if (remainingTurns <= 0)
            return 0;
        int affordableHand = CountAffordableCards(pcs.Hand.Cards, pcs.Energy, pcs.Hand.Cards.Count);
        int futureTurns = Math.Max(0, remainingTurns - 1);
        if (futureTurns == 0)
            return affordableHand;
        int maxEnergy = pcs.MaxEnergy;
        int baseDraw = Math.Max(1, (int)Hook.ModifyHandDraw(state, player, 5m));
        int drawCards = pcs.DrawPile.Cards.Count;
        int future = 0;
        for (int turn = 0, idx = 0; turn < futureTurns && idx < drawCards; turn++)
        {
            int take = Math.Min(baseDraw, drawCards - idx);
            future += CountAffordableDrawSlice(pcs.DrawPile.Cards, idx, take, maxEnergy);
            idx += take;
        }
        return affordableHand + future;
    }

    private static int CountAffordableCards(IReadOnlyList<CardModel> cards, int energy, int limit)
    {
        int count = 0;
        int remEnergy = Math.Max(0, energy);
        int max = Math.Min(cards.Count, Math.Max(0, limit));
        for (int i = 0; i < max && remEnergy >= 0; i++)
        {
            CardModel c = cards[i];
            if (c.HasKeyword(CardKeyword.Unplayable))
                continue;
            int cost = c.CostsXEnergy ? remEnergy : Math.Max(0, c.EnergyCost);
            if (cost == 0)
                count++;
            else if (cost <= remEnergy)
            {
                remEnergy -= cost;
                count++;
            }
        }
        return count;
    }

    private static int CountAffordableDrawSlice(IReadOnlyList<CardModel> cards, int start, int count, int energy)
    {
        int result = 0;
        int remEnergy = Math.Max(0, energy);
        int end = Math.Min(cards.Count, start + count);
        for (int i = start; i < end; i++)
        {
            CardModel c = cards[i];
            if (c.HasKeyword(CardKeyword.Unplayable))
                continue;
            int cost = c.CostsXEnergy ? remEnergy : Math.Max(0, c.EnergyCost);
            if (cost <= remEnergy)
            {
                remEnergy -= cost;
                result++;
            }
        }
        return result;
    }

    private static int ComputeSlySkillValue(CombatState state)
    {
        PlayerCombatState? pcs = state.Players[0].PlayerCombatState;
        if (pcs is null)
            return 0;
        return SumSlySkills(pcs.Hand.Cards) + SumSlySkills(pcs.DrawPile.Cards) + SumSlySkills(pcs.DiscardPile.Cards);

        static int SumSlySkills(IReadOnlyList<CardModel> cards)
        {
            int sum = 0;
            for (int i = 0; i < cards.Count; i++)
            {
                CardModel c = cards[i];
                if (c.Type == CardType.Skill && c.HasKeyword(CardKeyword.Sly))
                    sum += Math.Max(1, EstimateCardValue(c));
            }
            return sum;
        }
    }

    private static int EstimateCardValue(CardModel card)
    {
        if (card is ICardChoiceValueProvider cv && cv.CardChoiceValue > 0d)
            return (int)Math.Round(cv.CardChoiceValue);
        if (card is ICardChoiceBaseValueProvider cb && cb.CardChoiceBaseValues is { } bv)
            return Math.Max(1, (int)Math.Round(bv.Damage + bv.Block + bv.Cards * 3d + bv.Energy * 4d + bv.Stars * 3d));
        return 1;
    }

    private static int SumAttackValue(PlayerCombatState pcs) =>
        SumPileAttackValue(pcs.Hand.Cards) + SumPileAttackValue(pcs.DrawPile.Cards) + SumPileAttackValue(pcs.DiscardPile.Cards);

    private static int SumPileAttackValue(IReadOnlyList<CardModel> cards)
    {
        int sum = 0;
        for (int i = 0; i < cards.Count; i++)
        {
            if (cards[i].Type == CardType.Attack)
                sum += EstimateCardValue(cards[i]);
        }
        return sum;
    }

    private static int CountDrawTriggers(PlayerCombatState pcs) =>
        CountDrawsInPile(pcs.Hand.Cards, pcs.Hand.Cards.Count)
        + CountDrawsInPile(pcs.DrawPile.Cards, pcs.Hand.Cards.Count)
        + CountDrawsInPile(pcs.DiscardPile.Cards, pcs.Hand.Cards.Count);

    private static int CountDrawsInPile(IReadOnlyList<CardModel> cards, int handCount)
    {
        int sum = 0;
        for (int i = 0; i < cards.Count; i++)
        {
            sum += cards[i].Id.Entry switch
            {
                "DAGGER_THROW" or "ESCAPE_PLAN" or "REFLEX" => 1,
                "PREPARED" or "ADRENALINE" => 1,
                "ACROBATICS" or "BACKFLIP" or "EXPERTISE" => 2,
                "CALCULATED_GAMBLE" => Math.Max(0, handCount - 1),
                _ => 0,
            };
        }
        return sum;
    }

    private static int CountPositiveBuffStacks(CombatState state)
    {
        IReadOnlyList<PowerModel> powers = state.Players[0].Creature.Powers;
        int sum = 0;
        for (int i = 0; i < powers.Count; i++)
        {
            if (powers[i].Type == PowerType.Buff && powers[i].Amount > 0)
                sum += (int)powers[i].Amount;
        }
        return sum;
    }

    private static int SumAliveEnemyHp(CombatState state)
    {
        int sum = 0;
        for (int i = 0; i < state.Enemies.Count; i++)
        {
            Creature e = state.Enemies[i];
            if (e.IsAlive)
                sum += e.CurrentHp;
        }
        return sum;
    }

    private static int CountAliveEnemies(CombatState state)
    {
        int count = 0;
        for (int i = 0; i < state.Enemies.Count; i++)
        {
            if (state.Enemies[i].IsAlive)
                count++;
        }
        return count;
    }

    private static int CountLiveCards(PlayerCombatState pcs) =>
        pcs.Hand.Cards.Count + pcs.DrawPile.Cards.Count + pcs.DiscardPile.Cards.Count;

    private static int CountCardType(PlayerCombatState pcs, CardType type) =>
        CountTypeInPile(pcs.Hand.Cards, type) + CountTypeInPile(pcs.DrawPile.Cards, type) + CountTypeInPile(pcs.DiscardPile.Cards, type);

    private static int CountTypeInPile(IReadOnlyList<CardModel> cards, CardType type)
    {
        int count = 0;
        for (int i = 0; i < cards.Count; i++)
        {
            if (cards[i].Type == type)
                count++;
        }
        return count;
    }

    private static bool HasCardType(IReadOnlyList<CardModel> cards, CardType type)
    {
        for (int i = 0; i < cards.Count; i++)
        {
            if (cards[i].Type == type)
                return true;
        }
        return false;
    }

    private static int CountBlockSkills(PlayerCombatState pcs) =>
        CountBlockInPile(pcs.Hand.Cards) + CountBlockInPile(pcs.DrawPile.Cards) + CountBlockInPile(pcs.DiscardPile.Cards);

    private static bool HasBlockSkillCard(PlayerCombatState pcs) => CountBlockSkills(pcs) > 0;

    private static int CountBlockInPile(IReadOnlyList<CardModel> cards)
    {
        int count = 0;
        for (int i = 0; i < cards.Count; i++)
        {
            if (cards[i].Type == CardType.Skill && cards[i].GainsBlock)
                count++;
        }
        return count;
    }

    private static int CountKeywordCards(PlayerCombatState pcs, CardKeyword keyword) =>
        CountKeywordInPile(pcs.Hand.Cards, keyword) + CountKeywordInPile(pcs.DrawPile.Cards, keyword) + CountKeywordInPile(pcs.DiscardPile.Cards, keyword);

    private static int CountKeywordInPile(IReadOnlyList<CardModel> cards, CardKeyword keyword)
    {
        int count = 0;
        for (int i = 0; i < cards.Count; i++)
        {
            if (cards[i].HasKeyword(keyword))
                count++;
        }
        return count;
    }

    private static bool HasTagCard(PlayerCombatState pcs, CardTag tag) => CountTagCards(pcs, tag) > 0;

    private static int CountTagCards(PlayerCombatState pcs, CardTag tag) =>
        CountTagInPile(pcs.Hand.Cards, tag) + CountTagInPile(pcs.DrawPile.Cards, tag) + CountTagInPile(pcs.DiscardPile.Cards, tag);

    private static int CountTagInPile(IReadOnlyList<CardModel> cards, CardTag tag)
    {
        int count = 0;
        for (int i = 0; i < cards.Count; i++)
        {
            if (cards[i].Tags.Contains(tag))
                count++;
        }
        return count;
    }

    private static bool HasShivSource(PlayerCombatState pcs) =>
        HasTagCard(pcs, CardTag.Shiv)
        || HasCardInIds(pcs, "BLADE_DANCE", "CLOAK_AND_DAGGER", "INFINITE_BLADES", "FAN_OF_KNIVES", "STORM_OF_STEEL");

    private static bool HasCardInIds(PlayerCombatState pcs, params ReadOnlySpan<string> ids) =>
        HasIdInPile(pcs.Hand.Cards, ids) || HasIdInPile(pcs.DrawPile.Cards, ids) || HasIdInPile(pcs.DiscardPile.Cards, ids);

    private static bool HasIdInPile(IReadOnlyList<CardModel> cards, ReadOnlySpan<string> ids)
    {
        for (int i = 0; i < cards.Count; i++)
        {
            string entry = cards[i].Id.Entry;
            for (int k = 0; k < ids.Length; k++)
            {
                if (string.Equals(entry, ids[k], StringComparison.Ordinal))
                    return true;
            }
        }
        return false;
    }

    private static bool HasZeroCostAttack(PlayerCombatState pcs) =>
        ZeroCostAttackInPile(pcs.Hand.Cards) || ZeroCostAttackInPile(pcs.DrawPile.Cards) || ZeroCostAttackInPile(pcs.DiscardPile.Cards);

    private static bool ZeroCostAttackInPile(IReadOnlyList<CardModel> cards)
    {
        for (int i = 0; i < cards.Count; i++)
        {
            CardModel c = cards[i];
            if (c.Type == CardType.Attack && !c.CostsXEnergy && c.EnergyCost <= 0)
                return true;
        }
        return false;
    }

    private static bool HasStarCostCard(PlayerCombatState pcs) =>
        StarCostInPile(pcs.Hand.Cards) || StarCostInPile(pcs.DrawPile.Cards) || StarCostInPile(pcs.DiscardPile.Cards);

    private static bool StarCostInPile(IReadOnlyList<CardModel> cards)
    {
        for (int i = 0; i < cards.Count; i++)
        {
            if (cards[i].StarCost > 0)
                return true;
        }
        return false;
    }

    private static bool HasEnergyCostAtLeast(PlayerCombatState pcs, int minCost) =>
        EnergyAtLeastInPile(pcs.Hand.Cards, minCost) || EnergyAtLeastInPile(pcs.DrawPile.Cards, minCost) || EnergyAtLeastInPile(pcs.DiscardPile.Cards, minCost);

    private static bool EnergyAtLeastInPile(IReadOnlyList<CardModel> cards, int minCost)
    {
        for (int i = 0; i < cards.Count; i++)
        {
            if (cards[i].EnergyCost >= minCost)
                return true;
        }
        return false;
    }

    private static int CountMelodyCards(PlayerCombatState pcs) =>
        CountMelodyInPile(pcs.Hand.Cards) + CountMelodyInPile(pcs.DrawPile.Cards) + CountMelodyInPile(pcs.DiscardPile.Cards);

    private static int CountMelodyInPile(IReadOnlyList<CardModel> cards)
    {
        int count = 0;
        for (int i = 0; i < cards.Count; i++)
        {
            IReadOnlyCollection<CardTag> tags = cards[i].Tags;
            if (tags.Contains(CardTag.Wah) || tags.Contains(CardTag.Cho) || tags.Contains(CardTag.Ki))
                count++;
        }
        return count;
    }

    private static int CountNonShootAttacks(PlayerCombatState pcs) =>
        CountNonShootInPile(pcs.Hand.Cards) + CountNonShootInPile(pcs.DrawPile.Cards) + CountNonShootInPile(pcs.DiscardPile.Cards);

    private static int CountNonShootInPile(IReadOnlyList<CardModel> cards)
    {
        int count = 0;
        for (int i = 0; i < cards.Count; i++)
        {
            CardModel c = cards[i];
            if (c.Type == CardType.Attack && !c.Tags.Contains(CardTag.Shoot))
                count++;
        }
        return count;
    }

    private static bool HasEnemyPower<TPower>(CombatState state) where TPower : PowerModel
    {
        IReadOnlyList<Creature> enemies = state.Enemies;
        for (int e = 0; e < enemies.Count; e++)
        {
            Creature enemy = enemies[e];
            if (!enemy.IsAlive)
                continue;
            IReadOnlyList<PowerModel> powers = enemy.Powers;
            for (int p = 0; p < powers.Count; p++)
            {
                if (powers[p] is TPower && powers[p].Amount > 0)
                    return true;
            }
        }
        return false;
    }

    private static int GetPlayerPowerAmount<TPower>(Creature playerCreature) where TPower : PowerModel
    {
        IReadOnlyList<PowerModel> powers = playerCreature.Powers;
        int sum = 0;
        for (int i = 0; i < powers.Count; i++)
        {
            if (powers[i] is TPower && powers[i].Amount > 0)
                sum += (int)powers[i].Amount;
        }
        return sum;
    }

    private static int CountOrbType<TOrb>(IReadOnlyList<OrbModel> orbs) where TOrb : OrbModel
    {
        int count = 0;
        for (int i = 0; i < orbs.Count; i++)
        {
            if (orbs[i] is TOrb)
                count++;
        }
        return count;
    }

    private static int CountDistinctOrbs(IReadOnlyList<OrbModel> orbs)
    {
        if (orbs.Count <= 1)
            return orbs.Count;
        int mask = 0;
        for (int i = 0; i < orbs.Count; i++)
        {
            mask |= orbs[i] switch
            {
                LightningOrb => 1,
                FrostOrb => 2,
                DarkOrb => 4,
                PlasmaOrb => 8,
                GlassOrb => 16,
                _ => 32,
            };
        }
        return BitOperations.PopCount((uint)mask);
    }

    private static int EstimateIncomingHits(CombatState state, Creature playerCreature)
    {
        int hits = 0;
        for (int e = 0; e < state.Enemies.Count; e++)
        {
            Creature enemy = state.Enemies[e];
            if (!enemy.IsAlive || enemy.Monster?.NextMove?.Intents is not { } intents)
                continue;
            for (int ii = 0; ii < intents.Count; ii++)
            {
                if (intents[ii] is AttackIntent attack)
                    hits += Math.Max(0, attack.Repeats);
            }
        }
        _ = playerCreature;
        return hits;
    }

    private static int EstimateIncomingDamage(CombatState state, Creature playerCreature)
    {
        Creature[] singleTarget = [playerCreature];
        int damage = 0;
        for (int e = 0; e < state.Enemies.Count; e++)
        {
            Creature enemy = state.Enemies[e];
            if (!enemy.IsAlive || enemy.Monster?.NextMove?.Intents is not { } intents)
                continue;
            for (int ii = 0; ii < intents.Count; ii++)
            {
                if (intents[ii] is AttackIntent attack)
                    damage += Math.Max(0, attack.GetTotalDamage(singleTarget, enemy));
            }
        }
        return damage;
    }
}
