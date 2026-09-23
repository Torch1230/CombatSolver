using System.Reflection;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.Common.Mirrors;
using CombatSolver.Engine.InCombat.Mirrors;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnStart;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

static class AfterPlayerStartChecks
{
    public static void Run(string[] args)
    {
        int checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
        void Throws<T>(Action action, string message) where T : Exception
        { try { action(); } catch (T) { checks++; return; } throw new Exception(message); }
        Player player = new();
        bool Run(CombatPredictionSimulator simulator) => HookMirrors.AfterPlayerTurnStart(simulator, player, new());
        if (args.Contains("--vanilla"))
        {
            CombatPredictionSimulator vanilla = new();
            vanilla.State.CombatState = new SimulatedCombatState();
            Check(Run(vanilla) && vanilla.Events.SequenceEqual(["vanilla"]), "Unregistered vanilla path changed.");
            Console.WriteLine($"AFTER_PLAYER_START_VANILLA_OK checks={checks}"); return;
        }
        if (args.Contains("--seal"))
        {
            AfterPlayerTurnStartMirrors.Seal();
            Throws<InvalidOperationException>(() => AfterPlayerTurnStartMirrors.Register<AfterRelic>((_, _) => { }), "Root seal missed normal.");
            Throws<InvalidOperationException>(() => AfterPlayerTurnStartMirrors.RegisterEarly<AfterRelic>((_, _) => { }), "Root seal missed early.");
            Throws<InvalidOperationException>(() => AfterPlayerTurnStartMirrors.RegisterLate<AfterRelic>((_, _) => { }), "Root seal missed late.");
            Console.WriteLine($"AFTER_PLAYER_START_SEAL_OK checks={checks}"); return;
        }
        Throws<ArgumentNullException>(() => AfterPlayerTurnStartMirrors.Register<AfterRelic>(null!), "Null accepted.");
        Throws<ArgumentException>(() => AfterPlayerTurnStartMirrors.Register<AbstractAfterRelic>((_, _) => { }), "Abstract accepted.");
        Throws<InvalidOperationException>(() => AfterPlayerTurnStartMirrors.Register<RelicModel>((_, _) => { }), "Non-override accepted.");
        AfterPlayerTurnStartMirrors.RegisterEarly<AfterRelic>((r,c) => { c.Simulator.Events.Add("E:"+r.Label); r.Early?.Invoke(c); });
        AfterPlayerTurnStartMirrors.Register<AfterRelic>((r,c) =>
        { Check(ReferenceEquals(c.Player,player), "Lost player."); c.Simulator.Events.Add("N:"+r.Label); r.Normal?.Invoke(c); });
        AfterPlayerTurnStartMirrors.RegisterLate<AfterRelic>((r,c) => { c.Simulator.Events.Add("L:"+r.Label); r.Late?.Invoke(c); });
        AfterPlayerTurnStartMirrors.Register<AfterModifier>((_,c) => c.Simulator.Events.Add("modifier"));
        AfterPlayerTurnStartMirrors.Register<AfterPower>((_,c) => c.Simulator.Events.Add("power"));
        AfterPlayerTurnStartMirrors.Register<AfterCard>((r,c) => c.Simulator.Events.Add(r.Label));
        AfterPlayerTurnStartMirrors.RegisterLate<GeneratedLateListener>((_,c) => c.Simulator.Events.Add("generated late"));
        Throws<ArgumentException>(() => AfterPlayerTurnStartMirrors.Register<AfterRelic>((_,_)=>{}), "Duplicate accepted.");
        foreach (string field in new[]{"EarlyRegistry","Registry","LateRegistry"})
        {
            var descriptor = ((IMethodMirrorRegistryDescriptorProvider)typeof(AfterPlayerTurnStartMirrors)
                .GetField(field,BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!).DescribeMirrorSupport();
            string suffix=field=="Registry" ? "" : field.Replace("Registry","");
            Check(descriptor.ReceiverType==typeof(AbstractModel) && descriptor.BaseMethod.Name=="AfterPlayerTurnStart"+suffix,
                "Wrong coverage metadata.");
            Check(descriptor.Registrations.Count==(field=="Registry"?5:field=="LateRegistry"?2:1), "Lost coverage entries.");
        }
        CombatPredictionSimulator s = new() { Listeners=[new AfterRelic("a"),new AfterModifier(),new AfterPower(),new AfterRelic("b")] };
        Check(Run(s) && s.Events.SequenceEqual(["E:a","E:b","N:a","modifier","power","N:b","L:a","L:b"]), "Regrouped receivers or phases.");
        Throws<InvalidOperationException>(()=>AfterPlayerTurnStartMirrors.RegisterEarly<AfterRelic>((_,_)=>{}), "Late registration accepted.");
        s = new() { Listeners=[new AbstractModel()] };
        Check(Run(s) && s.Risks==0 && s.Events.Count==0, "Base no-op has risk.");
        s = new() { Listeners=[new UnknownAfterRelic(),new AfterRelic("later")] };
        Throws<NotSupportedException>(()=>Run(s), "Unknown override skipped.");
        Check(s.Risks==1 && s.Events.Count==0, "Unknown override continued.");
        foreach(AbstractModel unknown in new AbstractModel[]{new UnknownNormal(),new UnknownLate()})
        {
            s=new(){Listeners=[unknown]};Throws<NotSupportedException>(()=>Run(s),"Unknown adjacent phase skipped.");
            Check(s.Risks==1,"Adjacent phase lost unsupported risk.");
        }
        s = new() { Listeners=[new DerivedAfterRelic()] };
        Throws<NotSupportedException>(()=>Run(s), "Exact type was inherited.");
        foreach (int phase in new[]{0,1,2})
        {
            AfterRelic pause=new("pause");
            Action<AfterPlayerTurnStartMirrorContext> callback=c=>c.Simulator.HasPendingChoice=true;
            if(phase==0)pause.Early=callback; else if(phase==1)pause.Normal=callback; else pause.Late=callback;
            s=new(){Listeners=[pause,new AfterRelic("later")]};
            Check(!Run(s) && s.ContinuationRejected && s.Events.Count==phase*2+1, "Choice did not stop phase tail or reject partial continuation.");
        }
        s=new(){HasPendingChoice=true,Listeners=[new AfterRelic("never")]};
        Check(!Run(s)&&s.Events.Count==0,"Pending entry executed.");
        s=new(){Listeners=[new AfterRelic("throw"){Normal=_=>throw new ArithmeticException()},new AfterRelic("later")]};
        Throws<ArithmeticException>(()=>Run(s),"Exception swallowed.");
        Check(s.Events.SequenceEqual(["E:throw","E:later","N:throw"]),"Exception continued.");
        AfterRelic added=new("added");
        s=new(){Listeners=[new AfterRelic("mutate"){Early=c=>c.Simulator.Listeners=[added]},new AfterRelic("captured")]};
        Check(Run(s)&&s.Events.SequenceEqual(["E:mutate","E:captured","N:added","L:added"]),"Membership snapshot is not per round.");
        s=new(){Listeners=[new AfterRelic("end"){Normal=c=>c.Simulator.IsOverOrEnding=true},new AfterRelic("captured")]};
        Check(Run(s)&&s.Events.Contains("N:captured"),"Inserted a mid-round terminal gate.");
        AfterCard old=new(){Label="old"},fresh=new(){Label="fresh"}; PredictedCard card=new(){Preview=old};
        s=new(){Listeners=[new AfterRelic("cow"){Normal=_=>card.Preview=fresh},old]};s.State.Player.Cards[old]=card;
        Check(Run(s)&&s.Events.Contains("fresh")&&!s.Events.Contains("old"),"Stale card COW receiver.");
        s=new();s.State.CombatState=new SimulatedCombatState();
        Check(Run(s)&&s.Events.Count==0,"Registered handler bypassed native phase dispatch without an initial listener.");
        s=new(){Listeners=[new NativeNormalGenerator()]};s.State.CombatState=new SimulatedCombatState();
        Check(Run(s)&&s.Events.SequenceEqual(["native normal","generated late"]),
            "A normal-phase vanilla effect generated a late external listener that was skipped.");
        Console.WriteLine($"AFTER_PLAYER_START_MIRRORS_OK checks={checks}");
    }
}
class AfterRelic(string label="base") : RelicModel
{
    public string Label=label;
    public Action<AfterPlayerTurnStartMirrorContext>? Early,Normal,Late;
    public override Task AfterPlayerTurnStartEarly(PlayerChoiceContext c,Player p)=>throw new Exception("Native invoked.");
    public override Task AfterPlayerTurnStart(PlayerChoiceContext c,Player p)=>throw new Exception("Native invoked.");
    public override Task AfterPlayerTurnStartLate(PlayerChoiceContext c,Player p)=>throw new Exception("Native invoked.");
}
class DerivedAfterRelic:AfterRelic;
class UnknownAfterRelic:AfterRelic;
class UnknownNormal:ModifierModel { public override Task AfterPlayerTurnStart(PlayerChoiceContext c,Player p)=>throw new Exception("Native invoked."); }
class UnknownLate:ModifierModel { public override Task AfterPlayerTurnStartLate(PlayerChoiceContext c,Player p)=>throw new Exception("Native invoked."); }
abstract class AbstractAfterRelic:AfterRelic;
class AfterPower:PowerModel { public override Task AfterPlayerTurnStart(PlayerChoiceContext c,Player p)=>throw new Exception("Native invoked."); }
class AfterModifier:ModifierModel { public override Task AfterPlayerTurnStart(PlayerChoiceContext c,Player p)=>throw new Exception("Native invoked."); }
class AfterCard:CardModel { public string Label=""; public override Task AfterPlayerTurnStart(PlayerChoiceContext c,Player p)=>throw new Exception("Native invoked."); }
