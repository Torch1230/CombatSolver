using CombatSolver;
int checks = 0;
void Check(int actual, int expected) { if (actual != expected) throw new Exception($"{actual} != {expected}"); checks++; }
Check(PotionInventoryValue.RequiredCost(9, true, true, 9), 3);
Check(PotionInventoryValue.RequiredCost(18, true, true, 9), 12);
Check(PotionInventoryValue.RequiredCost(18, true, false, 9), 18);
Check(PotionInventoryValue.RequiredCost(9, false, true, 9), 9);
Check(PotionInventoryValue.RequiredCost(0, true, false, 9), 0);
Console.WriteLine($"POTION_INVENTORY_CHECKS_OK checks={checks}");
