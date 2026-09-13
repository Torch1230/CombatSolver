using System.Reflection;
using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using STS2RitsuLib.Models.Capabilities;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static string AssertRitsuTagFastPath(Player player)
    {
        if (SimulationNotificationIsolation.IsActive)
            throw new InvalidOperationException("Tag contract needs an unisolated native baseline.");
        MethodInfo method = RitsuEmptyCapabilityFastPath.CardHostTarget(
            "ApplyTags", typeof(CardModel), typeof(IEnumerable<CardTag>)).TargetType.GetMethod(
                "ApplyTags", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("CardModelCapabilityHost.ApplyTags");
        var apply = method.CreateDelegate<Func<CardModel, IEnumerable<CardTag>, IEnumerable<CardTag>>>();
        CardModel card = PredictionUtils.CreateCard(ModelDb.Card<DefendRegent>(), player);
        CardTag[] tags = Enum.GetValues<CardTag>();
        if (tags.Length < 2)
            throw new InvalidOperationException("Tag contract requires two native tags.");
        CardTag[][] inputs = [[], [tags[0]], [tags[0], tags[0], tags[1]], [(CardTag)1234567]];
        List<string> calls = [];
        int comparisons = 0;

        void Compare()
        {
            foreach (CardTag[] input in inputs)
            {
                calls.Clear();
                CardTag[] expected = apply(card, input).ToArray();
                string[] expectedCalls = calls.ToArray();
                calls.Clear();
                using (SimulationNotificationIsolation.Enter())
                {
                    if (!apply(card, input).SequenceEqual(expected)
                        || !calls.SequenceEqual(expectedCalls))
                        throw new InvalidOperationException("Tag fast path changed tags or contributor order.");
                }
                comparisons++;
            }
            CardTag[] native = card.Tags.ToArray();
            using (SimulationNotificationIsolation.Enter())
                if (!card.Tags.SequenceEqual(native))
                    throw new InvalidOperationException("Patched CardModel.Tags differs from native path.");
            comparisons++;
        }

        Compare(); // No materialized set and no default source.
        IEnumerable<CardTag> lazy = ThrowingTags();
        if (!ReferenceEquals(apply(card, lazy), lazy))
            throw new InvalidOperationException("Native empty tag pipeline did not preserve its input.");
        using (SimulationNotificationIsolation.Enter())
        {
            if (!RitsuEmptyCapabilityFastPath.CanSkip(card)
                || !ReferenceEquals(apply(card, lazy), lazy))
                throw new InvalidOperationException("Empty tag fast path evaluated or replaced lazy input.");
            try
            {
                _ = apply(card, lazy).ToArray();
                throw new InvalidOperationException("Lazy tag failure was lost.");
            }
            catch (TagEnumerationException)
            {
                comparisons++;
            }
        }

        // Measure the real patched host. Native baseline runs outside isolation, after warmup.
        CardTag[] empty = [];
        for (int i = 0; i < 2000; i++) _ = apply(card, empty);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) _ = apply(card, empty);
        long nativeAllocated = GC.GetAllocatedBytesForCurrentThread() - before;
        long fastAllocated;
        using (SimulationNotificationIsolation.Enter())
        {
            for (int i = 0; i < 2000; i++) _ = apply(card, empty);
            before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++) _ = apply(card, empty);
            fastAllocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        if (fastAllocated >= nativeAllocated)
            throw new InvalidOperationException("Empty tag pipeline did not reduce local allocations.");

        ModelCapabilitySet set = ModelCapabilities.Get(card);
        // The isolated fixture does not register capability persistence. Populate the same
        // attached collection as the existing Ritsu contracts, then test the real host.
        var attached = (List<IModelCapability>)(typeof(ModelCapabilitySet)
            .GetField("_capabilities", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(set)
            ?? throw new MissingFieldException(typeof(ModelCapabilitySet).FullName, "_capabilities"));
        FieldInfo attachedSnapshot = typeof(ModelCapabilitySet).GetField(
            "_attachedSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(ModelCapabilitySet).FullName, "_attachedSnapshot");
        void Attach(TagContractCapability capability)
        {
            capability.Attach(card, isInternal: true);
            attached.Add(capability);
            attachedSnapshot.SetValue(set, null);
        }
        void Remove(TagContractCapability capability)
        {
            if (!attached.Remove(capability))
                throw new InvalidOperationException("Tag contract could not remove a contributor.");
            capability.Detach(isInternal: true);
            attachedSnapshot.SetValue(set, null);
        }
        Compare(); // Existing empty set.
        TagContractCapability first = new("first", () => [tags[1], tags[0]], calls);
        TagContractCapability second = new("second", () => [tags[0], tags[1]], calls);
        Attach(first);
        Compare();
        Attach(second);
        Compare(); // Original first-seen order and duplicate handling.
        first.Tags = () => null;
        Compare(); // A contributor returning null still runs.
        first.Tags = () => [];
        Compare(); // An empty contribution is not an empty capability set.
        Remove(first);
        Remove(second);
        Compare();
        Attach(first);
        first.Tags = () => [tags[0]];
        Compare(); // Reacquisition, then in-place mutation without a type change.
        first.Tags = () => [tags[1]];
        Compare();
        Remove(first);

        // This process-local registration intentionally outlives the contract; this request
        // uses a fresh, non-reusable test process and a Regent card outside the live fixture.
        CardModel late = PredictionUtils.CreateCard(ModelDb.Card<DefendRegent>(), player);
        using (SimulationNotificationIsolation.Enter())
            if (!RitsuEmptyCapabilityFastPath.CanSkip(late))
                throw new InvalidOperationException("Late registration contract lacks a negative cache entry.");
        int generation = RitsuEmptyCapabilityFastPath.DefaultCapabilitySourceGenerationForTesting;
        int factoryCalls = 0;
        Type defaults = typeof(ModelCapabilities).Assembly.GetType(
            "STS2RitsuLib.Models.Capabilities.ModelCapabilityDefaults", throwOnError: true)!;
        MethodInfo modify = defaults.GetMethod("Modify", BindingFlags.Static | BindingFlags.Public,
            null, [typeof(string), typeof(string), typeof(Type),
                typeof(Action<AbstractModel, ModelCapabilityList>), typeof(int)], null)
            ?? throw new MissingMethodException(defaults.FullName, "Modify");
        Action<AbstractModel, ModelCapabilityList> factory = (_, list) =>
        {
            factoryCalls++;
            list.Add(new TagContractCapability("default", () => [tags[1]], calls));
        };
        modify.Invoke(null, [Entry.ModId, "unattended_tag_default_probe", typeof(DefendRegent), factory, 0]);
        using (SimulationNotificationIsolation.Enter())
        {
            if (RitsuEmptyCapabilityFastPath.DefaultCapabilitySourceGenerationForTesting == generation
                || RitsuEmptyCapabilityFastPath.CanSkip(late)
                || !apply(late, empty).SequenceEqual([tags[1]])
                || factoryCalls != 1)
                throw new InvalidOperationException("Tag fast path hid a newly registered default source.");
            // A previously materialized empty set stays empty, matching the framework.
            if (!RitsuEmptyCapabilityFastPath.CanSkip(card)
                || !ReferenceEquals(apply(card, empty), empty) || factoryCalls != 1)
                throw new InvalidOperationException("Tag fast path re-created an existing empty set.");
        }
        comparisons += 2;
        return $"RitsuTags:comparisons={comparisons}:lazy_identity=true:native_property=true:contributor_order=true:null_empty_contributions=true:remove_reacquire=true:late_default_registration=true:local_iterations=10000:native_allocated={nativeAllocated}:fast_allocated={fastAllocated}";
    }

    private static IEnumerable<CardTag> ThrowingTags()
    {
        yield return Throw();
        static CardTag Throw() => throw new TagEnumerationException();
    }

    private sealed class TagEnumerationException : Exception;

    private sealed class TagContractCapability(
        string id, Func<IEnumerable<CardTag>?> tags, List<string> calls)
        : IModelCapability, ICardPropertyContributor
    {
        public string CapabilityId => "combat_solver_tag_test_" + id;
        public AbstractModel? Owner { get; private set; }
        public Func<IEnumerable<CardTag>?> Tags { get; set; } = tags;
        public void Attach(AbstractModel owner, bool isInternal = false) => Owner = owner;
        public void Detach(bool isInternal = false) => Owner = null;
        public IEnumerable<CardTag> GetTags(CardModel card)
        {
            calls.Add(id);
            return Tags()!; // Exercise the framework's explicit null-contribution handling.
        }
    }
}
