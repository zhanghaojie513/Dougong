#!/usr/bin/env python3
"""Skill L1 验收 1：多 provider × 多协议矩阵，**逐组合落盘、可断点续跑**。

设计原因：一次跑 12 个组合（240 次调用）时，python 的 stdout 被整体缓冲，
中途看不到任何进度；且单个请求卡住会让整批无输出地耗掉几十分钟。
所以这里：
  - 每跑完一个组合立刻追加到 results.jsonl（可断点续跑）
  - 每个组合有独立超时，卡住就记 FAIL 继续下一个
  - 立即 flush 打印，能看到进度

用法：
  python scripts/l1-matrix.py probe
  python scripts/l1-matrix.py run               # 跑全部可用组合
  python scripts/l1-matrix.py run bigmodel      # 只跑名字含 bigmodel 的
  python scripts/l1-matrix.py report            # 从 results.jsonl 汇总
"""
import json
import os
import subprocess
import sys
import threading
import urllib.error
import urllib.request
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
CFG = Path.home() / ".zcode" / "v2" / "provider_config.json"
RESULTS = REPO / "artifacts" / "l1-matrix-v2.jsonl"

PROTO_ENV = {
    "anthropic-messages": "anthropic",
    "openai-chat-completions": "openai",
    "openai-responses": "responses",
}

# 用户指定的 6 家 × 各自已知的两种协议端点
CANDIDATES = {
    "bigmodel": [
        ("https://open.bigmodel.cn/api/paas/v4", "openai-chat-completions"),
        ("https://open.bigmodel.cn/api/anthropic", "anthropic-messages"),
    ],
    "deepseek": [
        ("https://api.deepseek.com", "openai-chat-completions"),
        ("https://api.deepseek.com/anthropic", "anthropic-messages"),
    ],
    "xiaomi": [
        ("https://token-plan-cn.xiaomimimo.com/v1", "openai-chat-completions"),
        ("https://token-plan-cn.xiaomimimo.com/anthropic", "anthropic-messages"),
    ],
    "minimax": [
        ("https://api.minimax.chat/v1", "openai-chat-completions"),
        ("https://api.minimax.chat/anthropic", "anthropic-messages"),
    ],
    "waygate": [
        ("https://www.haihao.xyz:8443/v1", "openai-chat-completions"),
        ("https://www.haihao.xyz:8443", "anthropic-messages"),
    ],
    "routin": [
        ("https://api.routin.ai", "anthropic-messages"),
        ("https://api.routin.ai/v1", "openai-chat-completions"),
    ],
}

TARGET_MODELS = {
    "bigmodel": ["GLM-5.3", "GLM-5.3-Flash"],
    "deepseek": ["deepseek-flash"],
    "xiaomi": ["mimo-v2.6-flash"],
    "routin": ["gpt-6.1-sol"],
    "minimax": ["MiniMax-M3.1-Flash"],
    "waygate": ["deepseek-v4.1-flash"],
}


def load_keys():
    """
    取出全部 provider 规则，供按 baseUrl 匹配 key。

    **不能按名字取 key**：`provider_config.json` 里同时存在 `Routin Plan`
    与 `Routin` 两条规则，名字都含 "routin"。按名字取第一条会拿到 Plan 的 key，
    拿它请求普通端点会返回 "Please use Meteor API Key instead"——
    这不是 provider 有问题，是取错了 key。所以下面按 baseUrl 前缀匹配。
    """
    cfg = json.loads(CFG.read_text(encoding="utf-8"))
    rules = []
    for r in cfg["config"]["providerConfigRules"]["providerRules"]:
        c = r.get("config", {})
        api = c.get("api", {})
        key = (c.get("access", {}).get("apiKey") or "").strip()
        if not key:
            continue
        rules.append({
            "name": (r.get("providerName") or "").strip(),
            "pid": (r.get("providerId") or "").strip(),
            "base": (api.get("baseUrl") or "").strip().rstrip("/"),
            "proto": api.get("type"),
            "key": key,
        })
    return rules


def key_for(tag, candidate_base, rules):
    """
    为某个候选端点找对应的 key。

    优先级：
      1. 规则的 baseUrl 与候选端点**完全相同**
      2. 规则的 baseUrl 是候选端点的前缀（例如配置写 .../v1，候选是同源其它路径）
      3. 候选端点是规则 baseUrl 的前缀
      4. 该家没有显式 baseUrl 的规则（builtin 模板）
    """
    matches = [r for r in rules
               if tag in r["name"].lower() or tag in r["pid"].lower()]
    if not matches:
        return None

    for r in matches:
        if r["base"] and candidate_base == r["base"]:
            return r["key"]
    for r in matches:
        if r["base"] and candidate_base.startswith(r["base"]):
            return r["key"]
    for r in matches:
        if r["base"] and r["base"].startswith(candidate_base):
            return r["key"]
    for r in matches:
        if not r["base"]:
            return r["key"]
    return matches[0]["key"]


