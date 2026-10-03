using System.Numerics;
using CombatSolver;

// Original production recurrence, deliberately retaining sequential field writes.
static void OriginalAdd(ref ulong first, ref ulong second, ulong value)
{
    first ^= value;
    first *= 1099511628211UL;
    first ^= first >> 32;
    second += value + 0x9e3779b97f4a7c15UL;
    second = BitOperations.RotateLeft(second, 27) * 14029467366897019727UL;
    second ^= second >> 29;
}
var random = new Random(65129);
var actual = new StateFingerprintBuilder();
ulong expectedFirst = 14695981039346656037UL, expectedSecond = 7809847782465536322UL;
void Check()
{
    var result = actual.Finish();
    if (result.First != expectedFirst || result.Second != expectedSecond)
        throw new InvalidOperationException("Fingerprint changed from original recurrence.");
}
foreach (ulong value in new[] { 0UL, 1UL, ulong.MaxValue, (ulong)long.MaxValue, 1UL << 63 })
{
    actual.Add(value); OriginalAdd(ref expectedFirst, ref expectedSecond, value); Check();
}
for (int index = 0; index < 200_000; index++)
{
    long raw = random.NextInt64(long.MinValue, long.MaxValue);
    switch (index % 8)
    {
        case 0: actual.Add(raw); OriginalAdd(ref expectedFirst, ref expectedSecond, unchecked((ulong)raw)); break;
        case 1: actual.Add((int)raw); OriginalAdd(ref expectedFirst, ref expectedSecond, unchecked((ulong)(long)(int)raw)); break;
        case 2: actual.Add((uint)raw); OriginalAdd(ref expectedFirst, ref expectedSecond, (uint)raw); break;
        case 3: actual.Add((char)raw); OriginalAdd(ref expectedFirst, ref expectedSecond, (char)raw); break;
        case 4: actual.Add((raw & 1) != 0); OriginalAdd(ref expectedFirst, ref expectedSecond, (ulong)(raw & 1)); break;
        case 5:
            string? text = index % 3 == 0 ? null : index % 3 == 1 ? "" : new string([(char)raw, '汉', 'x', '\ud800', '\udfff']);
            actual.Add(text);
            if (text is null) OriginalAdd(ref expectedFirst, ref expectedSecond, ulong.MaxValue);
            else
            {
                OriginalAdd(ref expectedFirst, ref expectedSecond, (ulong)text.Length);
                foreach (char c in text) OriginalAdd(ref expectedFirst, ref expectedSecond, c);
            }
            break;
        case 6:
            decimal value = new((int)raw, (int)(raw >> 32), random.Next(), raw < 0, (byte)random.Next(29));
            actual.Add(value);
            foreach (int bit in decimal.GetBits(value)) OriginalAdd(ref expectedFirst, ref expectedSecond, unchecked((ulong)(long)bit));
            break;
        case 7: actual.Add(unchecked((ulong)raw)); OriginalAdd(ref expectedFirst, ref expectedSecond, unchecked((ulong)raw)); break;
    }
    Check();
}
Console.WriteLine("STATE_FINGERPRINT_OK: original 128-bit output after 200,005 mixed and boundary inputs.");
