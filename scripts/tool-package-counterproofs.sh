#!/usr/bin/env bash
# dotnet tool 安装链路的反证。
#
# 最要紧的一条：--help 不能被 provider 配置检查拦住。
# 刚装完工具的人第一件事就是敲 --help，那时还没配 key；
# 若先报"缺少 provider 配置"，他连有哪些命令都不知道。
#
# 用法：bash scripts/tool-package-counterproofs.sh
set -uo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO" || exit 1

PROG="src/Dougong.Cli/Program.cs"
CSPROJ="src/Dougong.Cli/Dougong.Cli.csproj"

FILES=("$PROG" "$CSPROJ")
BACKUP_DIR="$(mktemp -d)"
trap 'restore; rm -rf "$BACKUP_DIR"' EXIT

for f in "${FILES[@]}"; do cp "$f" "$BACKUP_DIR/$(basename "$f")"; done
restore() { for f in "${FILES[@]}"; do cp "$BACKUP_DIR/$(basename "$f")" "$f" 2>/dev/null || true; done; }

PASS_LIST=""; FAIL_LIST=""

run_case() {
  local name="$1" expect="$2" mutate="$3"

  restore
  "$mutate"

  local changed=0
  for f in "${FILES[@]}"; do
    if ! cmp -s "$f" "$BACKUP_DIR/$(basename "$f")"; then changed=1; break; fi
  done
  if [ "$changed" -eq 0 ]; then
    echo "SETUP-FAIL $name（改动函数没有修改任何文件）"
    FAIL_LIST="$FAIL_LIST $name"
    return
  fi

  local out
  out="$(dotnet run --project spike/Dougong.Agent.Comparison -c Release -- tool-package-checks 2>&1)"

  if echo "$out" | grep -qE "生成失败|Build FAILED|error CS"; then
    echo "BUILD-FAIL $name（改动导致编译失败）"
    echo "$out" | grep -E "error CS" | head -3 | sed 's/^/           /'
    FAIL_LIST="$FAIL_LIST $name"
    return
  fi

  if echo "$out" | grep -q "^FAIL $expect"; then
    echo "RED-OK   $name  → $expect 如期变红"
    PASS_LIST="$PASS_LIST $name"
  else
    echo "NO-EFFECT $name  → $expect 没有变红"
    echo "$out" | sed 's/^/           /'
    FAIL_LIST="$FAIL_LIST $name"
  fi
}

# 1. --help 排在 provider 检查之后 → 新人装完第一件事就撞墙
mut01() {
  python - "$PROG" <<'PY'
import sys
p = sys.argv[1]
s = open(p, encoding='utf-8').read()
block = '''        // --help 必须排在 provider 配置检查之前。
        // 一个刚装完工具的人第一件事就是敲 --help——那时他还没配 provider，
        // 若在这里先报"缺少 provider 配置"，他连有哪些命令都不知道。
        if (args.Contains("--help") || args.Contains("-h"))
        {
            PrintHelp();
            return 0;
        }

'''
assert block in s
s = s.replace(block, "")
anchor = '        var workspace = Environment.GetEnvironmentVariable("DOUGONG_WORKSPACE")'
assert anchor in s
s = s.replace(anchor, '''        if (args.Contains("--help"))
            return 0;

''' + anchor)
open(p, 'w', encoding='utf-8').write(s)
PY
}

# 2. 帮助里漏掉一个命令 → 读者不知道它存在
mut02() {
  perl -0pi -e 's/--report\s+输出诊断信息包/RPT\s+输出诊断信息包/' "$PROG"
}

# 3. 命令名改成别的 → 安装后命令与文档不符
mut03() {
  perl -0pi -e 's/<ToolCommandName>dougong<\/ToolCommandName>/<ToolCommandName>dg<\/ToolCommandName>/' "$CSPROJ"
}

run_case help-blocked-by-config    help-works-without-provider-config       mut01
run_case help-missing-a-command    help-lists-every-user-facing-command     mut02
run_case wrong-tool-command-name   package-declares-tool-command            mut03

restore

echo
if [ -n "$FAIL_LIST" ]; then
  echo "COUNTERPROOFS: FAILED —$FAIL_LIST"
  exit 1
fi
echo "COUNTERPROOFS: ALL $(( $(echo "$PASS_LIST" | wc -w) )) CASES TURNED RED AS EXPECTED"
