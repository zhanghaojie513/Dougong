#!/usr/bin/env bash
# `--report` 的反证：破坏信息包的安全性质，确认 report 套件会变红。
#
# 这份输出的用途就是被贴到公开 issue。泄露一旦发生就收不回来，
# 所以"不含密钥"与"不读会话内容"两条必须有反证守着。
#
# 用法：bash scripts/report-counterproofs.sh
set -uo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO" || exit 1

PROG="src/Dougong.Cli/Program.cs"
BUNDLE="src/Dougong.Cli/DiagnosticBundle.cs"

FILES=("$PROG" "$BUNDLE")
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
  out="$(dotnet run --project spike/Dougong.Agent.Comparison -c Release -- report 2>&1)"

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

# 1. 直接打印密钥值 → 信息包被贴出去就泄露
mut01() {
  python - "$BUNDLE" <<'PY'
import sys
p = sys.argv[1]
s = open(p, encoding='utf-8').read()
old = 'writer.AppendLine($"  key     : {DescribeSecret(key)}");'
new = 'writer.AppendLine($"  key     : {key}");'
assert old in s
open(p, 'w', encoding='utf-8').write(s.replace(old, new))
PY
}

# 2. 把会话内容读进信息包 → 用户对话原文被贴出去
mut02() {
  python - "$BUNDLE" <<'PY'
import sys
p = sys.argv[1]
s = open(p, encoding='utf-8').read()
# 在文件行里附上首行内容
old2 = """                writer.AppendLine(
                    $"    {f.Name}  {f.Length} B  {lines} 条  {f.LastWriteTimeUtc:yyyy-MM-dd HH:mm:ss}");"""
new2 = """                var firstLine = "";
                try { firstLine = File.ReadLines(f.FullName).FirstOrDefault() ?? ""; } catch { }
                writer.AppendLine(
                    $"    {f.Name}  {f.Length} B  {lines} 条  {f.LastWriteTimeUtc:yyyy-MM-dd HH:mm:ss}  {firstLine}");"""
assert old2 in s
open(p, 'w', encoding='utf-8').write(s.replace(old2, new2))
PY
}

# 3. 信息包排在 provider 配置检查之后 → 缺配置时拿不到
mut03() {
  python - "$PROG" <<'PY'
import sys
p = sys.argv[1]
s = open(p, encoding='utf-8').read()
block = """        // 信息包也排在 provider 配置检查之前：它要能用于"配置不完整"的场景。
        if (args.Contains("--report"))
        {
            Console.WriteLine(await DiagnosticBundle.BuildAsync());
            return 0;
        }

"""
assert block in s
s = s.replace(block, "")
anchor = '        var workspace = Environment.GetEnvironmentVariable("DOUGONG_WORKSPACE")'
assert anchor in s
s = s.replace(anchor, """        if (args.Contains("--report"))
        {
            Console.WriteLine(await DiagnosticBundle.BuildAsync());
            return 0;
        }

""" + anchor)
open(p, 'w', encoding='utf-8').write(s)
PY
}

# 4. 不提示密钥形态异常 → 复制粘贴带入的空格无法被发现
mut04() {
  python - "$BUNDLE" <<'PY'
import sys
p = sys.argv[1]
s = open(p, encoding='utf-8').read()
old = """        var trimmed = value.Trim();
        if (trimmed.Length != value.Length)
        {
            return $"已设置，{value.Length} 字符（**含首尾空白，可能是复制粘贴带入的**）";
        }
"""
new = """        var trimmed = value.Trim();
        if (trimmed.Length != value.Length)
        {
            return $"已设置，{value.Length} 字符";
        }
"""
assert old in s
open(p, 'w', encoding='utf-8').write(s.replace(old, new))
PY
}

# 5. 版本号硬编码 → 与实际构建脱节，报问题时定位不到代码
mut05() {
  python - "$BUNDLE" <<'PY'
import sys
p = sys.argv[1]
s = open(p, encoding='utf-8').read()
old = """        var asm = typeof(DiagnosticBundle).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? asm.GetName().Version?.ToString()
            ?? "未知";
"""
new = """        var info = "0.1.0-alpha.1";
"""
assert old in s
open(p, 'w', encoding='utf-8').write(s.replace(old, new))
PY
}

# 6. 会话记录数恒为 0 → "有记录但解析失败"与"空文件"无法区分
mut06() {
  perl -0pi -e 's/while \(await reader\.ReadLineAsync\(cancellationToken\)\.ConfigureAwait\(false\) is not null\)/while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is not null \&\& path.Length < 0)/' "$BUNDLE"
}

run_case report-must-run-before-config  report-runs-without-config          mut03
run_case report-leaks-key              report-never-contains-key           mut01
run_case report-leaks-session-content  report-never-contains-session-content mut02
run_case report-hides-key-shape        report-flags-suspicious-key-shape   mut04
run_case report-hardcodes-version      report-has-version-and-commit       mut05
run_case report-always-zero-lines      report-lists-session-metadata-only  mut06

restore

echo
if [ -n "$FAIL_LIST" ]; then
  echo "COUNTERPROOFS: FAILED —$FAIL_LIST"
  exit 1
fi
echo "COUNTERPROOFS: ALL $(( $(echo "$PASS_LIST" | wc -w) )) CASES TURNED RED AS EXPECTED"
