using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Random;
using CombatSolver.Engine.Common;

namespace CombatSolver;

internal readonly record struct ForecastAttackHit(int Damage, int BaseDamage);

internal sealed record ForecastMove(
    Creature Owner,
    MoveState Move,
    IReadOnlyList<ForecastAttackHit> AttackHits);

internal sealed class IntentForecast
{
    public required IReadOnlyList<IReadOnlyList<ForecastMove>> Rounds { get; init; }
    public required bool HasUnsupportedIntent { get; init; }
    public required bool IsExactForModeledDamage { get; init; }
    public required IReadOnlyList<string> UnsupportedDetails { get; init; }
    public required IReadOnlyList<string> ApproximationDetails { get; init; }
    public required IReadOnlyList<int> MonsterAiCountersByRound { get; init; }
}

internal static class IntentForecaster
{
    internal static int CaptureBaseDamage(AttackIntent attack, MonsterModel monster, string moveId)
    {
        if (attack.DamageCalc is null)
            PredictionModPatchAudit.RejectForeignPatches(
                attack.GetType().GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                    .Cast<System.Reflection.MethodBase>()
                    .Append(HarmonyLib.AccessTools.Method(typeof(AttackIntent), nameof(AttackIntent.GetSingleDamage)))
                    .Append(HarmonyLib.AccessTools.Method(monster.GetType(), "GenerateMoveStateMachine")));
        Func<decimal> calculate = attack.DamageCalc
            ?? throw PredictionUnsupportedException.ForContent(
                $"Attack intent has no prediction damage calculation: {monster.GetType().FullName}.{moveId}/{attack.GetType().FullName}.",
                attack.GetType(), monster.GetType());
        return Math.Max(0, (int)calculate());
    }

    private sealed class Cursor(MonsterModel monster)
    {
        public MonsterModel Monster { get; } = monster;
        public MoveState Current { get; set; } = monster.NextMove;
        public bool Active { get; set; } = true;
        public List<string> Log { get; } = monster.MoveStateMachine?.StateLog.Select(state => state.Id).ToList() ?? [];
        public int KnowledgeDemonCurseCounter { get; set; } = monster.GetType().Name == "KnowledgeDemon"
            ? MonsterValueReader.ReadInt(monster, "_curseOfKnowledgeCounter")
            : 0;
    }

    public static IntentForecast Build(CombatState state, int roundCount)
    {
        List<Creature> enemies = state.Enemies.Where(enemy => enemy.Monster != null).ToList();
        List<Cursor> cursors = enemies.Select(enemy => new Cursor(enemy.Monster!)).ToList();
        Rng rng = new(state.RunState.Rng.MonsterAi.ToSerializable());
        List<IReadOnlyList<ForecastMove>> rounds = [];
        bool unsupported = false;
        bool exact = true;
        HashSet<string> unsupportedDetails = new(StringComparer.Ordinal);
        HashSet<string> approximationDetails = new(StringComparer.Ordinal);
        List<int> monsterAiCounters = [rng.ToSerializable().counter];

        for (int round = 0; round < roundCount; round++)
        {
            List<ForecastMove> moves = [];
            foreach (Cursor cursor in cursors)
            {
                if (!cursor.Active)
                    continue;
                IReadOnlyList<ForecastAttackHit> hits = GetAttackHits(cursor.Monster, cursor.Current, state,
                    ref unsupported, ref exact, unsupportedDetails, approximationDetails);
                moves.Add(new ForecastMove(cursor.Monster.Creature, cursor.Current, hits));
            }
            rounds.Add(moves);

            if (round + 1 >= roundCount)
                continue;

            foreach (Cursor cursor in cursors)
            {
                if (!cursor.Active)
                    continue;
                if (MonsterMoveEffects.RemovesOwner(cursor.Monster, cursor.Current.Id))
                {
                    cursor.Active = false;
                    continue;
                }
                cursor.Current = RollNext(cursor, rng, ref exact, approximationDetails);
            }
            monsterAiCounters.Add(rng.ToSerializable().counter);
        }

        return new IntentForecast
        {
            Rounds = rounds,
            HasUnsupportedIntent = unsupported,
            IsExactForModeledDamage = exact && !unsupported,
            UnsupportedDetails = unsupportedDetails.Order(StringComparer.Ordinal).ToList(),
            ApproximationDetails = approximationDetails.Order(StringComparer.Ordinal).ToList(),
            MonsterAiCountersByRound = monsterAiCounters,
        };
    }

