#!/usr/bin/env bash
# 验收 1 装置的反证：破坏判据或注入链路，确认 l1-harness 会变红。
#
# 为什么需要：l1-harness 全绿只证明"当前判据能区分已知的正反例"。
# 若判据写得过宽（什么都算遵循）或过窄（合理写法也算不遵循），
# 用真实模型跑出的数字就是错的，而且没人能发现。
#
# 用法：bash scripts/l1-harness-counterproofs.sh
set -uo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO" || exit 1

EFFECT="spike/Dougong.Agent.Comparison/L1EffectChecks.cs"
HARNESS="spike/Dougong.Agent.Comparison/L1HarnessChecks.cs"

FILES=("$EFFECT" "$HARNESS")
BACKUP_DIR="$(mktemp -d)"
trap 'restore; rm -rf "$BACKUP_DIR"' EXIT

for f in "${FILES[@]}"; do cp "$f" "$BACKUP_DIR/$(basename "$f")"; done
restore() { for f in "${FILES[@]}"; do cp "$BACKUP_DIR/$(basename "$f")" "$f" 2>/dev/null || true; done; }

PASS_LIST=""; FAIL_LIST=""

run_case() {
  local name="$1" expect="$2" mutate="$3"

  restore
  "$mutate"

  # perl 的 s/// 匹配不上时静默成功——不检查就等于"反证什么都没改"。
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
  out="$(dotnet run --project spike/Dougong.Agent.Comparison -c Release -- l1-harness 2>&1)"

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

# 1. 语言判据写反：英文也算"含中文" → 反例检测不出来
mut01() { perl -0pi -e 's/text\.Any\(c => c >= 0x4E00 && c <= 0x9FFF\);/text.Length > 0;/' "$EFFECT"; }

# 2. 列表判据放宽到任何非空行 → 段落也算列表
mut02() { perl -0pi -e 's/return bulletLines >= 2;/return lines.Length > 0;/' "$EFFECT"; }

# 3. 去掉"snake 必须多于 camel"这一半 → camelCase 为主也会被判为遵循
mut03() { perl -0pi -e 's/return snake > 0 && snake > camel;/return snake > 0;/' "$EFFECT"; }

# 4. snake_case 模式写宽到匹配任意标识符 → 任何标识符都算遵循。
#    直接替换整行正则字面量，避免在 perl 里处理嵌套转义。
mut04() {
  perl -0pi -e 's/\Q\b[a-z][a-z0-9]*(?:_[a-z0-9]+)+\b\E/\b[a-z][a-zA-Z0-9_]*\b/' "$EFFECT"
}

# 5. 无记忆组也传注入器 → 对照组失效。
#    注意必须改 L1HarnessChecks 里那段：检查项有自己的代码路径，
#    改 L1EffectChecks 的同名行不会影响它（上一版就是这样错过的）。
mut05() { perl -0pi -e 's/Memory = null,\n                MemoryWorkspace = dir,/Memory = new StoreMemoryInjector(store),\n                MemoryWorkspace = dir,/' "$HARNESS"; }

# 6. 提示里泄漏偏好 → 两组都会照做，指标失去区分力
mut06() { perl -0pi -e 's/"简要说明 HTTP 缓存的工作方式。"/"请用中文简要说明 HTTP 缓存的工作方式。"/' "$EFFECT"; }

run_case criteria-accept-following        criteria-reject-non-following      mut01
run_case bullets-any-nonempty-line        criteria-reject-non-following      mut02
run_case naming-drops-camel-comparison    criteria-reject-non-following      mut03
run_case naming-pattern-too-wide          criteria-accept-following          mut04
run_case no-memory-group-gets-injector    no-memory-group-sees-nothing       mut05
run_case prompt-leaks-preference          prompt-does-not-leak-preference    mut06

restore

echo
if [ -n "$FAIL_LIST" ]; then
  echo "COUNTERPROOFS: FAILED —$FAIL_LIST"
  exit 1
fi
echo "COUNTERPROOFS: ALL $(( $(echo "$PASS_LIST" | wc -w) )) CASES TURNED RED AS EXPECTED"
