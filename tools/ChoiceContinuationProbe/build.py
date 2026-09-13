#!/usr/bin/env python3
"""Build a diagnostic DLL in a disposable checkout; restore every injected source afterwards."""
import argparse
from pathlib import Path
import shutil
import subprocess

parser = argparse.ArgumentParser()
parser.add_argument('--source', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
source, output = args.source.resolve(), args.output.resolve()
here = Path(__file__).resolve().parent
saved = {}
created = []

def replace(path, old, new):
    p = source / path
    if p not in saved:
        saved[p] = p.read_bytes()
    text = p.read_text()
    assert text.count(old) == 1, (path, old[:100], text.count(old))
    p.write_text(text.replace(old, new))

try:
    probe = source / 'src/Search/ChoiceContinuationProbe.cs'
    assert not probe.exists()
    shutil.copy2(here / probe.name, probe)
    created.append(probe)
    replace('src/Search/CombatBeamSolver.Expansion.cs',
        '        ReplayForkSeed? gatedSeed = null;',
        '''        using var continuationProbe = ChoiceContinuationProbe.EnterReplay(parent,
            BuildCycleDeterministicActionFingerprint(action with
            { Choice = null, NestedChoices = null, TurnStartChoices = null }));
        ReplayForkSeed? gatedSeed = null;''')
    replace('src/Engine/InCombat/Simulation/CombatPredictionSimulator.Card.cs',
        '        int historyEntryStart = History.Entries.Count;\n        var resources = SpendResources(card, isAutoPlay: false);',
        '        using var continuationProbe = ChoiceContinuationProbe.EnterManual(this, card);\n        int historyEntryStart = History.Entries.Count;\n        var resources = SpendResources(card, isAutoPlay: false);')
    replace('src/Engine/InCombat/Simulation/CombatPredictionSimulator.Card.cs',
        '            if (!isAutoPlay\n                && State.CombatState is ICombatPredictionManualCardChoiceSink choiceSink',
        '            if (!isAutoPlay) ChoiceContinuationProbe.SetPlayCount(playCount);\n            if (!isAutoPlay\n                && State.CombatState is ICombatPredictionManualCardChoiceSink choiceSink')
    replace('src/Search/SimulatedCombatState.ActionChoices.cs',
        '''    bool ICombatPredictionManualCardChoiceSink.ResolveManualCardChoice(
        CombatPredictionSimulator simulator,
        PredictedCard card)
    {
        ISet<uint> processedEnemyDeaths = _activeCardExecutionDeaths ?? new HashSet<uint>();
        CardChoiceSpec? spec = CardChoiceSupport.GetSpec(simulator, card);''',
        '''    bool ICombatPredictionManualCardChoiceSink.ResolveManualCardChoice(
        CombatPredictionSimulator simulator,
        PredictedCard card)
    {
        ISet<uint> processedEnemyDeaths = _activeCardExecutionDeaths ?? new HashSet<uint>();
        CardChoiceSpec? spec = CardChoiceSupport.GetSpec(simulator, card);
        ChoiceContinuationProbe.AtChoice(spec, _activeActionChoices?.ContinuationProbeIndex ?? -1,
            _cardExecutionScopeDepth, _pendingPowerAmountChanges?.Count ?? 0, _playerTurnEndRequested);''')
    replace('src/Prediction/TurnStartChoiceSupport.cs',
        '    private int _index;', '    private int _index;\n    internal int ContinuationProbeIndex => _index;')
    replace('src/Engine/InCombat/Simulation/CombatPredictionHistory.cs',
        '    private int _pendingDeferredEntries;',
        '    private int _pendingDeferredEntries;\n    internal int ContinuationProbePending => _pendingDeferredEntries;')
    replace('src/Engine/Common/PredictionStateStore.cs',
        '    private Dictionary<AbstractModel, AbstractModel>? _modelAliases;',
        '''    private Dictionary<AbstractModel, AbstractModel>? _modelAliases;
    internal void ContinuationProbeBlockers(List<string> blockers)
    {
        if (_states == null) return;
        foreach (var state in _states.Values)
        {
            if (state is not IPredictionForkBoundary boundary) continue;
            // Only invoke repository-owned, audited read-only boundary predicates.
            string name = state.GetType().FullName!;
            bool known = state.GetType().Assembly == typeof(PredictionStateStore).Assembly
                && (name.EndsWith("CardPlayPairPredictionState")
                    || name.EndsWith("MusicBoxPredictionState")
                    || name.EndsWith("ImitationLearningPredictionState")
                    || name.EndsWith("PenNibPredictionState")
                    || name.EndsWith("CurlUpPredictionState")
                    || name.Contains("VigorPowerMirrors+")
                    || name.Contains("GigantificationPowerMirrors+"));
            if (!known) { blockers.Add("UninspectedState:" + name); continue; }
            try { boundary.AssertForkable(); }
            catch (InvalidOperationException) { blockers.Add("ActiveState:" + name); }
        }
    }''')
    replace('src/Engine/InCombat/Simulation/CombatPredictionSimulator.cs',
        '    private CombatDamageSource? _damageSource;',
        '''    private CombatDamageSource? _damageSource;
    internal List<string> ContinuationProbeBlockers()
    {
        List<string> result = [];
        if (_damageSource != null) result.Add("DamageSource");
        if (_activeDrawDepth != 0) result.Add("ActiveDraw");
        if (History.ContinuationProbePending != 0) result.Add("DeferredHistory");
        if (ActionRelicTriggers != null) result.Add("RelicTriggerRecorder");
        if (_blockGainedByCardPlay.Count != 0) result.Add("BlockByCardPlay");
        StateStore.ContinuationProbeBlockers(result);
        return result;
    }''')
    replace('src/Testing/UnattendedTestRunner.Writer.cs',
        '        public void CaptureSolverResult(SolverResult result)\n        {',
        '''        public void CaptureSolverResult(SolverResult result)
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(getRequest().EvidenceDirectory!, "continuation-census.json"), ChoiceContinuationProbe.Export());
            System.IO.File.WriteAllText(System.IO.Path.Combine(getRequest().EvidenceDirectory!, "details.json"), JsonSerializer.Serialize(new { result = SolverDiagnostics.DescribeResult(result), actions = result.BestNode.Actions }));''')
    subprocess.run(['dotnet', 'build', 'CombatSolver.csproj', '-c', 'Release',
        '-p:CopyModOnBuild=false', '-o', str(output)], cwd=source, check=True)
    shutil.copy2(source/'CombatSolver.json', output/'CombatSolver.json')
finally:
    for p, data in saved.items():
        p.write_bytes(data)
    for p in created:
        p.unlink()
