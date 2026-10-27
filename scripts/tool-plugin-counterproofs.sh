#!/usr/bin/env bash
# 工具插件教程与 spike 子命令的反证。
#
# 最要紧的一条：spike 对未知子命令必须报错。真实发生过——读者用新版文档里的
# `-- load-tool` 跑旧副本，spike 把 "load-tool" 当候选名、跑完 11 项对比
# 并全部 PASS。屏幕上看起来像"成功了"，实际什么都没做。
#
# 用法：bash scripts/tool-plugin-counterproofs.sh
set -uo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO" || exit 1

PROG="spike/Dougong.Agent.Comparison/Program.cs"
GUIDE="docs/plugin-author-guide.md"
GS="docs/getting-started.md"

FILES=("$PROG" "$GUIDE" "$GS")
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
  out="$(dotnet run --project spike/Dougong.Agent.Comparison -c Release -- tool-plugin-checks 2>&1)"

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

# 1. 未知子命令退回默认对比测试 → 输出被误读成成功
mut01() {
  perl -0pi -e 's/if \(candidate is not \("a" or "b"\)\)/if (candidate is not ("a" or "b") \&\& candidate.Length < 0)/' "$PROG"
}

# 2. 报错文案不再说明"未知子命令" → 读者不知道发生了什么
mut02() {
  perl -0pi -e 's/未知子命令：/参数有问题：/' "$PROG"
}

# 3. 指南不再讲 Reads/Writes → 判据 1 的反例重新出现
mut03() {
  perl -0pi -e 's/读-读不冲突/读和读也可以一起跑/' "$GUIDE"
}

# 4. getting-started 漏掉"配置模型"这一步 → 判据 1 无法由读者独立完成
mut04() {
  perl -0pi -e 's/DOUGONG_PROVIDER_BASE/DOUGONG_SOMETHING_ELSE/g' "$GS"
}

# 5. getting-started 不再讲 DescribePermission → 判据反例重新出现
mut05() {
  perl -0pi -e 's/`DescribePermission`/那个权限方法/g' "$GS"
}

# 6. 章节编号退回非整数（"## 6." → "## 5.5"）→ 结构错乱重现
mut06() {
  perl -0pi -e 's/^## 6\. 接上真模型/## 5.5 接上真模型/m' "$GS"
}

run_case non-integer-section-number  getting-started-sections-are-contiguous          mut06
run_case missing-model-config-step   getting-started-covers-all-four-criterion-steps  mut04
run_case missing-describe-permission getting-started-covers-all-four-criterion-steps  mut05
run_case unknown-subcommand-falls-through  unknown-subcommand-errors-instead-of-silent-run  mut01
run_case unknown-subcommand-says-nothing   unknown-subcommand-errors-instead-of-silent-run  mut02
run_case guide-drops-read-conflict-rule    guide-explains-reads-writes-semantics            mut03

restore

echo
if [ -n "$FAIL_LIST" ]; then
  echo "COUNTERPROOFS: FAILED —$FAIL_LIST"
  exit 1
fi
echo "COUNTERPROOFS: ALL $(( $(echo "$PASS_LIST" | wc -w) )) CASES TURNED RED AS EXPECTED"
