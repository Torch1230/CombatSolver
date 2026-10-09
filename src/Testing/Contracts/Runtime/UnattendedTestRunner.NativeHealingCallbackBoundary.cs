using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using STS2RitsuLib.Combat.Healing;

namespace CombatSolver;
internal sealed partial class UnattendedTestRunner
{
    private sealed class NativeHealingBoundaryListener : IHealHookListener
    {
        internal int Calls;
        public decimal ModifyHealMultiplicative(HealContext context, decimal amount)
        { Calls++; return 2m; }
    }

    private async Task AssertNativeHealingCallbackBoundaryAsync(CombatState live, Player player)
    {
        foreach(var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach(var power in live.Creatures.SelectMany(c=>c.Powers).ToArray()) await PowerCmd.Remove(power);
        await ClearPlayerPilesAsync(player);
        await InjectCardAsync(live,player,new(){CardId="DEFEND_REGENT",Pile="Hand",TreatAsDeckCard=true});
        foreach(var potion in player.PotionSlots.ToArray()) potion?.Discard();
        await CreatureCmd.SetCurrentHp(player.Creature,35);
        await PowerCmd.Apply<RegenPower>(new ThrowingPlayerChoiceContext(),player.Creature,5,player.Creature,null);
        var root=CombatRootSnapshot.Capture(live);var parent=root.ForkSimulator();
        if(!root.UsesComponentHealingCertificate
            ||StrategicHpRecoveryBound.ComponentHealingUpperBound(parent,player,0)!=15)
            throw new InvalidOperationException("Clean native callback fixture not certified.");
        string Stamp(CombatPredictionSimulator sim)=>DescribeContinuationContractState(sim,root,player);
        string parentBefore=Stamp(parent),liveBefore=ContinuationStamp.CaptureLive(live).StateText;
        void MustReject(Action capture,string message)
        {
            try{capture();}
            catch(PredictionUnsupportedException error) when(error.Message.Contains(message,StringComparison.Ordinal)){return;}
            throw new InvalidOperationException("Unknown callback was admitted: "+message);
        }
        var pool=PredictionModHookSubscriberCapture.EnumerateAuditableNativeGenerationCards().Select(c=>c.GetType()).ToHashSet();
        if(!pool.Contains(typeof(SecretWeapon)) || pool.Contains(typeof(Feed))
            ||pool.Contains(typeof(NotYet)) ||pool.Contains(typeof(Alchemize)))
            throw new InvalidOperationException("Native generation filter boundary changed.");
        if(player.PlayerCombatState!.AllCards.Concat(player.Deck.Cards).Any(c=>c is SecretWeapon))
            throw new InvalidOperationException("Future card is already initial.");
        var assembly=AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("NativeHealingBoundaryUnknownPatch"),AssemblyBuilderAccess.Run);
        var builder=assembly.DefineDynamicModule("probe").DefineType("NativeHealingBoundaryUnknownPatch.Source",TypeAttributes.Public);
        var method=builder.DefineMethod("Prefix",MethodAttributes.Public|MethodAttributes.Static,typeof(bool),Type.EmptyTypes);
        var il=method.GetILGenerator();il.Emit(OpCodes.Ldc_I4_0);il.Emit(OpCodes.Ret);
        MethodInfo prefix=builder.CreateType()!.GetMethod("Prefix")!;
        var harmony=new Harmony("combatsolver.testing.native-healing-callback-boundary");
        MethodInfo future=AdaptedCardOnPlayMirrors.ResolveOnPlay(typeof(SecretWeapon))!;
        try
        {
            harmony.Patch(future,prefix:new HarmonyMethod(prefix));
            MustReject(()=>CombatRootSnapshot.Capture(live),"Unknown Harmony patch");
            MustReject(()=>ContinuationStamp.CaptureLive(live),"Unknown Harmony patch");
        }
        finally{harmony.Unpatch(future,HarmonyPatchType.All,harmony.Id);}
        MethodInfo heal=AccessTools.Method(typeof(CreatureCmd),nameof(CreatureCmd.Heal),
            [typeof(MegaCrit.Sts2.Core.Entities.Creatures.Creature),typeof(decimal),typeof(bool)]);
        try
        {
            harmony.Patch(heal,prefix:new HarmonyMethod(prefix));
            MustReject(()=>CombatRootSnapshot.Capture(live),"Unreviewed native Heal patch combination");
            MustReject(()=>ContinuationStamp.CaptureLive(live),"Unreviewed native Heal patch combination");
        }
        finally{harmony.Unpatch(heal,HarmonyPatchType.All,harmony.Id);}
        object registry=typeof(HealHook).GetField("GlobalListeners",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!;
        FieldInfo values=registry.GetType().GetField("_snapshot",BindingFlags.Instance|BindingFlags.NonPublic)!;
        Array original=(Array)values.GetValue(registry)!;
        if(original.Length!=0)throw new InvalidOperationException("Global listener fixture is not empty.");
        var listener=new NativeHealingBoundaryListener();
        HealHook.RegisterGlobalListener(listener);
        try
        {
            MustReject(()=>CombatRootSnapshot.Capture(live),"Unsupported RitsuLib global healing listener");
            MustReject(()=>ContinuationStamp.CaptureLive(live),"Unsupported RitsuLib global healing listener");
            var children=Enumerable.Range(0,16).Select(_=>parent.Fork()).ToArray();
            await Task.WhenAll(children.Select(child=>Task.Run(()=>
            {
                using var isolation=SimulationNotificationIsolation.Enter();
                if(StrategicHpRecoveryBound.ComponentHealingUpperBound(child,player,0)!=15
                    ||Stamp(child)!=parentBefore ||Stamp(child.Fork())!=parentBefore)
                    throw new InvalidOperationException("Frozen callback parent/Fork changed with live registration.");
            })));
            if(listener.Calls!=0)throw new InvalidOperationException("Callback inspection executed an unknown listener.");
        }
        finally
        {
            ((IList)registry.GetType().GetField("_listeners",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(registry)!).Remove(listener);
            values.SetValue(registry,original);
        }
        if(Stamp(parent)!=parentBefore ||ContinuationStamp.CaptureLive(live).StateText!=liveBefore)
            throw new InvalidOperationException("Native callback guard changed parent/live state or RNG.");
        if(!CombatRootSnapshot.Capture(live).UsesComponentHealingCertificate)
            throw new InvalidOperationException("Unpatch/unregister did not restore fresh-root qualification.");
        _completedChecks.Add("NativeHealingCallbackBoundary:CleanBound15:GeneratedSecretWeapon:FeedNotYetAlchemizeExcluded:UnknownOnPlayAndHealPatchRejected:GlobalHealRejected:LateLiveBoundaryRejected:16ParallelForks:FullStateHistoryRngParentLiveIsolation:ListenerNotInvoked:FreshRootRecovers");
    }
}
