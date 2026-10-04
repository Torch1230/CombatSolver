# 0.49.1：内存回退紧急修复

修复默认 GC 搜索的不可分割提交回退：常规检查点刷新系统内存上限，显式 `UseDefaultGcAndContinue` 在完成回收后将分配所有权交给 CLR。普通请求各自建立限额，取消与作用域释放保留原有生命周期。

0.49.0 日志站固定 18 份报告、16 场战斗的计算失败均命中同一异常；新真实 CLR 合同在修改前失败，修改后通过。`default-commit` 2 项、`default-entry` 2 项与 `recovery-lifecycle` 3 项通过；未重放原包整场或验证可见游戏性能。逐包身份、首因与验证范围见 [内存提交回归](../../issues/0.49.0-memory-commit-regression-20261004.md)，玩家说明见 [0.49.1 更新日志](../../releases/0.49.1-RELEASE_NOTES.md)。
