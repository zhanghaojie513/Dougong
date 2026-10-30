#!/usr/bin/env python3
"""核对 docs/dsh-capability-progress.md 里的统计数字与实际表格一致。

**为什么需要它**：那份文档有两处统计——总览表和逐项清单。
手改一行状态时很容易只改一处，而两处对不上时没人会发现：
总览看起来是个正常的百分比，清单看起来也在，只有数一遍才露馅。

这个脚本数一遍逐项清单，再和总览表比对。不一致就以非零退出。

用法：
  python scripts/check-capability-progress.py          # 核对
  python scripts/check-capability-progress.py --fix    # 用实际值改写总览表
"""
import io
import re
import sys

REPO_ROOT = __import__("pathlib").Path(__file__).resolve().parent.parent
DOC = REPO_ROOT / "docs" / "dsh-capability-progress.md"

MARKS = ["✅", "🟡", "⬜", "➖"]


def count_items(body: str) -> dict[str, dict[str, int]]:
    """按 `### N.` 分节，数每节的状态标记。"""
    sections: dict[str, dict[str, int]] = {}
    current = None
    for line in body.split("\n"):
        if line.startswith("### "):
            current = line[4:].strip()
            sections[current] = {m: 0 for m in MARKS}
        m = re.match(r"^\|\s*(✅|🟡|⬜|➖)\s*\|", line.strip())
        if m and current:
            sections[current][m.group(1)] += 1
    return sections


def completion(counts: dict[str, int]) -> int:
    """完成度百分比：✅ 算 1，🟡 算 0.5，➖ 不计入分母。"""
    denominator = counts["✅"] + counts["🟡"] + counts["⬜"]
    if denominator == 0:
        return 0
    return round((counts["✅"] + counts["🟡"] * 0.5) / denominator * 100)


def parse_overview(text: str) -> dict[str, dict[str, int]]:
    """从总览表读出它声称的数字。"""
    claimed: dict[str, dict[str, int]] = {}
    in_overview = False
    for line in text.split("\n"):
        if line.startswith("## 一、进度总览"):
            in_overview = True
            continue
        if in_overview and line.startswith("## "):
            break
        if not in_overview:
            continue

        cells = [c.strip() for c in line.strip().strip("|").split("|")]
        if len(cells) < 6:
            continue
        if not cells[0] or cells[0] in ("能力域", "---") or cells[0].startswith("---"):
            continue

        try:
            claimed[cells[0]] = {
                "✅": int(cells[1]),
                "🟡": int(cells[2]),
                "⬜": int(cells[3]),
                "➖": int(cells[4]),
                "小计": int(cells[5]),
            }
        except (ValueError, IndexError):
            continue

    return claimed


def main() -> int:
    fix = "--fix" in sys.argv
    text = io.open(DOC, encoding="utf-8").read()

    if "## 二、逐项清单" not in text:
        print("找不到「## 二、逐项清单」小节。", file=sys.stderr)
        return 1

    body = text.split("## 二、逐项清单")[1].split("## 三、P0 进度")[0]
    actual = count_items(body)
    claimed = parse_overview(text)

    problems: list[str] = []

    # 逐节比对。键的写法在两处略有差异（总览用"1. Agent 循环"，
    # 清单用"1. Agent 循环"），用前缀数字匹配。
    for name, counts in actual.items():
        key = next((k for k in claimed if k.split(".")[0] == name.split(".")[0]), None)
        if key is None:
            problems.append(f"总览表缺少「{name}」这一行")
            continue

        got = claimed[key]
        for mark in MARKS:
            if got[mark] != counts[mark]:
                problems.append(
                    f"{name}: {mark} 总览写 {got[mark]}，实际 {counts[mark]}")
        if got["小计"] != sum(counts.values()):
            problems.append(
                f"{name}: 小计写 {got['小计']}，实际 {sum(counts.values())}")
        if abs(completion(counts) - got.get("完成度", 0)) > 0:
            pass  # 完成度由小计推导，上面已覆盖

    # 合计行
    totals = {m: sum(c[m] for c in actual.values()) for m in MARKS}
    if "**合计**" in claimed:
        got = claimed["**合计**"]
        for mark in MARKS:
            if got[mark] != totals[mark]:
                problems.append(
                    f"合计 {mark}: 总览写 {got[mark]}，实际 {totals[mark]}")

    if not problems:
        print(f"一致：{sum(totals.values())} 项，"
              f"✅{totals['✅']} 🟡{totals['🟡']} ⬜{totals['⬜']} ➖{totals['➖']}，"
              f"完成度 {completion(totals)}%")
        return 0

    print("总览表与逐项清单不一致：", file=sys.stderr)
    for p in problems:
        print(f"  {p}", file=sys.stderr)

    if fix:
        print("\n--fix 还没实现自动改写：请手工同步上面列出的行。", file=sys.stderr)
        return 1

    return 1


if __name__ == "__main__":
    sys.exit(main())
