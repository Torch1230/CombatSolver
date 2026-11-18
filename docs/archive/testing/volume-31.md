# 测试历史卷 31

## 0.51.8 治疗补丁启动修复

- `COMBAT-DIAGNOSTIC-LOG`，`PHROG_PARASITE_ELITE`，运行 `7f63a02dd65c45a7ad6b91026b4b4568` 通过（24.75 秒，含启动）。游戏 `v0.113.1 d09d48e5`，加载薄荷尖塔 1.3.1、BaseLib 和 RitsuLib。进一步安装空操作治疗后置补丁，真实短搜完成终局物化，伤害及治疗的原生状态差分通过。注册未镜像的 Ritsu 全局治疗监听器后，诊断 ZIP 继续生成，生产上传客户端收到测试 HTTP 回执。
- 本机 0.51.7 对照运行 `56b9e1f0fc8448d6b88e50f16c740099`（RitsuLib）与 `024dd0d2adf44cd9a91c4dd4134ecf8e`（另加载 BaseLib 与薄荷尖塔）通过。0.51.7 合同验证了附加治疗后置补丁触发相同的根捕获拒绝。玩家机器未复测。
- 合同入口为 `tools/testing/run-unattended-test.ps1 -ScenarioId COMBAT-DIAGNOSTIC-LOG -EncounterId PHROG_PARASITE_ELITE -TimeoutSeconds 110 -CleanupInstanceOnExit`。组合验证在独立实例中加载额外 Workshop 组件，启动器已清理实例。
- `NATIVE-HEALING-CALLBACK-BOUNDARY`（`REGENT` / `FUZZY_WURM_CRAWLER_WEAK`）的 0.51.7 基线 `eb18d63ad9ab443ba37537f1c8fec9d4` 与修复版 `8ae78b4039854f14b79b1a91fc4e0735` 都在初始证书检查返回 `used-potion-event-layout`。该场景未覆盖后续断言，两个实例已清理。
