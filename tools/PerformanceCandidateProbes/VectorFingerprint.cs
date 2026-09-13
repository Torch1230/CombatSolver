using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
namespace CombatSolver;
internal struct VectorFingerprintBuilder
{
    private ulong _first=14695981039346656037UL, _second=7809847782465536322UL;
    public VectorFingerprintBuilder() { }
    public void Add(ulong value)
    {
        Vector128<ulong> mixed = Vector128.Create(_first ^ value,
            BitOperations.RotateLeft(_second + value + 0x9e3779b97f4a7c15UL,27))
            * Vector128.Create(1099511628211UL,14029467366897019727UL);
        mixed ^= Avx2.ShiftRightLogicalVariable(mixed,Vector128.Create(32UL,29UL));
        _first=mixed.GetElement(0);_second=mixed.GetElement(1);
    }
    public readonly StateFingerprint Finish() => new(_first,_second);
}
