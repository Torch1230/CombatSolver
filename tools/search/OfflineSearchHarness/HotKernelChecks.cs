using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.HotKernel;
using CombatSolver.HotSearch;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;

namespace OfflineSearchHarness;

internal static class HotKernelChecks
{
    private static string ResolveRepoRoot()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.."));

    public static int RunLayoutChecks()
    {
        Console.WriteLine("=== --check-hot-layout ===");
        Stopwatch sw = Stopwatch.StartNew();

        // 1. Struct sizes & offsets validation
        CombatHotState.ValidateLayout();
        Console.WriteLine("[PASS] 1. CombatHotState.ValidateLayout() (FixedCore=1128 B, Total=16232 B)");

        // 2. Populated 30-card state -> Pack -> Unpack -> Pack byte-identical + packed size check
        CombatHotState populated = BuildPopulated30CardState(uidOffset: 100, dirtyPadding: false);
        HotColdSidecar? cold = null;
        using PackedStateArena arena = new();

        int expectedSize = HotStatePacker.ComputePackedSize(in populated);
        // 1128 + 2*64 + 4*24 + 2*8 + 30*48 = 2808 B (or 3 enemies/5 powers -> ~2872 B)
        Check(expectedSize is >= 2500 and <= 3200,
            $"Unexpected 30-card packed size: {expectedSize} B.");

        Span<byte> buf1 = arena.Reserve(expectedSize);
        int packedLen1 = HotStatePacker.Pack(ref populated, ref cold, buf1, canonicalize: true);
        Check(packedLen1 == expectedSize, $"Pack returned {packedLen1}, expected {expectedSize}.");
        HotStateHandle h1 = arena.CommitReserved(packedLen1);
        byte[] packed1 = arena.GetSpan(h1).ToArray();
        HotHash128 hash1 = HotHash.ComputeHash(packed1, cold);

        CombatHotState unpacked = default;
        HotStatePacker.Unpack(packed1, ref unpacked);
        HotColdSidecar? cold2 = cold;
        Span<byte> buf2 = arena.Reserve(expectedSize);
        int packedLen2 = HotStatePacker.Pack(ref unpacked, ref cold2, buf2, canonicalize: true);
        HotStateHandle h2 = arena.CommitReserved(packedLen2);
        byte[] packed2 = arena.GetSpan(h2).ToArray();
        HotHash128 hash2 = HotHash.ComputeHash(packed2, cold2);

        Check(packedLen1 == packedLen2 && packed1.AsSpan().SequenceEqual(packed2),
            "Pack -> Unpack -> Pack was NOT byte-identical.");
        Check(hash1 == hash2 && HotHash.PackedEquals(packed1, cold, packed2, cold2),
            "Pack -> Unpack -> Pack hash/PackedEquals mismatch.");
        Console.WriteLine($"[PASS] 2. Pack -> Unpack -> Pack byte-identical (30-card packed={packedLen1} B, hash={hash1})");

        // 3. CardUid canonicalization dedupe + explicit padding zeroing dedupe
        CombatHotState altUid = BuildPopulated30CardState(uidOffset: 500, dirtyPadding: true);
        HotColdSidecar? altCold = null;
        Span<byte> bufAlt = arena.Reserve(HotStatePacker.ComputePackedSize(in altUid));
        int packedLenAlt = HotStatePacker.Pack(ref altUid, ref altCold, bufAlt, canonicalize: true);
        HotStateHandle hAlt = arena.CommitReserved(packedLenAlt);
        byte[] packedAlt = arena.GetSpan(hAlt).ToArray();
        HotHash128 hashAlt = HotHash.ComputeHash(packedAlt, altCold);

        Check(packedLenAlt == packedLen1 && packed1.AsSpan().SequenceEqual(packedAlt),
            $"CardUid/padding canonicalization dedupe failed at first differing byte {FirstDiffByte(packed1, packedAlt)}.");
        Check(hashAlt == hash1 && HotHash.PackedEquals(packed1, cold, packedAlt, altCold),
            "Canonicalized states failed hash/PackedEquals equality.");
        Console.WriteLine("[PASS] 3. CardUid canonicalization + dirty-padding zeroing dedupe byte-identical");

        // 4. Automated byte-flip mutation test across EVERY byte in packed buffer + non-padding verification
        HashSet<int> paddingOffsets = ComputePaddingAndUnusedOffsets(in populated);
        byte[] mutated = new byte[packed1.Length];
        int totalBytesTested = 0;
        int nonPaddingTested = 0;
        for (int i = 0; i < packed1.Length; i++)
        {
            Buffer.BlockCopy(packed1, 0, mutated, 0, packed1.Length);
            mutated[i] ^= 0xFF;

            HotHash128 mutHash = HotHash.ComputeHash(mutated, null);
            bool mutEq = HotHash.PackedEquals(packed1, null, mutated, null);
            Check(mutHash != hash1, $"HotHash.ComputeHash FAILED to detect 0xFF byte flip at offset {i}.");
            Check(!mutEq, $"HotHash.PackedEquals FAILED to detect 0xFF byte flip at offset {i}.");

            // Also verify single-bit flip detection
            mutated[i] = (byte)(packed1[i] ^ 0x01);
            HotHash128 bitHash = HotHash.ComputeHash(mutated, null);
            bool bitEq = HotHash.PackedEquals(packed1, null, mutated, null);
            Check(bitHash != hash1, $"HotHash.ComputeHash FAILED to detect 0x01 bit flip at offset {i}.");
            Check(!bitEq, $"HotHash.PackedEquals FAILED to detect 0x01 bit flip at offset {i}.");

            totalBytesTested++;
            if (!paddingOffsets.Contains(i))
                nonPaddingTested++;
        }

        // Cold sidecar mutation check
        HotColdSidecar sidecarA = new(
            DecimalVars: [new HotDecimalVarEntry(1, 3.5m)],
            MonsterLogs: [new HotMonsterStateLogOverflow(0, [1, 2, 3])]);
        HotColdSidecar sidecarB = new(
            DecimalVars: [new HotDecimalVarEntry(1, 3.75m)],
            MonsterLogs: [new HotMonsterStateLogOverflow(0, [1, 2, 3])]);
        HotHash128 scHashA = HotHash.ComputeHash(packed1, sidecarA);
        HotHash128 scHashB = HotHash.ComputeHash(packed1, sidecarB);
        Check(scHashA != hash1 && scHashA != scHashB && !HotHash.PackedEquals(packed1, sidecarA, packed1, sidecarB),
            "Cold sidecar mutation was not detected by HotHash/PackedEquals.");
        Console.WriteLine(
            $"[PASS] 4. Automated byte-flip mutation test: {totalBytesTested}/{packed1.Length} total bytes " +
            $"({nonPaddingTested} non-padding bytes, 0xFF & 0x01 flips) + cold sidecar 100% detected");

        // 5. HotRng 1,000-draw + UnstableShuffle bit-exact parity vs MegaCrit.Sts2.Core.Random.Rng
        VerifyHotRngParity();
        Console.WriteLine("[PASS] 5. HotRng 1,000-draw + UnstableShuffle bit-exact parity vs STS2 Rng");

        // 6. HotPiles zero-copy DrawTopToHand, MoveCard slice rotation, Add/Remove, ShuffleDiscardIntoDraw
        VerifyHotPiles();
        Console.WriteLine("[PASS] 6. HotPiles DrawTopToHand, MoveCard, Add/Remove, ShuffleDiscardIntoDraw (0 B alloc)");

        // 7. HotCostModifiers inline merge, overflow, expiration
        VerifyHotCostModifiers();
        Console.WriteLine("[PASS] 7. HotCostModifiers inline merge, >4 cold overflow, turn-end & after-play expiration");

        Console.WriteLine($"ALL --check-hot-layout CHECKS PASSED in {sw.ElapsedMilliseconds} ms");
        return 0;
    }

