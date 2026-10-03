# CombatSolver 适配验证入口

可重跑 fixture 和结构化证据由 `coverage/` 维护。本文保留官方名称查询方法；历史闭环数字使用归档时的统计口径。

历史记录见 [归档索引](archive/adaptation/README.md)。

> 适配基线：CombatSolver `0.13.22`、《杀戮尖塔 2》`0.111.0`、RitsuLib 实测 `0.5.14`（最低 `0.5.13`）；模拟核心已内置，不依赖 RandomForeseer。
>
> 本文档由开发者手工维护。覆盖目录工具不会生成或覆盖本文档。内部类名和行动 ID 保留原文，便于查日志与源码；怪物名和行动名读取当前游戏 PCK 中的官方简体中文本地化。游戏没有独立词条时保留内部 ID 并明确标注，不自行翻译。

> 当前状态：下文早期批次保留为历史验证记录，其中写有 `DynamicResolution`、`NativeAutoRescan`、“原生结算后重搜”、固定洗牌边界或“首回合仍需玩家选择”的结论均不再描述当前生产行为。当前状态以本文最前方批次、`coverage/*.json` 门禁和 `docs/TEST_MATRIX.md` 为准；首回合玩家选择欠账与原生重扫边界都为零项。

本文登记经过本项目逐项核对并完成独立闭环的确定性战斗语义，包括内置引擎 Mirror 与求解器补偿。原生启动前状态、纯表现和范围外条目见 `COMBAT_HOOK_COVERAGE.md`。

## 官方简中名称读取方法

官中名称以当前目标版本游戏目录中的 `SlayTheSpire2.pck` 为唯一依据。项目使用的游戏目录通常记录在 `local.props` 的 `Sts2Dir`；查询时把该目录下的 PCK 路径传给 Windows 的 `tools/read-game-localization.ps1 -PckPath` 或 Linux 的 `tools/read-game-localization.sh --pck-path`。两套脚本都解析 Godot PCK 文件表，只读取 `localization/zhs/*.json` 并用 JSON 键精确查询；不能再用二进制文本行号推断简中/繁中区间。脚本各自带有平台默认路径，也可显式传入 PCK 路径。工具只读 PCK，不生成或改写本文档。

Windows（PowerShell 7）：

```powershell
# 查询怪物名、行动名和 Power 名。
pwsh -NoProfile -Command "& .\tools\read-game-localization.ps1 -PckPath 'D:\Steam\steamapps\common\Slay the Spire 2\SlayTheSpire2.pck' -Key ([string[]]@('AXEBOT.name','AXEBOT.moves.HAMMER_UPPERCUT.title','STEAM_ERUPTION_POWER.title'))"
```

Linux（Bash）：

```bash
# 查询怪物名、行动名和 Power 名。
./tools/read-game-localization.sh \
  --key AXEBOT.name \
  --key AXEBOT.moves.HAMMER_UPPERCUT.title \
  --key STEAM_ERUPTION_POWER.title
```

Windows 脚本和命令使用 PowerShell 7（`pwsh`），禁止调用 Windows PowerShell 5.1（`powershell.exe`）；Linux 脚本使用 Bash 与 GNU 风格长参数，不调用 PowerShell。修改解析规则时同步维护并验证两套入口。

键名规则：

- 怪物名称：`<MonsterModel.Id.Entry>.name`，例如 `AXEBOT.name`。
- 怪物行动：通常先去掉行动 ID 末尾的 `_MOVE`，再查询 `<怪物ID>.moves.<行动名>.title`，例如 `HAMMER_UPPERCUT_MOVE` 对应 `AXEBOT.moves.HAMMER_UPPERCUT.title`。
- Power、卡牌、遗物等实体：使用其模型 ID 和游戏表实际采用的字段；Power 名通常是 `<POWER_ID>.title`。不确定字段时先在 PCK 中搜索该模型 ID 的完整前缀，不能套用猜测结果。
- 查询脚本从 PCK 目录读取 `localization/zhs` 下的原始 JSON 资源；同一键在多个简中表中重复时直接报错，不猜测覆盖顺序。
- 返回 `Text = null` 表示游戏没有该精确键。此时检查是否为共用回调/重复行动：只有反编译源码确认两个行动共用同一语义和标题时才写“共用词条”；否则在文档中写“游戏无独立简中词条（内部 ID）”，禁止自行翻译。
- 本地化结果由开发者逐项写入对应专题文档；不得增加自动生成本文档的逻辑。
- 新验证记录写入当前测试入口或对应专题证据，完成批次后归档。

当前覆盖状态见 [覆盖目录](COMBAT_HOOK_COVERAGE.md)；先检查其生成版本和实际工具结果，再使用统计。
