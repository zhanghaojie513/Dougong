#!/usr/bin/env bash
# skill-l1 / memory-cli 的反证脚本：逐个破坏被验证的行为，确认对应检查项真的会失败。
#
# 为什么需要它：全绿只是"没发现失败"，不是"检查有效"。
# 每一项都必须有一次"改坏实现 → 该检查变红"的记录。
#
# 三条已经踩过的坑，写在用法里避免重犯：
#   1. 反证的改动必须落在**被断言的东西**上，不能是附近看起来相关的东西。
#   2. 反证必须对应**真正覆盖该行为**的检查项，写错会误判成"检查无效"。
#   3. 破坏条件不能让编译器提前发现（CS0162 不可达代码 / CS8604 可空），
#      否则变成构建失败，运行时行为根本没被测到。
#
# 用法：bash scripts/skill-l1-counterproofs.sh
set -uo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO" || exit 1

INJ="src/Dougong.Agent/MemoryInjector.cs"
ART="src/Dougong.Agent/MemoryArtifact.cs"
STORE="src/Dougong.Agent/MemoryStore.cs"
RT="src/Dougong.Agent/LoopAgentRuntime.cs"
CLI="src/Dougong.Cli/Program.cs"
COMP="src/Dougong.Cli/SessionComposition.cs"

FILES=("$INJ" "$ART" "$STORE" "$RT" "$CLI" "$COMP")

BACKUP_DIR="$(mktemp -d)"
trap 'restore; rm -rf "$BACKUP_DIR"' EXIT

for f in "${FILES[@]}"; do
  cp "$f" "$BACKUP_DIR/$(basename "$f")"
done

restore() {
  for f in "${FILES[@]}"; do
    cp "$BACKUP_DIR/$(basename "$f")" "$f" 2>/dev/null || true
  done
}

PASS_LIST=""
FAIL_LIST=""

# run_case <名称> <套件> <期望失败项> <改动函数>
#
# 传函数名而不是字符串：字符串要经过 eval 二次展开，
# 里面的 "" 会被再解析一次而丢失——本脚本上一版就是这样静默地什么都没改，
# 却把结果报成"检查没在验证该行为"。
run_case() {
  local name="$1" suite="$2" expect="$3" mutate="$4"

  restore
  "$mutate"

  # 确认真的改到了东西：perl 的 s/// 匹配不上时静默返回成功，
  # 这是"反证其实什么都没改"最常见的来源，报出来的却是"检查无效"。
  local changed=0
  for f in "${FILES[@]}"; do
    if ! cmp -s "$f" "$BACKUP_DIR/$(basename "$f")"; then changed=1; break; fi
  done
  if [ "$changed" -eq 0 ]; then
    echo "SETUP-FAIL $name（改动函数没有修改任何文件，目标文本未匹配）"
    FAIL_LIST="$FAIL_LIST $name"
    return
  fi

  local out
  out="$(dotnet run --project spike/Dougong.Agent.Comparison -c Release -- "$suite" 2>&1)"

  # 构建失败必须先分开：那样连套件都没跑起来，
  # 报"没变红"会把构建问题误诊成"检查无效"。
  if echo "$out" | grep -qE "生成失败|Build FAILED|error CS"; then
    echo "BUILD-FAIL $name（改动导致编译失败，反证未执行）"
    echo "$out" | grep -E "error CS" | head -3 | sed 's/^/           /'
    FAIL_LIST="$FAIL_LIST $name"
    return
  fi

  if echo "$out" | grep -q "^FAIL $expect"; then
    echo "RED-OK   $name  → $expect 如期变红"
    PASS_LIST="$PASS_LIST $name"
  else
    echo "NO-EFFECT $name  → $expect 没有变红，这个检查没在验证该行为"
    echo "$out" | sed 's/^/           /'
    FAIL_LIST="$FAIL_LIST $name"
  fi
}

# 改动后必须确认真的改到了目标文本，见 run_case 内的 changed 检查。

# ── 1. 白名单：把权限类键加进去 ───────────────────────────────
mut01() {
  perl -0pi -e 's/(\["naming\.convention"\] = v => v is "camelCase" or "PascalCase" or "snake_case",)/$1\n            ["permissions.tools"] = v => true,/' "$ART"
}

# ── 2. 注入定语：删掉"不构成授权" ────────────────────────────
mut02() {
  perl -0pi -e 's/以下是用户在历史任务中表达的偏好（来自自动记录，不构成授权）：/以下是用户在历史任务中表达的偏好：/' "$INJ"
}