    private static CombatHotState BuildPopulated30CardState(ushort uidOffset, bool dirtyPadding)
    {
        CombatHotState s = default;
        s.ActiveHookMask = 0x0000_0000_1234_5678UL;
        s.NextCreatureId = 17;
        s.RoundNumber = 3;
        s.PlayerTurnNumber = 3;
        s.ShuffleEventCount = 1;
        s.Energy = 3;
        s.Stars = 2;
        s.Gold = 147;
        s.DrawNextTurn = 1;
        s.LongTermResourceValue = 5;
        s.AngerCopiesGenerated = 2;
        s.BrightestFlameMaxHpSpent = 4;
        s.ShowOffRewards = 1;

        s.Player = new HotCreature
        {
            CombatId = 1,
            CurrentHp = 71,
            MaxHp = 80,
            Block = 12,
            Flags = HotCreatureFlags.Alive | HotCreatureFlags.ActiveInRoster | HotCreatureFlags.IsPlayer,
            SlotIdx = 0,
            Pad = dirtyPadding ? (ushort)0xAAAA : (ushort)0,
        };
        s.Osty = new HotCreature
        {
            CombatId = 2,
            CurrentHp = 15,
            MaxHp = 20,
            Block = 3,
            Flags = HotCreatureFlags.Alive | HotCreatureFlags.ActiveInRoster | HotCreatureFlags.IsOsty | HotCreatureFlags.IsPet,
            SlotIdx = 1,
            Pad = dirtyPadding ? (ushort)0xBBBB : (ushort)0,
        };
        // Leave HasWuppy unset to test zeroing when dirtyPadding=true
        if (dirtyPadding)
        {
            s.Wuppy.CombatId = 999;
            s.Wuppy.CurrentHp = 77;
            s.Wuppy.Pad = 0xCCCC;
        }

        s.CombatFlags = HotCombatFlags.IsInProgress
            | HotCombatFlags.PlayerHooksActive
            | HotCombatFlags.HasOsty
            | HotCombatFlags.HasHistoryCourseCard;

        s.HistoryFinishedPlays = 4;
        s.HistoryEtherealPlays = 1;
        s.HistoryLightningChannels = 2;
        s.HistoryUnblockedHitsReceived = 1;
        s.HistoryCardsDrawn = 14;
        s.HistoryCardsGenerated = 2;

        s.HandCount = 5;
        s.DrawCount = 12;
        s.DiscardCount = 8;
        s.ExhaustCount = 4;
        s.PlayCount = 1;
        s.NextCardUid = (ushort)(uidOffset + 100);

        for (int i = 0; i < 11; i++)
            s.Growth[i] = (ushort)(i + 1);
        if (dirtyPadding)
            s.Growth[11] = 0xBEEF;

        s.CurrentSide = HotCombatSide.Player;
        s.PlayerPhase = 1;
        s.TerminalOutcome = HotCombatOutcome.Ongoing;
        s.MaxHandSize = 10;
        s.EnemyCount = 2;
        s.RosterCount = 2;
        s.PowerCount = 4;
        s.RelicStateCount = 2;
        s.OrbCount = 2;
        s.OrbCapacity = 3;
        s.PotionSlotCount = 3;
        s.ArrowCount = 3;
        if (dirtyPadding)
        {
            s.PadCore0 = 0xAAAA;
            s.PadCore1 = 0xAA;
            s.PadCore2 = 0xBB;
            s.PadCore3 = 0xCC;
        }

        for (int i = 0; i < 16; i++)
            s.Counters[i] = i * 3 + 1;

        for (int i = 0; i < HotLimits.MaxRngStreams; i++)
        {
            s.Rng[i] = HotRng.CreateFromSeed((ulong)(1000 + i));
            HotRng.Advance(ref s.Rng[i], i + 1);
            if (dirtyPadding)
                s.Rng[i].Pad = 0xDEADBEEF;
        }

        s.Potions[0] = 5;
        s.Potions[1] = 0;
        s.Potions[2] = 9;
        if (dirtyPadding)
            s.Potions[5] = 0x7777;

        s.Orbs[0] = new HotOrb { OrbTypeId = 1, Flags = 1, PassiveVal = 3, EvokeVal = 8 };
        s.Orbs[1] = new HotOrb { OrbTypeId = 2, Flags = 0, PassiveVal = 2, EvokeVal = 5 };
        if (dirtyPadding)
            s.Orbs[4] = new HotOrb { OrbTypeId = 9, Flags = 9, PassiveVal = 99, EvokeVal = 999 };

        s.Fool.Arrows[0] = 0x0021;
        s.Fool.Arrows[1] = 0x0002;
        s.Fool.Arrows[2] = 0x0013;
        s.Fool.MelodyStarted[0] = 1;
        s.Fool.MelodyFinished[1] = 2;
        s.Fool.MelodyCardsFinished = 3;
        if (dirtyPadding)
        {
            s.Fool.Arrows[10] = 0xFFFF;
            s.Fool.Pad0 = 0xAA;
            s.Fool.Pad1 = 0xBBBB;
        }

        s.ActiveEnemyRoster[0] = 0;
        s.ActiveEnemyRoster[1] = 1;
        if (dirtyPadding)
            s.ActiveEnemyRoster[5] = 0x55;

        // 2 enemies
        for (int e = 0; e < 2; e++)
        {
            ref HotEnemy enemy = ref s.Enemies[e];
            enemy.Vital = new HotCreature
            {
                CombatId = (uint)(10 + e),
                CurrentHp = 40 + e * 10,
                MaxHp = 50 + e * 10,
                Block = e * 5,
                Flags = HotCreatureFlags.Alive | HotCreatureFlags.ActiveInRoster,
                SlotIdx = (byte)(3 + e),
                Pad = dirtyPadding ? (ushort)0xDDDD : (ushort)0,
            };
            enemy.MonsterDefId = (ushort)(1 + e);
            enemy.CurrentMoveNode = (short)(e + 1);
            enemy.DeathPhase = HotDeathPhase.None;
            enemy.Flags = HotEnemyFlags.Awake;
            enemy.RecentMoveCount = 2;
            enemy.RecentMoves[0] = 1;
            enemy.RecentMoves[1] = 2;
            if (dirtyPadding)
                enemy.RecentMoves[5] = 0xEE;
            enemy.EverUsedMovesMask = 0b110UL;
            enemy.AiRegs[0] = 7 + e;
        }

        // 4 powers (power 0 references card index 3)
        ushort refUidPower = (ushort)(uidOffset + 3);
        ushort refUidMelody = (ushort)(uidOffset + 7);
        ushort refUidHistory = (ushort)(uidOffset + 12);
        s.Fool.FirstPlayCardUid[0] = refUidMelody;
        s.HistoryCourseUid = refUidHistory;

        for (int p = 0; p < 4; p++)
        {
            s.Powers[p] = new HotPower
            {
                PowerDefId = (ushort)(1 + p),
                OwnerSlot = (byte)(p == 0 ? 0 : 3),
                ApplierSlot = 0,
                TargetSlot = 0xFF,
                Flags = p == 1 ? HotPowerFlags.SkipNextDurationTick : HotPowerFlags.None,
                AuxCardUid = p == 0 ? refUidPower : (ushort)0,
                Amount = p + 2,
                AmountOnTurnStart = p + 1,
                Aux0 = p * 10,
                Aux1 = p * 20,
            };
        }

        // 2 relic states
        s.RelicStates[0] = new HotRelicState { Current = 3, PreviousOrAux = 1 };
        s.RelicStates[1] = new HotRelicState { Current = 6, PreviousOrAux = 0 };

        // 30 cards across Hand(5)|Draw(12)|Discard(8)|Exhaust(4)|Play(1)
        for (int i = 0; i < 30; i++)
        {
            ref HotCard c = ref s.Cards[i];
            c.CardDefId = (ushort)(1 + (i % 6));
            c.CardUid = (ushort)(uidOffset + i);
            c.UpgradeLevel = (byte)(i % 2);
            c.BaseEnergyCost = (sbyte)(1 + (i % 3));
            c.BaseStarCost = (sbyte)(i == 4 ? 1 : -1);
            c.BaseReplayCount = (byte)(i % 2);
            c.LocalKeywordsMask = i == 2 ? HotCardKeywordMask.Exhaust : HotCardKeywordMask.None;
            c.Flags = HotCardFlags.HasDeckVersion;
            c.EnchId = (byte)(i == 1 ? 1 : 0);
            c.EnchAmt = (short)(i == 1 ? 2 : 0);
            c.Var0Delta = (short)(i % 3);
            c.Var1Delta = (short)(i % 2);
            c.CardPrivate = i * 5;
            if (i % 5 == 0)
            {
                c.CostModCount = 1;
                c.EnergyCostMods[0] = new HotEnergyCostMod
                {
                    Amount = -1,
                    PackedFlags = HotEnergyCostMod.PackFlags(false, LocalCostModifierExpiration.EndOfTurn, false),
                };
            }
            if (dirtyPadding)
            {
                c.EnergyCostMods[3] = new HotEnergyCostMod { Amount = 99, PackedFlags = 0x0F };
                c.TempStarCostPacked = 0x3F; // high bit 0 -> unused, should be zeroed
            }
        }

        s.HistoryCourseCard = s.Cards[12];
        if (dirtyPadding)
            s.HistoryCourseCard.EnergyCostMods[2] = new HotEnergyCostMod { Amount = 55, PackedFlags = 0x05 };

        return s;
    }

    private static HashSet<int> ComputePaddingAndUnusedOffsets(in CombatHotState s)
    {
        HashSet<int> pads = [];
        void AddRange(int start, int length)
        {
            for (int i = 0; i < length; i++)
                pads.Add(start + i);
        }

        // FixedCore explicit pads (verified by CombatHotState.ValidateLayout)
        AddRange(80 + 18, 2);   // Player.Pad
        if ((s.CombatFlags & HotCombatFlags.HasOsty) == 0) AddRange(100, 20); else AddRange(100 + 18, 2);
        if ((s.CombatFlags & HotCombatFlags.HasWuppy) == 0) AddRange(120, 20); else AddRange(120 + 18, 2);
        AddRange(182, 2);       // PadCore0
        AddRange(184 + 22, 2);  // Growth[11]
        AddRange(221, 3);       // PadCore1..3
        for (int i = 0; i < HotLimits.MaxRngStreams; i++)
            AddRange(480 + i * 40 + 36, 4); // Rng[i].Pad
        for (int i = s.PotionSlotCount; i < HotLimits.MaxPotions; i++)
            AddRange(880 + i * 2, 2);
        for (int i = s.OrbCount; i < HotLimits.MaxOrbs; i++)
            AddRange(904 + i * 8, 8);
        for (int i = s.ArrowCount; i < HotLimits.MaxArrows; i++)
            AddRange(984 + i * 2, 2);
        AddRange(984 + 71, 1);  // Fool.Pad0
        AddRange(984 + 78, 2);  // Fool.Pad1
        for (int i = s.RosterCount; i < HotLimits.MaxEnemies; i++)
            AddRange(1064 + i, 1);
        if ((s.CombatFlags & HotCombatFlags.HasHistoryCourseCard) == 0)
            AddRange(1080, 48);
        else
        {
            int hcMods = Math.Min((int)s.HistoryCourseCard.CostModCount, HotLimits.MaxEnergyCostMods);
            AddRange(1080 + 16 + hcMods * 3, (HotLimits.MaxEnergyCostMods - hcMods) * 3);
        }

        int offset = CombatHotState.FixedCoreSize;
        for (int e = 0; e < s.EnemyCount; e++)
        {
            AddRange(offset + 18, 2); // Vital.Pad
            int rc = Math.Min((int)s.Enemies[e].RecentMoveCount, HotLimits.MaxMonsterRecentMoves);
            AddRange(offset + 40 + rc, HotLimits.MaxMonsterRecentMoves - rc);
            offset += 64;
        }
        offset += s.PowerCount * 24;
        offset += s.RelicStateCount * 8;
        int totalCards = s.TotalCardCount;
        for (int c = 0; c < totalCards; c++)
        {
            int mc = Math.Min((int)s.Cards[c].CostModCount, HotLimits.MaxEnergyCostMods);
            AddRange(offset + 16 + mc * 3, (HotLimits.MaxEnergyCostMods - mc) * 3);
            offset += 48;
        }
        return pads;
    }