    private static IReadOnlyList<ForecastAttackHit> GetAttackHits(
        MonsterModel monster,
        MoveState move,
        CombatState state,
        ref bool unsupported,
        ref bool exact,
        ISet<string> unsupportedDetails,
        ISet<string> approximationDetails)
    {
        List<ForecastAttackHit> hits = [];
        foreach (AbstractIntent intent in move.Intents)
        {
            if (intent is AttackIntent attack)
            {
                int baseDamage = CaptureBaseDamage(attack, monster, move.Id);
                int single = attack.GetSingleDamage(state.PlayerCreatures, monster.Creature);
                for (int i = 0; i < Math.Max(attack.Repeats, 1); i++)
                    hits.Add(new ForecastAttackHit(single, baseDamage));
                if (attack.DamageCalc?.Target != null && !IsKnownStableAttack(monster, move.Id))
                {
                    exact = false;
                    approximationDetails.Add($"{monster.Id.Entry}.{move.Id}:动态伤害");
                }
            }
            else if (MonsterMoveEffects.Supports(monster, move.Id))
            {
                // 该行动的非攻击部分由 MonsterMoveEffects 在回合推进时补偿。
            }
            else if (intent.IntentType is IntentType.Buff or IntentType.Debuff or IntentType.DebuffStrong)
            {
                if (!MonsterMoveEffects.Supports(monster, move.Id))
                {
                    unsupported = true;
                    unsupportedDetails.Add($"{monster.Id.Entry}.{move.Id}:{intent.IntentType}");
                }
            }
            else if (intent.IntentType is not (IntentType.Sleep or IntentType.Stun or IntentType.Hidden))
            {
                unsupported = true;
                unsupportedDetails.Add($"{monster.Id.Entry}.{move.Id}:{intent.IntentType}");
            }
        }
        return hits;
    }

