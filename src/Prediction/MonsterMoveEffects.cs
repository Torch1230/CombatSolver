using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal static partial class MonsterMoveEffects
{
    public static bool RemovesOwner(MonsterModel monster, string moveId)
        => (monster.GetType().Name, moveId) is
            ("GasBomb", "EXPLODE_MOVE") or
            ("WaterfallGiant", "EXPLODE_MOVE") or
            ("FatGremlin", "FLEE_MOVE") or
            ("ThievingHopper", "ESCAPE_MOVE");

    public static bool Supports(MonsterModel monster, string moveId)
    {
        return (monster.GetType().Name, moveId) is
            ("SludgeSpinner", "OIL_SPRAY_MOVE") or
            ("SludgeSpinner", "RAGE_MOVE") or
            ("Flyconid", "VULNERABLE_SPORES_MOVE") or
            ("Flyconid", "FRAIL_SPORES_MOVE") or
            ("FrogKnight", "FOR_THE_QUEEN") or
            ("FrogKnight", "TONGUE_LASH") or
            ("GasBomb", "EXPLODE_MOVE") or
            ("GlobeHead", "SHOCKING_SLAP") or
            ("GlobeHead", "GALVANIC_BURST") or
            ("BowlbugSilk", "TOXIC_SPIT_MOVE") or
            ("BowlbugEgg", "BITE_MOVE") or
            ("HauntedShip", "HAUNT_MOVE") or
            ("HunterKiller", "TENDERIZING_GOOP_MOVE") or
            ("Guardbot", "GUARD_MOVE") or
            ("InfestedPrism", "RADIATE_MOVE") or
            ("InfestedPrism", "PULSATE_MOVE") or
            ("KinFollower", "POWER_DANCE_MOVE") or
            ("KinPriest", "ORB_OF_FRAILTY_MOVE") or
            ("KinPriest", "ORB_OF_WEAKNESS_MOVE") or
            ("KinPriest", "RITUAL_MOVE") or
            ("KnowledgeDemon", "PONDER_MOVE" or "CURSE_OF_KNOWLEDGE_MOVE") or
            ("LagavulinMatriarch", "SLASH2_MOVE") or
            ("LagavulinMatriarch", "SOUL_SIPHON_MOVE") or
            ("LeafSlimeM", "STICKY_SHOT") or
            ("LeafSlimeS", "GOOP_MOVE") or
            ("LivingShield", "SMASH_MOVE") or
            ("Mawler", "ROAR_MOVE") or
            ("Myte", "TOXIC_MOVE") or
            ("Myte", "SUCK_MOVE") or
            ("Nibbit", "SLICE_MOVE") or
            ("Chomper", "SCREECH_MOVE") or
            ("MagiKnight", "POWER_SHIELD_MOVE") or
            ("MagiKnight", "PREP_MOVE") or
            ("MechaKnight", "FLAMETHROWER_MOVE") or
            ("MechaKnight", "WINDUP_MOVE") or
            ("PunchConstruct", "READY_MOVE") or
            ("PunchConstruct", "FAST_PUNCH_MOVE") or
            ("Seapunk", "BUBBLE_BURP_MOVE") or
            ("SewerClam", "PRESSURIZE_MOVE") or
            ("Wriggler", "WRIGGLE_MOVE") or
            ("FlailKnight", "WAR_CHANT") or
            ("Exoskeleton", "ENRAGE_MOVE") or
            ("BowlbugNectar", "BUFF_MOVE") or
            ("Nibbit", "HISS_MOVE") or
            ("CorpseSlug", "GOOP_MOVE") or
            ("SoulFysh", "SCREAM_MOVE") or
            ("TheLost", "DEBILITATING_SMOG") or
            ("CalcifiedCultist", "INCANTATION_MOVE") or
            ("CeremonialBeast", "PLOW_MOVE" or "CRUSH_MOVE") or
            ("EyeWithTeeth", "DISTRACT_MOVE") or
            ("Ovicopter", "TENDERIZER_MOVE" or "NUTRITIONAL_PASTE_MOVE") or
            ("SpinyToad", "PROTRUDING_SPIKES_MOVE" or "SPIKE_EXPLOSION_MOVE") or
            ("Stabbot", "STAB_MOVE") or
            ("ScrollOfBiting", "MORE_TEETH") or
            ("ShrinkerBeetle", "SHRINKER_MOVE") or
            ("SlitheringStrangler", "CONSTRICT" or "THWACK") or
            ("SnappingJaxfruit", "ENERGY_ORB_MOVE") or
            ("SpectralKnight", "HEX") or
            ("SoulNexus", "DRAIN_LIFE_MOVE") or
            ("SkulkingColony", "INERTIA_MOVE") or
            ("SlimedBerserker", "VOMIT_ICHOR_MOVE" or "LEECHING_HUG_MOVE") or
            ("TerrorEel", "TERROR_MOVE") or
            ("TwigSlimeM", "STICKY_SHOT_MOVE") or
            ("VineShambler", "GRASPING_VINES_MOVE") or
            ("TurretOperator", "RELOAD_MOVE") or
            ("TheAdversaryMkOne", "BARRAGE_MOVE") or
            ("TheAdversaryMkTwo", "BARRAGE_MOVE") or
            ("TheAdversaryMkThree", "BARRAGE_MOVE") or
            ("Toadpole", "SPIKEN_MOVE" or "SPIKE_SPIT_MOVE") or
            ("MysteriousKnight", "WAR_CHANT") or
            ("PhrogParasite", "INFECT_MOVE") or
            ("Vantom", "DISMEMBER_MOVE" or "PREPARE_MOVE") or
            ("TheInsatiable", "SALIVATE_MOVE") or
            ("TheObscura", "HARDENING_STRIKE_MOVE") or
            ("TheForgotten", "MIASMA") or
            ("OwlMagistrate", "JUDICIAL_FLIGHT" or "VERDICT") or
            ("CeremonialBeast", "STAMP_MOVE" or "BEAST_CRY_MOVE") or
            ("Queen", "PUPPET_STRINGS_MOVE" or "YOU_ARE_MINE_MOVE" or "BURN_BRIGHT_FOR_ME_MOVE" or "ENRAGE_MOVE") or
            ("LouseProgenitor", "WEB_CANNON_MOVE" or "CURL_AND_GROW_MOVE") or
            ("Tunneler", "BURROW_MOVE") or
            ("CubexConstruct", "CHARGE_UP_MOVE" or "REPEATER_BLAST_MOVE" or "REPEATER_BLAST_MOVE_2") or
            ("SlumberingBeetle", "ROLL_OUT_MOVE") or
            ("Crusher", "BUG_STING_MOVE" or "ADAPT_MOVE" or "GUARDED_STRIKE_MOVE") or
            ("Rocket", "CHARGE_UP_MOVE") or
            ("TrackerRubyRaider", "TRACK_MOVE") or
            ("Noisebot", "NOISE_MOVE") or
            ("SoulFysh", "BECKON_MOVE" or "GAZE_MOVE" or "FADE_MOVE") or
            ("TerrorEel", "THRASH_MOVE") or
            ("TheObscura", "SAIL_MOVE") or
            ("PhantasmalGardener", "ENLARGE_MOVE") or
            ("FuzzyWurmCrawler", "INHALE") or
            ("Axebot", "BOOT_UP_MOVE") or
            ("Axebot", "HAMMER_UPPERCUT_MOVE") or
            ("Aeonglass", "EBB_MOVE") or
            ("Aeonglass", "INCREASING_INTENSITY_MOVE") or
            ("AxeRubyRaider", "SWING_1") or
            ("AxeRubyRaider", "SWING_2") or
            ("BruteRubyRaider", "ROAR_MOVE") or
            ("BygoneEffigy", "WAKE_MOVE") or
            ("CrossbowRubyRaider", "RELOAD_MOVE") or
            ("DampCultist", "INCANTATION_MOVE") or
            ("DevotedSculptor", "FORBIDDEN_INCANTATION_MOVE") or
            ("Entomancer", "PHEROMONE_SPIT_MOVE") or
            ("FakeMerchantMonster", "ENRAGE_MOVE") or
            ("FakeMerchantMonster", "THROW_RELIC_MOVE") or
            ("Fogmog", "SWIPE_MOVE") or
            ("Fogmog", "SWIPE_RANDOM_MOVE") or
            ("FossilStalker", "TACKLE_MOVE") or
            ("WaterfallGiant", "PRESSURIZE_MOVE") or
            ("WaterfallGiant", "STOMP_MOVE") or
            ("WaterfallGiant", "RAM_MOVE") or
            ("WaterfallGiant", "SIPHON_MOVE") or
            ("WaterfallGiant", "PRESSURE_GUN_MOVE") or
            ("WaterfallGiant", "PRESSURE_UP_MOVE") or
            ("WaterfallGiant", "ABOUT_TO_BLOW_MOVE") or
            ("WaterfallGiant", "EXPLODE_MOVE") or
            ("DecimillipedeSegmentBack", "BULK_MOVE" or "CONSTRICT_MOVE" or "DEAD_MOVE") or
            ("DecimillipedeSegmentFront", "BULK_MOVE" or "CONSTRICT_MOVE" or "DEAD_MOVE") or
            ("DecimillipedeSegmentMiddle", "BULK_MOVE" or "CONSTRICT_MOVE" or "DEAD_MOVE") or
            ("GremlinMerc", "GIMME_MOVE" or "DOUBLE_SMASH_MOVE" or "HEHE_MOVE") or
            ("LivingFog", "ADVANCED_GAS_MOVE") or
            ("TheInsatiable", "LIQUIFY_GROUND_MOVE") or
            ("ThievingHopper", "FLUTTER_MOVE") or
            ("TwoTailedRat", "SCREECH_MOVE" or "CALL_FOR_BACKUP_MOVE") or
            ("Fabricator", "FABRICATE_MOVE" or "FABRICATING_STRIKE_MOVE") or
            ("FatGremlin", "FLEE_MOVE") or
            ("Fogmog", "ILLUSION_MOVE") or
            ("LivingFog", "BLOAT_MOVE") or
            ("Ovicopter", "LAY_EGGS_MOVE") or
            ("TheObscura", "ILLUSION_MOVE") or
            ("ThievingHopper", "THIEVERY_MOVE" or "ESCAPE_MOVE") or
            ("Parafright", "REVIVE_MOVE") or
            ("ToughEgg", "HATCH_MOVE") or
            ("FrogKnight", "BEETLE_CHARGE") or
            ("MagiKnight", "DAMPEN_MOVE") or
            ("TestSubject", "RESPAWN_MOVE" or "SKULL_BASH_MOVE" or "MULTI_CLAW_MOVE"
                or "PHASE3_LACERATE_MOVE" or "BIG_POUNCE" or "BURNING_GROWL_MOVE") or
            ("DecimillipedeSegmentBack", "REATTACH_MOVE") or
            ("DecimillipedeSegmentFront", "REATTACH_MOVE") or
            ("DecimillipedeSegmentMiddle", "REATTACH_MOVE");
    }

    public static void ApplyBeforeAttack(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player)
    {
        if (move.Owner.Monster is LivingFog && move.Move.Id == "BLOAT_MOVE")
        {
            int count = combat.GetMonsterStaticInt(move.Owner, "BloatAmount");
            for (int index = 0; index < count; index++)
            {
                string? slot = MonsterSpawnSupport.NextSlot(combat);
                if (string.IsNullOrEmpty(slot))
                    break;
                MonsterSpawnSupport.Spawn<GasBomb>(simulator, combat, move.Owner, slot);
            }
        }

        if (move.Owner.Monster?.GetType().Name != "ThievingHopper"
            || move.Move.Id != "THIEVERY_MOVE")
        {
            return;
        }

        Func<PredictedCard, bool>[] priorities =
        [
            card => card.Preview.Enchantment is not Imbued && card.Preview.Rarity == CardRarity.Uncommon,
            card => card.Preview.Enchantment is not Imbued
                && card.Preview.Rarity is CardRarity.Common or CardRarity.Rare or CardRarity.Event,
            card => card.Preview.Enchantment is not Imbued
                && card.Preview.Rarity is CardRarity.Basic or CardRarity.Quest,
            card => card.Preview.Rarity == CardRarity.Ancient || card.Preview.Enchantment is Imbued,
        ];
        List<(Creature Target, PredictedCard Card)> stolenCards = [];
        foreach (var member in combat.Players)
        {
            if (!simulator.State.GetCreature(member.Creature).IsAlive)
                continue;
            SimPlayerCombatState state = simulator.State.GetPlayerCombatState(member);
            List<PredictedCard> cards = state.DrawPile.Cards
                .Concat(state.DiscardPile.Cards)
                .Where(card => card.Preview.DeckVersion != null)
                .ToList();
            if (cards.Count == 0)
                continue;

            IEnumerable<PredictedCard> candidates = cards;
            foreach (Func<PredictedCard, bool> priority in priorities)
            {
                PredictedCard[] preferred = cards.Where(priority).ToArray();
                if (preferred.Length == 0)
                    continue;
                candidates = preferred;
                break;
            }

            PredictedCard stolen = simulator.Rng.CombatCardGeneration.NextItem(candidates)
                ?? throw new InvalidOperationException("飞贼的偷牌候选非空但没有选中牌。");
            simulator.RemoveFromCombat(stolen);
            stolenCards.Add((member.Creature, stolen));
        }

        foreach ((Creature target, PredictedCard stolen) in stolenCards)
        {
            combat.RecordStolenCard(simulator);
            SwipePower swipe = combat.AddPowerInstance<SwipePower>(move.Owner, 1, move.Owner);
            swipe._target = target;
            swipe.StolenCard = stolen.Preview;
        }
    }

    public static bool Apply(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        out bool killedOwner,
        IReadOnlyList<PlanCardChoice>? plannedChoices = null)
    {
        killedOwner = false;
        MonsterModel monster = move.Owner.Monster!;
        string type = monster.GetType().Name;
        string id = move.Move.Id;

        combat.ResolveReviveMove(simulator, move.Owner, id);
        if (id == "STUNNED")
        {
            switch (monster)
            {
                case LagavulinMatriarch:
                case SlumberingBeetle:
                    combat.SetMonsterBool(move.Owner, "_isAwake", true);
                    combat.SetAmount<PlatingPower>(move.Owner, 0);
                    break;
                case Tunneler:
                    combat.SetMonsterBool(move.Owner, "_isStunned", false);
                    break;
            }
        }

        if (type == "FrogKnight" && id == "BEETLE_CHARGE")
            combat.SetMonsterBool(move.Owner, "_hasBeetleCharged", true);
        if (type == "TwoTailedRat" && id is "SCRATCH_MOVE" or "DISEASE_BITE_MOVE" or "SCREECH_MOVE")
        {
            combat.SetMonsterInt(
                move.Owner,
                "_turnsUntilSummonable",
                combat.GetMonsterInt(move.Owner, "_turnsUntilSummonable") - 1);
        }

        switch ((type, id))
        {
            case ("Fabricator", "FABRICATE_MOVE"):
                SpawnFabricatorBot(simulator, combat, move.Owner, defensive: true);
                SpawnFabricatorBot(simulator, combat, move.Owner, defensive: false);
                return true;
            case ("Fabricator", "FABRICATING_STRIKE_MOVE"):
                SpawnFabricatorBot(simulator, combat, move.Owner, defensive: false);
                return true;
            case ("Fogmog", "ILLUSION_MOVE"):
                MonsterSpawnSupport.Spawn<EyeWithTeeth>(
                    simulator, combat, move.Owner, "illusion");
                return true;
            case ("LivingFog", "BLOAT_MOVE"):
                return true;
            case ("Ovicopter", "LAY_EGGS_MOVE"):
                for (int index = 0; index < 3; index++)
                {
                    string? slot = MonsterSpawnSupport.LastFreeSlot(combat);
                    if (slot == null)
                        break;
                    MonsterSpawnSupport.Spawn<ToughEgg>(
                        simulator, combat, move.Owner, slot, minion: true);
                }
                return true;
            case ("TheObscura", "ILLUSION_MOVE"):
                MonsterSpawnSupport.Spawn<Parafright>(
                    simulator, combat, move.Owner, "illusion");
                combat.SetMonsterBool(move.Owner, "_hasSummoned", true);
                return true;
            case ("TwoTailedRat", "CALL_FOR_BACKUP_MOVE"):
            {
                string? slot = MonsterSpawnSupport.LastFreeSlot(combat);
                if (!string.IsNullOrEmpty(slot))
                    MonsterSpawnSupport.Spawn<TwoTailedRat>(simulator, combat, move.Owner, slot);
                Creature[] rats = combat.Enemies
                    .Where(creature => creature.Monster is TwoTailedRat)
                    .ToArray();
                int nextCount = rats.Max(rat => combat.GetMonsterInt(rat, "_callForBackupCount") + 1);
                foreach (Creature rat in rats)
                    combat.SetMonsterInt(rat, "_callForBackupCount", nextCount);
                return true;
            }
            case ("ToughEgg", "HATCH_MOVE"):
            {
                combat.SetMonsterBool(move.Owner, "_isHatched", true);
                combat.SetMonsterBool(move.Owner, "_hatched", true);
                foreach (PowerModel power in combat.EffectivePowers()
                             .Where(power => power.Owner == move.Owner && power is not MinionPower)
                             .ToArray())
                {
                    combat.SetPowerAmount(power, 0);
                }
                int minHp = combat.GetMonsterStaticInt(move.Owner, "HatchlingMinHp");
                int maxHp = combat.GetMonsterStaticInt(move.Owner, "HatchlingMaxHp");
                int baseHp = simulator.Rng.Niche.NextInt(minHp, maxHp + 1);
                int scaledHp = (int)Creature.ScaleHpForMultiplayer(
                    baseHp,
                    combat.Encounter,
                    combat.Players.Count,
                    combat.CurrentActIndex);
                SimCreatureState state = simulator.State.GetCreature(move.Owner);
                state.SetMaxHp(scaledHp);
                state.CurrentHp = scaledHp;
                return true;
            }
            case ("FatGremlin", "FLEE_MOVE"):
            case ("ThievingHopper", "ESCAPE_MOVE"):
                combat.CreatureEscaped(move.Owner);
                return true;
            case ("MagiKnight", "DAMPEN_MOVE"):
                combat.ApplyDampen(simulator, player, move.Owner);
                return true;
            case ("KnowledgeDemon", "CURSE_OF_KNOWLEDGE_MOVE"):
                KnowledgeDemonChoiceSupport.Resolve(
                    combat,
                    move.Owner,
                    player,
                    plannedChoices);
                return true;
            case ("TestSubject", "RESPAWN_MOVE"):
                return true;
            case ("TestSubject", "SKULL_BASH_MOVE"):
                DebuffAllPlayers<VulnerablePower>(simulator, combat, 1, move.Owner);
                return true;
            case ("TestSubject", "MULTI_CLAW_MOVE"):
                combat.SetMonsterInt(
                    move.Owner,
                    "_extraMultiClawCount",
                    combat.GetMonsterInt(move.Owner, "_extraMultiClawCount") + 1);
                return true;
            case ("TestSubject", "PHASE3_LACERATE_MOVE"):
            case ("TestSubject", "BIG_POUNCE"):
                return true;
            case ("TestSubject", "BURNING_GROWL_MOVE"):
                AddToAllPlayers<Burn>(simulator, combat, PileType.Discard,
                    combat.GetMonsterStaticInt(move.Owner, "BurningGrowlBurnCount"));
                combat.Apply<StrengthPower>(
                    move.Owner,
                    combat.GetMonsterStaticInt(move.Owner, "BurningGrowlStrengthGain"),
                    move.Owner);
                return true;
            case ("SludgeSpinner", "OIL_SPRAY_MOVE"):
                DebuffAllPlayers<WeakPower>(simulator, combat, 1, move.Owner);
                return true;
            case ("SludgeSpinner", "RAGE_MOVE"):
                combat.Apply<StrengthPower>(move.Owner, 3, move.Owner);
                return true;
            case ("Flyconid", "VULNERABLE_SPORES_MOVE"):
                DebuffAllPlayers<VulnerablePower>(simulator, combat, 2, move.Owner);
                return true;
            case ("Flyconid", "FRAIL_SPORES_MOVE"):
                DebuffAllPlayers<FrailPower>(simulator, combat, 2, move.Owner);
                return true;
            case ("FrogKnight", "FOR_THE_QUEEN"):
                combat.Apply<StrengthPower>(move.Owner, 5, move.Owner);
                return true;
            case ("FrogKnight", "TONGUE_LASH"):
                DebuffAllPlayers<FrailPower>(simulator, combat, 2, move.Owner);
                return true;
            case ("GasBomb", "EXPLODE_MOVE"):
                using (simulator.PushDamageSource(
                    CombatDamageSource.For(CombatDamageSourceKind.MonsterMove, move.Owner.Monster?.Id.Entry)))
                {
                    simulator.Kill(move.Owner, force: true);
                }
                if (simulator.HasPendingChoice)
                    return true;
                killedOwner = true;
                return true;
            case ("GlobeHead", "SHOCKING_SLAP"):
                DebuffAllPlayers<FrailPower>(simulator, combat, 2, move.Owner);
                return true;
            case ("GlobeHead", "GALVANIC_BURST"):
                combat.Apply<StrengthPower>(move.Owner, 2, move.Owner);
                return true;
            case ("BowlbugSilk", "TOXIC_SPIT_MOVE"):
                DebuffAllPlayers<WeakPower>(simulator, combat, 1, move.Owner);
                return true;
            case ("BowlbugEgg", "BITE_MOVE"):
                GainBlock(simulator, move.Owner, combat.GetMonsterStaticInt(move.Owner, "ProtectBlock"));
                return true;
            case ("HauntedShip", "HAUNT_MOVE"):
                DebuffAllPlayers<WeakPower>(simulator, combat, 3, move.Owner);
                AddToAllPlayers<Dazed>(simulator, combat, PileType.Discard, 5);
                return true;
            case ("HunterKiller", "TENDERIZING_GOOP_MOVE"):
                DebuffAllPlayers<TenderPower>(simulator, combat, 1, move.Owner);
                return true;
            case ("Guardbot", "GUARD_MOVE"):
                foreach (Creature enemy in combat.Enemies)
                {
                    if (enemy.Monster?.GetType().Name == "Fabricator")
                        simulator.GainBlock(enemy, 15, ValueProp.Unpowered);
                }
                return true;
            case ("InfestedPrism", "RADIATE_MOVE"):
                GainBlock(simulator, move.Owner, combat.GetMonsterStaticInt(move.Owner, "RadiateBlock"));
                return true;
            case ("InfestedPrism", "PULSATE_MOVE"):
                GainBlock(simulator, move.Owner, combat.GetMonsterStaticInt(move.Owner, "PulsateBlock"));
                combat.Apply<VitalSparkPower>(
                    move.Owner,
                    combat.GetMonsterStaticInt(move.Owner, "VitalSparkAmount"),
                    move.Owner);
                return true;
            case ("KinFollower", "POWER_DANCE_MOVE"):
                combat.Apply<StrengthPower>(
                    move.Owner,
                    combat.GetMonsterStaticInt(move.Owner, "DanceStrength"),
                    move.Owner);
                return true;
            case ("KinPriest", "ORB_OF_FRAILTY_MOVE"):
                DebuffAllPlayers<FrailPower>(simulator, combat, 1, move.Owner);
                return true;
            case ("KinPriest", "ORB_OF_WEAKNESS_MOVE"):
                DebuffAllPlayers<WeakPower>(simulator, combat, 1, move.Owner);
                return true;
            case ("KinPriest", "RITUAL_MOVE"):
                combat.Apply<StrengthPower>(
                    move.Owner,
                    combat.GetMonsterStaticInt(move.Owner, "RitualStrength"),
                    move.Owner);
                return true;
            case ("KnowledgeDemon", "PONDER_MOVE"):
                simulator.Heal(move.Owner, 30 * combat.Players.Count);
                combat.Apply<StrengthPower>(
                    move.Owner,
                    combat.GetMonsterStaticInt(move.Owner, "PonderStrength"),
                    move.Owner);
                return true;
            case ("LagavulinMatriarch", "SLASH2_MOVE"):
                GainBlock(simulator, move.Owner, combat.GetMonsterStaticInt(move.Owner, "Slash2Block"));
                return true;
            case ("LagavulinMatriarch", "SOUL_SIPHON_MOVE"):
                foreach (var member in combat.Players)
                {
                    if (!simulator.State.GetCreature(member.Creature).IsAlive)
                        continue;
                    combat.Apply<StrengthPower>(member.Creature, -2, move.Owner);
                    combat.Apply<DexterityPower>(member.Creature, -2, move.Owner);
                }
                combat.Apply<StrengthPower>(move.Owner, 2, move.Owner);
                return true;
            case ("LeafSlimeM", "STICKY_SHOT"):
                AddToAllPlayers<Slimed>(simulator, combat, PileType.Discard, 2);
                return true;
            case ("LeafSlimeS", "GOOP_MOVE"):
                AddToAllPlayers<Slimed>(simulator, combat, PileType.Discard, 1);
                return true;
            case ("LivingShield", "SMASH_MOVE"):
                combat.Apply<StrengthPower>(move.Owner, 3, move.Owner);
                return true;
            case ("Mawler", "ROAR_MOVE"):
                DebuffAllPlayers<VulnerablePower>(simulator, combat, 3, move.Owner);
                return true;
            case ("Myte", "TOXIC_MOVE"):
                AddToAllPlayers<Toxic>(simulator, combat, PileType.Hand, 2);
                return true;
            case ("Myte", "SUCK_MOVE"):
                combat.Apply<StrengthPower>(
                    move.Owner,
                    combat.GetMonsterStaticInt(move.Owner, "SuckStrength"),
                    move.Owner);
                return true;
            case ("Nibbit", "SLICE_MOVE"):
                GainBlock(simulator, move.Owner, combat.GetMonsterStaticInt(move.Owner, "SliceBlock"));
                return true;
            case ("Chomper", "SCREECH_MOVE"):
                AddToAllPlayers<Dazed>(simulator, combat, PileType.Discard, 3);
                return true;
            case ("MagiKnight", "POWER_SHIELD_MOVE"):
            case ("MagiKnight", "PREP_MOVE"):
                GainBlock(simulator, move.Owner, combat.GetMonsterStaticInt(move.Owner, "PowerShieldBlock"));
                return true;
            case ("MechaKnight", "FLAMETHROWER_MOVE"):
                AddToAllPlayers<Burn>(simulator, combat, PileType.Hand, 4);
                return true;
            case ("MechaKnight", "WINDUP_MOVE"):
                GainBlock(simulator, move.Owner, 15);
                combat.Apply<StrengthPower>(move.Owner, 5, move.Owner);
                return true;
            case ("PunchConstruct", "READY_MOVE"):
                GainBlock(simulator, move.Owner, 10);
                return true;
            case ("PunchConstruct", "FAST_PUNCH_MOVE"):
                DebuffAllPlayers<FrailPower>(simulator, combat, 1, move.Owner);
                return true;
            case ("Seapunk", "BUBBLE_BURP_MOVE"):
                GainBlock(simulator, move.Owner, combat.GetMonsterStaticInt(move.Owner, "BubbleBlock"));
                combat.Apply<StrengthPower>(
                    move.Owner,
                    combat.GetMonsterStaticInt(move.Owner, "BubbleStr"),
                    move.Owner);
                return true;
            case ("SewerClam", "PRESSURIZE_MOVE"):
                combat.Apply<StrengthPower>(move.Owner, 4, move.Owner);
                return true;
            case ("Wriggler", "WRIGGLE_MOVE"):
                AddToAllPlayers<Infection>(simulator, combat, PileType.Discard, 1);
                combat.Apply<StrengthPower>(move.Owner, 2, move.Owner);
                return true;
            case ("FlailKnight", "WAR_CHANT"):
                combat.Apply<StrengthPower>(move.Owner, 3, move.Owner);
                return true;
            case ("Exoskeleton", "ENRAGE_MOVE"):
                combat.Apply<StrengthPower>(move.Owner, 2, move.Owner);
                return true;
            case ("BowlbugNectar", "BUFF_MOVE"):
                combat.Apply<StrengthPower>(move.Owner, combat.GetMonsterStaticInt(move.Owner, "BuffStrengthGain"), move.Owner);
                return true;
            case ("Nibbit", "HISS_MOVE"):
                combat.Apply<StrengthPower>(move.Owner, combat.GetMonsterStaticInt(move.Owner, "HissStrengthGain"), move.Owner);
                return true;
            case ("CorpseSlug", "GOOP_MOVE"):
                DebuffAllPlayers<FrailPower>(simulator, combat,
                    combat.GetMonsterStaticInt(move.Owner, "GoopFrailAmt"), move.Owner);
                return true;
            case ("SoulFysh", "SCREAM_MOVE"):
                DebuffAllPlayers<VulnerablePower>(simulator, combat,
                    combat.GetMonsterStaticInt(move.Owner, "ScreamMoveAmount"), move.Owner);
                return true;
            case ("TheLost", "DEBILITATING_SMOG"):
                int strength = combat.GetMonsterStaticInt(move.Owner, "DebilitatingSmogStrengthStealAmount");
                foreach (var member in combat.Players)
                    if (simulator.State.GetCreature(member.Creature).IsAlive)
                        combat.Apply<StrengthPower>(member.Creature, -strength, move.Owner);
                combat.Apply<StrengthPower>(move.Owner, strength, move.Owner);
                return true;
            case ("CalcifiedCultist", "INCANTATION_MOVE"):
                combat.Apply<RitualPower>(
                    move.Owner,
                    combat.GetMonsterStaticInt(move.Owner, "IncantationAmount"),
                    move.Owner);
                return true;
            case ("CeremonialBeast", "PLOW_MOVE"):
                combat.Apply<StrengthPower>(
                    move.Owner,
                    combat.GetMonsterStaticInt(move.Owner, "PlowStrength"),
                    move.Owner);
                return true;
            case ("CeremonialBeast", "CRUSH_MOVE"):
                combat.Apply<StrengthPower>(
                    move.Owner,
                    combat.GetMonsterStaticInt(move.Owner, "CrushStrength"),
                    move.Owner);
                return true;
            case ("EyeWithTeeth", "DISTRACT_MOVE"):
                AddToAllPlayers<Dazed>(simulator, combat, PileType.Discard, 3);
                return true;
            case ("Ovicopter", "TENDERIZER_MOVE"):
                DebuffAllPlayers<VulnerablePower>(simulator, combat, 2, move.Owner);
                return true;
            case ("Ovicopter", "NUTRITIONAL_PASTE_MOVE"):
                combat.Apply<StrengthPower>(
                    move.Owner,
                    combat.GetMonsterStaticInt(move.Owner, "NutritionalPasteStrengthAmount"),
                    move.Owner);
                return true;
            case ("SpinyToad", "PROTRUDING_SPIKES_MOVE"):
                combat.Apply<ThornsPower>(move.Owner, 5, move.Owner);
                return true;
            case ("SpinyToad", "SPIKE_EXPLOSION_MOVE"):
                combat.Apply<ThornsPower>(move.Owner, -5, move.Owner);
                return true;
            case ("Stabbot", "STAB_MOVE"):
                DebuffAllPlayers<FrailPower>(simulator, combat, 1, move.Owner);
                return true;
            case ("ScrollOfBiting", "MORE_TEETH"):
                combat.Apply<StrengthPower>(move.Owner, 2, move.Owner);
                return true;
            case ("ShrinkerBeetle", "SHRINKER_MOVE"):
                foreach (var member in combat.Players)
                    if (simulator.State.GetCreature(member.Creature).IsAlive)
                        combat.Apply<ShrinkPower>(member.Creature, -1, move.Owner);
                return true;
            case ("VineShambler", "GRASPING_VINES_MOVE"):
                DebuffAllPlayers<TangledPower>(simulator, combat, 1, move.Owner);
                return true;
            case ("SlitheringStrangler", "THWACK"):
                GainBlock(simulator, move.Owner, 5);
                return true;
            case ("SlitheringStrangler", "CONSTRICT"):
                foreach (var member in combat.Players)
                    if (simulator.State.GetCreature(member.Creature).IsAlive)
                        combat.Apply<ConstrictPower>(member.Creature, 3, move.Owner);
                return true;
            case ("SpectralKnight", "HEX"):
                foreach (var member in combat.Players)
                    if (simulator.State.GetCreature(member.Creature).IsAlive)
                        combat.Apply<HexPower>(member.Creature, 2, move.Owner);
                return true;
            case ("SnappingJaxfruit", "ENERGY_ORB_MOVE"):
                combat.Apply<StrengthPower>(move.Owner, 2, move.Owner);
                return true;
            case ("SoulNexus", "DRAIN_LIFE_MOVE"):
                DebuffAllPlayers<VulnerablePower>(simulator, combat, 2, move.Owner);
                DebuffAllPlayers<WeakPower>(simulator, combat, 2, move.Owner);
                return true;
            case ("SkulkingColony", "INERTIA_MOVE"):
                combat.Apply<StrengthPower>(
                    move.Owner,
                    combat.GetMonsterStaticInt(move.Owner, "InertiaStrengthGain"),
                    move.Owner);
                return true;
            case ("SlimedBerserker", "VOMIT_ICHOR_MOVE"):
                AddToAllPlayers<Slimed>(simulator, combat, PileType.Discard, 10);
                return true;
            case ("SlimedBerserker", "LEECHING_HUG_MOVE"):
                DebuffAllPlayers<WeakPower>(simulator, combat, 3, move.Owner);
                combat.Apply<StrengthPower>(move.Owner, 3, move.Owner);
                return true;
            case ("TerrorEel", "TERROR_MOVE"):
                DebuffAllPlayers<VulnerablePower>(simulator, combat, 99, move.Owner);
                return true;
            case ("TwigSlimeM", "STICKY_SHOT_MOVE"):
                AddToAllPlayers<Slimed>(simulator, combat, PileType.Discard, 1);
                return true;
            case ("TurretOperator", "RELOAD_MOVE"):
                combat.Apply<StrengthPower>(move.Owner, 1, move.Owner);
                return true;
            case ("TheAdversaryMkOne", "BARRAGE_MOVE"):
                combat.Apply<StrengthPower>(move.Owner, 2, move.Owner);
                return true;
            case ("TheAdversaryMkTwo", "BARRAGE_MOVE"):
                combat.Apply<StrengthPower>(move.Owner, 3, move.Owner);
                return true;
            case ("TheAdversaryMkThree", "BARRAGE_MOVE"):
                combat.Apply<StrengthPower>(move.Owner, 4, move.Owner);
                return true;
            case ("Toadpole", "SPIKEN_MOVE"):
                combat.Apply<ThornsPower>(move.Owner, 2, move.Owner);
                return true;
            case ("Toadpole", "SPIKE_SPIT_MOVE"):
                combat.Apply<ThornsPower>(move.Owner, -2, move.Owner);
                return true;
            case ("MysteriousKnight", "WAR_CHANT"):
                combat.Apply<StrengthPower>(move.Owner, 3, move.Owner);
                return true;
            case ("PhrogParasite", "INFECT_MOVE"):
                AddToAllPlayers<Infection>(simulator, combat, PileType.Discard, 3);
                return true;
            case ("Vantom", "DISMEMBER_MOVE"):
                AddToAllPlayers<Wound>(simulator, combat, PileType.Discard, 3);
                return true;
            case ("Vantom", "PREPARE_MOVE"):
                combat.Apply<StrengthPower>(move.Owner, 2, move.Owner);
                return true;
            case ("TheInsatiable", "SALIVATE_MOVE"):
                combat.Apply<StrengthPower>(
                    move.Owner,
                    combat.GetMonsterStaticInt(move.Owner, "SalivateStrength"),
                    move.Owner);
                return true;
            case ("TheObscura", "HARDENING_STRIKE_MOVE"):
                GainBlock(simulator, move.Owner, combat.GetMonsterStaticInt(move.Owner, "HardeningStrikeBlock"));
                return true;
            case ("TheForgotten", "MIASMA"):
                foreach (var member in combat.Players)
                    if (simulator.State.GetCreature(member.Creature).IsAlive)
                        combat.Apply<DexterityPower>(member.Creature,
                            -combat.GetMonsterStaticInt(move.Owner, "DebilitatingSmogDexStealAmount"), move.Owner);
                GainBlock(simulator, move.Owner, 8);
                combat.Apply<DexterityPower>(
                    move.Owner,
                    combat.GetMonsterStaticInt(move.Owner, "DebilitatingSmogDexStealAmount"),
                    move.Owner);
                return true;
            case ("OwlMagistrate", "JUDICIAL_FLIGHT"):
                combat.Apply<SoarPower>(move.Owner, 1, move.Owner);
                return true;
            case ("OwlMagistrate", "VERDICT"):
                DebuffAllPlayers<VulnerablePower>(simulator, combat, 4, move.Owner);
                combat.SetAmount<SoarPower>(move.Owner, 0);
                return true;
            case ("CeremonialBeast", "STAMP_MOVE"):
                combat.Apply<PlowPower>(
                    move.Owner,
                    combat.GetMonsterStaticInt(move.Owner, "PlowAmount"),
                    move.Owner);
                return true;
            case ("CeremonialBeast", "BEAST_CRY_MOVE"):
                DebuffAllPlayers<RingingPower>(simulator, combat, 1, move.Owner);
                return true;
            case ("Queen", "PUPPET_STRINGS_MOVE"):
                foreach (var member in combat.Players)
                    if (simulator.State.GetCreature(member.Creature).IsAlive)
                        Debuff<ChainsOfBindingPower>(combat, member.Creature, 3, move.Owner);
                return true;
            case ("Queen", "YOU_ARE_MINE_MOVE"):
                foreach (var member in combat.Players)
                {
                    if (!simulator.State.GetCreature(member.Creature).IsAlive)
                        continue;
                    Debuff<FrailPower>(combat, member.Creature, 99, move.Owner);
                    Debuff<WeakPower>(combat, member.Creature, 99, move.Owner);
                    Debuff<VulnerablePower>(combat, member.Creature, 99, move.Owner);
                }
                return true;
            case ("Queen", "BURN_BRIGHT_FOR_ME_MOVE"):
                foreach (Creature enemy in combat.Enemies)
                {
                    if (!ReferenceEquals(enemy, move.Owner))
                        combat.Apply<StrengthPower>(enemy, 1, move.Owner);
                }
                GainBlock(simulator, move.Owner, 20);
                return true;
            case ("Queen", "ENRAGE_MOVE"):
                combat.Apply<StrengthPower>(move.Owner, 2, move.Owner);
                return true;
            case ("LouseProgenitor", "WEB_CANNON_MOVE"):
                DebuffAllPlayers<FrailPower>(simulator, combat, 2, move.Owner);
                return true;
            case ("LouseProgenitor", "CURL_AND_GROW_MOVE"):
                GainBlock(simulator, move.Owner, combat.GetMonsterStaticInt(move.Owner, "CurlBlock"));
                combat.Apply<StrengthPower>(
                    move.Owner,
                    combat.GetMonsterStaticInt(move.Owner, "GrowStrength"),
                    move.Owner);
                return true;
            case ("Tunneler", "BURROW_MOVE"):
                combat.Apply<BurrowedPower>(move.Owner, 1, move.Owner);
                GainBlock(simulator, move.Owner, combat.GetMonsterStaticInt(move.Owner, "BlockGain"));
                return true;
            case ("CubexConstruct", "CHARGE_UP_MOVE"):
            case ("CubexConstruct", "REPEATER_BLAST_MOVE"):
            case ("CubexConstruct", "REPEATER_BLAST_MOVE_2"):
                combat.Apply<StrengthPower>(move.Owner, 2, move.Owner);
                return true;
            case ("SlumberingBeetle", "ROLL_OUT_MOVE"):
                combat.Apply<StrengthPower>(move.Owner, 2, move.Owner);
                return true;
            case ("Crusher", "BUG_STING_MOVE"):
                DebuffAllPlayers<WeakPower>(simulator, combat, 2, move.Owner);
                DebuffAllPlayers<FrailPower>(simulator, combat, 2, move.Owner);
                return true;
            case ("Crusher", "ADAPT_MOVE"):
                combat.Apply<StrengthPower>(
                    move.Owner,
                    combat.GetMonsterStaticInt(move.Owner, "AdaptStrengthGain"),
                    move.Owner);
                return true;
            case ("Crusher", "GUARDED_STRIKE_MOVE"):
                GainBlock(simulator, move.Owner, 18);
                return true;
            case ("Rocket", "CHARGE_UP_MOVE"):
                combat.Apply<StrengthPower>(
                    move.Owner,
                    combat.GetMonsterStaticInt(move.Owner, "ChargeUpStrengthGain"),
                    move.Owner);
                return true;
            case ("TrackerRubyRaider", "TRACK_MOVE"):
                DebuffAllPlayers<FrailPower>(simulator, combat, 2, move.Owner);
                return true;
            case ("Noisebot", "NOISE_MOVE"):
                foreach (var member in combat.Players)
                {
                    if (!simulator.State.GetCreature(member.Creature).IsAlive)
                        continue;
                    simulator.AddToCombat<Dazed>(member.Creature, PileType.Discard, 1, null);
                    simulator.AddToCombat<Dazed>(member.Creature, PileType.Draw, 1, null,
                        CardPilePosition.Random);
                }
                return true;
            case ("SoulFysh", "BECKON_MOVE"):
                foreach (var member in combat.Players)
                {
                    if (!simulator.State.GetCreature(member.Creature).IsAlive)
                        continue;
                    simulator.AddToCombat<Beckon>(member.Creature, PileType.Draw, 1, null,
                        CardPilePosition.Random);
                    simulator.AddToCombat<Beckon>(member.Creature, PileType.Discard, 1, null);
                }
                return true;
            case ("SoulFysh", "GAZE_MOVE"):
                AddToAllPlayers<Beckon>(simulator, combat, PileType.Discard,
                    combat.GetMonsterStaticInt(move.Owner, "GazeMoveAmount"));
                return true;
            case ("SoulFysh", "FADE_MOVE"):
                combat.Apply<IntangiblePower>(move.Owner, 2, move.Owner);
                return true;
            case ("TerrorEel", "THRASH_MOVE"):
                combat.Apply<VigorPower>(move.Owner, 6, move.Owner);
                return true;
            case ("TheObscura", "SAIL_MOVE"):
                foreach (Creature enemy in combat.Enemies)
                {
                    if (simulator.State.GetCreature(enemy).IsAlive)
                        combat.Apply<StrengthPower>(enemy, 3, move.Owner);
                }
                return true;
            case ("PhantasmalGardener", "ENLARGE_MOVE"):
                combat.Apply<StrengthPower>(move.Owner, combat.GetMonsterStaticInt(move.Owner, "EnlargeStr"), move.Owner);
                return true;
            case ("FuzzyWurmCrawler", "INHALE"):
                combat.Apply<StrengthPower>(move.Owner, 7, move.Owner);
                return true;
            case ("Axebot", "BOOT_UP_MOVE"):
                GainBlock(simulator, move.Owner, combat.GetMonsterStaticInt(move.Owner, "BootUpBlock"));
                combat.Apply<StrengthPower>(
                    move.Owner,
                    combat.GetMonsterStaticInt(move.Owner, "BootUpStrGain") *
                    combat.GetMonsterStaticInt(move.Owner, "RespawnCount"),
                    move.Owner);
                return true;
            case ("Axebot", "HAMMER_UPPERCUT_MOVE"):
                DebuffAllPlayers<WeakPower>(simulator, combat, 2, move.Owner);
                DebuffAllPlayers<FrailPower>(simulator, combat, 2, move.Owner);
                return true;
            case ("Aeonglass", "EBB_MOVE"):
                GainBlock(simulator, move.Owner, combat.GetMonsterStaticInt(move.Owner, "EbbBlock"));
                return true;
            case ("Aeonglass", "INCREASING_INTENSITY_MOVE"):
                foreach (var member in combat.Players)
                {
                    if (!simulator.State.GetCreature(member.Creature).IsAlive)
                        continue;
                    SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(member);
                    foreach (PredictedCard card in playerState.AllCards)
                    {
                        if (card.Preview is Wither)
                            ((Wither)card.MutablePreview).FakeUpgrade();
                    }
                }
                combat.AdvanceAeonglassWitherUpgrade(move.Owner);
                foreach (var member in combat.Players)
                    if (simulator.State.GetCreature(member.Creature).IsAlive)
                        simulator.CreateAndAddGeneratedCardsToCombat<Wither>(
                            member, PileType.Discard,
                            combat.GetMonsterStaticInt(move.Owner, "WitherAmount"), null);
                combat.Apply<StrengthPower>(
                    move.Owner,
                    combat.GetMonsterStaticInt(move.Owner, "IncreasingIntensityBaseStrength")
                        + combat.AdvanceAeonglassAdditionalStrength(move.Owner),
                    move.Owner);
                return true;
            case ("AxeRubyRaider", "SWING_1"):
            case ("AxeRubyRaider", "SWING_2"):
                GainBlock(simulator, move.Owner, combat.GetMonsterStaticInt(move.Owner, "SwingBlock"));
                return true;
            case ("BruteRubyRaider", "ROAR_MOVE"):
                combat.Apply<StrengthPower>(move.Owner, 3, move.Owner);
                return true;
            case ("BygoneEffigy", "WAKE_MOVE"):
                combat.Apply<StrengthPower>(move.Owner, 10, move.Owner);
                return true;
            case ("CrossbowRubyRaider", "RELOAD_MOVE"):
                GainBlock(simulator, move.Owner, 3);
                return true;
            case ("DampCultist", "INCANTATION_MOVE"):
                combat.Apply<RitualPower>(
                    move.Owner,
                    combat.GetMonsterStaticInt(move.Owner, "IncantationAmount"),
                    move.Owner);
                return true;
            case ("DevotedSculptor", "FORBIDDEN_INCANTATION_MOVE"):
                combat.Apply<RitualPower>(move.Owner, combat.GetMonsterStaticInt(move.Owner, "_ritualGain"));
                return true;
            case ("Entomancer", "PHEROMONE_SPIT_MOVE"):
                PersonalHivePower? hive = combat.GetPower<PersonalHivePower>(move.Owner);
                if (hive == null || hive.Amount >= 3)
                {
                    combat.Apply<StrengthPower>(move.Owner, 2, move.Owner);
                    return true;
                }
                combat.Apply<PersonalHivePower>(move.Owner, 1, move.Owner);
                combat.Apply<StrengthPower>(move.Owner, 1, move.Owner);
                return true;
            case ("FakeMerchantMonster", "ENRAGE_MOVE"):
                combat.Apply<StrengthPower>(move.Owner, 2, move.Owner);
                return true;
            case ("FakeMerchantMonster", "THROW_RELIC_MOVE"):
                DebuffAllPlayers<FrailPower>(simulator, combat, 1, move.Owner);
                return true;
            case ("Fogmog", "SWIPE_MOVE"):
            case ("Fogmog", "SWIPE_RANDOM_MOVE"):
                combat.Apply<StrengthPower>(move.Owner, 1, move.Owner);
                return true;
            case ("FossilStalker", "TACKLE_MOVE"):
                DebuffAllPlayers<FrailPower>(simulator, combat, 1, move.Owner);
                return true;
            case ("WaterfallGiant", "PRESSURIZE_MOVE"):
                combat.Apply<SteamEruptionPower>(move.Owner, combat.GetMonsterStaticInt(move.Owner, "PressurizeAmount"), move.Owner);
                return true;
            case ("WaterfallGiant", "STOMP_MOVE"):
                foreach (var member in combat.Players)
                    if (simulator.State.GetCreature(member.Creature).IsAlive)
                        Debuff<WeakPower>(combat, member.Creature, 1, move.Owner);
                combat.Apply<SteamEruptionPower>(move.Owner, 3, move.Owner);
                return true;
            case ("WaterfallGiant", "RAM_MOVE"):
            case ("WaterfallGiant", "PRESSURE_UP_MOVE"):
                combat.Apply<SteamEruptionPower>(move.Owner, 3, move.Owner);
                return true;
            case ("WaterfallGiant", "SIPHON_MOVE"):
                simulator.Heal(move.Owner, combat.GetMonsterStaticInt(move.Owner, "SiphonHeal") * combat.Players.Count);
                combat.Apply<SteamEruptionPower>(move.Owner, 3, move.Owner);
                return true;
            case ("WaterfallGiant", "PRESSURE_GUN_MOVE"):
                combat.IncreasePressureGun(move.Owner, combat.GetMonsterStaticInt(move.Owner, "PressureGunIncrease"));
                combat.Apply<SteamEruptionPower>(move.Owner, 3, move.Owner);
                return true;
            case ("WaterfallGiant", "ABOUT_TO_BLOW_MOVE"):
                combat.PrepareSteamEruption(move.Owner);
                return true;
            case ("WaterfallGiant", "EXPLODE_MOVE"):
                using (simulator.PushDamageSource(
                    CombatDamageSource.For(CombatDamageSourceKind.MonsterMove, move.Owner.Monster?.Id.Entry)))
                {
                    simulator.Kill(move.Owner, force: true);
                }
                if (simulator.HasPendingChoice)
                    return true;
                killedOwner = true;
                return true;
            case ("DecimillipedeSegmentBack", "BULK_MOVE"):
            case ("DecimillipedeSegmentFront", "BULK_MOVE"):
            case ("DecimillipedeSegmentMiddle", "BULK_MOVE"):
                combat.Apply<StrengthPower>(move.Owner, 2, move.Owner);
                return true;
            case ("DecimillipedeSegmentBack", "CONSTRICT_MOVE"):
            case ("DecimillipedeSegmentFront", "CONSTRICT_MOVE"):
            case ("DecimillipedeSegmentMiddle", "CONSTRICT_MOVE"):
                foreach (var member in combat.Players)
                    if (simulator.State.GetCreature(member.Creature).IsAlive)
                        Debuff<WeakPower>(combat, member.Creature, 1, move.Owner);
                return true;
            case ("DecimillipedeSegmentBack", "DEAD_MOVE"):
            case ("DecimillipedeSegmentFront", "DEAD_MOVE"):
            case ("DecimillipedeSegmentMiddle", "DEAD_MOVE"):
                return true;
            case ("DecimillipedeSegmentBack", "REATTACH_MOVE"):
            case ("DecimillipedeSegmentFront", "REATTACH_MOVE"):
            case ("DecimillipedeSegmentMiddle", "REATTACH_MOVE"):
                return true;
            case ("GremlinMerc", "DOUBLE_SMASH_MOVE"):
                combat.RecordThievery(simulator, move.Owner);
                foreach (var member in combat.Players)
                    if (simulator.State.GetCreature(member.Creature).IsAlive)
                        Debuff<WeakPower>(combat, member.Creature, 2, move.Owner);
                return true;
            case ("GremlinMerc", "GIMME_MOVE"):
                combat.RecordThievery(simulator, move.Owner);
                return true;
            case ("GremlinMerc", "HEHE_MOVE"):
                combat.RecordThievery(simulator, move.Owner);
                combat.Apply<StrengthPower>(move.Owner, 2, move.Owner);
                return true;
            case ("LivingFog", "ADVANCED_GAS_MOVE"):
                DebuffAllPlayers<SmoggyPower>(simulator, combat, 1, move.Owner);
                return true;
            case ("TheInsatiable", "LIQUIFY_GROUND_MOVE"):
                foreach (var member in combat.Players)
                    if (simulator.State.GetCreature(member.Creature).IsAlive)
                        combat.ApplyTargeted<SandpitPower>(move.Owner, member.Creature, 4, move.Owner);
                foreach (var member in combat.Players)
                {
                    if (!simulator.State.GetCreature(member.Creature).IsAlive)
                        continue;
                    simulator.AddToCombat<FranticEscape>(member.Creature, PileType.Draw, 3, null,
                        CardPilePosition.Random);
                    simulator.AddToCombat<FranticEscape>(member.Creature, PileType.Discard, 3, null,
                        CardPilePosition.Random);
                }
                return true;
            case ("ThievingHopper", "FLUTTER_MOVE"):
                combat.Apply<FlutterPower>(move.Owner, 5, move.Owner);
                return true;
            case ("TwoTailedRat", "SCREECH_MOVE"):
                DebuffAllPlayers<FrailPower>(simulator, combat, 1, move.Owner);
                return true;
            default:
                return false;
        }
    }

    private static void SpawnFabricatorBot(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Creature source,
        bool defensive)
    {
        int previous = combat.GetFabricatorLastSpawn(source);
        int[] options = defensive ? [1, 2] : [3, 4];
        int[] candidates = options.Where(value => value != previous).ToArray();
        int selected = simulator.Rng.MonsterAi.NextItem(candidates);
        combat.SetMonsterInt(source, "fabricator_last_spawn", selected);
        string? slot = MonsterSpawnSupport.NextSlot(combat);
        switch (selected)
        {
            case 1:
                MonsterSpawnSupport.Spawn<Guardbot>(simulator, combat, source, slot, minion: true);
                break;
            case 2:
                MonsterSpawnSupport.Spawn<Noisebot>(simulator, combat, source, slot, minion: true);
                break;
            case 3:
                MonsterSpawnSupport.Spawn<Zapbot>(simulator, combat, source, slot, minion: true);
                break;
            case 4:
                MonsterSpawnSupport.Spawn<Stabbot>(simulator, combat, source, slot, minion: true);
                break;
            default:
                throw new InvalidOperationException($"未知的组装师召唤编号 {selected}。");
        }
    }

    private static void Debuff<T>(
        SimulatedCombatState combat,
        Creature target,
        int amount,
        Creature applier) where T : PowerModel
        => combat.ApplyFromMonster<T>(target, amount, applier);

    private static void DebuffAllPlayers<T>(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        int amount,
        Creature applier) where T : PowerModel
    {
        foreach (var member in combat.Players)
            if (simulator.State.GetCreature(member.Creature).IsAlive)
                Debuff<T>(combat, member.Creature, amount, applier);
    }

    private static void AddToAllPlayers<TCard>(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        PileType pile,
        int count) where TCard : CardModel
    {
        foreach (var member in combat.Players)
            if (simulator.State.GetCreature(member.Creature).IsAlive)
                simulator.AddToCombat<TCard>(member.Creature, pile, count, null);
    }

    private static void GainBlock(CombatPredictionSimulator simulator, Creature target, int amount)
        => simulator.GainBlock(target, amount, ValueProp.Move);

}
