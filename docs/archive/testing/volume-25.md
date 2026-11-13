# 测试记录 · 卷 25

0.51.2，2026-11-13 定稿。

## 0.51.2 属性削减与诅咒持续时间

`lagavulin-mode-debuff.json` 在标准与恐惧分别核对吸取属性、力量归零移除及完整状态。`curse-duration-protection.json` 核对疑虑／羞耻的首次施加、已有层数叠加和 `Artifact` 抵挡，比较回合末完整续用状态。`POWER-DURATION-APPLICATION-NATIVE` 补正显式保护与普通施加的原生区别，覆盖十六步施加、叠加、消退、重新获得、阻挡、状态键及实机推进后的 Fork 回放。两份夹具分别位于 `coverage/fixtures/monsters/` 与 `coverage/fixtures/powers/`；所有请求采用 120 秒上限及实例清理。族母恐惧 `b6972d820a7d401b949ba3cb94607fc0`、标准 `3fe347c113564014a6bcc53c4b1843c3`，诅咒三种条件 `5cebbe7076e54d008cb3e930c9916681`、十六步合同 `5c0ab67fe1234783a47cf1d9558eb8bf` 均 Passed，实例已清理。

仪式兽追加使用 `coverage/fixtures/monsters/ceremonial-beast-ritual-transition.json`：出牌打断后触发敌方回合末、重复伤害，以及炼狱在玩家回合末打断；分别验证恐惧与标准。恐惧 `5fb1b9dad6e848bcb89faa056256e9d7`、标准 `6670b0c777ed4a76aeddbd697a4c243c` 均 Passed，实例已清理。原始玩家环境及整包路线未恢复。

