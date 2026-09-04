---
name: release-gate
description: 用户要求准备或发布 CombatSolver 版本、生成 ZIP、创建版本标签、上传 Steam 创意工坊、同步 GitHub、执行干净安装或完整发布验收时，按口令边界完成对应阶段；普通开发不触发外部发布。
---

# CombatSolver 发布、渠道上传与远端同步

## 适用边界

先按用户口令确定唯一动作范围：

| 用户口令 | 执行范围 | 不执行 |
| --- | --- | --- |
| `写更新日志` | 撰写当前开发版本的玩家更新日志 | 版本提升、构建、ZIP、标签、上传、推送 |
| `准备发版` | 版本同步、更新日志、提交、一次 Release 构建、一次最小 ZIP | 标签、创意工坊、GitHub 推送、完整门禁 |
| `给我审核/我拍板后再发` | 整理并提交更新日志草案 | 构建、ZIP、标签和外部动作，直到用户批准 |
| `发版/发布` | 补齐尚未完成的准备阶段，创建当前版本 annotated tag | 创意工坊、GitHub 推送、完整门禁 |
| `上传/更新创意工坊` | 上传当前已定版版本和玩家更新说明 | GitHub 推送、发布后复测、远端页面核对 |
| `推送/同步远端` | 干净提交并推送当前分支；当前版本标签已存在时一并推送 | 构建、打包、创意工坊、创建无关标签 |
| `完整发布门禁/完整验收/干净安装` | 执行第 7 节完整门禁 | 不省略用户点名的门禁项 |

用户声明活动发布批次时，批次规则优先：在用户结束批次前只提交到指定开发版本，不逐项定版。普通“发版”不等于完整发布门禁。

一句请求包含多个口令时，执行覆盖其全部要求的最窄阶段；“给我审核/我拍板后”始终暂停构建、标签和外部动作，直到用户批准。

## 1. 版本与发布来源

- 同步 `CombatSolver.csproj`、`CombatSolver.json`、`docs/DEVELOPMENT_NOTES.md`、`docs/TEST_MATRIX.md` 和该版本玩家更新日志。
- 玩家更新日志使用当前游戏官方简中译名，只写玩家可感知的变化。开发日志中的根因、内部职责、runId、构建和测试信息不复制进去。
- 提交源码、fixture 和文档后，从该提交构建；记录该提交为 release source commit。构建后行为源码、编译配置、依赖或 manifest 变化才使构建失效，纯渠道暂存变化不会。
- “当前最新版”来自仓库中已同步、已提交且完成最小发包的最高版本，不来自游戏 Mods 目录或创意工坊暂存目录。
- 版本创建标签或成功上传创意工坊后即冻结。后续行为改动进入新的“下一版本（开发中）”，不回写已发布版本；用户未指定新版本号时保留待定，不自行猜版本。
- 普通发布不计算哈希。只有来源冲突、发布渠道争议或用户明确要求时才计算。

## 2. 一次 Release 构建

Windows（PowerShell 7）：

```powershell
dotnet clean -c Release
pwsh -NoProfile -File tools\build-local-stack.ps1 -Configuration Release
```

Linux（Bash）：

```bash
dotnet clean -c Release
./tools/build-local-stack.sh --configuration Release
```

从 release source commit 构建，不从游戏 Mods 目录反向复制 DLL，不复用未知来源旧构建。构建成功后不再反射 DLL 版本、重复构建、再次复制部署或重跑已经通过的行为场景。

目标行为已在当前行为源码上通过，之后只改版本号、文档或发布元数据时，Release 构建足以进入打包，不重复行为审计。

## 3. 一次最小 ZIP

当前 `has_pck=false`。命令显式只写入：

- `CombatSolver.json`；
- 刚完成的 Release `CombatSolver.dll`；
- Windows 发布还包括同次构建的 `CombatSolver.MemoryCleaner.exe`；
- 根目录 `THIRD_PARTY_NOTICES.md`；
- manifest 将来明确要求的其他资产。

`THIRD_PARTY_NOTICES.md` 是 Random Foreseer 书面许可要求的二进制分发文件，不得省略。不得加入源码、日志、问题包、存档、fixture、游戏依赖 DLL、`bin/obj/.godot/.local` 或旧 DLL。

ZIP 创建命令成功就是完成证据。不要重新打开、解压、枚举条目、读取 DLL 版本或计算哈希。

## 4. 版本标签

- `准备发版` 不创建标签；用户批准并说 `发版/发布` 时，在 release source commit 创建 annotated tag `v<manifest version>`，消息为 `Combat Solver <manifest version>`。
- 如果用户跳过单独的“发版”口令，直接把已准备版本上传创意工坊，上传成功即表示该版本已经发布；此时在 release source commit 补建本地 annotated tag。
- 标签存在时不移动、不删除、不重建。标签目标与 release source commit 冲突时停止并报告，不猜测哪个版本正确。
- 创建本地标签不等于已同步 GitHub。只有用户要求推送远端时才推送该标签。

## 5. 创意工坊上传

默认工具与暂存目录：

