# 已适配 OnPlay 补丁的精确登记

`AdaptedCardOnPlayMirrors` 为根可达卡牌登记完整 Harmony 补丁组合及唯一预测实现。声明绑定精确类型、目标方法、补丁方法、类别、owner 和执行顺序；逐个补丁可识别不代表整个组合已适配。

根捕获核对当前补丁集合并冻结选择，命中后由对应镜像独占 OnPlay。live 配置变化使旧路线在采用、续用和部署核对时失效；worker 不扫描 Harmony 表，也不执行原生补丁。

未知来源、明确不兼容的 Mod 和未登记组合继续拒绝。登记不解除其他 subscriber、隐藏状态或方法检查，也不代表整个 Mod 已兼容。

合同检查、真实 OnPlay 替换、完整 Fork、T1→T2 对账、缓存执行资格与组合续用通过。接口用法和限制见[OnPlay 补丁适配](../third-party-onplay-patches.md)，验证范围见[测试清单](../TEST_MATRIX.md)。
