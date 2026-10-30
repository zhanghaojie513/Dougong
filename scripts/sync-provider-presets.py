#!/usr/bin/env python3
"""从 models.dev v2 拉取模型能力数据，生成本仓库的 provider 预设表。

**数据源为什么只有一个：models.dev v2。**
  · pi（earendil-works/pi）的 catalog/ 是它的加工层，不是事实来源——它自己的
    README 就写着 `sources/models-dev.ts` normalizes the models.dev catalog。
    读它等于读二手数据再反推一手逻辑。
  · 它的 `baseUrl` 有 6/25 家是变量而非字面量（`baseUrl: BASE_URL`），
    靠正则解析 TypeScript 拿不全，而上游一改写法就失效。
  · 它的 compat 里有私有枚举（`thinkingFormat: "zai"`），语义只有它自己懂；
    照抄进我们的 WireCompat 是错的——我们的开关是**字段名**，不是标签。
  · models.dev 反而更全：233 家 provider，`interleaved.field` 直接就是
    我们要的推理字段名，`reasoning_options` 直接就是档位。

**加工层的三层处理**（见 `CORRECTIONS` 与 `effort_map`）：
  1. 机械映射：limit / api / interleaved 原样搬运，零判断。
  2. 实测修正：`CORRECTIONS` 表，**只填我们自己真实验证过的**，目前为空。
  3. 明确不抄：promptCache / maxRequestBytes / inputLimits / zaiToolStream 等。
     那些服务于 pi 自己的缓存与图片卸载策略，我们的运行时没有对应机制，
     抄过来是死字段。

用法：
  python scripts/sync-provider-presets.py            # 生成
  python scripts/sync-provider-presets.py --check    # 只比对，不改文件

产物：src/Dougong.Ai/ProviderPresets.g.cs
"""
import hashlib
import json
import sys
import urllib.request
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
OUTPUT = REPO_ROOT / "src" / "Dougong.Ai" / "ProviderPresets.g.cs"

MODELS_DEV = "https://models.dev/api.json"

# 收录白名单。**不用黑名单**：models.dev 有 233 家，绝大多数是聚合器/转售商，
# 全收进来等于给用户一个长到没法选、且大半没验证过的列表。
# 这份名单是"我们真的想支持、且端点和协议都确定"的那些家。
#
# 新增一家前先确认三件事：端点可解析、协议在三种之内、密钥走环境变量。
INCLUDE = {
    # 国内
    "deepseek", "zai", "zhipuai", "moonshotai", "moonshotai-cn",
    "xiaomi", "minimax", "minimax-cn", "alibaba", "volcengine",
    "stepfun", "baichuan", "hunyuan",
    # 国际
    "openai", "anthropic", "google", "xai", "groq", "mistral",
    "together", "deepinfra", "cerebras", "perplexity",
    # 聚合
    "openrouter", "vercel-ai-gateway",
}

# models.dev 对官方 SDK 类 provider 不给 `api`（SDK 自带端点），
# 这些直接补端点。**每条都 curl 确认过主机存在**（401/403/404 而非 DNS 失败）。
BASE_URL_FALLBACK = {
    "openai": "https://api.openai.com/v1",
    "anthropic": "https://api.anthropic.com",
    "xai": "https://api.x.ai/v1",
    "groq": "https://api.groq.com/openai/v1",
    "mistral": "https://api.mistral.ai/v1",
    "cerebras": "https://api.cerebras.ai/v1",
    "deepinfra": "https://api.deepinfra.com/v1/openai",
    "together": "https://api.together.xyz/v1",
    "perplexity": "https://api.perplexity.ai",
    "volcengine": "https://ark.cn-beijing.volces.com/api/v3",
    "stepfun": "https://api.stepfun.com/v1",
    "baichuan": "https://api.baichuan-ai.com/v1",
    "hunyuan": "https://api.hunyuan.cloud.tencent.com/v1",
}

