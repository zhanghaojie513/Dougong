#!/usr/bin/env bash
# API 兼容机制的反证。
#
# 关键一条：breaking-change-is-detected 会真的改代码再改回来。
# 如果它是空转的（例如"找不到签名就跳过"），那么"ApiCompat 保护了五个包"
# 就只是一句声明。所以必须验证它会因为机制失效而变红。
#
# 用法：bash scripts/apicompat-counterproofs.sh
set -uo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO" || exit 1

SCRIPT="scripts/update-api-baseline.sh"
AI="src/Dougong.Ai/Dougong.Ai.csproj"

FILES=("$SCRIPT" "$AI")
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
  out="$(dotnet run --project spike/Dougong.Agent.Comparison -c Release -- apicompat 2>&1)"

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

# 1. 基线脚本漏掉一个包 → 那个包永远没有基线，等于没有保护
mut01() {
  perl -0pi -e 's/PACKAGES=\(Dougong\.Kernel Dougong\.Agent Dougong\.Ai Dougong\.Mcp Dougong\.Acp\)/PACKAGES=(Dougong.Kernel Dougong.Agent)/' "$SCRIPT"
}

# 2. 关掉 Ai 包的校验 → 破坏性变更不再被检出
mut02() {
  perl -0pi -e 's/<EnablePackageValidation>true<\/EnablePackageValidation>/<EnablePackageValidation>false<\/EnablePackageValidation>/' "$AI"
}

run_case baseline-script-missing-package  baseline-list-matches-script      mut01
run_case ai-package-validation-disabled   breaking-change-is-detected       mut02

restore

echo
if [ -n "$FAIL_LIST" ]; then
  echo "COUNTERPROOFS: FAILED —$FAIL_LIST"
  exit 1
fi
echo "COUNTERPROOFS: ALL $(( $(echo "$PASS_LIST" | wc -w) )) CASES TURNED RED AS EXPECTED"
