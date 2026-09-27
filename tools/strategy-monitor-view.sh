#!/usr/bin/env bash
set -Eeuo pipefail
session="${1:?session directory required}"
state="$session/monitor-state.json"
while [[ ! -e "$session/monitor.stop" ]]; do
    clear
    if [[ -f "$state" ]]; then
        jq -r '
            "CombatSolver  策略会话监控\n",
            "包            \(.reportId // "—")",
            "请求          \(.runId // "—")",
            "状态          \(.state // "—")",
            "阶段          \(.phase // "—")",
            "游戏 PID      \(.processId // "—")",
            "脚本版本      \((.scriptHash // "—")[:12])",
            "参数版本      \((.parametersHash // "—")[:12])\n",
            "搜索配置      \(.performancePreset // "—") / \(.parallelism // "—") 并行",
            "请求时限      \(.requestTimeoutSeconds // "—") 秒",
            "已用          \(if .elapsedMilliseconds then ((.elapsedMilliseconds / 1000) | tostring) + " 秒" else "—" end)",
            "剩余          \(if .remainingMilliseconds then ((.remainingMilliseconds / 1000) | tostring) + " 秒" else "—" end)",
            "展开节点      \(.expandedNodes // "—")",
            "已查世界线    \(.reviewedWorldlines // "—")",
            "搜索速率      \(.worldlinesPerSecond // "—") 条/秒",
            "前沿节点      \(.frontierNodes // "—")\n",
            "当前最好战损  \(.bestProjectedHpLoss // "—")",
            "当前用药      \(.bestPotionCount // "—")",
            "结果或错误    \(.reason // "—")\n",
            "关闭此窗口不会停止 headless 搜索。"
        ' "$state"
    else
        echo '等待会话状态…'
    fi
    sleep 1
done