    private static bool IsKnownStableAttack(MonsterModel monster, string moveId)
        => (monster.GetType().Name, moveId) is
            ("PhantasmalGardener", "BITE_MOVE" or "LASH_MOVE" or "FLAIL_MOVE") or
            ("FuzzyWurmCrawler", "FIRST_ACID_GOOP" or "ACID_GOOP") or
            ("BowlbugNectar", "THRASH_MOVE" or "THRASH2_MOVE") or
            ("BowlbugEgg", "BITE_MOVE") or
            ("Axebot", "ONE_TWO_MOVE" or "HAMMER_UPPERCUT_MOVE") or
            ("Aeonglass", "EBB_MOVE" or "EYE_LASERS_MOVE") or
            ("AxeRubyRaider", "SWING_1" or "SWING_2" or "BIG_SWING") or
            ("AssassinRubyRaider", "KILLSHOT_MOVE") or
            ("BowlbugSilk", "THRASH_MOVE") or
            ("BruteRubyRaider", "BEAT_MOVE") or
            ("BygoneEffigy", "SLASHES_MOVE") or
            ("Byrdonis", "PECK_MOVE" or "SWOOP_MOVE") or
            ("CalcifiedCultist", "DARK_STRIKE_MOVE") or
            ("CeremonialBeast", "PLOW_MOVE" or "STOMP_MOVE" or "CRUSH_MOVE") or
            ("Chomper", "CLAMP_MOVE") or
            ("CorpseSlug", "GLOMP_MOVE" or "WHIP_SLAP_MOVE") or
            ("CrossbowRubyRaider", "FIRE_MOVE") or
            ("DampCultist", "DARK_STRIKE_MOVE") or
            ("DevotedSculptor", "SAVAGE_MOVE") or
            ("Entomancer", "BEES_MOVE" or "SPEAR_MOVE") or
            ("Exoskeleton", "MANDIBLES_MOVE" or "SKITTER_MOVE") or
            ("Fabricator", "DISINTEGRATE_MOVE") or
            ("FakeMerchantMonster", "SWIPE_MOVE" or "SPEW_COINS_MOVE" or "THROW_RELIC_MOVE") or
            ("FlailKnight", "FLAIL_MOVE" or "RAM_MOVE") or
            ("Flyconid", "FRAIL_SPORES_MOVE" or "SMASH_MOVE") or
            ("Fogmog", "HEADBUTT_MOVE" or "SWIPE_MOVE" or "SWIPE_RANDOM_MOVE") or
            ("FossilStalker", "LATCH_MOVE" or "LASH_MOVE" or "TACKLE_MOVE") or
            ("FrogKnight", "STRIKE_DOWN_EVIL" or "TONGUE_LASH") or
            ("GasBomb", "EXPLODE_MOVE") or
            ("GlobeHead", "THUNDER_STRIKE" or "SHOCKING_SLAP" or "GALVANIC_BURST") or
            ("HauntedShip", "SWIPE_MOVE" or "STOMP_MOVE") or
            ("HunterKiller", "BITE_MOVE" or "PUNCTURE_MOVE") or
            ("InfestedPrism", "JAB_MOVE" or "RADIATE_MOVE" or "WHIRLWIND_MOVE" or "PULSATE_MOVE") or
            ("Inklet", "JAB_MOVE" or "WHIRLWIND_MOVE" or "PIERCING_GAZE_MOVE") or
            ("KinFollower", "QUICK_SLASH_MOVE" or "BOOMERANG_MOVE") or
            ("KinPriest", "ORB_OF_FRAILTY_MOVE" or "ORB_OF_WEAKNESS_MOVE" or "BEAM_MOVE") or
            ("KnowledgeDemon", "SLAP_MOVE" or "KNOWLEDGE_OVERWHELMING_MOVE" or "PONDER_MOVE") or
            ("LagavulinMatriarch", "SLASH_MOVE" or "SLASH2_MOVE" or "DISEMBOWEL_MOVE") or
            ("LeafSlimeM", "CLUMP_SHOT") or
            ("LeafSlimeS", "TACKLE_MOVE") or
            ("LivingShield", "SHIELD_SLAM_MOVE" or "SMASH_MOVE") or
            ("Mawler", "RIP_AND_TEAR_MOVE" or "CLAW_MOVE") or
            ("Myte", "BITE_MOVE" or "SUCK_MOVE") or
            ("Nibbit", "BUTT_MOVE" or "SLICE_MOVE") or
            ("Chomper", "CLAMP_MOVE") or
            ("MagiKnight", "POWER_SHIELD_MOVE" or "MAGIC_BOMB" or "RAM_MOVE") or
            ("MechaKnight", "CHARGE_MOVE" or "FLAMETHROWER_MOVE" or "HEAVY_CLEAVE_MOVE") or
            ("PunchConstruct", "STRONG_PUNCH_MOVE" or "FAST_PUNCH_MOVE") or
            ("Parafright", "SLAM_MOVE") or
            ("Seapunk", "SEA_KICK_MOVE" or "SPINNING_KICK_MOVE") or
            ("SewerClam", "JET_MOVE") or
            ("ShrinkerBeetle", "CHOMP_MOVE" or "STOMP_MOVE") or
            ("SludgeSpinner", "OIL_SPRAY_MOVE" or "SLAM_MOVE" or "RAGE_MOVE") or
            ("Wriggler", "NASTY_BITE_MOVE") or
            ("TheLost", "EYE_LASERS") or
            ("Ovicopter", "SMASH_MOVE" or "TENDERIZER_MOVE") or
            ("SpinyToad", "SPIKE_EXPLOSION_MOVE" or "TONGUE_LASH_MOVE") or
            ("Stabbot", "STAB_MOVE") or
            ("ScrollOfBiting", "CHOMP" or "CHEW") or
            ("SlitheringStrangler", "THWACK" or "LASH") or
            ("SnappingJaxfruit", "ENERGY_ORB_MOVE") or
            ("SpectralKnight", "SOUL_SLASH" or "SOUL_FLAME") or
            ("SoulNexus", "SOUL_BURN_MOVE" or "MAELSTROM_MOVE" or "DRAIN_LIFE_MOVE") or
            ("SkulkingColony", "ZOOM_MOVE" or "ZOOM_MOVE_2" or "INERTIA_MOVE" or "PIERCING_STABS_MOVE") or
            ("SlimedBerserker", "FURIOUS_PUMMELING_MOVE" or "SMOTHER_MOVE") or
            ("TerrorEel", "CRASH_MOVE") or
            ("SneakyGremlin", "TACKLE_MOVE") or
            ("TwigSlimeM", "POKEY_POUNCE_MOVE") or
            ("TwigSlimeS", "TACKLE_MOVE") or
            ("TorchHeadAmalgam", "STRONG_TACKLE_MOVE" or "TACKLE_2_MOVE" or "BEAM_MOVE" or "TACKLE_3_MOVE" or "TACKLE_4_MOVE") or
            ("TurretOperator", "UNLOAD_MOVE" or "UNLOAD_MOVE_2") or
            ("TheAdversaryMkOne", "SMASH_MOVE" or "BEAM_MOVE" or "BARRAGE_MOVE") or
            ("TheAdversaryMkTwo", "BASH_MOVE" or "FLAME_BEAM_MOVE" or "BARRAGE_MOVE") or
            ("TheAdversaryMkThree", "CRASH_MOVE" or "FLAME_BEAM_MOVE" or "BARRAGE_MOVE") or
            ("Toadpole", "SPIKE_SPIT_MOVE" or "WHIRL_MOVE") or
            ("VineShambler", "SWIPE_MOVE" or "CHOMP_MOVE") or
            ("MysteriousKnight", "FLAIL_MOVE" or "RAM_MOVE") or
            ("PhrogParasite", "LASH_MOVE") or
            ("Vantom", "INK_BLOT_MOVE" or "INKY_LANCE_MOVE" or "DISMEMBER_MOVE") or
            ("TheInsatiable", "THRASH_MOVE" or "THRASH_MOVE_2" or "LUNGING_BITE_MOVE") or
            ("TheObscura", "PIERCING_GAZE_MOVE" or "HARDENING_STRIKE_MOVE") or
            ("TheForgotten", "DREAD") or
            ("OwlMagistrate", "MAGISTRATE_SCRUTINY" or "PECK_ASSAULT" or "VERDICT") or
            ("Queen", "OFF_WITH_YOUR_HEAD_MOVE" or "EXECUTION_MOVE") or
            ("LouseProgenitor", "WEB_CANNON_MOVE" or "POUNCE_MOVE") or
            ("Tunneler", "BITE_MOVE" or "BELOW_MOVE") or
            ("CubexConstruct", "REPEATER_BLAST_MOVE" or "REPEATER_BLAST_MOVE_2" or "EXPEL_MOVE") or
            ("SlumberingBeetle", "ROLL_OUT_MOVE") or
            ("Crusher", "THRASH_MOVE" or "ENLARGING_STRIKE_MOVE" or "BUG_STING_MOVE" or "GUARDED_STRIKE_MOVE") or
            ("Rocket", "TARGETING_RETICLE_MOVE" or "PRECISION_BEAM_MOVE" or "LASER_MOVE") or
            ("TrackerRubyRaider", "HOUNDS_MOVE") or
            ("SoulFysh", "DE_GAS_MOVE" or "GAZE_MOVE" or "SCREAM_MOVE") or
            ("BowlbugRock", "HEADBUTT_MOVE") or
            ("TerrorEel", "THRASH_MOVE") or
            ("ToughEgg", "NIBBLE_MOVE") or
            ("DecimillipedeSegmentBack", "WRITHE_MOVE" or "BULK_MOVE" or "CONSTRICT_MOVE") or
            ("DecimillipedeSegmentFront", "WRITHE_MOVE" or "BULK_MOVE" or "CONSTRICT_MOVE") or
            ("DecimillipedeSegmentMiddle", "WRITHE_MOVE" or "BULK_MOVE" or "CONSTRICT_MOVE") or
            ("Fabricator", "FABRICATING_STRIKE_MOVE") or
            ("FrogKnight", "BEETLE_CHARGE") or
            ("GremlinMerc", "GIMME_MOVE" or "DOUBLE_SMASH_MOVE" or "HEHE_MOVE") or
            ("LivingFog", "ADVANCED_GAS_MOVE" or "BLOAT_MOVE" or "SUPER_GAS_BLAST_MOVE") or
            ("ThievingHopper", "THIEVERY_MOVE" or "NAB_MOVE" or "HAT_TRICK_MOVE") or
            ("TwoTailedRat", "SCRATCH_MOVE" or "DISEASE_BITE_MOVE") or
            ("Zapbot", "ZAP") or
            ("TestSubject", "BITE_MOVE" or "SKULL_BASH_MOVE" or "MULTI_CLAW_MOVE"
                or "PHASE3_LACERATE_MOVE" or "BIG_POUNCE") or
            ("WaterfallGiant", "STOMP_MOVE" or "RAM_MOVE" or "PRESSURE_GUN_MOVE" or "PRESSURE_UP_MOVE");

