using CombatSolver;
int checks = 0;
void Check(int actual, int expected) { if (actual != expected) throw new Exception($"{actual} != {expected}"); checks++; }
Check(PotionInventoryValue.RequiredCost(9, true, true, 9), 3);
Check(PotionInventoryValue.RequiredCost(18, true, true, 9), 12);
Check(PotionInventoryValue.RequiredCost(18, true, false, 9), 18);
Check(PotionInventoryValue.RequiredCost(9, false, true, 9), 9);
Check(PotionInventoryValue.RequiredCost(0, true, false, 9), 0);
for (int regular = 1; regular <= 20; regular++)
for (int first = 1; first <= regular; first++)
for (int hp = 0; hp <= 100; hp++)
{
    int expected = 0;
    while (first + expected * regular <= hp) expected++;
    Check(PotionInventoryValue.PaidCapacity(hp, regular, first), expected);
}
Check(PotionInventoryValue.PaidCapacity(100, int.MaxValue, int.MaxValue), 0);
Check(PotionInventoryValue.PaidCapacity(-1, 9, 3), 0);
Check(PotionInventoryValue.PaidCapacity(3, 9, 3), 1);
Check(PotionInventoryValue.PaidCapacity(11, 9, 3), 1);
Check(PotionInventoryValue.PaidCapacity(12, 9, 3), 2);
Console.WriteLine($"POTION_INVENTORY_CHECKS_OK checks={checks}");