- `D:\Desktop\sts2mod\ModUploader-win-x64\ModUploader.exe`；
- `D:\Desktop\sts2mod\ModUploader-win-x64\CombatSolverWorkshop`。

Linux 不使用上述 Windows 路径。上传前必须设置 `COMBATSOLVER_MOD_UPLOADER` 和 `COMBATSOLVER_WORKSHOP_DIR`；任一路径未设置、不可执行或不存在时原样报告阻塞，不猜测其他目录或兼容层。

上传前只做一次本地暂存：

1. 用当前 release source 的 `CombatSolver.json`、刚完成的 Release DLL、Windows `CombatSolver.MemoryCleaner.exe` 和根目录 `THIRD_PARTY_NOTICES.md` 覆盖 `CombatSolverWorkshop/content/`；
2. 保留标题、长描述、作者、封面、效果图、标签、依赖和可见性，除非用户明确要求修改或兼容性事实已经变化；
3. 将该版本玩家更新日志提炼为 `workshop.json` 的 `changeNote`；
4. Windows 执行一次 `ModUploader.exe upload -w .\CombatSolverWorkshop`；Linux 执行一次 `"$COMBATSOLVER_MOD_UPLOADER" upload -w "$COMBATSOLVER_WORKSHOP_DIR"`。

更新说明只写新增功能、实战结果修复、路线质量、UI/操作、兼容性和玩家能感知的性能变化。不要写类名、方法名、Beam/Mirror/GC 实现、runId、提交、构建、测试或打包过程。

上传命令报告成功就是远端完成证据。不要打开创意工坊页面、重新下载订阅内容、再次读取版本或重复上传。失败时只修正命令明确报告的原因，再重试一次；原因不明则原样报告。

### Steam 工坊 `FileNotFound` 排查

- `k_EItemUpdateStatusInvalid` 与 `k_EResultFileNotFound` 不一定表示暂存目录缺文件。若工作区的 `image.png`、`workshop.json`、`content/` 和 `mod_id.txt` 已通过一次本地读取确认存在，先读取 Steam 客户端日志 `D:\Steam\logs\workshop_log.txt` 中对应时间和 AppID 的记录。
- 如果日志写明 `Getting Workshop info for item <id> failed : File Not Found`，而工坊页面和条目仍存在，根因是 Steam 客户端当前无法查询条目，通常与 Steam CM/网络连接状态有关，不要改名、删除或重建暂存文件。等待客户端恢复登录连接后，按第 5 节的原命令重试一次。
- 这类重试期间以 Steam 日志中的 `Uploaded new content ...` 和 `Upload finished for workshop item <id> : OK` 作为实际成功证据；上传器末尾若同时打印中间状态 `k_EItemUpdateStatusInvalid`，以最终成功行和 Steam 日志为准，不再重复上传。

## 6. GitHub 干净提交与推送

- 读取一次 `git status --short --branch`、当前分支、远端和领先关系。显式暂存本任务的跟踪文件；保留并排除用户其他改动、发布 ZIP、构建产物、日志和创意工坊暂存内容。
- “干净提交”指提交内容边界干净，不表示删除未跟踪文件、清空工作区或回退用户改动。
- 没有新改动但本地提交领先远端时直接推送，不创建空提交。需要提交时，一个提交只表达当前这组文档、修复或发布准备。
- 推送当前分支；当前 manifest 版本的 release tag 已存在时，在同一次 `git push` 中显式推送该 tag。不要使用 `--tags` 把无关标签一并推送。
- `git push` 成功就是完成证据。不要随后 fetch、再次 status、打开 GitHub 页面或检查远端提交。

## 7. 完整发布门禁

只有用户明确说“完整发布门禁”“完整验收”或“干净安装”时执行：

1. 运行 `tools/verify-refactor-boundaries.ps1`；
2. 对当前 DLL 运行 CoverageCatalog 全 verify；
3. 运行目标严格差分、相关类型族、增量等价和至少一场完整自动部署；
4. 跑一场稳定长线质量基准，断言跨回合复用与零非预期重算；
5. 使用正常可见 Steam 会话验证 UI、输入、动画、部署和真实卡顿；
6. 从 ZIP 干净安装到已确认的精确 Mod 目录并做最终冒烟。

完整门禁产生受控文件变化时，审查并提交，再从最终提交重新构建。安装只处理确认过的精确 Mod 目录和已知 manifest/DLL；目录含未知文件时停止，不递归清空。

## 8. 阶段停止条件

每阶段成功一次就向前推进：

- 行为 fixture 成功：不因随后只改文档、版本或发布元数据而复跑；
- Release 构建成功：不反射版本或重复部署；
- ZIP 创建成功：不重新打开或检查包；
- 暂存复制成功：不逐文件比较；
- 创意工坊上传成功：不打开页面或重新下载；
- Git 推送成功：不 fetch/status 或查看网页复核。

最终汇报只列版本、release source commit、实际完成的阶段、发布包或渠道结果，以及确实未执行但用户要求的项目。只有完整门禁全部执行后才能写“完整门禁通过”。