# models.dev 的 npm 包名 -> 我们的 ChatProtocol 枚举名。
# 只映射我们真的实现了的三种；其余跳过——写进表却实现不了，
# 用户选中后只会得到一个含糊的失败。
NPM_TO_PROTOCOL = {
    "@ai-sdk/openai-compatible": "OpenAiChatCompletions",
    "@ai-sdk/openai": "OpenAiChatCompletions",
    "@ai-sdk/groq": "OpenAiChatCompletions",
    "@ai-sdk/xai": "OpenAiChatCompletions",
    "@ai-sdk/cerebras": "OpenAiChatCompletions",
    "@ai-sdk/deepinfra": "OpenAiChatCompletions",
    "@ai-sdk/togetherai": "OpenAiChatCompletions",
    "@ai-sdk/perplexity": "OpenAiChatCompletions",
    "@openrouter/ai-sdk-provider": "OpenAiChatCompletions",
    "ai-gateway-provider": "OpenAiChatCompletions",
    "@ai-sdk/anthropic": "AnthropicMessages",
}

# 协议推断：某些家虽然 npm 是通用包，但官方端点说的是另一种协议。
# 只放已确认的；不确定就让它走 npm 映射。
PROTOCOL_OVERRIDE = {
    # MiniMax 只提供 Anthropic Messages 兼容端点（路径里带 /anthropic）。
    "minimax": "AnthropicMessages",
    "minimax-cn": "AnthropicMessages",
}

# models.dev 的 provider id -> 我们对外暴露的名字。
# 只改名字，不改数据。保留与上游一致的名字便于对照，只有缩写不直观的才改名。
PROVIDER_ALIASES = {
    # 名字要一眼认得出是哪家。models.dev 的 id 有些是缩写或旧称，改掉。
    "moonshotai": "kimi",
    "moonshotai-cn": "kimi-cn",
    "zai": "glm",          # Z.AI 国际站
    "zhipuai": "glm-cn",   # 智谱国内站
    "xiaomi": "mimo",
    "alibaba": "qwen",
    "volcengine": "doubao",
    "vercel-ai-gateway": "vercel-gateway",
}

# ── 第二层：实测修正表 ─────────────────────────────────────────────
# **只填我们自己真实验证过的条目。** 加之前先问：这是我自己调通后确认的，
# 还是从 pi / models.dev 抄来的？抄来的不算。
#
# 每条必须写清三件事：
#   · 哪天测的
#   · 怎么测的（用什么请求、看到什么响应）
#   · 不修会怎样（用户会看到什么症状）
#
# 为什么这个表空着是正确状态：models.dev 会错（pi 的源码里就记着
# gpt-5-pro 的输出上限被报成 272000，实际 128000），但我们**还没有一次
# 真实调用**能证明哪条错。填进去的每一条都必须是自己测出来的结论——
# 否则就是拿别人的实测冒充自己的验证，而 goal.md P4 明确把
# 「宣称未验证的能力」列为反目标。
CORRECTIONS: dict[str, dict[str, dict]] = {
    # 示例（注释掉，等有实测再启用）：
    # "openai": {
    #     "gpt-5-pro": {
    #         "maxOutputTokens": 128000,
    #         "why": "2026-XX-XX 实测：models.dev 报 272000（那是输入子限额），"
    #                "实际请求 128001 output 被 400 拒绝，128000 通过。",
    #     },
    # },
}

# 本地服务：无需密钥。models.dev 里没有（那是云端目录），单独补。
LOCAL_PRESETS = [
    ("ollama", "http://127.0.0.1:11434/v1", "OpenAiChatCompletions"),
    ("lmstudio", "http://127.0.0.1:1234/v1", "OpenAiChatCompletions"),
]


def fetch(url: str) -> bytes:
    """拉取 JSON。

    **必须显式给 User-Agent**：models.dev 会拦掉默认的 `Python-urllib/x.y`
    （返回 403），而 curl 能过——所以"curl 能拉到"不等于脚本能拉到。
    实测 2026-10-30：不带 UA 403，带 UA 200。
    """
    request = urllib.request.Request(
        url, headers={"User-Agent": "dougong-provider-sync/1.0"})
    with urllib.request.urlopen(request, timeout=60) as response:
        return response.read()


def protocol_for(npm: str | None) -> str | None:
    return NPM_TO_PROTOCOL.get(npm or "")


