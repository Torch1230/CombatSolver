using System.Reflection;
using System.Text;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Enchantments;

namespace CombatSolver;

internal static class EnchantmentStateSupport
{
    private static readonly FieldInfo MomentumExtraDamageField = RequiredField(typeof(Momentum), "_extraDamage");

    public static void Append(ref StateFingerprintBuilder key, EnchantmentModel? enchantment)
    {
        key.Add(enchantment?.Id.Entry);
        if (enchantment == null)
            return;

        key.Add(enchantment.Amount);
        key.Add((int)enchantment.Status);
        switch (enchantment)
        {
            case Momentum momentum:
                key.Add((int)MomentumExtraDamageField.GetValue(momentum)!);
                break;
        }
    }

    public static void Append(StringBuilder text, EnchantmentModel? enchantment)
    {
        if (enchantment == null)
        {
            text.Append("-:0:0");
            return;
        }

        text.Append(enchantment.Id.Entry)
            .Append(':').Append(enchantment.Amount)
            .Append(':').Append((int)enchantment.Status);
        switch (enchantment)
        {
            case Momentum momentum:
                text.Append(':').Append((int)MomentumExtraDamageField.GetValue(momentum)!);
                break;
        }
    }

    public static string Describe(EnchantmentModel enchantment)
    {
        StringBuilder text = new();
        Append(text, enchantment);
        return text.ToString();
    }

    private static FieldInfo RequiredField(Type type, string name)
        => type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
           ?? throw new MissingFieldException(type.FullName, name);
}