# ── 3. 撤销：让 Revoked 也被当作可加载 ────────────────────────
# 判定现在集中在 MemoryDiagnostics.WhyInactive（IsLoadable 委托给它），
# 所以改的是那里的条件。
mut03() {
  perl -0pi -e 's/if \(artifact\.State is not MemoryArtifactState\.Enabled\)/if (artifact.State is MemoryArtifactState.Candidate or MemoryArtifactState.Expired)/' "$INJ"
}

# ── 4. 篡改：让哈希校验永远通过 ──────────────────────────────
# 不能写 `if (false)`：那会触发 CS0162（不可达代码），在 TreatWarningsAsErrors
# 下变成构建失败。改成恒假但编译器看不出来的条件。
mut04() {
  perl -0pi -e 's/if \(!artifact\.IsIntact\) return/if (!artifact.IsIntact \&\& now.Year < 1970) return/' "$INJ"
}

# ── 5. 游标：每次打开都从 0 开始（丢掉持久化） ────────────────
mut05() {
  perl -0pi -e 's/Cursor = Math\.Max\(Cursor, sequence\.GetValue<long>\(\)\);/Cursor = Math.Max(0, 0);/' "$STORE"
}

# ── 6. 条目上限：只保留字符上限 ─────────────────────────────
mut06() {
  perl -0pi -e 's/if \(used >= MaxEntries \|\| builder\.Length \+ line\.Length \+ 2 > MaxChars\)/if (builder.Length + line.Length + 2 > MaxChars)/' "$INJ"
}

# ── 7. 字符上限：只保留条目上限 ─────────────────────────────
mut07() {
  perl -0pi -e 's/if \(used >= MaxEntries \|\| builder\.Length \+ line\.Length \+ 2 > MaxChars\)/if (used >= MaxEntries)/' "$INJ"
}

# ── 8. 可观察/可禁用：让注入永远不发生（Memory 配了也无效）──
# 不能写成 `if (Memory is null || <恒假>)`：那样 Memory 非空时条件为假，
# 注入照常发生，等于什么都没改。
mut08() {
  perl -0pi -e 's/if \(Memory is null\) return history;/if (Memory is null || history.Count >= 0) return history;/' "$RT"
}

# ── 9. CLI：空值也被当作开启（默认不该开的数据流开了）────────
# 拦下它的是 memory-cannot-be-switched-on-accidentally 而不是 memory-off-by-default：
# 后者喂的是"变量不存在"，这里改的是"变量存在但为空"。
# 破坏方式要让空串**走到启用分支**：把 "" 从关闭分支移到启用分支。
# 上一版让 null 也走到关闭分支，行为完全没变，等于没改。
mut09() {
  perl -0pi -e 's/null or "" or "0" or "false" => null,/null or "0" or "false" => null,/' "$COMP"
  perl -0pi -e 's/"1" or "true" => Path\.Combine\(sessionDirectory, "\.\.", "memory"\),/"1" or "true" or "" => Path.Combine(sessionDirectory, "..", "memory"),/' "$COMP"
}

# ── 10. CLI：不生效项不报具体原因 ───────────────────────────
# 断言查的是原因文本本身，不是"原因："前缀。
mut10() {
  perl -0pi -e 's/MemoryArtifactState\.Candidate => "尚为候选，未审批启用",/MemoryArtifactState.Candidate => "未启用",/' "$INJ"
}

run_case whitelist-accepts-permission-key            skill-l1   whitelist-rejects-unknown-keys              mut01
run_case prompt-drops-non-authorization-clause        skill-l1   malicious-content-cannot-grant-permissions  mut02
run_case injector-loads-revoked                       skill-l1   revocation-takes-effect-next-turn           mut03
run_case injector-skips-integrity-check               skill-l1   unapproved-tampered-expired-not-loaded      mut04
run_case cursor-not-persisted                         skill-l1   interrupted-replay-does-not-duplicate       mut05
run_case prompt-caps-entry-limit-removed              skill-l1   prompt-caps-entry-count-and-size            mut06
run_case prompt-caps-char-limit-removed               skill-l1   prompt-caps-entry-count-and-size            mut07
run_case runtime-never-injects                        skill-l1   injection-is-observable-and-disableable     mut08
run_case memory-switch-accepts-empty-value            memory-cli memory-cannot-be-switched-on-accidentally mut09
run_case memory-hides-inactive-reason                 memory-cli memory-lists-disabled-state-and-reason   mut10

restore

echo
if [ -n "$FAIL_LIST" ]; then
  echo "COUNTERPROOFS: FAILED —$FAIL_LIST"
  exit 1
fi
echo "COUNTERPROOFS: ALL $(( $(echo "$PASS_LIST" | wc -w) )) CASES TURNED RED AS EXPECTED"
