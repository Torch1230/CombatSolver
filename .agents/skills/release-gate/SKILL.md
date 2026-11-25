---
name: release-gate
description: Prepare CombatSolver releases, synchronize versions and changelogs, build publication ZIPs, and publish via Steam Workshop, GitHub Release, and Quark when requested.
---

# Release Workflow & Gate

严格区分 **准备发版** 与 **正式发版** 两个阶段，只在用户明确发出对应指令时执行相应阶段，并严格遵循用户当次对渠道的保留或跳过指示。

## 1. 指令边界

| 用户指令 | 执行范围 | 关键禁忌 |
| --- | --- | --- |
| 日常修改 / 修复 | 改码与验证 | **不**改版本号、**不**打标签、**不**推远端、**不**上传渠道 |
| **准备发版** | 版本号同步 + 文档归档 + 验证 + 打最小发布包 + 提交 `main` | **不**创建版本标签、**不** push 远端、**不**上传 Steam / GitHub / 夸克 |
| **发版** | 确认主干提交与安装包就绪 + 打注释标签 `v<ver>` + 多渠道发布 | 若用户声明跳过某渠道（如“不推夸克网盘”），严格跳过该渠道 |

## 2. 准备发版检查表

1. **同步版本号**：检查并对齐以下 3 处版本字段：
   - `CombatSolver.csproj`（`<Version>`）
   - `mod_manifest.json`（`"version"`）
   - `CombatSolver.json`（`"version"`）
2. **编写更新日志与分卷归档**：
   - 新增 `docs/releases/<ver>-RELEASE_NOTES.md`（简洁玩家视角说明 + 实测覆盖范围）；
   - 更新根目录 `CHANGELOG.md`（中文）与 `CHANGELOG.en.md`（英文）；
   - 如 `docs/DEVELOPMENT_NOTES.md` 或 `docs/TEST_MATRIX.md` 留有待定稿条目，将其整卷归档到 `docs/archive/development/` 与 `docs/archive/testing/`（更新对应 `README.md` 索引），保持两文件本身在 **200 行以内**；
   - 更新 `README.md` 与 `README.en.md` 中的版本引用（若有）。
3. **验证当前待发版源码**：
   - 运行本轮改动对应的检查与离线门禁，至少保证 Release 主工程与 `OfflineSearchHarness` 编译 0 警告 0 错误、文档检查 `python -B tools\inspection\verify-documentation.py` 通过，且具备本版本的核心正确性/回归证据。
4. **打干净的最小发布 ZIP**：
   ```powershell
   pwsh -NoProfile -File tools\build\build-local-stack.ps1 -ForPublication
   ```
   确认生成的 `dist\CombatSolver-<ver>.zip` 仅包含发布的 4 个文件（`CombatSolver.dll`、`CombatSolver.pck`、`mod_manifest.json`、`CombatSolver.json`）且大小与内容正常；可同时检查游戏本地部署是否最新（不强杀游戏进程）。
5. **在 `main` 提交**：确保仓库无意外脏文件或遗留临时目录后提交 `main`（例如 `release: prepare <ver>`）。

## 3. 正式发版流程

1. **前置确认**：`git status --short` 干净，`HEAD` 与 `dist\CombatSolver-<ver>.zip` 对应待发布版本。
2. **打带注释 Git 标签**：
   ```powershell
   git tag -a "v<ver>" -m "CombatSolver <ver>"
   ```
3. **Steam 创意工坊上传（`ModUploader.exe`）**：
   - 若直接调用上传工具更新创意工坊，变更说明（changelog / change note）**必须严格使用 Steam BBCode 格式**（`[h1]`、`[h2]`、`[list]`、`[*]`、`[b]`、`[url]`），**严禁出现原始 Markdown 语法**（如 `#`、`**`、`*` 列表或 `[text](url)`）；
   - 上传前确认保留原有创意工坊预览图与视频（`previews-preserved`），不覆盖丢失已有媒体资源。
4. **GitHub 推送与 Release**：
   ```powershell
   git push origin main "v<ver>"
   gh release create "v<ver>" "dist\CombatSolver-<ver>.zip" --title "CombatSolver <ver>" --notes-file "docs\releases\<ver>-RELEASE_NOTES.md"
   ```
5. **全渠道脚本 / 夸克网盘**：
   - 若无需跳过网盘，可执行仓库内置发布脚本 `tools/release/publish-release.ps1` 并确保夸克网盘公开共享包仅含对应版本归档目录；
   - 若用户要求跳过特定渠道（如 `不推夸克网盘`），只跑其余渠道并在收尾汇报明确说明已按指示跳过。
