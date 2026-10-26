#!/usr/bin/env bash
# `--doctor` 的反证：破坏自检的关键性质，确认 doctor 套件会变红。
#
# 最要紧的一条是"自检必须排在 provider 配置检查之前"——
# 顺序错了，缺配置时用户永远看不到自检结果，而那正是最需要它的时候。
#
# 用法：bash scripts/doctor-counterproofs.sh
set -uo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO" || exit 1

PROG="src/Dougong.Cli/Program.cs"
DOC="src/Dougong.Cli/Doctor.cs"
LOG="src/Dougong.Cli/Log.cs"

FILES=("$PROG" "$DOC" "$LOG")
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
  out="$(dotnet run --project spike/Dougong.Agent.Comparison -c Release -- doctor 2>&1)"

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

# 1. 自检排在 provider 配置检查之后 → 缺配置时永远跑不到自检
mut01() {
  python - "$PROG" <<'PY'
import sys
p = sys.argv[1]
s = open(p, encoding='utf-8').read()
block = """        // 自检必须在读取 provider 配置之前：它的用途正是诊断"配置不全导致跑不起来"，
        // 如果放在后面，缺配置时会先退出 2，自检永远跑不到。
        if (args.Contains("--doctor"))
            return await Doctor.RunAsync();

"""
assert block in s
s = s.replace(block, "")
# 挪到 provider 检查之后
anchor = "        var workspace = Environment.GetEnvironmentVariable(\"DOUGONG_WORKSPACE\")"
assert anchor in s
s = s.replace(anchor, """        if (args.Contains("--doctor"))
            return await Doctor.RunAsync();

""" + anchor)
open(p, 'w', encoding='utf-8').write(s)
PY
}

# 2. 失败项不给"下一步" → 只说失败等于把问题换个地方显示
mut02() {
  python - "$DOC" <<'PY'
import sys
p = sys.argv[1]
s = open(p, encoding='utf-8').read()
old = """            if (r.Next is { Length: > 0 })
                Console.WriteLine($"         下一步：{r.Next}");"""
new = """            if (r.Next is { Length: > 0 } && r.Detail.Length < 0)
                Console.WriteLine($"         下一步：{r.Next}");"""
assert old in s
open(p, 'w', encoding='utf-8').write(s.replace(old, new))
PY
}

# 3. 打印完整密钥 → 自检输出被贴进 issue 时泄露
mut03() {
  python - "$DOC" <<'PY'
import sys
p = sys.argv[1]
s = open(p, encoding='utf-8').read()
old = 'var detail = $"base={baseUrl}  model={model}  protocol={protocol}  key=已设置({key!.Length} 字符)";'
new = 'var detail = $"base={baseUrl}  model={model}  protocol={protocol}  key={key}";'
assert old in s
open(p, 'w', encoding='utf-8').write(s.replace(old, new))
PY
}

# 4. 工作区不存在也报通过 → 写文件工具会在运行时才失败
mut04() {
  python - "$DOC" <<'PY'
import sys
p = sys.argv[1]
s = open(p, encoding='utf-8').read()
old = """            return new(Level.Fail, "工作区",
                $"目录不存在：{workspace}", "检查 DOUGONG_WORKSPACE；不设时使用当前目录。");"""
new = """            return new(Level.Pass, "工作区",
                $"目录不存在：{workspace}", "检查 DOUGONG_WORKSPACE；不设时使用当前目录。");"""
assert old in s
open(p, 'w', encoding='utf-8').write(s.replace(old, new))
PY
}

# 5. 有失败项也返回 0 → 脚本无法据此判断能否启动
mut05() {
  python - "$DOC" <<'PY'
import sys
p = sys.argv[1]
s = open(p, encoding='utf-8').read()
old = "return results.Any(r => r.Level is Level.Fail) ? 1 : 0;"
new = "return results.Any(r => r.Level is Level.Fail && results.Count < 0) ? 1 : 0;"
assert old in s
open(p, 'w', encoding='utf-8').write(s.replace(old, new))
PY
}

# 6. 默认级别改成 info → 不设 DOUGONG_LOG 也开始刷日志，既有输出被改变
mut06() {
  perl -0pi -e 's/if \(string\.IsNullOrWhiteSpace\(raw\)\) return Level\.Warn;/if (string.IsNullOrWhiteSpace(raw)) return Level.Info;/' "$LOG"
}

# 7. 阈值比较改成 >= → 连恰好等于阈值的级别也被挡掉。
#    不能用 < ：那样 level == Threshold 仍然放行，反证打不到（第一版就是这样漏掉的）。
mut07() {
  perl -0pi -e 's/if \(level > Threshold\) return;/if (level \>= Threshold) return;/' "$LOG"
}

# 8. 未知级别静默按默认 → 用户以为生效了
mut08() {
  perl -0pi -e 's/_ => Unknown\(raw\),/_ => Level.Warn,/' "$LOG"
}

run_case doctor-must-run-before-config   doctor-runs-without-any-config            mut01
run_case failure-must-give-next-step    doctor-leads-with-next-step              mut02
run_case must-not-print-full-key        doctor-never-prints-full-key             mut03
run_case missing-workspace-not-pass     doctor-reports-unwritable-workspace      mut04
run_case exit-code-always-zero          doctor-runs-without-any-config           mut05
run_case log-default-level-changed      log-silent-by-default                    mut06
run_case log-threshold-inverted         log-info-adds-startup-facts              mut07
run_case log-unknown-silently-ignored   log-unknown-value-is-reported            mut08

restore

echo
if [ -n "$FAIL_LIST" ]; then
  echo "COUNTERPROOFS: FAILED —$FAIL_LIST"
  exit 1
fi
echo "COUNTERPROOFS: ALL $(( $(echo "$PASS_LIST" | wc -w) )) CASES TURNED RED AS EXPECTED"