def reasoning_field_of(model: dict) -> str | None:
    """取推理内容的字段名（对应 WireCompat.ReasoningField）。

    models.dev 的 `interleaved` 有三种形态，**不处理会直接崩**：
      · {"field": "reasoning_content"}   正常
      · true                             只说了"支持交错"，没给字段名
      · 缺失                              不支持
    """
    interleaved = model.get("interleaved")
    if isinstance(interleaved, dict):
        field = interleaved.get("field")
        return field if isinstance(field, str) and field else None
    return None


def effort_map_of(model: dict) -> dict[str, str]:
    """把 models.dev 的 reasoning_options 转成 {统一档位: 该模型实际档位词}。

    **只收 type=effort 且带 values 的条目**：type=toggle 是"开/关"两态，
    没有档位可言；type=budget_tokens 是绝对 token 预算，语义不同，混进来
    会让"high"这种词指向一个 token 数。

    为什么必须显式映射而不是直接透传：各家档位词不一样。把用户选的
    "max" 原样发给只认 low/medium/high 的端点，会被 400 拒绝或静默忽略；
    静默忽略最坏——用户以为设置生效了。
    """
    result: dict[str, str] = {}
    for option in model.get("reasoning_options") or []:
        if not isinstance(option, dict) or option.get("type") != "effort":
            continue
        for value in option.get("values") or []:
            if isinstance(value, str) and value:
                # 目前统一档位词与 models.dev 的 values 同名，直接对应；
                # 若将来某家用不同词，在这里加映射而不是在运行时猜。
                result[value] = value
    return result


def main() -> int:
    check_only = "--check" in sys.argv

    print(f"拉取 {MODELS_DEV} …")
    raw = fetch(MODELS_DEV)
    digest = hashlib.sha256(raw).hexdigest()[:16]
    catalog = json.loads(raw)
    print(f"  provider: {len(catalog)} 个，源哈希 {digest}")

    providers: dict[str, dict] = {}
    skipped: list[tuple[str, str]] = []
    missing: list[str] = []

    for provider_id in sorted(INCLUDE):
        entry = catalog.get(provider_id)
        if entry is None:
            missing.append(provider_id)
            continue

        protocol = PROTOCOL_OVERRIDE.get(provider_id) or protocol_for(entry.get("npm"))
        if protocol is None:
            skipped.append((provider_id, f"协议未实现：{entry.get('npm') or '未声明'}"))
            continue

        # 端点：先看 models.dev 的 api，再看我们的回退表。
        # 官方 SDK 类 provider 的 api 为空（SDK 自带端点），只能靠回退表。
        base_url = (entry.get("api") or "").rstrip("/") or BASE_URL_FALLBACK.get(provider_id, "")
        if not base_url:
            skipped.append((provider_id, "缺端点（models.dev 未给且回退表没有）"))
            continue

        models = []
        for model in (entry.get("models") or {}).values():
            context = ((model.get("limit") or {}).get("context")) or 0
            output = ((model.get("limit") or {}).get("output")) or 0
            if not model.get("id") or context <= 0:
                # 没有上下文窗口的条目对我们没用：选完模型也算不出压缩阈值。
                continue

            # 只要支持工具调用的：harness 的核心是工具，不支持 tool_call 的
            # 模型选进列表就是给用户一个不能干活的选项。
            if model.get("tool_call") is False:
                continue

            models.append({
                "id": model["id"],
                "contextWindow": context,
                "maxOutputTokens": output,
                "supportsReasoning": bool(model.get("reasoning")),
                "reasoningField": reasoning_field_of(model),
                "reasoningEfforts": effort_map_of(model),
            })

        if not models:
            skipped.append((provider_id, "没有可用的 chat 模型（需 tool_call 且带上下文信息）"))
            continue

        models.sort(key=lambda m: m["id"])

        # 第二层：应用实测修正（目前表为空，循环空转）。
        for model in models:
            fix = CORRECTIONS.get(provider_id, {}).get(model["id"])
            if not fix:
                continue
            for field in ("contextWindow", "maxOutputTokens"):
                if field in fix:
                    model[field] = fix[field]

        name = PROVIDER_ALIASES.get(provider_id, provider_id)
        if name in providers:
            skipped.append((provider_id, f"与已收录的 '{name}' 重名"))
            continue

        # 密钥变量名：models.dev 的 env 字段直接给，不用猜。
        # 取第一个——它是上游列的优先项，多值的情况通常是同一家的新旧变量名。
        env = entry.get("env") or []

        providers[name] = {
            "protocol": protocol,
            "baseUrl": base_url,
            "apiKeyEnv": env[0] if env else None,
            "models": models,
        }

    for name, base_url, protocol in LOCAL_PRESETS:
        # 本地服务不鉴权：apiKeyEnv 为 None，列表里显示"无需密钥"。
        providers[name] = {
            "protocol": protocol, "baseUrl": base_url,
            "apiKeyEnv": None, "models": [],
        }

    if not providers:
        print("没有拉到任何 provider，放弃写文件。", file=sys.stderr)
        return 1

    source = render(providers, digest)

    if check_only:
        if OUTPUT.exists() and OUTPUT.read_text(encoding="utf-8") == source:
            print("已是最新。")
            return 0
        print("数据有更新，需要重新运行本脚本（去掉 --check）。")
        return 2

    OUTPUT.write_text(source, encoding="utf-8", newline="\n")

    model_count = sum(len(p["models"]) for p in providers.values())
    print()
    print(f"写入 {OUTPUT.relative_to(REPO_ROOT)}")
    print(f"  provider : {len(providers)} 个")
    print(f"  模型条目 : {model_count} 个")
    print(f"  源哈希   : {digest}")
    if skipped:
        print(f"  跳过 {len(skipped)} 个：")
        for provider_id, reason in skipped[:12]:
            print(f"    {provider_id:24} {reason}")
        if len(skipped) > 12:
            print(f"    …另有 {len(skipped) - 12} 个")
    return 0