def probe(base, proto, key, model):
    if proto == "anthropic-messages":
        url, body = base + "/v1/messages", {
            "model": model, "max_tokens": 16,
            "messages": [{"role": "user", "content": "hi"}]}
        headers = {"content-type": "application/json", "x-api-key": key,
                   "anthropic-version": "2023-06-01"}
    elif proto == "openai-responses":
        url, body = base + "/responses", {
            "model": model, "max_output_tokens": 16, "input": "hi"}
        headers = {"content-type": "application/json", "authorization": f"Bearer {key}"}
    else:
        url, body = base + "/chat/completions", {
            "model": model, "max_tokens": 16,
            "messages": [{"role": "user", "content": "hi"}]}
        headers = {"content-type": "application/json", "authorization": f"Bearer {key}"}

    req = urllib.request.Request(url, data=json.dumps(body).encode(),
                                 method="POST", headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=45) as r:
            return r.status, ""
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode("utf-8", "replace")[:80].replace("\n", " ")
    except Exception as e:
        return 0, type(e).__name__


def build_matrix():
    rules = load_keys()
    rows = []
    for tag, cands in CANDIDATES.items():
        for model in TARGET_MODELS[tag]:
            for base, proto in cands:
                key = key_for(tag, base, rules)
                if not key:
                    print(f"  (跳过 {tag} {base}：没有匹配的 key)", flush=True)
                    continue
                rows.append((tag, model, base, proto, key))
    return rows


def do_probe():
    rows = build_matrix()
    ok = []
    print(f"{'family':10s} {'model':20s} {'协议':22s} 结果", flush=True)
    print("-" * 90, flush=True)
    for tag, model, base, proto, key in rows:
        status, detail = probe(base, proto, key, model)
        if status == 200:
            verdict = "OK"
            ok.append((tag, model, base, proto, key))
        else:
            verdict = f"{status} {detail}"[:70]
        print(f"{tag:10s} {model:20s} {proto:22s} {verdict}", flush=True)
    return ok


def do_run(ok, only=None):
    """
    并行执行：**按 provider 分组，组内串行、组间并行**。

    为什么这样分：一次组合 = 20 次模型调用，每次回答约千 token，单组合要 3–5 分钟。
    14 个组合串行需要一小时以上。但不同厂商是不同服务，可以同时跑；
    同一厂商的两个协议若也并行，容易触发限流（MiniMax 已出现过 429）。
    所以按 provider 并行、组内串行，总耗时从一小时降到十分钟量级。
    """
    if only:
        low = only.lower()
        ok = [r for r in ok if low in r[0].lower() or low in r[1].lower()]
    if not ok:
        print("没有可用组合", flush=True)
        return 2

    RESULTS.parent.mkdir(parents=True, exist_ok=True)

    done = set()
    if RESULTS.exists():
        kept = []
        for line in RESULTS.read_text(encoding="utf-8").splitlines():
            try:
                r = json.loads(line)
            except json.JSONDecodeError:
                continue
            # 只保留真正跑完的记录；中断留下的半条会被丢弃，避免污染汇总
            if r.get("status") == "OK" and r.get("lift", "-") != "-":
                done.add(r["label"])
                kept.append(line)
        RESULTS.write_text("\n".join(kept) + ("\n" if kept else ""), encoding="utf-8")

    groups: dict[str, list] = {}
    for row in ok:
        groups.setdefault(row[0], []).append(row)

    todo = {k: [r for r in v if _label(r) not in done] for k, v in groups.items()}
    total = sum(len(v) for v in todo.values())
    print(f"\n== 验收 1：{total} 个待跑组合（已完成 {len(done)}）==", flush=True)

    lock = threading.Lock()
    threads = []

    def worker(family: str, rows: list):
        for tag, model, base, proto, key in rows:
            label = _label((tag, model, base, proto, key))
            with lock:
                print(f"  ▶ {label}", flush=True)

            record = run_one(tag, model, base, proto, key)
            with lock:
                with RESULTS.open("a", encoding="utf-8") as f:
                    f.write(json.dumps(record, ensure_ascii=False) + "\n")
                if record["status"] == "OK":
                    print(f"  ✓ {label}  有记忆 {record['with']}  "
                          f"无记忆 {record['without']}  提升 {record['lift']}", flush=True)
                else:
                    print(f"  ✗ {label}  {record['status']}: "
                          f"{record.get('error','')[:100]}", flush=True)

    for family, rows in todo.items():
        if not rows:
            continue
        t = threading.Thread(target=worker, args=(family, rows), daemon=False)
        t.start()
        threads.append(t)

    for t in threads:
        t.join()

    return do_report()


def _label(row) -> str:
    tag, model, base, proto, key = row
    return f"{tag}/{model}/{PROTO_ENV[proto]}"


