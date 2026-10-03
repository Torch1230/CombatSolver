# 状态指纹逐位一致性检查

```bash
dotnet run --project tools/testing/checks/StateFingerprintChecks -c Release
```

直接编译生产 `StateFingerprint.cs`，用原来的顺序字段混合公式作独立 oracle。对 200,005 个边界和混合输入逐步比较两个 64 位结果，包括有符号整数、溢出边界、decimal 四字段、布尔、Unicode/代理字符、空串及 null。无需游戏依赖。

该检查证明本次混合公式重写保持既有指纹，不是性能微基准，也不替代搜索工作量/路线的实际对照。
