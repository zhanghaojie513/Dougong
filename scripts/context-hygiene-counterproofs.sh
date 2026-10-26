#!/usr/bin/env bash
# 上下文卫生两项改动的反证。
#
# 覆盖：
#   1. 工具结果进上下文时裁剪（日志保持完整、裁剪可观察）
#   2. 压缩时保留最近一次计划
#
# 用法：bash scripts/context-hygiene-counterproofs.sh
set -uo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO" || exit 1

LIMITS="src/Dougong.Agent/ToolResultLimits.cs"
RT="src/Dougong.Agent/LoopAgentRuntime.cs"
COMPACT="src/Dougong.Agent/ContextCompaction.cs"

FILES=("$LIMITS" "$RT" "$COMPACT")
BACKUP_DIR="$(mktemp -d)"
trap 'restore; rm -rf "$BACKUP_DIR"' EXIT

for f in "${FILES[@]}"; do cp "$f" "$BACKUP_DIR/$(basename "$f")"; done
restore() { for f in "${FILES[@]}"; do cp "$BACKUP_DIR/$(basename "$f")" "$f" 2>/dev/null || true; done; }

PASS_LIST=""; FAIL_LIST=""

run_case() {
  local name="$1" suite="$2" expect="$3" mutate="$4"

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
  out="$(dotnet run --project spike/Dougong.Agent.Comparison -c Release -- "$suite" 2>&1)"

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

# ── 裁剪：上限失效 ────────────────────────────────────────
# 不能写 `if (true) return content;`——那会触发 CS0162 不可达代码，
# 在 TreatWarningsAsErrors 下变成构建失败，反而测不到运行时行为。
mut01() {
  perl -0pi -e 's/if \(content\.Length <= MaxContextChars\) return content;/if (content.Length >= 0) return content;/' "$LIMITS"
}

# ── 裁剪：去掉截断标记 ────────────────────────────────────
mut02() {
  perl -0pi -e 's/\.Append\("\\n\.\.\.\[结果过长已截断：省略中间 "\)/.Append("\\n")/' "$LIMITS"
}

# ── 裁剪：丢掉尾部（只留头部，模型看不到结论/错误） ────────
mut03() { perl -0pi -e 's/var tail = content\[\^TailChars\.\.\];/var tail = "";/' "$LIMITS"; }

# ── 裁剪没接上主路径：直接返回原文（模拟"能力只在测试里"） ──
mut04() {
  perl -0pi -e 's/var content = ToolResultLimits\.Clamp\(raw, out var truncated\);/var content = raw; var truncated = false;/' "$RT"
}

# ── 裁剪污染了持久化：在提交记录前就裁 ────────────────────
# 匹配提交 payload 里的 content（唯一还直接用 GetRawText 的地方，
# AppendToolExchange 已改成先取 raw 再 Clamp）。
mut05() {
  perl -0pi -e 's/: result\.Result\.Content\.GetRawText\(\),/: ToolResultLimits.Clamp(result.Result.Content.GetRawText(), out _),/' "$RT"
}

# ── 压缩保护失效：不排除计划 ─────────────────────────────
mut06() {
  perl -0pi -e 's/last = TrimToExcludeLatestPlan\(input\.CommittedHistory, first, last\);//' "$COMPACT"
}

# ── 压缩保护误伤：把区间砍到空（所有历史都不压） ──────────
# 这条改到的是"有计划时"的分支。"无计划"路径不受影响，
# 所以拦下它的必然是那两条含计划消息的检查，取其一作代表。
mut07() {
  perl -0pi -e 's/return boundary - 1;/return first - 1;/' "$COMPACT"
}

# ── 压缩保护拆散了工具配对：只跳过 tool 消息而不回退到 assistant ──
mut08() {
  perl -0pi -e 's/while \(boundary > first && history\[boundary\]\.Role == AgentRole\.Tool\)\n            boundary--;/while (boundary > first \&\& history[boundary].Role == AgentRole.Tool)\n            break;/' "$COMPACT"
}

# ── 恢复路径不裁剪（第一版真实遗留的缺口） ────────────────
# ToChatMessage 是第二条把工具结果放进模型历史的路径。
mut09() {
  perl -0pi -e 's/var content = ToolResultLimits\.Clamp\(message\.Content, out _\);/var content = message.Content;/' "$RT"
}

run_case clamp-limit-removed          context-hygiene  long-result-clamped              mut01
run_case clamp-marker-removed         context-hygiene  clamp-marker-is-explicit         mut02
run_case clamp-tail-dropped           context-hygiene  clamp-keeps-head-and-tail        mut03
run_case clamp-not-wired-to-runtime   context-hygiene  history-gets-clamped             mut04
run_case clamp-poisons-store          context-hygiene  store-keeps-full-content         mut05
run_case restore-path-not-clamped     context-hygiene  restore-path-also-clamps         mut09
run_case plan-protection-removed      compaction       latest-plan-survives-compaction  mut06
# 砍到空会让含计划的两条检查都变红，取其一作代表：
# "无计划"路径不受这条改动影响，所以不能用 no-plan-means-full-range 验它。
run_case plan-protection-overreaches  compaction       latest-plan-survives-compaction  mut07

restore

echo
if [ -n "$FAIL_LIST" ]; then
  echo "COUNTERPROOFS: FAILED —$FAIL_LIST"
  exit 1
fi
echo "COUNTERPROOFS: ALL $(( $(echo "$PASS_LIST" | wc -w) )) CASES TURNED RED AS EXPECTED"
