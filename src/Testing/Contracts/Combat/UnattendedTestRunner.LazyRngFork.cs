using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static string AssertLazyRngFork(RunRngSet live)
    {
        (Func<CombatPredictionRngSet, Rng> Mutable,
            Func<CombatPredictionRngSet, PredictionRngState> Read)[] streams =
        [
            (s => s.Shuffle, s => s.ShuffleState),
            (s => s.CombatCardGeneration, s => s.CombatCardGenerationState),
            (s => s.CombatPotionGeneration, s => s.CombatPotionGenerationState),
            (s => s.CombatCardSelection, s => s.CombatCardSelectionState),
            (s => s.CombatEnergyCosts, s => s.CombatEnergyCostsState),
            (s => s.CombatTargets, s => s.CombatTargetsState),
            (s => s.CombatOrbGeneration, s => s.CombatOrbGenerationState),
            (s => s.MonsterAi, s => s.MonsterAiState),
            (s => s.Niche, s => s.NicheState)
        ];
        CombatPredictionRngSet root = CombatPredictionRngSet.From(live);
        PredictionRngState[] initial = streams.Select(s => s.Read(root)).ToArray();
        Rng[] expected = streams.Select(s => new Rng(s.Mutable(root).ToSerializable())).ToArray();
        CombatPredictionRngSet parent = root.Fork();
        CombatPredictionRngSet untouched = parent.Fork();
        CombatPredictionRngSet sibling = parent.Fork();
        for (int index = 0; index < streams.Length; index++)
        {
            var stream = streams[index];
            Rng retained = stream.Mutable(parent);
            CombatPredictionRngSet child = parent.Fork();
            PredictionRngState childBefore = stream.Read(child);
            for (int draw = 0; draw <= index; draw++)
            {
                if (retained.NextInt(3 + index) != expected[index].NextInt(3 + index))
                    throw new InvalidOperationException("Lazy RNG changed a native integer draw.");
            }
            if (stream.Read(child) != childBefore || stream.Read(untouched) != initial[index]
                || stream.Read(sibling) != initial[index] || stream.Read(root) != initial[index])
                throw new InvalidOperationException("Retained parent RNG escaped into another branch.");
            if (stream.Read(parent) != expected[index].CaptureState())
                throw new InvalidOperationException("Lazy RNG lost a counter or a generator state word.");

            CombatPredictionRngSet grandchild = child.Fork().Fork();
            Rng childExpected = new(stream.Mutable(root).ToSerializable());
            Rng grandchildRng = stream.Mutable(grandchild);
            if (ReferenceEquals(grandchildRng, retained)
                || ReferenceEquals(grandchildRng, stream.Mutable(child)))
                throw new InvalidOperationException("Fork shared a materialized RNG.");
            for (int draw = 0; draw < 32; draw++)
            {
                if (grandchildRng.NextUnsignedLong() != childExpected.NextUnsignedLong()
                    || grandchildRng.NextDouble() != childExpected.NextDouble()
                    || grandchildRng.NextBool() != childExpected.NextBool())
                    throw new InvalidOperationException("Lazy RNG changed the native draw sequence.");
            }
            if (stream.Read(grandchild) != childExpected.CaptureState()
                || stream.Read(child) != childBefore)
                throw new InvalidOperationException("Grandchild RNG advancement changed its parent.");
            Rng projection = stream.Read(parent).ToRng();
            if (projection.NextUnsignedLong() != expected[index].NextUnsignedLong()
                || stream.Read(parent) == projection.CaptureState())
                throw new InvalidOperationException("Read-only RNG projection was not independent.");
        }

        // Read-only observers must not turn a cold branch into nine allocated native streams.
        CombatPredictionRngSet cold = untouched.Fork();
        foreach (var stream in streams) _ = stream.Read(cold);
        for (int index = 0; index < streams.Length; index++)
            if (streams[index].Read(cold) != initial[index])
                throw new InvalidOperationException("Cold RNG observation changed a state.");
        int coldStreamCount = 0;
        foreach (var field in typeof(CombatPredictionRngSet).GetFields(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic))
        {
            var mutable = field.FieldType.GetField("_mutable",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (mutable is null)
                continue;
            coldStreamCount++;
            if (mutable.GetValue(field.GetValue(cold)) is not null)
                throw new InvalidOperationException("Read-only RNG observation materialized a native stream.");
        }
        if (coldStreamCount != streams.Length)
            throw new InvalidOperationException("Cold RNG contract did not inspect all nine streams.");

        // A fully materialized parent and a cold sibling both preserve all nine states on Fork.
        CombatPredictionRngSet materializedChild = parent.Fork();
        for (int index = 0; index < streams.Length; index++)
        {
            var stream = streams[index];
            if (stream.Read(materializedChild) != stream.Read(parent)
                || stream.Read(untouched) != initial[index])
                throw new InvalidOperationException("All-stream Fork changed a state or an untouched sibling.");
        }
        return "LazyRngFork:NineStreams:NativeSequence:RetainedAlias:ParentSiblingGrandchild:ColdReadNoMaterialization";
    }
}
