using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Extensions;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class SimulatedCombatState
{
    /// <summary>
    /// 每个回合开始都要枚举一遍参与方的遗物；用普通循环替代 SelectMany/Where/Contains，
    /// 避免迭代器、闭包与 ForkableList 的装箱枚举器分配。顺序与原实现一致：按玩家顺序、再按遗物顺序。
    /// </summary>
    private List<RelicModel> RelicsParticipatingInSideTurn(IReadOnlyList<Creature> participants)
    {
        List<RelicModel> relics = [];
        for (int playerIndex = 0; playerIndex < Players.Count; playerIndex++)
        {
            IReadOnlyList<RelicModel> owned = RelicsOf(Players[playerIndex]);
            for (int relicIndex = 0; relicIndex < owned.Count; relicIndex++)
            {
                RelicModel relic = owned[relicIndex];
                if (relic.IsMelted)
                    continue;
                Creature ownerCreature = relic.Owner.Creature;
                bool participating = false;
                for (int index = 0; index < participants.Count; index++)
                {
                    if (ReferenceEquals(participants[index], ownerCreature))
                    {
                        participating = true;
                        break;
                    }
                }
                if (participating)
                    relics.Add(relic);
            }
        }
        return relics;
    }

    public void PrepareRelicsBeforeSideTurnStart(
        CombatPredictionSimulator simulator,
        IReadOnlyList<Creature> participants)
    {
        foreach (RelicModel relic in RelicsParticipatingInSideTurn(participants))
        {
            RelicPredictionStateSupport.ResetBeforeSideTurnStart(simulator, relic);
            switch (relic)
            {
                case Pocketwatch:
                {
                    StatefulRelicState state = GetStatefulRelicState(relic);
                    SetStatefulRelicState(relic, new StatefulRelicState(0, state.Current));
                    break;
                }
                case BagOfMarbles:
                    if (GetPlayerTurnNumber(relic.Owner) <= 1)
                    {
                        foreach (Creature enemy in LivingOpponents(simulator, relic.Owner.Creature))
                            Apply<VulnerablePower>(enemy, relic.DynamicVars.Vulnerable.IntValue, relic.Owner.Creature);
                    }
                    break;
                case CrackedCore:
                    if (GetPlayerTurnNumber(relic.Owner) <= 1)
                        simulator.OrbChannel<LightningOrb>(relic.Owner, relic.DynamicVars["Lightning"].IntValue);
                    break;
                case MiniRegent:
                    SetStatefulRelicState(relic, default);
                    break;
                case RainbowRing:
                    SetStatefulRelicState(relic, default);
                    break;
                case RedMask:
                    if (GetPlayerTurnNumber(relic.Owner) <= 1)
                    {
                        foreach (Creature enemy in LivingOpponents(simulator, relic.Owner.Creature))
                            Apply<WeakPower>(enemy, relic.DynamicVars["WeakPower"].IntValue, relic.Owner.Creature);
                    }
                    break;
            }
        }
    }

    public void TriggerRelicsAfterEnergyReset(
        CombatPredictionSimulator simulator,
        Player player)
    {
        foreach (RelicModel relic in RelicsOf(player).Where(static relic => !relic.IsMelted))
        {
            switch (relic)
            {
                case ArtOfWar:
                {
                    StatefulRelicState state = GetStatefulRelicState(relic);
                    if (GetPlayerTurnNumber(player) > 1 && state.Previous == 0)
                        simulator.GainEnergy(player, relic.DynamicVars.Energy.BaseValue);
                    SetStatefulRelicState(relic, default);
                    break;
                }
                case VenerableTeaSet or FakeVenerableTeaSet:
                {
                    StatefulRelicState state = GetStatefulRelicState(relic);
                    if (state.Current != 0)
                    {
                        simulator.GainEnergy(player, relic.DynamicVars.Energy.BaseValue);
                        SetStatefulRelicState(relic, default);
                    }
                    break;
                }
            }
        }
    }

    public bool TriggerRelicsAfterPlayerTurnStart(
        CombatPredictionSimulator simulator,
        Player player,
        TurnStartChoiceCursor choices)
    {
        int turn = GetPlayerTurnNumber(player);
        foreach (RelicModel relic in RelicsOf(player).Where(static relic => !relic.IsMelted))
        {
            switch (relic)
            {
                case Bellows when turn <= 1:
                    UpgradeHand(simulator, player);
                    break;
                case BoneTea when turn <= 1:
                {
                    StatefulRelicState state = GetStatefulRelicState(relic);
                    if (state.Current > 0)
                    {
                        UpgradeHand(simulator, player);
                        SetStatefulRelicState(relic, state with { Current = state.Current - 1 });
                    }
                    break;
                }
                case BloodVial when turn <= 1:
                case FakeBloodVial when turn <= 1:
                    simulator.Heal(player.Creature, relic.DynamicVars.Heal.BaseValue);
                    break;
                case ChoicesParadox when turn <= 1:
                {
                    IReadOnlyList<PredictedCard> options = player.Character.CardPool
                        .GetUnlockedCards(player.UnlockState, _cardMultiplayerConstraint)
                        .GetDistinctForCombat(
                            player,
                            relic.DynamicVars.Cards.IntValue,
                            simulator.Rng.CombatCardGeneration,
                            _cardMultiplayerConstraint)
                        .ToArray();
                    foreach (PredictedCard option in options)
                        option.MutablePreview.AddKeyword(CardKeyword.Retain);
                    if (!TurnStartChoiceSupport.ResolveGeneratedToHand(
                            simulator,
                            this,
                            player,
                            choices,
                            relic.Id.Entry,
                            options,
                            "AFTER_PLAYER_TURN_START"))
                    {
                        return true;
                    }
                    break;
                }
                case EmotionChip value:
                    TriggerEmotionChip(simulator, value);
                    break;
                case FestivePopper when turn <= 1:
                    using (simulator.PushDamageSource(
                        CombatDamageSource.For(CombatDamageSourceKind.Relic, relic.Id.Entry)))
                    {
                        simulator.Damage(
                            LivingOpponents(simulator, player.Creature),
                            relic.DynamicVars.Damage.BaseValue,
                            ValueProp.Unpowered,
                            player.Creature);
                    }
                    break;
                case GamblingChip when turn <= 1:
                    if (!TurnStartChoiceSupport.ResolveDiscardAndDraw(
                            simulator,
                            this,
                            player,
                            choices,
                            relic.Id.Entry,
                            "AFTER_PLAYER_TURN_START"))
                    {
                        return true;
                    }
                    break;
                case MercuryHourglass:
                    using (simulator.PushDamageSource(
                        CombatDamageSource.For(CombatDamageSourceKind.Relic, relic.Id.Entry)))
                    {
                        simulator.Damage(
                            LivingOpponents(simulator, player.Creature),
                            relic.DynamicVars.Damage.BaseValue,
                            ValueProp.Unpowered,
                            player.Creature);
                    }
                    break;
                case RoyalPoison when turn <= 1:
                    simulator.Damage(
                        player.Creature,
                        relic.DynamicVars.Damage.BaseValue,
                        ValueProp.Unblockable | ValueProp.Unpowered,
                        null);
                    break;
                case MrStruggles:
                    using (simulator.PushDamageSource(
                        CombatDamageSource.For(CombatDamageSourceKind.Relic, relic.Id.Entry)))
                    {
                        simulator.Damage(
                            LivingOpponents(simulator, player.Creature),
                            turn,
                            ValueProp.Unpowered,
                            player.Creature);
                    }
                    break;
                case ToastyMittens:
                    if (!simulator.State.GetPlayerCombatState(player).Hand.IsEmpty
                        && !TurnStartChoiceSupport.Resolve(
                            simulator,
                            this,
                            player,
                            choices,
                            relic.Id.Entry,
                            PlanChoiceEffect.Exhaust,
                            1))
                    {
                        return true;
                    }
                    Apply<StrengthPower>(
                        player.Creature,
                        relic.DynamicVars.Strength.IntValue,
                        player.Creature);
                    break;
                case VexingPuzzlebox when turn <= 1:
                {
                    PredictedCard generated = player.Character.CardPool
                        .GetUnlockedCards(player.UnlockState, _cardMultiplayerConstraint)
                        .GetDistinctForCombat(
                            player,
                            1,
                            simulator.Rng.CombatCardGeneration,
                            _cardMultiplayerConstraint)
                        .First();
                    generated.SetToFreeThisTurn();
                    simulator.AddGeneratedCardsToCombat(
                        [generated],
                        PileType.Hand,
                        player,
                        CardPilePosition.Bottom,
                        CardGenerationResultKind.Random);
                    break;
                }
            }
        }
        return false;
    }

    public void TriggerRelicsAfterSideTurnStart(
        CombatPredictionSimulator simulator,
        CombatSide side,
        IReadOnlyList<Creature> participants)
    {
        foreach (RelicModel relic in RelicsParticipatingInSideTurn(participants))
        {
            int turn = GetPlayerTurnNumber(relic.Owner);
            RelicPredictionStateSupport.ResetAfterSideTurnStart(simulator, relic, turn);
            switch (relic)
            {
                case Akabeko when turn <= 1:
                    Apply<VigorPower>(relic.Owner.Creature, relic.DynamicVars["VigorPower"].IntValue, relic.Owner.Creature);
                    break;
                case BoomingConch when turn <= 1
                    && _currentRoomType == RoomType.Elite:
                    simulator.GainEnergy(relic.Owner, relic.DynamicVars.Energy.BaseValue);
                    break;
                case BigHat when turn <= 1:
                    GenerateRelicCards(
                        simulator,
                        relic,
                        relic.Owner.Character.CardPool
                            .GetUnlockedCards(relic.Owner.UnlockState, _cardMultiplayerConstraint)
                            .Where(card => card.Keywords.Contains(CardKeyword.Ethereal)),
                        relic.DynamicVars.Cards.IntValue);
                    break;
                case Bread when turn == 1:
                    simulator.LoseEnergy(relic.Owner, relic.DynamicVars["LoseEnergy"].BaseValue);
                    break;
                case Brimstone:
                    Apply<StrengthPower>(relic.Owner.Creature, relic.DynamicVars["SelfStrength"].IntValue, relic.Owner.Creature);
                    foreach (Creature enemy in LivingOpponents(simulator, relic.Owner.Creature))
                        Apply<StrengthPower>(enemy, relic.DynamicVars["EnemyStrength"].IntValue);
                    break;
                case Candelabra when turn == 2:
                case Chandelier when turn == 3:
                    simulator.GainEnergy(relic.Owner, relic.DynamicVars.Energy.BaseValue);
                    break;
                case DiamondDiadem when turn <= 1:
                    simulator.GainBlock(relic.Owner.Creature, relic.DynamicVars.Block.BaseValue, ValueProp.Unpowered);
                    Apply<BlurPower>(relic.Owner.Creature, 1, relic.Owner.Creature);
                    break;
                case DivineDestiny when turn <= 1:
                    simulator.GainStars(relic.Owner, relic.DynamicVars.Stars.BaseValue);
                    break;
                case FencingManual when turn <= 1:
                    PersistentPowerSupport.Forge(
                        simulator,
                        relic.Owner,
                        relic.DynamicVars.Forge.IntValue);
                    break;
                case FakeHappyFlower:
                case HappyFlower:
                {
                    StatefulRelicState state = GetStatefulRelicState(relic);
                    int next = (state.Current + 1) % relic.DynamicVars["Turns"].IntValue;
                    SetStatefulRelicState(relic, state with { Current = next });
                    if (next == 0)
                        simulator.GainEnergy(relic.Owner, relic.DynamicVars.Energy.BaseValue);
                    break;
                }
                case Lantern when turn <= 1:
                    simulator.GainEnergy(relic.Owner, relic.DynamicVars.Energy.BaseValue);
                    break;
                case Crossbow:
                    GenerateRelicCards(
                        simulator,
                        relic,
                        relic.Owner.Character.CardPool
                            .GetUnlockedCards(relic.Owner.UnlockState, _cardMultiplayerConstraint)
                            .Where(card => card.Type == CardType.Attack),
                        1,
                        setFreeThisTurn: true);
                    break;
                case OrangeDough when turn <= 1:
                    GenerateRelicCards(
                        simulator,
                        relic,
                        ModelDb.CardPool<ColorlessCardPool>()
                            .GetUnlockedCards(relic.Owner.UnlockState, _cardMultiplayerConstraint),
                        relic.DynamicVars.Cards.IntValue);
                    break;
                case PaelsEye:
                {
                    StatefulRelicState state = GetStatefulRelicState(relic);
                    SetStatefulRelicState(relic, state with { Previous = 1 });
                    break;
                }
                case PaelsTears:
                {
                    StatefulRelicState state = GetStatefulRelicState(relic);
                    if (state.Current != 0)
                        simulator.GainEnergy(relic.Owner, relic.DynamicVars.Energy.BaseValue);
                    break;
                }
                case PhylacteryUnbound:
                    SummonOsty(
                        simulator,
                        relic.Owner,
                        relic.DynamicVars["StartOfTurn"].IntValue);
                    break;
                case RunicCapacitor when turn <= 1:
                    simulator.AddOrbSlots(relic.Owner, relic.DynamicVars.Repeat.IntValue);
                    break;
                case Sai:
                    simulator.GainBlock(
                        relic.Owner.Creature,
                        relic.DynamicVars.Block.BaseValue,
                        ValueProp.Unpowered);
                    break;
                case SealOfGold when GetPlayerGold(relic.Owner) >= relic.DynamicVars.Gold.IntValue:
                    simulator.GainEnergy(relic.Owner, relic.DynamicVars.Energy.BaseValue);
                    LosePlayerGold(relic.Owner, relic.DynamicVars.Gold.IntValue);
                    break;
                case SymbioticVirus when turn <= 1:
                    simulator.OrbChannel<DarkOrb>(
                        relic.Owner,
                        relic.DynamicVars["Dark"].IntValue);
                    break;
                case VeryHotCocoa when turn <= 1:
                    simulator.GainEnergy(relic.Owner, relic.DynamicVars.Energy.BaseValue);
                    break;
            }
        }
    }

    private void GenerateRelicCards(
        CombatPredictionSimulator simulator,
        RelicModel relic,
        IEnumerable<CardModel> options,
        int count,
        bool setFreeThisTurn = false)
    {
        List<PredictedCard> generated = options
            .GetDistinctForCombat(
                relic.Owner,
                count,
                simulator.Rng.CombatCardGeneration,
                _cardMultiplayerConstraint)
            .ToList();
        if (setFreeThisTurn)
        {
            foreach (PredictedCard card in generated)
                card.SetToFreeThisTurn();
        }
        simulator.AddGeneratedCardsToCombat(
            generated,
            PileType.Hand,
            relic.Owner,
            CardPilePosition.Bottom,
            CardGenerationResultKind.Random);
    }

    public void PrepareRelicsBeforeSideTurnEnd(
        CombatPredictionSimulator simulator,
        IReadOnlyList<Creature> participants)
    {
        foreach (PaelsTears relic in Players
                     .SelectMany(RelicsOf)
                     .OfType<PaelsTears>()
                     .Where(relic => !relic.IsMelted && participants.Contains(relic.Owner.Creature)))
        {
            int hasEnergy = simulator.State.GetPlayerCombatState(relic.Owner).Energy > 0 ? 1 : 0;
            SetStatefulRelicState(relic, new StatefulRelicState(hasEnergy, 0));
        }
    }

    public void CompleteRelicsAfterSideTurnEnd(
        CombatPredictionSimulator simulator,
        IReadOnlyList<Creature> participants,
        int etherealExhaustCount)
    {
        foreach (ArtOfWar relic in Players
                     .SelectMany(RelicsOf)
                     .OfType<ArtOfWar>()
                     .Where(relic => !relic.IsMelted && participants.Contains(relic.Owner.Creature)))
        {
            StatefulRelicState state = GetStatefulRelicState(relic);
            SetStatefulRelicState(relic, new StatefulRelicState(0, state.Current));
        }
        TriggerRelicsAfterSideTurnEnd(simulator, participants, etherealExhaustCount);
    }

    private IReadOnlyList<Creature> LivingOpponents(
        CombatPredictionSimulator simulator,
        Creature owner)
        => (owner.Side == CombatSide.Player ? Enemies : Allies)
            .Where(creature => simulator.State.GetCreature(creature).IsAlive)
            .ToArray();

    private static void UpgradeHand(CombatPredictionSimulator simulator, Player player)
    {
        foreach (PredictedCard card in simulator.State.GetPlayerCombatState(player).Hand)
        {
            if (card.Preview.IsUpgradable)
                card.Upgrade();
        }
    }
}