    private static int FirstDiffByte(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
            if (a[i] != b[i]) return i;
        return a.Length == b.Length ? -1 : n;
    }

    private static void VerifyHotRngParity()
    {
        ulong[] seeds = [0UL, 1UL, 42UL, 0xDEADBEEFCAFEBABEUL, 0x9E3779B97F4A7C15UL];
        foreach (ulong seed in seeds)
        {
            Rng native = new(seed);
            HotRngState hot = HotRng.CreateFromSeed(seed);
            AssertRngStateEq(native, in hot, $"seed={seed} init");

            for (int i = 0; i < 1000; i++)
            {
                switch (i % 6)
                {
                    case 0:
                        Check(native.NextUnsignedLong() == HotRng.NextULong(ref hot), $"NextULong mismatch at draw {i} seed {seed}");
                        break;
                    case 1:
                        Check(BitConverter.DoubleToInt64Bits(native.NextDouble()) == BitConverter.DoubleToInt64Bits(HotRng.NextDouble(ref hot)),
                            $"NextDouble mismatch at draw {i} seed {seed}");
                        break;
                    case 2:
                        Check(BitConverter.SingleToInt32Bits(native.NextFloat()) == BitConverter.SingleToInt32Bits(HotRng.NextFloat(ref hot)),
                            $"NextFloat mismatch at draw {i} seed {seed}");
                        break;
                    case 3:
                        int max = (i % 97) + 1;
                        Check(native.NextInt(max) == HotRng.NextInt(ref hot, max), $"NextInt({max}) mismatch at draw {i} seed {seed}");
                        break;
                    case 4:
                        int lo = -10 + (i % 15);
                        int hi = lo + 1 + (i % 30);
                        Check(native.NextInt(lo, hi) == HotRng.NextInt(ref hot, lo, hi), $"NextInt({lo},{hi}) mismatch at draw {i} seed {seed}");
                        break;
                    case 5:
                        Check(native.NextBool() == HotRng.NextBool(ref hot), $"NextBool mismatch at draw {i} seed {seed}");
                        break;
                }
            }
            AssertRngStateEq(native, in hot, $"seed={seed} after 1000 draws");

            // UnstableShuffle parity across multiple list sizes
            int[] lengths = [0, 1, 2, 5, 13, 30, 50];
            foreach (int len in lengths)
            {
                List<int> nativeList = Enumerable.Range(0, len).ToList();
                int[] hotArr = Enumerable.Range(0, len).ToArray();
                nativeList.UnstableShuffle(native);
                HotRng.UnstableShuffle(hotArr.AsSpan(), ref hot);
                for (int k = 0; k < len; k++)
                    Check(nativeList[k] == hotArr[k], $"UnstableShuffle mismatch seed={seed} len={len} idx={k}");
                AssertRngStateEq(native, in hot, $"seed={seed} shuffle len={len}");
            }
        }
    }

    private static void AssertRngStateEq(Rng native, in HotRngState hot, string ctx)
    {
        var ser = native.ToSerializable();
        Check(ser.counter == hot.Counter
            && ser.state0 == hot.S0
            && ser.state1 == hot.S1
            && ser.state2 == hot.S2
            && ser.state3 == hot.S3,
            $"HotRng state diverged ({ctx}): native=({ser.counter},{ser.state0:X},{ser.state1:X},{ser.state2:X},{ser.state3:X}) " +
            $"hot=({hot.Counter},{hot.S0:X},{hot.S1:X},{hot.S2:X},{hot.S3:X})");
    }

    private static void VerifyHotPiles()
    {
        CombatHotState s = default;
        s.MaxHandSize = 10;
        s.Rng[(int)HotRngStream.Shuffle] = HotRng.CreateFromSeed(12345UL);

        // Add cards: Hand=[1,2], Draw=[3,4,5], Discard=[6,7], Exhaust=[8], Play=[]
        for (ushort id = 1; id <= 2; id++)
            HotPiles.AddCard(ref s, new HotCard { CardDefId = id, CardUid = id }, HotPileKind.Hand, s.HandCount);
        for (ushort id = 3; id <= 5; id++)
            HotPiles.AddCard(ref s, new HotCard { CardDefId = id, CardUid = id }, HotPileKind.Draw, s.DrawCount);
        for (ushort id = 6; id <= 7; id++)
            HotPiles.AddCard(ref s, new HotCard { CardDefId = id, CardUid = id }, HotPileKind.Discard, s.DiscardCount);
        HotPiles.AddCard(ref s, new HotCard { CardDefId = 8, CardUid = 8 }, HotPileKind.Exhaust, 0);

        // Zero-copy DrawTopToHand: Draw[0]=3 -> Hand[^1]
        Check(HotPiles.DrawTopToHand(ref s), "DrawTopToHand returned false.");
        Check(s.HandCount == 3 && s.DrawCount == 2 && s.Cards[2].CardDefId == 3 && s.Cards[3].CardDefId == 4,
            "DrawTopToHand zone boundary mismatch.");

        // MoveCard Hand[1](id=2) -> Play[0]
        int playIdx = HotPiles.MoveCard(ref s, 1, HotPileKind.Play, 0);
        Check(s.HandCount == 2 && s.PlayCount == 1 && s.Cards[playIdx].CardDefId == 2,
            "MoveCard Hand->Play mismatch.");
        Check(s.Cards[0].CardDefId == 1 && s.Cards[1].CardDefId == 3,
            "Hand order corrupted after MoveCard.");

        // MoveCard Play[0](id=2) -> Discard end
        int discIdx = HotPiles.MoveCard(ref s, playIdx, HotPileKind.Discard, s.DiscardCount);
        Check(s.PlayCount == 0 && s.DiscardCount == 3 && s.Cards[discIdx].CardDefId == 2,
            "MoveCard Play->Discard mismatch.");
        // Discard should be [6, 7, 2]
        int dStart = HotPiles.ZoneStart(in s, HotPileKind.Discard);
        Check(s.Cards[dStart].CardDefId == 6 && s.Cards[dStart + 1].CardDefId == 7 && s.Cards[dStart + 2].CardDefId == 2,
            "Discard order corrupted.");

        // RemoveCard from Exhaust
        HotPiles.RemoveCard(ref s, HotPiles.ZoneStart(in s, HotPileKind.Exhaust));
        Check(s.ExhaustCount == 0 && s.TotalCardCount == 7, "RemoveCard mismatch.");

        // ShuffleDiscardIntoDraw: verify exact match against sorted Discard+Draw + native UnstableShuffle AND 0-byte alloc
        HotExecScratch scratch = new();
        // Warmup once on throwaway copy
        CombatHotState warm = s;
        HotPiles.ShuffleDiscardIntoDraw(ref warm, null, scratch);

        Rng nativeRng = new(12345UL);
        // Combined cards in Draw+Discard sorted by CardDefId: [2, 4, 5, 6, 7]
        List<ushort> expectedOrder = [2, 4, 5, 6, 7];
        expectedOrder.UnstableShuffle(nativeRng);

        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        HotPiles.ShuffleDiscardIntoDraw(ref s, null, scratch);
        long allocAfter = GC.GetAllocatedBytesForCurrentThread();

        Check(allocAfter - allocBefore == 0, $"ShuffleDiscardIntoDraw allocated {allocAfter - allocBefore} bytes (expected 0).");
        Check(s.DrawCount == 5 && s.DiscardCount == 0 && s.ShuffleEventCount == 1,
            "ShuffleDiscardIntoDraw counts mismatch.");
        for (int i = 0; i < 5; i++)
            Check(s.Cards[s.DrawStart + i].CardDefId == expectedOrder[i],
                $"ShuffleDiscardIntoDraw order mismatch at {i}: got {s.Cards[s.DrawStart + i].CardDefId}, expected {expectedOrder[i]}.");
        AssertRngStateEq(nativeRng, in s.Rng[(int)HotRngStream.Shuffle], "ShuffleDiscardIntoDraw RNG");
    }

