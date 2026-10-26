#!/usr/bin/env python3
"""按 ~/.zcode/v2/provider_config.json 的**实际配置**探测各家可用性。

关键：这个文件里每条 provider 规则都同时给出 api.type（协议）和 baseUrl，
两者必须配对使用。早先只从 config.json 取 baseUrl 而猜协议，
结果 Requests 全部失败——不是 key 无效，是协议/路径不匹配。

只输出可用性，不打印密钥。
"""
import json
import sys
import urllib.error
import urllib.request
from pathlib import Path

CFG = Path.home() / ".zcode" / "v2" / "provider_config.json"

# api.type → (路径后缀, 认证头构造)
PROTOCOLS = {
    "anthropic-messages": ("/v1/messages", "anthropic"),
    "openai-chat-completions": ("/chat/completions", "bearer"),
    "openai-responses": ("/responses", "responses"),
}


# builtin 模板没有显式 api 段，端点和协议内建在 zcode 客户端里
BUILTIN_DEFAULTS = {
    "minimax": ("https://api.minimax.chat/v1", "openai-chat-completions"),
    "bigmodel-api": ("https://open.bigmodel.cn/api/paas/v4", "openai-chat-completions"),
    "deepseek": ("https://api.deepseek.com", "openai-chat-completions"),
}


def load_rules():
    if not CFG.exists():
        print(f"找不到 {CFG}")
        sys.exit(2)
    cfg = json.loads(CFG.read_text(encoding="utf-8"))
    rules = cfg["config"]["providerConfigRules"]["providerRules"]

    out = []
    for r in rules:
        c = r.get("config", {})
        api = c.get("api", {})
        key = (c.get("access", {}).get("apiKey") or "").strip()
        base = (api.get("baseUrl") or "").strip().rstrip("/")
        proto = api.get("type")
        name = (r.get("providerName") or r["providerId"]).strip()

        if not base or not proto:
            default = BUILTIN_DEFAULTS.get(r["providerId"])
            if default:
                base = base or default[0]
                proto = proto or default[1]

        # personalModelIds 里可能只有 TTS/图像模型，必须跳过
        model = pick_chat_model(c)
        if key and base and proto and model:
            out.append((name, proto, base, key, model))
    return out


_NON_CHAT_MARKERS = ("tts", "voiceclone", "voicedesign", "speech",
                     "image", "seedream", "flare", "sunburst")


def pick_chat_model(provider_config: dict):
    # personalModelIds 优先：那是用户实际勾选在用的模型；
    # modelOrder 是模板清单，第一个往往不是用户想用的那个。
    for group in (provider_config.get("personalModelIds") or [],
                  provider_config.get("modelOrder") or []):
        for m in group:
            m = (m or "").strip()
            if not m:
                continue
            if any(marker in m.lower() for marker in _NON_CHAT_MARKERS):
                continue
            return m
    return None


def probe(base, proto, key, model):
    suffix, style = PROTOCOLS[proto]
    url = base + suffix
    headers = {"content-type": "application/json"}

    if style == "anthropic":
        body = {"model": model, "max_tokens": 16,
                "messages": [{"role": "user", "content": "hi"}]}
        headers["x-api-key"] = key
        headers["anthropic-version"] = "2023-06-01"
    elif style == "bearer":
        body = {"model": model, "max_tokens": 16,
                "messages": [{"role": "user", "content": "hi"}]}
        headers["authorization"] = f"Bearer {key}"
    else:  # responses
        body = {"model": model, "max_output_tokens": 16, "input": "hi"}
        headers["authorization"] = f"Bearer {key}"

    req = urllib.request.Request(url, data=json.dumps(body).encode(),
                                 method="POST", headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=45) as r:
            return r.status, url, ""
    except urllib.error.HTTPError as e:
        return e.code, url, e.read().decode("utf-8", "replace")[:140].replace("\n", " ")
    except Exception as e:
        return 0, url, f"{type(e).__name__}: {e}"[:140]


def main() -> int:
    rules = load_rules()
    print(f"{'provider':16s} {'协议':24s} {'model':22s} 结果")
    print("-" * 96)
    for name, proto, base, key, model in rules:
        status, url, detail = probe(base, proto, key, model)
        if status == 200:
            verdict = "OK"
        elif status in (401, 403):
            verdict = f"失效/无权限 HTTP {status}: {detail[:70]}"
        elif status == 429:
            verdict = "限流/余额 HTTP 429"
        elif status == 404:
            verdict = f"HTTP 404（路径不对）{url}"
        elif status == 0:
            verdict = f"连不上：{detail}"
        else:
            verdict = f"HTTP {status}: {detail[:70]}"
        print(f"{name:16s} {proto:24s} {model:22s} {verdict}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
