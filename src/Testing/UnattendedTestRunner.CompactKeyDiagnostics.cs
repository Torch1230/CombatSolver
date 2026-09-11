using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private void WriteCompactKeyFieldDifference(CombatPredictionSimulator expected, CombatPredictionSimulator actual,
        CompactDiscardProjection adapter, string stage)
    {
        var left = (SimulatedCombatState)expected.State.CombatState;
        var right = (SimulatedCombatState)actual.State.CombatState;
        string Format(object? value, int depth = 0)
        {
            if (value == null) return "null";
            if (value is string text) return text;
            if (value is Creature creature) return $"creature:{creature.CombatId}";
            if (value is Player player) return $"player:{player.Creature.CombatId}";
            if (value is PredictedCard card) return $"card:{adapter.IndexOf(card.Original)}:{card.Preview.Id}:{CombatBeamSolver.CaptureCardStateFingerprintForTesting(card)}";
            if (value is CardModel model) return $"card:{adapter.IndexOf(model)}:{model.Id}";
            if (value is PowerModel power) return string.Join('|', CompactPowerValues([power]));
            if (value is AbstractModel other) return other.Id.ToString();
            if (value is Type type) return type.FullName!;
            if (depth >= 4) return value.GetType().Name;
            if (value is ITuple tuple) return "(" + string.Join(',', Enumerable.Range(0, tuple.Length).Select(index => Format(tuple[index], depth + 1))) + ")";
            if (value.GetType().IsGenericType && value.GetType().GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
                return Format(value.GetType().GetProperty("Key")!.GetValue(value), depth + 1) + "="
                    + Format(value.GetType().GetProperty("Value")!.GetValue(value), depth + 1);
            if (value is IEnumerable sequence) return "[" + string.Join(',', sequence.Cast<object?>().Select(item => Format(item, depth + 1))) + "]";
            return value.ToString()!;
        }
        var fields = typeof(SimulatedCombatState).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Select(field => new { field.Name, expected = Format(field.GetValue(left)), actual = Format(field.GetValue(right)) })
            .Where(field => field.expected != field.actual).ToArray();
        Directory.CreateDirectory(_request.EvidenceDirectory!);
        File.WriteAllText(Path.Combine(_request.EvidenceDirectory!, "compact-key-difference.json"),
            JsonSerializer.Serialize(new { stage, fields }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