    private static void VerifyHotCostModifiers()
    {
        HotCard card = new() { CardDefId = 1, CardUid = 10, BaseEnergyCost = 2, BaseStarCost = -1 };
        HotColdSidecar? cold = null;

        // Add 2 consecutive EndOfTurn relative modifiers -> should merge to -2 in 1 slot
        HotCostModifiers.AddEnergyModifier(ref card, ref cold, -1, false, (int)LocalCostModifierExpiration.EndOfTurn, false);
        HotCostModifiers.AddEnergyModifier(ref card, ref cold, -1, false, (int)LocalCostModifierExpiration.EndOfTurn, false);
        Check(card.CostModCount == 1 && HotCostModifiers.GetLocalEnergyCost(in card, cold) == 0 && cold == null,
            "Consecutive relative cost modifier merge failed.");

        // Add 4 distinct expiration/mode modifiers to force overflow (>4)
        HotCostModifiers.AddEnergyModifierExact(ref card, ref cold, 1, false, (int)LocalCostModifierExpiration.WhenPlayed, false);
        HotCostModifiers.AddEnergyModifierExact(ref card, ref cold, 1, false, (int)LocalCostModifierExpiration.EndOfCombat, false);
        HotCostModifiers.AddEnergyModifierExact(ref card, ref cold, 1, false, (int)LocalCostModifierExpiration.EndOfTurn, false);
        HotCostModifiers.AddEnergyModifierExact(ref card, ref cold, 1, false, (int)LocalCostModifierExpiration.WhenPlayed, false);
        Check((card.Flags & HotCardFlags.HasCostOverflow) != 0 && cold?.CostOverflow?.Length == 1,
            "Cost modifier overflow transition failed.");
        Check(HotCostModifiers.GetLocalEnergyCost(in card, cold) == 4,
            $"Unexpected overflow local energy cost: {HotCostModifiers.GetLocalEnergyCost(in card, cold)}.");

        // ExpireAfterPlay removes both WhenPlayed entries -> demotes back to 3 inline slots, cold becomes null
        HotCostModifiers.ExpireAfterPlay(ref card, ref cold);
        Check((card.Flags & HotCardFlags.HasCostOverflow) == 0 && card.CostModCount == 3 && cold == null,
            "Overflow demotion after ExpireAfterPlay failed.");
        Check(HotCostModifiers.GetLocalEnergyCost(in card, cold) == 2,
            $"Unexpected cost after ExpireAfterPlay: {HotCostModifiers.GetLocalEnergyCost(in card, cold)}.");
    }

    public static int RunParityChecks(string[] args)
    {
        bool runA = true, runB1 = true, runB2 = true, runC = true;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--gate" && i + 1 < args.Length)
            {
                string gate = args[++i].ToLowerInvariant();
                runA = gate.Contains('a');
                bool hasB1 = gate.Contains("b1");
                bool hasB2 = gate.Contains("b2");
                if (hasB1 || hasB2)
                {
                    runB1 = hasB1;
                    runB2 = hasB2;
                }
                else
                {
                    runB1 = gate.Contains('b');
                    runB2 = gate.Contains('b');
                }
                runC = gate.Contains('c');
            }
        }

        string repoRoot = ResolveRepoRoot();
        string outDir = Path.Combine(repoRoot, ".local", "offline-harness", "hot-parity");
        Directory.CreateDirectory(outDir);

        HarnessLog.TraceInit = false;
        HarnessLog.VerboseGameLog = false;
        HarnessLog.Language = "eng";

        MainLoopContext loop = new();
        SynchronizationContext.SetSynchronizationContext(loop);

        GameBootstrap.ApplyGodotBypasses();
        _ = GameBootstrap.SkipGodotNodeStaticConstructors();
        _ = GameBootstrap.InitializeStaticState();
        HarnessOptions bootOptions = new()
        {
            WorkspaceDirectory = Path.GetDirectoryName(outDir)!,
            OutputDirectory = outDir,
            Profile = "Custom",
            Backend = "legacy",
        };
        _ = ModRuntime.Initialize(bootOptions);