def cs(value: str) -> str:
    return '"' + value.replace("\\", "\\\\").replace('"', '\\"') + '"'


def render(providers: dict, digest: str) -> str:
    """生成 C# 源码。

    **数据以字面量写死，运行时零依赖、零 JSON 解析**：预设是编译期常量，
    运行时去读文件只多一条失败路径，而这个表本身不该失败。
    """
    lines = [
        "// <auto-generated>",
        "//   由 scripts/sync-provider-presets.py 生成，不要手工编辑。",
        "//",
        "//   数据来源：models.dev v2（https://models.dev）。",
        f"//   源哈希 {digest}",
        "//",
        "//   为什么只认 models.dev 一个来源：pi 的 catalog/ 是它的加工层，",
        "//   不是事实来源；它的 baseUrl 有 6/25 家是变量而非字面量，",
        "//   正则解析拿不全且上游一改写法就失效；它的 compat 里是私有枚举",
        "//   （thinkingFormat: \"zai\"），语义只有它自己懂。",
        "// </auto-generated>",
        "",
        "#nullable enable",
        "",
        "namespace Dougong.Ai;",
        "",
        "/// <summary>内置 provider 预设与模型能力表。</summary>",
        "/// <remarks>",
        "/// <b>数据自 models.dev v2 生成</b>：端点、协议、上下文窗口、输出上限、",
        "/// 推理字段名与档位都是上游给的事实，我们原样搬运并记录源哈希，",
        "/// 出问题能追到是哪天的数据。",
        "/// <para>",
        "/// <b>为什么不是 provider 矩阵</b>：每条只是一行数据，没有厂商特例代码。",
        "/// 协议差异收敛在三种 wire format 与 <see cref=\"WireCompat\"/>。判据见 goal.md §3。",
        "/// </para>",
        "/// <para>",
        "/// <b>上游会错，但修正必须来自我们自己的实测</b>：pi 的源码里记着",
        "/// gpt-5-pro 的输出上限被 models.dev 报成 272000（实际 128000）。",
        "/// 这类修正放在生成脚本的 CORRECTIONS 表里，只填真实调用验证过的条目——",
        "/// 目前为空，因为有实测结论之前，抄别人的等于宣称未验证的能力。",
        "/// </para>",
        "/// </remarks>",
        "internal static class ProviderPresets",
        "{",
        "    /// <summary>全部预设，按名字排序。</summary>",
        "    internal static readonly IReadOnlyDictionary<string, ProviderPresetData> All =",
        "        new Dictionary<string, ProviderPresetData>(StringComparer.OrdinalIgnoreCase)",
        "        {",
    ]

    for name in sorted(providers):
        entry = providers[name]
        lines.append(f"            [{cs(name)}] = new(")
        lines.append(f"                BaseUrl: {cs(entry['baseUrl'])},")
        lines.append(f"                Protocol: ChatProtocol.{entry['protocol']},")
        key_env = entry.get("apiKeyEnv")
        lines.append(
            f"                ApiKeyEnvironmentVariable: {cs(key_env) if key_env else 'null'},")
        lines.append("                Models: [")

        for model in entry["models"]:
            efforts = model["reasoningEfforts"]
            if efforts:
                pairs = ", ".join(
                    f"[{cs(k)}] = {cs(v)}" for k, v in sorted(efforts.items()))
                efforts_expr = (
                    "new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) "
                    f"{{ {pairs} }}")
            else:
                efforts_expr = "null"

            field_expr = cs(model["reasoningField"]) if model["reasoningField"] else "null"

            lines.append(
                f"                    new({cs(model['id'])}, {model['contextWindow']}, "
                f"{model['maxOutputTokens']}, "
                f"{'true' if model['supportsReasoning'] else 'false'}, "
                f"{field_expr}, {efforts_expr}),")

        lines.append("                ]),")

    lines += [
        "        };",
        "",
        "    /// <summary>按 provider 与模型名查能力数据；查不到返回 null。</summary>",
        "    internal static ModelCapabilities? FindModel(string provider, string modelId) =>",
        "        All.TryGetValue(provider, out var preset)",
        "            ? preset.Models.FirstOrDefault(m =>",
        "                string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase))",
        "            : null;",
        "}",
        "",
        "/// <summary>一个内置 provider 预设。</summary>",
        "/// <param name=\"BaseUrl\">默认端点。</param>",
        "/// <param name=\"Protocol\">默认线协议。</param>",
        "/// <param name=\"ApiKeyEnvironmentVariable\">",
        "/// 密钥变量名。null 表示不鉴权（本地服务）。",
        "/// </param>",
        "/// <param name=\"Models\">该端点下的模型能力表；本地服务为空（模型由用户填）。</param>",
        "public sealed record ProviderPresetData(",
        "    string BaseUrl,",
        "    ChatProtocol Protocol,",
        "    string? ApiKeyEnvironmentVariable,",
        "    IReadOnlyList<ModelCapabilities> Models);",
        "",
        "/// <summary>一个模型的能力数据。</summary>",
        "/// <param name=\"Id\">模型标识。</param>",
        "/// <param name=\"ContextWindow\">上下文窗口（token）。</param>",
        "/// <param name=\"MaxOutputTokens\">输出上限（token）。0 表示上游未给。</param>",
        "/// <param name=\"SupportsReasoning\">是否支持推理/思考模式。</param>",
        "/// <param name=\"ReasoningField\">",
        "/// 推理内容的字段名，对应 <see cref=\"WireCompat.ReasoningField\"/>；",
        "/// null 表示上游只说支持交错、没给字段名。",
        "/// </param>",
        "/// <param name=\"ReasoningEfforts\">",
        "/// 统一档位到该模型实际档位词的映射。null 或空表示不支持档位。",
        "/// <b>只有映射里出现的档位才能发给这个模型</b>——发一个对方不认的值",
        "/// 会被 400 拒绝或静默忽略，而静默忽略会让用户以为设置生效了。",
        "/// </param>",
        "public sealed record ModelCapabilities(",
        "    string Id,",
        "    int ContextWindow,",
        "    int MaxOutputTokens,",
        "    bool SupportsReasoning,",
        "    string? ReasoningField,",
        "    IReadOnlyDictionary<string, string>? ReasoningEfforts);",
        "",
    ]

    return "\n".join(lines)


if __name__ == "__main__":
    sys.exit(main())
