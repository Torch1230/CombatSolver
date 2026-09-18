#!/bin/zsh
# macOS 上跑一次 CombatSolver 无人测试请求：APFS 克隆游戏包到 .local/headless-mac、只装本仓库构建的
# CombatSolver 与工坊里的 RitsuLib、用隔离 HOME 承载 user://，写入请求后轮询结果。
#
# 用法：tools/run-unattended-test-macos.sh <请求 JSON> [超时秒，默认 180]
# 环境变量：
#   COMBATSOLVER_STS2_APP     游戏包路径（默认 Steam 库里的 SlayTheSpire2.app）
#   COMBATSOLVER_RITSU_DIR    RitsuLib 工坊目录（默认 workshop/content/2868840/3747602295）
#   COMBATSOLVER_HEADLESS_MAC 实例根（默认仓库 .local/headless-mac）
# 请求 JSON 与 Windows/Linux 入口同形（见 coverage/unattended/*.json）；结果打印 status/error/completedChecks。
set -euo pipefail
setopt nullglob
request_src="$1"; timeout_sec="${2:-180}"
repo="$(cd "$(dirname "$0")/.." && pwd)"
steam="$HOME/Library/Application Support/Steam/steamapps"
src_app="${COMBATSOLVER_STS2_APP:-$steam/common/Slay the Spire 2/SlayTheSpire2.app}"
ritsu="${COMBATSOLVER_RITSU_DIR:-$steam/workshop/content/2868840/3747602295}"
here="${COMBATSOLVER_HEADLESS_MAC:-$repo/.local/headless-mac}"
app="$here/SlayTheSpire2.app"
iso="$here/home"
dll="$repo/.godot/mono/temp/bin/Release/CombatSolver.dll"
manifest="$repo/CombatSolver.json"
[[ -f "$dll" ]] || { echo "缺少 $dll，先 dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false" >&2; exit 2; }
[[ -f "$ritsu/mod_manifest.json" ]] || { echo "缺少 RitsuLib：$ritsu" >&2; exit 2; }

mkdir -p "$here"
if [[ ! -d "$app" ]]; then
    cp -cR "$src_app" "$app"   # APFS 克隆，几乎不占盘
fi
mods="$app/Contents/MacOS/mods"
rm -rf "$mods"; mkdir -p "$mods/CombatSolver" "$mods/STS2-RitsuLib"
cp "$dll" "$mods/CombatSolver/CombatSolver.dll"; cp "$manifest" "$mods/CombatSolver/CombatSolver.json"
cp -R "$ritsu/." "$mods/STS2-RitsuLib/"
cp "$ritsu/mod_manifest.json" "$mods/STS2-RitsuLib/STS2-RitsuLib.json"
rm -f "$mods/STS2-RitsuLib/mod_manifest.json" "$mods/STS2-RitsuLib/RitsuLib.References.props"

data="$iso/Library/Application Support/SlayTheSpire2"
mkdir -p "$data"
rm -f "$data"/combat_solver_test_result.json "$data"/combat_solver_test_running.json "$data"/combat_solver_test_ready.json
cp "$request_src" "$data/combat_solver_test_request.json"
# 全新 profile 第一次启动会因“没看过模组警告”跳过所有模组；有 settings.save 时把模组置为启用。
settings="$data/default/1/settings.save"
if [[ -f "$settings" ]]; then
    python3 - "$settings" <<'PY'
import json, sys, pathlib
p = pathlib.Path(sys.argv[1]); s = json.loads(p.read_text())
s['mod_settings'] = {'mods_enabled': True, 'mod_list': []}
p.write_text(json.dumps(s))
PY
else
    echo "首次启动：本次只用来生成 profile，请再跑一遍" >&2
fi
log="$here/godot-headless.log"; rm -f "$log"

cd "$app/Contents/MacOS"
env HOME="$iso" COMBATSOLVER_HEADLESS=1 "./Slay the Spire 2" --headless --disable-vsync --max-fps 0 --force-steam=off --log-file "$log" >"$here/launcher.log" 2>&1 &
pid=$!
result="$data/combat_solver_test_result.json"
for ((i=0; i<timeout_sec; i++)); do
    [[ -f "$result" ]] && break
    kill -0 $pid 2>/dev/null || break
    sleep 1
done
rc=1
if [[ -f "$result" ]]; then
    python3 -c "import json,sys; r=json.load(open(sys.argv[1])); print('status=',r.get('status'),'error=',r.get('error')); print('checks=',r.get('completedChecks')); sys.exit(0 if r.get('status')=='Passed' else 1)" "$result" && rc=0
else
    echo "NO RESULT (pid alive: $(kill -0 $pid 2>/dev/null && echo yes || echo no))" >&2
fi
sleep 2; kill $pid 2>/dev/null || true; wait $pid 2>/dev/null || true
exit $rc