        int failures = 0;
        try
        {
            if (runA)
            {
                Console.WriteLine();
                Console.WriteLine("=== Gate A: Root Capture Parity (19 Vanilla Roots) ===");
                failures += RunGateA(repoRoot, outDir, loop);
            }

            if (runB1)
            {
                Console.WriteLine();
                Console.WriteLine("=== Gate B1: Phase 0 Legacy Route Lockstep Parity (7 Elite/Boss Roots) ===");
                failures += RunGateB1RouteReplay(repoRoot, outDir, loop);
            }

            if (runB2)
            {
                Console.WriteLine();
                Console.WriteLine("=== Gate B2: Multi-Turn Lockstep Action Parity (All 6 Characters incl. Fool) ===");
                failures += RunGateB(loop);
            }

            if (runC)
            {
                Console.WriteLine();
                Console.WriteLine("=== Gate C: Raw Kernel Micro-Benchmark (50,000 Hot vs 5,000 Legacy) ===");
                failures += RunGateC(loop);
            }
        }
        finally
        {
            CleanupActiveRun();
            ModRuntime.Session?.Dispose();
            ModRuntime.FlushDiagnostics();
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "ALL --verify-hot-parity GATES PASSED (0 failures)"
            : $"--verify-hot-parity FAILED ({failures} failures)");
        return failures == 0 ? 0 : 1;
    }

    private static int RunGateA(string repoRoot, string outDir, MainLoopContext loop)
    {
        List<(string Label, UnattendedTestRequest Request)> roots = [];

        string noveltyDir = Path.Combine(repoRoot, "coverage", "corpora", "novelty");
        foreach (string path in Directory.GetFiles(noveltyDir, "dev-*.json")
            .Concat(Directory.GetFiles(noveltyDir, "holdout-*.json"))
            .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal))
        {
            string label = Path.GetFileNameWithoutExtension(path);
            UnattendedTestRequest req = JsonSerializer.Deserialize<UnattendedTestRequest>(
                JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    scenarioId = label,
                    cards = Array.Empty<object>(),
                    generatedScenarioPath = path,
                }),
                UnattendedTestFiles.JsonOptions)!;
            roots.Add((label, req));
        }

        string strategyDir = Path.Combine(repoRoot, "coverage", "corpora", "strategy", "roots");
        foreach (string name in new[] { "GA-IRONCLAD-ELITE-00-request.json", "GA-SILENT-BOSS-00-request.json" })
        {
            string reqPath = Path.Combine(strategyDir, name);
            UnattendedTestRequest req = GeneratedScenarioSetup.ReadRequest(reqPath);
            if (req.GeneratedScenarioPath != null && !Path.IsPathRooted(req.GeneratedScenarioPath))
            {
                string absSpec = Path.GetFullPath(Path.Combine(repoRoot, req.GeneratedScenarioPath));
                var json = JsonSerializer.SerializeToNode(req, UnattendedTestFiles.JsonOptions)!.AsObject();
                json["generatedScenarioPath"] = absSpec;
                req = json.Deserialize<UnattendedTestRequest>(UnattendedTestFiles.JsonOptions)!;
            }
            roots.Add((req.ScenarioId, req));
        }

        Check(roots.Count == 19, $"Expected 19 vanilla roots, found {roots.Count}.");

        int passed = 0;
        int failed = 0;
        using PackedStateArena arena = new();

        foreach ((string label, UnattendedTestRequest request) in roots)
        {
            CleanupActiveRun();
            IDisposable? choiceScope = null;
            try
            {
                string evidenceDir = Path.Combine(outDir, "gate-a-evidence", label);
                Directory.CreateDirectory(evidenceDir);
                GeneratedScenarioSetup setup = GeneratedScenarioSetup.Prepare(request, evidenceDir);
                Task<IDisposable?> enter = setup.EnterCombatRoomAsync();
                loop.RunUntilCompleted(enter, TimeSpan.FromSeconds(120), $"GateA Enter {label}");
                choiceScope = enter.GetAwaiter().GetResult();

                CombatState combat = OfflineCombat.WaitForPlayableCombat(loop);
                setup.CaptureOpening(combat);
                if (!setup.Request.PreserveNativeCombatStateForTest)
                {
                    loop.RunUntilCompleted(
                        setup.Session.InjectInitialStateAsync(combat, LocalContext.GetMe(combat)!),
                        TimeSpan.FromSeconds(30), $"GateA Inject {label}");
                }

                string liveRaw = ContinuationStamp.CaptureLive(combat).StateText;
                string liveStripped = HotContinuationStamp.StripModStampSections(liveRaw);

                HotRootBundle bundle = HotRootBuilder.BuildFromLive(combat);
                CombatHotState rootState = bundle.RootState;
                string hotText = HotContinuationStamp.CaptureFromHot(in rootState, bundle.RootCold, bundle.Rules);

                if (string.Equals(liveStripped, hotText, StringComparison.Ordinal))
                {
                    // Also verify Pack -> Unpack round-trip preserves 0-diff
                    CombatHotState rtState = bundle.RootState;
                    HotColdSidecar? rtCold = bundle.RootCold;
                    arena.Reset();
                    int size = HotStatePacker.ComputePackedSize(in rtState);
                    Span<byte> buf = arena.Reserve(size);
                    int written = HotStatePacker.Pack(ref rtState, ref rtCold, buf, canonicalize: true);
                    HotStateHandle handle = arena.CommitReserved(written);
                    CombatHotState unpacked = default;
                    HotStatePacker.Unpack(arena.GetSpan(handle), ref unpacked);
                    string rtText = HotContinuationStamp.CaptureFromHot(in unpacked, rtCold, bundle.Rules);

                    if (!string.Equals(liveStripped, rtText, StringComparison.Ordinal))
                    {
                        var diffs = new ContinuationStamp(liveStripped).DescribeDifferences(new ContinuationStamp(rtText), 5);
                        Console.WriteLine($"[FAIL] Gate A {label} (Pack/Unpack round-trip diff): {string.Join(" | ", diffs)}");
                        failed++;
                    }
                    else
                    {
                        Console.WriteLine(
                            $"[PASS] Gate A {label}: 0-diff (packed={written} B, cards={bundle.RootState.TotalCardCount}, " +
                            $"enemies={bundle.RootState.EnemyCount}, powers={bundle.RootState.PowerCount}, cold={(bundle.RootCold == null ? "null" : "set")})");
                        passed++;
                    }
                }
                else
                {
                    var diffs = new ContinuationStamp(liveStripped).DescribeDifferences(new ContinuationStamp(hotText), 5);
                    Console.WriteLine($"[FAIL] Gate A {label}: {diffs.Count} diff(s):");
                    foreach (string d in diffs)
                        Console.WriteLine($"       -> {d}");
                    failed++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FAIL] Gate A {label}: {ex.GetType().Name}: {ex.Message}");
                failed++;
            }
            finally
            {
                choiceScope?.Dispose();
                CleanupActiveRun();
            }
        }

        Console.WriteLine($"Gate A Summary: {passed}/{roots.Count} PASS, {failed} FAIL");
        return failed;
    }

    private static int RunGateB1RouteReplay(string repoRoot, string outDir, MainLoopContext loop)
    {
        (string Label, string RequestPath, string RoutePath)[] cases =
        [
            ("dev-00-ironclad-elite",
                Path.Combine(repoRoot, ".local", "bench", "phase0", "dev-00-ironclad-elite-request.json"),
                Path.Combine(repoRoot, ".local", "bench", "phase0", "out-coord-dop8", "runs", "dev-00-ironclad-elite", "route.json")),
            ("dev-01-silent-elite",
                Path.Combine(repoRoot, ".local", "bench", "phase0", "dev-01-silent-elite-request.json"),
                Path.Combine(repoRoot, ".local", "bench", "phase0", "out-coord-dop8", "runs", "dev-01-silent-elite", "route.json")),
            ("dev-02-defect-elite",
                Path.Combine(repoRoot, ".local", "bench", "phase0", "dev-02-defect-elite-request.json"),
                Path.Combine(repoRoot, ".local", "bench", "phase0", "out-coord-dop8", "runs", "dev-02-defect-elite", "route.json")),
            ("dev-03-regent-elite",
                Path.Combine(repoRoot, ".local", "bench", "phase0", "dev-03-regent-elite-request.json"),
                Path.Combine(repoRoot, ".local", "bench", "phase0", "out-coord-dop8", "runs", "dev-03-regent-elite", "route.json")),
            ("dev-04-necrobinder-elite",
                Path.Combine(repoRoot, ".local", "bench", "phase0", "dev-04-necrobinder-elite-request.json"),
                Path.Combine(repoRoot, ".local", "bench", "phase0", "out-coord-dop8", "runs", "dev-04-necrobinder-elite", "route.json")),
            ("dev-08-regent-boss",
                Path.Combine(repoRoot, ".local", "bench", "phase0", "dev-08-regent-boss-request.json"),
                Path.Combine(repoRoot, ".local", "bench", "phase0", "out-coord-dop8", "runs", "dev-08-regent-boss", "route.json")),
            ("GA-IRONCLAD-ELITE-00",
                Path.Combine(repoRoot, "coverage", "corpora", "strategy", "roots", "GA-IRONCLAD-ELITE-00-request.json"),
                Path.Combine(repoRoot, ".local", "bench", "phase0", "out-eval-dop1", "runs", "GA-IRONCLAD-ELITE-00", "route.json")),
        ];

        int totalSteps = 0;
        int passedRoots = 0;
        int failedRoots = 0;

        foreach ((string label, string requestPath, string routePath) in cases)
        {
            CleanupActiveRun();
            IDisposable? choiceScope = null;
            try
            {
                if (!File.Exists(routePath))
                {
                    Console.WriteLine($"[FAIL] Gate B1 {label}: missing route file {routePath}");
                    failedRoots++;
                    continue;
                }

                PlanAction[] route = JsonSerializer.Deserialize<PlanAction[]>(
                    File.ReadAllText(routePath), UnattendedTestFiles.JsonOptions) ?? [];
                Check(route.Length > 0, $"Empty route.json for {label}.");

                UnattendedTestRequest req = GeneratedScenarioSetup.ReadRequest(requestPath);
                if (req.GeneratedScenarioPath != null && !Path.IsPathRooted(req.GeneratedScenarioPath))
                {
                    string absSpec = Path.GetFullPath(Path.Combine(repoRoot, req.GeneratedScenarioPath));
                    var json = JsonSerializer.SerializeToNode(req, UnattendedTestFiles.JsonOptions)!.AsObject();
                    json["generatedScenarioPath"] = absSpec;
                    req = json.Deserialize<UnattendedTestRequest>(UnattendedTestFiles.JsonOptions)!;
                }

                string evidenceDir = Path.Combine(outDir, "gate-b1-evidence", label);
                Directory.CreateDirectory(evidenceDir);
                GeneratedScenarioSetup setup = GeneratedScenarioSetup.Prepare(req, evidenceDir);
                Task<IDisposable?> enter = setup.EnterCombatRoomAsync();
                loop.RunUntilCompleted(enter, TimeSpan.FromSeconds(120), $"GateB1 Enter {label}");
                choiceScope = enter.GetAwaiter().GetResult();

                CombatState combat = OfflineCombat.WaitForPlayableCombat(loop);
                setup.CaptureOpening(combat);
                if (!setup.Request.PreserveNativeCombatStateForTest)
                {
                    loop.RunUntilCompleted(
                        setup.Session.InjectInitialStateAsync(combat, LocalContext.GetMe(combat)!),
                        TimeSpan.FromSeconds(30), $"GateB1 Inject {label}");
                }

                HotRootBundle bundle = HotRootBuilder.BuildFromLive(combat);
                CombatRootSnapshot legacyRoot = CombatRootSnapshot.Capture(combat);
                SolverDisplayNames names = SolverDisplayNames.Capture(combat);
                BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
                SolverSettingsSnapshot settings = SolverSettings.Capture();
                SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(
                    settings, combat, includeTurnSetup: false, theftPolicy: SolverController.ResolveTheftPolicy(combat));
                CombatBeamSolver solver = new(legacyRoot, names, damage, policy, searchProfile: policy.Profile);

                CombatHotState hotState = bundle.RootState;
                HotColdSidecar? hotCold = bundle.RootCold;
                HotExecScratch scratch = new();
                List<PlanAction> history = [];
                int rootSteps = 0;
                bool rootFailed = false;

                for (int step = 0; step < route.Length; step++)
                {
                    PlanAction legacyAction = route[step];
                    HotParallelExpander.AssignDeterministicCardUids(ref hotState);

                    HotAction hotAction = MapLegacyPlanActionToHot(legacyAction, in hotState, hotCold, bundle.Rules);
                    List<(PlanCardChoice Choice, PlanCardToken? Token)> choiceCursor = FlattenPlanChoices(legacyAction);
                    int cursorIdx = 0;
                    List<HotChoiceSelection> hotChoices = [];

                    HotStepResult hotRes;
                    int choiceGuard = 0;
                    while (true)
                    {
                        CombatHotState probe = hotState;
                        HotColdSidecar? probeCold = hotCold;
                        HotStepResult probeRes = HotCombatEngine.Step(
                            ref probe, ref probeCold, bundle.Rules, scratch, hotAction,
                            CollectionsMarshal.AsSpan(hotChoices));
                        if (!probeRes.NeedsChoice)
                        {
                            hotState = probe;
                            hotCold = probeCold;
                            hotRes = probeRes;
                            break;
                        }

                        if (++choiceGuard > HotLimits.MaxChoicesPerAction)
                        {
                            Console.WriteLine(
                                $"[FAIL] Gate B1 {label} step={step + 1}/{route.Length} ({legacyAction.Kind}:{legacyAction.CardId}{legacyAction.PotionId}): " +
                                $"exceeded MaxChoicesPerAction resolving pending choice {scratch.PendingChoiceKind}");
                            rootFailed = true;
                            hotRes = probeRes;
                            break;
                        }

                        HotChoiceSelection sel = ResolvePlanChoiceSelection(
                            in probe, probeCold, bundle.Rules, scratch, choiceCursor, ref cursorIdx);
                        hotChoices.Add(sel);
                    }

                    if (rootFailed)
                        break;

                    if (!hotRes.Executed)
                    {
                        Console.WriteLine(
                            $"[FAIL] Gate B1 {label} step={step + 1}/{route.Length} ({legacyAction.Kind}:{legacyAction.CardId}{legacyAction.PotionId}): " +
                            $"HotCombatEngine.Step returned Executed=false (energy={hotState.Energy}, stars={hotState.Stars}, hand={hotState.HandCount})");
                        rootFailed = true;
                        break;
                    }

                    history.Add(legacyAction);
                    SimulationSnapshot legacySnap = solver.ReplayDiagnosticPrefix(history);
                    try
                    {
                        rootSteps++;
                        totalSteps++;

                        HotCombatOutcome legacyOutcome = legacySnap.PlayerDead
                            ? HotCombatOutcome.Defeat
                            : legacySnap.AllEnemiesDead
                                ? HotCombatOutcome.Victory
                                : HotCombatOutcome.Ongoing;
                        if (hotRes.Outcome != legacyOutcome)
                        {
                            Console.WriteLine(
                                $"[FAIL] Gate B1 {label} step={step + 1}/{route.Length} ({legacyAction.Kind}:{legacyAction.CardId}{legacyAction.PotionId}): " +
                                $"outcome mismatch hot={hotRes.Outcome} legacy={legacyOutcome}");
                            rootFailed = true;
                            break;
                        }

                        string legacyStamp = HotContinuationStamp.StripModStampSections(
                            solver.CaptureDiagnosticContinuation(legacySnap).StateText);
                        string hotStamp = HotContinuationStamp.CaptureFromHot(in hotState, hotCold, bundle.Rules);

                        if (!string.Equals(legacyStamp, hotStamp, StringComparison.Ordinal))
                        {
                            var diffs = new ContinuationStamp(legacyStamp).DescribeDifferences(new ContinuationStamp(hotStamp), 5);
                            Console.WriteLine(
                                $"[FAIL] Gate B1 {label} step={step + 1}/{route.Length} ({legacyAction.Kind}:{legacyAction.CardId}{legacyAction.PotionId} turn={legacyAction.Turn}):");
                            foreach (string d in diffs)
                                Console.WriteLine($"       -> {d}");
                            rootFailed = true;
                            break;
                        }
                    }
                    finally
                    {
                        legacySnap.ReleaseSimulator();
                    }
                }

                if (rootFailed)
                {
                    failedRoots++;
                }
                else
                {
                    Console.WriteLine($"[PASS] Gate B1 {label}: {rootSteps}/{route.Length} route steps 0-diff (outcome={hotState.TerminalOutcome})");
                    passedRoots++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FAIL] Gate B1 {label}: {ex.GetType().Name}: {ex.Message}");
                failedRoots++;
            }
            finally
            {
                choiceScope?.Dispose();
                CleanupActiveRun();
            }
        }

        Console.WriteLine($"Gate B1 Summary: {passedRoots}/{cases.Length} roots PASS ({totalSteps} route steps), {failedRoots} FAIL");
        return failedRoots;
    }

    private static HotAction MapLegacyPlanActionToHot(
        PlanAction action,
        in CombatHotState state,
        HotColdSidecar? cold,
        HotRules rules)
    {
        if (action.Kind == PlanActionKind.EndTurn)
            return HotAction.EndTurn();

        int targetSlot = -1;
        if (action.TargetCombatId is uint targetCid)
        {
            for (int e = 0; e < state.EnemyCount; e++)
            {
                if (state.Enemies[e].Vital.CombatId == targetCid)
                {
                    targetSlot = e;
                    break;
                }
            }
            Check(targetSlot >= 0, $"TargetCombatId #{targetCid} not found in HotState for {action.Kind}:{action.CardId}{action.PotionId}.");
        }

        if (action.Kind == PlanActionKind.UsePotion)
            return HotAction.UsePotion(action.PotionSlot, targetSlot);

        // Match hand card by CardStateKey + CardStateOccurrence, fallback to CardId + CardOccurrence
        if (!string.IsNullOrEmpty(action.CardStateKey))
        {
            int occ = action.CardStateOccurrence;
            for (int i = 0; i < state.HandCount; i++)
            {
                string key = HotContinuationStamp.FormatChoiceCardKey(in state.Cards[i], cold, rules);
                if (string.Equals(key, action.CardStateKey, StringComparison.Ordinal))
                {
                    if (occ-- == 0)
                        return HotAction.PlayCard(i, targetSlot);
                }
            }
        }

        int cardOcc = action.CardOccurrence;
        for (int i = 0; i < state.HandCount; i++)
        {
            if (string.Equals(rules.Cards[state.Cards[i].CardDefId].Entry, action.CardId, StringComparison.Ordinal))
            {
                if (cardOcc-- == 0)
                    return HotAction.PlayCard(i, targetSlot);
            }
        }

        throw new InvalidOperationException(
            $"Could not find hand card {action.CardId}#{action.CardOccurrence} (stateKey={action.CardStateKey}#{action.CardStateOccurrence}) in HotState hand (count={state.HandCount}).");
    }

    private static List<(PlanCardChoice Choice, PlanCardToken? Token)> FlattenPlanChoices(PlanAction action)
    {
        List<(PlanCardChoice, PlanCardToken?)> list = [];
        if (action.Kind == PlanActionKind.EndTurn)
        {
            if (action.TurnStartChoices != null)
            {
                foreach (PlanCardChoice choice in action.TurnStartChoices)
                {
                    if (choice.Cards.Count == 0)
                        list.Add((choice, null));
                    else
                        foreach (PlanCardToken token in choice.Cards)
                            list.Add((choice, token));
                }
            }
            return list;
        }

        foreach (PlanCardChoice choice in action.GetActionChoicesInExecutionOrder())
        {
            if (choice.Cards.Count == 0)
                list.Add((choice, null));
            else
                foreach (PlanCardToken token in choice.Cards)
                    list.Add((choice, token));
        }
        return list;
    }

    private static HotChoiceSelection ResolvePlanChoiceSelection(
        in CombatHotState probe,
        HotColdSidecar? probeCold,
        HotRules rules,
        HotExecScratch scratch,
        List<(PlanCardChoice Choice, PlanCardToken? Token)> cursor,
        ref int cursorIdx)
    {
        if (cursorIdx >= cursor.Count)
        {
            if (scratch.PendingChoiceMin == 0)
                return new HotChoiceSelection(HotChoiceKind.None, 0, 0);
            HotChoiceSelection[] fallback = new HotChoiceSelection[32];
            int count = HotCombatEngine.EnumerateChoiceOptions(in probe, probeCold, rules, scratch, fallback);
            Check(count > 0, $"No PlanCardChoice token left and no fallback option for {scratch.PendingChoiceKind}.");
            return fallback[0];
        }

        (PlanCardChoice choice, PlanCardToken? token) = cursor[cursorIdx++];
        if (token == null)
            return new HotChoiceSelection(HotChoiceKind.None, 0, 0);

        if (scratch.PendingChoiceKind == HotChoiceKind.GeneratedCard)
        {
            HotChoiceSelection[] opts = new HotChoiceSelection[32];
            int count = HotCombatEngine.EnumerateChoiceOptions(in probe, probeCold, rules, scratch, opts);
            int occ = token.OptionOccurrence;
            for (int i = 0; i < count; i++)
            {
                if (opts[i].Kind != HotChoiceKind.GeneratedCard)
                    continue;
                if (string.Equals(rules.Cards[opts[i].CardDefId].Entry, token.CardId, StringComparison.Ordinal))
                {
                    if (occ-- == 0)
                        return opts[i];
                }
            }
            for (int i = 0; i < count; i++)
            {
                if (opts[i].Kind == HotChoiceKind.GeneratedCard
                    && string.Equals(rules.Cards[opts[i].CardDefId].Entry, token.CardId, StringComparison.Ordinal))
                    return opts[i];
            }
            throw new InvalidOperationException($"GeneratedCard choice option {token.CardId} not found among {count} options.");
        }

        HotPileKind pile = scratch.PendingChoiceKind switch
        {
            HotChoiceKind.HandCard => HotPileKind.Hand,
            HotChoiceKind.DrawPile => HotPileKind.Draw,
            HotChoiceKind.DiscardPile => HotPileKind.Discard,
            HotChoiceKind.ExhaustPile => HotPileKind.Exhaust,
            _ => throw new InvalidOperationException($"Unsupported pending choice kind {scratch.PendingChoiceKind}."),
        };

        int start = HotPiles.ZoneStart(in probe, pile);
        int len = HotPiles.ZoneCount(in probe, pile);

        if (!string.IsNullOrEmpty(token.StateKey))
        {
            int occ = token.SourceOccurrence;
            for (int offset = 0; offset < len; offset++)
            {
                ref readonly HotCard c = ref probe.Cards[start + offset];
                if (string.Equals(HotContinuationStamp.FormatChoiceCardKey(in c, probeCold, rules), token.StateKey, StringComparison.Ordinal))
                {
                    if (occ-- == 0)
                        return new HotChoiceSelection(scratch.PendingChoiceKind, c.CardUid, c.CardDefId);
                }
            }
        }

        int fallbackOcc = token.SourceOccurrence;
        for (int offset = 0; offset < len; offset++)
        {
            ref readonly HotCard c = ref probe.Cards[start + offset];
            if (string.Equals(rules.Cards[c.CardDefId].Entry, token.CardId, StringComparison.Ordinal)
                && c.UpgradeLevel == token.UpgradeLevel)
            {
                if (fallbackOcc-- == 0)
                    return new HotChoiceSelection(scratch.PendingChoiceKind, c.CardUid, c.CardDefId);
            }
        }

        for (int offset = 0; offset < len; offset++)
        {
            ref readonly HotCard c = ref probe.Cards[start + offset];
            if (string.Equals(rules.Cards[c.CardDefId].Entry, token.CardId, StringComparison.Ordinal))
                return new HotChoiceSelection(scratch.PendingChoiceKind, c.CardUid, c.CardDefId);
        }

        throw new InvalidOperationException(
            $"Choice token {token.CardId}+{token.UpgradeLevel} (occ={token.SourceOccurrence}) not found in {pile} (count={len}).");
    }

    private static int RunGateB(MainLoopContext loop)
    {
        HarnessScenario[] scenarios =
        [
            new("IRONCLAD", "FUZZY_WURM_CRAWLER_WEAK", "OFFLINEHARNESS1", 0, 0),
            new("IRONCLAD", "NIBBITS_NORMAL", "OFFLINEHARNESS2", 0, 0),
            new("IRONCLAD", "NIBBITS_WEAK", "OFFLINEHARNESS3", 0, 0),
            new("SILENT", "FUZZY_WURM_CRAWLER_WEAK", "OFFLINEHARNESS4", 0, 0),
            new("DEFECT", "FUZZY_WURM_CRAWLER_WEAK", "OFFLINEHARNESS5", 0, 0),
            new("REGENT", "FUZZY_WURM_CRAWLER_WEAK", "OFFLINEHARNESS6", 0, 0),
            new("NECROBINDER", "FUZZY_WURM_CRAWLER_WEAK", "OFFLINEHARNESS7", 0, 0),
            new("FOOL", "FUZZY_WURM_CRAWLER_WEAK", "OFFLINEHARNESS8", 0, 0),
        ];

        int totalSteps = 0;
        int totalEpisodes = 0;
        int totalFailures = 0;

        foreach (HarnessScenario scenario in scenarios)
        {
            CleanupActiveRun();
            try
            {
                Task enter = OfflineCombat.EnterCombatRoomAsync(scenario);
                loop.RunUntilCompleted(enter, TimeSpan.FromSeconds(120), $"GateB2 Enter {scenario.CharacterId}/{scenario.EncounterId}");
                CombatState combat = OfflineCombat.WaitForPlayableCombat(loop);

                HotRootBundle bundle = HotRootBuilder.BuildFromLive(combat);
                CombatRootSnapshot legacyRoot = CombatRootSnapshot.Capture(combat);
                SolverDisplayNames names = SolverDisplayNames.Capture(combat);
                BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
                SolverSettingsSnapshot settings = SolverSettings.Capture();
                SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(
                    settings, combat, includeTurnSetup: false, theftPolicy: SolverController.ResolveTheftPolicy(combat));
                CombatBeamSolver solver = new(legacyRoot, names, damage, policy, searchProfile: policy.Profile);
                Player player = LocalContext.GetMe(combat)!;

                // Verify root step 0 parity
                CombatHotState rootState = bundle.RootState;
                SimulationSnapshot rootSnap = solver.ReplayDiagnosticPrefix([]);
                try
                {
                    string legacyRootStamp = HotContinuationStamp.StripModStampSections(
                        solver.CaptureDiagnosticContinuation(rootSnap).StateText);
                    string hotRootStamp = HotContinuationStamp.CaptureFromHot(in rootState, bundle.RootCold, bundle.Rules);
                    if (!string.Equals(legacyRootStamp, hotRootStamp, StringComparison.Ordinal))
                    {
                        var diffs = new ContinuationStamp(legacyRootStamp).DescribeDifferences(new ContinuationStamp(hotRootStamp), 5);
                        Console.WriteLine($"[FAIL] Gate B2 {scenario.CharacterId}/{scenario.EncounterId} Step 0 (root): {string.Join(" | ", diffs)}");
                        totalFailures++;
                        continue;
                    }
                }
                finally
                {
                    rootSnap.ReleaseSimulator();
                }

                int scenarioSteps = 0;
                int scenarioFailures = 0;
                HotExecScratch scratch = new();
                HotAction[] legalBuf = new HotAction[64];
                HotChoiceSelection[] choiceOptBuf = new HotChoiceSelection[32];

                int episodesPerScenario = scenario.CharacterId == "IRONCLAD" ? 3 : 2;
                for (int episode = 0; episode < episodesPerScenario; episode++)
                {
                    totalEpisodes++;
                    CombatHotState hotState = bundle.RootState;
                    HotColdSidecar? hotCold = bundle.RootCold;
                    List<PlanAction> history = [];
                    System.Random picker = new(2000 + episode * 97 + scenario.CharacterId.Length * 13 + scenario.EncounterId.Length);

                    SimulationSnapshot currentLegacy = solver.ReplayDiagnosticPrefix([]);
                    try
                    {
                        const int maxEpisodeSteps = 25;
                        for (int step = 1; step <= maxEpisodeSteps; step++)
                        {
                            if (hotState.TerminalOutcome != HotCombatOutcome.Ongoing)
                                break;

                            HotParallelExpander.AssignDeterministicCardUids(ref hotState);
                            int legalCount = HotCombatEngine.EnumerateLegalActions(in hotState, hotCold, bundle.Rules, legalBuf);
                            if (legalCount == 0)
                                break;

                            HotAction chosen = episode switch
                            {
                                0 => legalBuf[0],
                                1 => legalBuf[picker.Next(legalCount)],
                                _ => step % 2 == 0 ? legalBuf[legalCount - 1] : legalBuf[Math.Max(0, legalCount - 2)],
                            };

                            // Probe for NeedsChoice; if a starter card (e.g. SURVIVOR) requests a hand discard choice,
                            // resolve it deterministically and build matching PlanCardChoice for Legacy.
                            CombatHotState probe = hotState;
                            HotColdSidecar? probeCold = hotCold;
                            HotStepResult hotRes = HotCombatEngine.Step(ref probe, ref probeCold, bundle.Rules, scratch, chosen);
                            PlanCardChoice? mappedChoice = null;

                            if (hotRes.NeedsChoice)
                            {
                                int optCount = HotCombatEngine.EnumerateChoiceOptions(in probe, probeCold, bundle.Rules, scratch, choiceOptBuf);
                                if (optCount > 0 && scratch.PendingChoiceKind == HotChoiceKind.HandCard
                                    && chosen.Kind == HotActionKind.PlayCard
                                    && bundle.Rules.Cards[hotState.Cards[chosen.SlotOrHandIdx].CardDefId].Entry == "SURVIVOR")
                                {
                                    HotChoiceSelection picked = choiceOptBuf[0];
                                    int pickedIdx = FindHandIndexByUid(in probe, picked.CardUidOrIndex);
                                    if (pickedIdx >= 0)
                                    {
                                        string pickedKey = HotContinuationStamp.FormatChoiceCardKey(in probe.Cards[pickedIdx], probeCold, bundle.Rules);
                                        int pickedOcc = 0;
                                        for (int k = 0; k < pickedIdx; k++)
                                            if (string.Equals(HotContinuationStamp.FormatChoiceCardKey(in probe.Cards[k], probeCold, bundle.Rules), pickedKey, StringComparison.Ordinal))
                                                pickedOcc++;
                                        string pickedEntry = bundle.Rules.Cards[probe.Cards[pickedIdx].CardDefId].Entry;
                                        mappedChoice = new PlanCardChoice(
                                            PlanChoiceEffect.Discard,
                                            PileType.Hand,
                                            [new PlanCardToken(pickedEntry, probe.Cards[pickedIdx].UpgradeLevel, pickedKey, pickedOcc, 0, pickedEntry)],
                                            SourceId: "");
                                        probe = hotState;
                                        probeCold = hotCold;
                                        HotChoiceSelection[] oneChoice = [picked];
                                        hotRes = HotCombatEngine.Step(ref probe, ref probeCold, bundle.Rules, scratch, chosen, oneChoice);
                                    }
                                }

                                // If still NeedsChoice (e.g. complex generated/multi choice in random walk), fall back to EndTurn
                                if (hotRes.NeedsChoice)
                                {
                                    chosen = HotAction.EndTurn();
                                    mappedChoice = null;
                                    probe = hotState;
                                    probeCold = hotCold;
                                    hotRes = HotCombatEngine.Step(ref probe, ref probeCold, bundle.Rules, scratch, chosen);
                                }
                            }

                            PlanAction legacyAction = MapHotActionToLegacy(
                                chosen, in hotState, currentLegacy, player, bundle.Rules);
                            if (mappedChoice != null)
                                legacyAction = legacyAction with { Choice = mappedChoice };

                            Check(hotRes.Executed && !hotRes.NeedsChoice,
                                $"Gate B2 {scenario.CharacterId}/{scenario.EncounterId} ep={episode} step={step}: HotStep unexpected result {hotRes}");

                            hotState = probe;
                            hotCold = probeCold;

                            history.Add(legacyAction);
                            currentLegacy.ReleaseSimulator();
                            currentLegacy = solver.ReplayDiagnosticPrefix(history);

                            scenarioSteps++;
                            totalSteps++;

                            HotCombatOutcome legacyOutcome = currentLegacy.PlayerDead
                                ? HotCombatOutcome.Defeat
                                : currentLegacy.AllEnemiesDead
                                    ? HotCombatOutcome.Victory
                                    : HotCombatOutcome.Ongoing;
                            if (hotRes.Outcome != legacyOutcome)
                            {
                                Console.WriteLine(
                                    $"[FAIL] Gate B2 {scenario.CharacterId}/{scenario.EncounterId} ep={episode} step={step} action={DescribeAction(chosen, legacyAction)}: " +
                                    $"outcome mismatch hot={hotRes.Outcome} legacy={legacyOutcome}");
                                scenarioFailures++;
                                totalFailures++;
                                break;
                            }

                            string legacyStamp = HotContinuationStamp.StripModStampSections(
                                solver.CaptureDiagnosticContinuation(currentLegacy).StateText);
                            string hotStamp = HotContinuationStamp.CaptureFromHot(in hotState, hotCold, bundle.Rules);

                            if (!string.Equals(legacyStamp, hotStamp, StringComparison.Ordinal))
                            {
                                var diffs = new ContinuationStamp(legacyStamp).DescribeDifferences(new ContinuationStamp(hotStamp), 5);
                                Console.WriteLine(
                                    $"[FAIL] Gate B2 {scenario.CharacterId}/{scenario.EncounterId} ep={episode} step={step} action={DescribeAction(chosen, legacyAction)} " +
                                    $"(turn={hotState.PlayerTurnNumber}):");
                                foreach (string d in diffs)
                                    Console.WriteLine($"       -> {d}");
                                scenarioFailures++;
                                totalFailures++;
                                break;
                            }
                        }
                    }
                    finally
                    {
                        currentLegacy.ReleaseSimulator();
                    }
                }

                Console.WriteLine(
                    $"[{(scenarioFailures == 0 ? "PASS" : "FAIL")}] Gate B2 {scenario.CharacterId}/{scenario.EncounterId}: " +
                    $"{scenarioSteps} lockstep steps across {episodesPerScenario} episodes, {scenarioFailures} mismatch(es)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FAIL] Gate B2 {scenario.CharacterId}/{scenario.EncounterId}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                totalFailures++;
            }
            finally
            {
                CleanupActiveRun();
            }
        }

        Check(totalSteps >= 100, $"Gate B2 executed only {totalSteps} steps (expected >= 100).");
        Console.WriteLine($"Gate B2 Summary: {totalSteps} total lockstep steps across {totalEpisodes} episodes (6 characters), {totalFailures} failure(s)");
        return totalFailures;
    }

    private static int FindHandIndexByUid(in CombatHotState s, ushort cardUid)
    {
        for (int i = 0; i < s.HandCount; i++)
            if (s.Cards[i].CardUid == cardUid)
                return i;
        return -1;
    }

    private static int RunGateC(MainLoopContext loop)
    {
        CleanupActiveRun();
        try
        {
            HarnessScenario scenario = new("IRONCLAD", "FUZZY_WURM_CRAWLER_WEAK", "OFFLINEHARNESS1", 0, 0);
            Task enter = OfflineCombat.EnterCombatRoomAsync(scenario);
            loop.RunUntilCompleted(enter, TimeSpan.FromSeconds(120), "GateC Enter");
            CombatState combat = OfflineCombat.WaitForPlayableCombat(loop);

            HotRootBundle bundle = HotRootBuilder.BuildFromLive(combat);
            CombatRootSnapshot legacyRoot = CombatRootSnapshot.Capture(combat);
            SolverDisplayNames names = SolverDisplayNames.Capture(combat);
            BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
            SolverSettingsSnapshot settings = SolverSettings.Capture();
            SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(
                settings, combat, includeTurnSetup: false, theftPolicy: SolverController.ResolveTheftPolicy(combat));
            CombatBeamSolver solver = new(legacyRoot, names, damage, policy, searchProfile: policy.Profile);
            Player player = LocalContext.GetMe(combat)!;

            // Pick first playable card action
            CombatHotState rootState = bundle.RootState;
            HotAction[] legalBuf = new HotAction[32];
            int legalCount = HotCombatEngine.EnumerateLegalActions(in rootState, bundle.RootCold, bundle.Rules, legalBuf);
            HotAction hotAction = default;
            bool foundCard = false;
            for (int i = 0; i < legalCount; i++)
            {
                if (legalBuf[i].Kind == HotActionKind.PlayCard)
                {
                    hotAction = legalBuf[i];
                    foundCard = true;
                    break;
                }
            }
            Check(foundCard, "Gate C could not find a playable card action on root.");

            SimulationSnapshot rootSnap = solver.ReplayDiagnosticPrefix([]);
            PlanAction legacyAction;
            try
            {
                legacyAction = MapHotActionToLegacy(hotAction, in rootState, rootSnap, player, bundle.Rules);
            }
            finally
            {
                rootSnap.ReleaseSimulator();
            }
            PlanAction[] singleLegacyAction = [legacyAction];

            // Pre-pack root state into PackedStateArena
            using PackedStateArena arena = new();
            CombatHotState rootCopy = bundle.RootState;
            HotColdSidecar? rootCold = bundle.RootCold;
            int packedSize = HotStatePacker.ComputePackedSize(in rootCopy);
            Span<byte> initSpan = arena.Reserve(packedSize);
            int rootPackedLen = HotStatePacker.Pack(ref rootCopy, ref rootCold, initSpan, canonicalize: true);
            HotStateHandle rootHandle = arena.CommitReserved(rootPackedLen);

            HotExecScratch scratch = new();
            byte[] packBuf = new byte[HotLimits.MaxPackedStateBytes];
            CombatHotState workState = default;
            ulong hashSink = 0;

            // Warmup both
            for (int i = 0; i < 1000; i++)
            {
                HotStatePacker.Unpack(arena.GetSpan(rootHandle), ref workState);
                HotColdSidecar? wc = rootCold;
                _ = HotCombatEngine.Step(ref workState, ref wc, bundle.Rules, scratch, hotAction);
                int len = HotStatePacker.Pack(ref workState, ref wc, packBuf, canonicalize: true);
                hashSink ^= HotHash.ComputeHash(packBuf.AsSpan(0, len), wc).Lo;
            }
            for (int i = 0; i < 200; i++)
            {
                SimulationSnapshot snap = solver.ReplayDiagnosticPrefix(singleLegacyAction);
                hashSink ^= (ulong)snap.EnemyHp;
                snap.ReleaseSimulator();
            }

            // Timed HotKernel: 50,000 iterations of Unpack + PlayCard + Pack + Hash
            const int hotIters = 50_000;
            long hotAllocBefore = GC.GetAllocatedBytesForCurrentThread();
            Stopwatch hotSw = Stopwatch.StartNew();
            for (int i = 0; i < hotIters; i++)
            {
                HotStatePacker.Unpack(arena.GetSpan(rootHandle), ref workState);
                HotColdSidecar? wc = rootCold;
                _ = HotCombatEngine.Step(ref workState, ref wc, bundle.Rules, scratch, hotAction);
                int len = HotStatePacker.Pack(ref workState, ref wc, packBuf, canonicalize: true);
                hashSink ^= HotHash.ComputeHash(packBuf.AsSpan(0, len), wc).Lo;
            }
            hotSw.Stop();
            long hotAllocAfter = GC.GetAllocatedBytesForCurrentThread();

            // Timed Legacy: 5,000 iterations of Fork + ManualPlay + Snapshot
            const int legacyIters = 5_000;
            long legacyAllocBefore = GC.GetAllocatedBytesForCurrentThread();
            Stopwatch legacySw = Stopwatch.StartNew();
            for (int i = 0; i < legacyIters; i++)
            {
                SimulationSnapshot snap = solver.ReplayDiagnosticPrefix(singleLegacyAction);
                hashSink ^= (ulong)snap.EnemyHp;
                snap.ReleaseSimulator();
            }
            legacySw.Stop();
            long legacyAllocAfter = GC.GetAllocatedBytesForCurrentThread();

            double hotUs = hotSw.Elapsed.TotalMicroseconds / hotIters;
            double hotTps = hotIters / hotSw.Elapsed.TotalSeconds;
            double hotBytesPerStep = (double)(hotAllocAfter - hotAllocBefore) / hotIters;

            double legacyUs = legacySw.Elapsed.TotalMicroseconds / legacyIters;
            double legacyTps = legacyIters / legacySw.Elapsed.TotalSeconds;
            double legacyBytesPerStep = (double)(legacyAllocAfter - legacyAllocBefore) / legacyIters;

            double speedup = hotTps / legacyTps;

            Console.WriteLine($"Action benchmarked: {legacyAction.CardId} (target={legacyAction.TargetCombatId?.ToString() ?? "-"}) | sink={hashSink:X16}");
            Console.WriteLine($"| Engine | Iters | Elapsed ms | us/step | trans/s | bytes/step |");
            Console.WriteLine($"|---|---:|---:|---:|---:|---:|");
            Console.WriteLine($"| Legacy (`Fork+ManualPlay+Snapshot`) | {legacyIters} | {legacySw.Elapsed.TotalMilliseconds:F2} | {legacyUs:F2} | {legacyTps:N0} | {legacyBytesPerStep:N1} |");
            Console.WriteLine($"| HotKernel (`Unpack+PlayCard+Pack+Hash`) | {hotIters} | {hotSw.Elapsed.TotalMilliseconds:F2} | {hotUs:F2} | {hotTps:N0} | {hotBytesPerStep:N1} |");
            Console.WriteLine($"Speedup: {speedup:F2}x (target >= 8.0x) | Hot alloc: {hotBytesPerStep:F1} B/step (target ~0 B/step)");

            if (speedup < 8.0)
            {
                Console.WriteLine($"[FAIL] Gate C speedup {speedup:F2}x is below 8.0x target.");
                return 1;
            }
            if (hotBytesPerStep > 64.0)
            {
                Console.WriteLine($"[FAIL] Gate C HotKernel allocated {hotBytesPerStep:F1} B/step (expected ~0 B/step).");
                return 1;
            }

            Console.WriteLine("[PASS] Gate C micro-benchmark");
            return 0;
        }
        finally
        {
            CleanupActiveRun();
        }
    }

    private static PlanAction MapHotActionToLegacy(
        HotAction hotAction,
        in CombatHotState hotState,
        SimulationSnapshot legacySnap,
        Player player,
        HotRules rules)
    {
        if (hotAction.Kind == HotActionKind.EndTurn)
            return new PlanAction(PlanActionKind.EndTurn, Turn: hotState.PlayerTurnNumber);

        Check(hotAction.Kind == HotActionKind.PlayCard, $"Unsupported Gate B action kind: {hotAction.Kind}");
        int handIdx = hotAction.SlotOrHandIdx;
        SimPlayerCombatState pcs = legacySnap.Simulator.State.GetPlayerCombatState(player);
        Check(handIdx < pcs.Hand.Cards.Count,
            $"Hand index {handIdx} out of range for legacy hand ({pcs.Hand.Cards.Count}).");

        PredictedCard card = pcs.Hand.Cards[handIdx];
        string hotCardEntry = rules.Cards[hotState.Cards[handIdx].CardDefId].Entry;
        Check(string.Equals(card.Preview.Id.Entry, hotCardEntry, StringComparison.Ordinal),
            $"Hand[{handIdx}] card mismatch before step: hot={hotCardEntry} legacy={card.Preview.Id.Entry}.");

        string cardId = card.Preview.Id.Entry;
        string stateKey = CardChoiceSupport.ChoiceCardKey(card);
        int occurrence = 0;
        int stateOccurrence = 0;
        for (int i = 0; i < handIdx; i++)
        {
            if (string.Equals(pcs.Hand.Cards[i].Preview.Id.Entry, cardId, StringComparison.Ordinal))
                occurrence++;
            if (string.Equals(CardChoiceSupport.ChoiceCardKey(pcs.Hand.Cards[i]), stateKey, StringComparison.Ordinal))
                stateOccurrence++;
        }

        uint? targetCombatId = hotAction.TargetEnemySlot >= 0
            ? hotState.Enemies[hotAction.TargetEnemySlot].Vital.CombatId
            : null;

        return new PlanAction(
            PlanActionKind.PlayCard,
            Turn: hotState.PlayerTurnNumber,
            CardId: cardId,
            CardOccurrence: occurrence,
            TargetCombatId: targetCombatId,
            CardStateKey: stateKey,
            CardStateOccurrence: stateOccurrence);
    }

    private static string DescribeAction(HotAction action, PlanAction legacyAction)
        => action.Kind == HotActionKind.EndTurn
            ? "EndTurn"
            : $"PlayCard[{action.SlotOrHandIdx}]={legacyAction.CardId}(target={legacyAction.TargetCombatId?.ToString() ?? "-"})";

    private static void CleanupActiveRun()
    {
        if (RunManager.Instance.IsInProgress)
            RunManager.Instance.CleanUp(graceful: false);
        else if (CombatManager.Instance.IsInProgress)
            CombatManager.Instance.Reset(graceful: false);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