    private static MoveState RollNext(
        Cursor cursor,
        Rng rng,
        ref bool exact,
        ISet<string> approximationDetails)
    {
        MonsterMoveStateMachine machine = cursor.Monster.MoveStateMachine
            ?? throw new InvalidOperationException($"怪物 {cursor.Monster.Id.Entry} 没有行动状态机。");
        if (cursor.Monster.GetType().Name == "KnowledgeDemon")
        {
            if (cursor.Current.Id == "CURSE_OF_KNOWLEDGE_MOVE")
                cursor.KnowledgeDemonCurseCounter++;
            if (cursor.Current.Id == "PONDER_MOVE")
            {
                string nextId = cursor.KnowledgeDemonCurseCounter < 3
                    ? "CURSE_OF_KNOWLEDGE_MOVE"
                    : "SLAP_MOVE";
                MoveState next = (MoveState)machine.States[nextId];
                cursor.Log.Add(next.Id);
                return next;
            }
        }
        MonsterState state = ResolveFollowUp(machine, cursor.Current);

        for (int guard = 0; guard < 32; guard++)
        {
            switch (state)
            {
                case MoveState move:
                    cursor.Log.Add(move.Id);
                    return move;
                case RandomBranchState random:
                    state = machine.States[MonsterRandomBranchResolver.Pick(
                        machine,
                        random,
                        cursor.Log,
                        rng)];
                    break;
                case ConditionalBranchState conditional:
                    exact = false;
                    approximationDetails.Add($"{cursor.Monster.Id.Entry}.{conditional.Id}:条件分支");
                    state = machine.States[conditional.GetNextState(cursor.Monster.Creature, rng)];
                    break;
                default:
                    exact = false;
                    approximationDetails.Add($"{cursor.Monster.Id.Entry}.{state.Id}:状态分支");
                    state = machine.States[state.GetNextState(cursor.Monster.Creature, rng)];
                    break;
            }
        }

        throw new InvalidOperationException($"怪物 {cursor.Monster.Id.Entry} 的行动状态机未能在 32 步内落到行动节点。");
    }

    private static MonsterState ResolveFollowUp(MonsterMoveStateMachine machine, MoveState move)
    {
        string id = move.FollowUpState?.Id ?? move.FollowUpStateId
            ?? throw new InvalidOperationException($"行动 {move.Id} 没有后继状态。");
        return machine.States[id];
    }

}