def run_one(tag, model, base, proto, key) -> dict:
    label = f"{tag}/{model}/{PROTO_ENV[proto]}"
    record = {"label": label, "family": tag, "model": model,
              "base": base, "protocol": PROTO_ENV[proto]}

    env = dict(os.environ)
    env.update({
        "DOUGONG_PROVIDER_BASE": base,
        "DOUGONG_PROVIDER_MODEL": model,
        "DOUGONG_PROVIDER_PROTOCOL": PROTO_ENV[proto],
        "DOUGONG_PROVIDER_KEY": key,
        "MSYS_NO_PATHCONV": "1",
    })

    try:
        proc = subprocess.run(
            ["dotnet", "run", "--project", str(REPO / "spike/Dougong.Agent.Comparison"),
             "-c", "Release", "--no-build", "--", "l1-effect"],
            capture_output=True, text=True, encoding="utf-8",
            errors="replace", env=env, timeout=900, cwd=str(REPO))
        out = proc.stdout or ""
        record["returncode"] = proc.returncode
        record["with"] = _extract(out, "有记忆遵循率")
        record["without"] = _extract(out, "无记忆遵循率")
        record["lift"] = _extract(out, "提升")
        record["status"] = "OK" if record["lift"] != "-" else "ERROR"

        # 分项：两类判据性质不同，合并成一个数字会掩盖差异
        for line in out.splitlines():
            for key in ("output.format", "naming.convention"):
                if line.strip().startswith(key) and "判据）" in line:
                    record[key] = line.split("：", 1)[-1].strip()

        if record["status"] == "ERROR":
            err = [l for l in out.splitlines()
                   if "Exception" in l or "未正常结束" in l]
            record["error"] = (err[0][:200] if err else (proc.stderr or "")[:200])
    except subprocess.TimeoutExpired:
        record["status"] = "TIMEOUT"
        record["with"] = record["without"] = record["lift"] = "-"
        record["error"] = "单个组合超过 900 秒"

    return record


def _extract(text, key):
    """
    从输出里取一个指标值。

    只认"数字% 后面紧跟括号"的形状，例如 `100%（10/10）`、`+20.0%（...）`。
    早先的实现取 key 之后的任意文本，结果某些 provider 的回答正文里
    恰好含"提升"二字时会把正文当成指标——那会让汇总表混进垃圾。
    """
    import re
    pattern = re.compile(r"([+-]?\d+(?:\.\d+)?%)")
    for line in text.splitlines():
        if key not in line:
            continue
        # 指标行形如：  有记忆遵循率 100%（10/10）
        #              提升 +20.0%（预先登记的最小幅度 50%）
        if "遵循率" in key or "提升" in key:
            m = pattern.search(line.split(key, 1)[-1])
            if m:
                return m.group(1)
    return "-"


def do_report():
    if not RESULTS.exists():
        print("还没有结果文件", flush=True)
        return 1

    rows = [json.loads(l) for l in RESULTS.read_text(encoding="utf-8").splitlines()
            if l.strip()]

    print("\n== 汇总 ==", flush=True)
    print(f"{'组合':44s} {'有记忆':9s} {'无记忆':9s} {'提升':11s} 状态", flush=True)
    print("-" * 92, flush=True)
    for r in rows:
        print(f"{r['label']:44s} {r.get('with','-'):9s} {r.get('without','-'):9s} "
              f"{r.get('lift','-'):11s} {r['status']}", flush=True)

    ok = [r for r in rows if r["status"] == "OK"]
    if ok:
        lifts = [float(r["lift"].replace("%", "").replace("+", ""))
                 for r in ok if r.get("lift", "-") != "-"]
        if lifts:
            lifts.sort()
            print(f"\n  可用组合 {len(ok)} 个；提升范围 "
                  f"{min(lifts):+.0f}% ~ {max(lifts):+.0f}%，"
                  f"中位数 {lifts[len(lifts)//2]:+.0f}%", flush=True)

        # 分项汇总：两类判据混在一起看会掩盖"哪一类真的有效"
        for key in ("output.format", "naming.convention"):
            vals = [r[key] for r in ok if r.get(key)]
            if vals:
                unique = sorted(set(vals))
                print(f"  {key}：{' | '.join(unique)}", flush=True)

        passed = [r for r in ok if r["returncode"] == 0]
        print(f"  达到预先登记阈值（≥50%）的组合：{len(passed)}/{len(ok)}", flush=True)
    return 0


def main() -> int:
    mode = sys.argv[1] if len(sys.argv) > 1 else "probe"
    only = sys.argv[2] if len(sys.argv) > 2 else None

    if mode == "probe":
        do_probe()
        return 0
    if mode == "run":
        return do_run(do_probe(), only)
    if mode == "report":
        return do_report()

    print(__doc__)
    return 2


if __name__ == "__main__":
    sys.exit(main())
