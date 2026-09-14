using System.Globalization;
using System.Text;

namespace CombatSolver;

internal delegate void ModelPredictionStateWrite<in T>(T source, ref ModelPredictionStateWriter writer);

/// <summary>
/// Writes the same ordered, typed fields to a search fingerprint or a continuation stamp.
/// Field order and collection order are part of the adapter's state contract.
/// </summary>
internal partial struct ModelPredictionStateWriter
{
    private StateFingerprintBuilder _fingerprint;
    private readonly StringBuilder? _text;
    private MegaCrit.Sts2.Core.Combat.ICombatState? _referenceCombat;
    private CombatSolver.Engine.InCombat.Simulation.CombatPredictionSimulator? _referenceSimulator;
    private Dictionary<object, CardPosition>? _cardPositions;

    internal ModelPredictionStateWriter(StateFingerprintBuilder fingerprint, StringBuilder? text = null)
    {
        _fingerprint = fingerprint;
        _text = text;
    }

    internal readonly StateFingerprintBuilder Fingerprint => _fingerprint;

    public void Add(string name, long value)
    {
        BeginField(name, 'i');
        _fingerprint.Add(value);
        _text?.Append(value.ToString(CultureInfo.InvariantCulture));
    }

    public void Add(string name, ulong value)
    {
        BeginField(name, 'u');
        _fingerprint.Add(value);
        _text?.Append(value.ToString(CultureInfo.InvariantCulture));
    }

    public void Add(string name, bool value)
    {
        BeginField(name, 'b');
        _fingerprint.Add(value);
        _text?.Append(value ? '1' : '0');
    }

    public void Add(string name, string? value)
    {
        BeginField(name, value is null ? 'n' : 's');
        _fingerprint.Add(value);
        if (value is not null && _text is not null)
            AppendEscaped(_text, value);
    }

    private void BeginField(string name, char kind)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        _fingerprint.Add(kind);
        _fingerprint.Add(name);
        if (_text is null)
            return;
        _text.Append('|').Append(kind).Append(':');
        AppendEscaped(_text, name);
        _text.Append('=');
    }

    private static void AppendEscaped(StringBuilder text, string value)
    {
        // ContinuationStamp splits on semicolons. Escape delimiters and control characters
        // by UTF-16 code unit, preserving null/empty and even unpaired surrogate identities.
        foreach (char character in value)
        {
            if (character is ';' or '|' or ':' or '=' or '\\' || char.IsControl(character)
                || char.IsSurrogate(character))
                text.Append("\\u").Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
            else
                text.Append(character);
        }
    }
}
