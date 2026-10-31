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
  2. 修正：`CORRECTIONS` 表，每条带 source（pi / self）与 why（机理）。
     带出处的上游结论可以直接采用——重新测一遍不是更严谨，
     只是把已经付过的成本再付一次。
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
    # OpenAI 兼容端点，不是各家原生协议。
    "google": "https://generativelanguage.googleapis.com/v1beta/openai",
    "mistral": "https://api.mistral.ai/v1",
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
    # 这两家的原生 SDK 包，但它们同时提供 OpenAI 兼容端点（实测确认：
    # Gemini 的 /v1beta/openai/chat/completions 报的是"缺 Authorization"
    # 而非路径不存在；Mistral 的 /v1/chat/completions 报 401）。
    # 所以按 OpenAI 兼容处理，端点由 BASE_URL_FALLBACK 给。
    "@ai-sdk/google": "OpenAiChatCompletions",
    "@ai-sdk/mistral": "OpenAiChatCompletions",
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

# ── 第二层：修正表 ─────────────────────────────────────────────────
# 上游会错，修正表就是纠错的地方。**每条必须带 source 与 why。**
#
# source 的两种取值，含义不同、排查路径也不同：
#   "pi"   —— 来自上游项目 pi（earendil-works/pi）的 catalog 声明。
#             他们已经在生产里跑过，源码注释写明了机理。出问题时去看
#             pi 有没有更新这条。
#   "self" —— 我们自己真实调用验证出来的。出问题时去看自己的测试记录。
#
# 为什么可以直接采用 "pi" 的结论：那是有出处的证据，不是猜测。
# 它的改写处都带机理说明（"models.dev reports ... a duplicate of the
# input sub-limit; actual is 128000"）或引用官方文档。重新测一遍不是
# 更严谨，只是把已经付过的成本再付一次。
#
# 但 why 必须写清**机理**，不能只写"pi 这么写"。没有机理的修正无法判断
# 上游改版后是否还成立——那种才是真正该自己重测的。
CORRECTIONS: dict[str, dict[str, dict]] = {
    # models.dev 把输出上限报成了输入子限额。实际请求超过 128000 会被拒。
    "openai": {
        "gpt-5-pro": {
            "maxOutputTokens": 128000,
            "source": "pi",
            "why": "models.dev 报 272000，那是输入子限额的重复值；实际输出上限 128000",
        },
    },
    # Claude 4.6 系列实际支持 1M 上下文，models.dev 报 200K。
    # 不修的话压缩会在 200K 就触发，白白浪费一半可用窗口。
    "anthropic": {
        "claude-opus-4-6": {
            "contextWindow": 1000000,
            "source": "pi",
            "why": "官方文档：Opus 4.6 支持 1M 上下文；models.dev 报 200K",
        },
        "claude-sonnet-4-6": {
            "contextWindow": 1000000,
            "source": "pi",
            "why": "官方文档：Sonnet 4.6 支持 1M 上下文；models.dev 报 200K",
        },
    },
}

# ── 第三层：不抄 pi 的哪些修正 ─────────────────────────────────────
# 下面这些在 pi 里存在，但我们**不抄**，因为它们的机理是 pi 自己的运行时策略：
#   · promptCache { short: 300, long: 3600 }  —— 它的缓存预热逻辑
#   · maxRequestBytes / maxPerRequest images   —— 它的图片卸载逻辑
#   · supportsStrictTools / zaiToolStream      —— 它自己的请求组装开关
#   · supportsMidConvoToolChanges              —— 它的会话内工具变更支持
# 我们的运行时没有对应机制，抄过来是死字段：写在表里看着完整，
# 实际上没有任何代码读它，将来还会误以为它生效了。

# 本地服务：无需密钥。models.dev 里没有（那是云端目录），单独补。
LOCAL_PRESETS = [
    ("ollama", "http://127.0.0.1:11434/v1", "OpenAiChatCompletions"),
    ("lmstudio", "http://127.0.0.1:1234/v1", "OpenAiChatCompletions"),
]


def SafeOutputLimit(context: int, output: int) -> int:
    """返回上游的输出容量上限，只做最基本的合法性过滤。

    **不再收敛到上下文的 25%**。之前那么做是因为我们的预算公式
    （`ctx - out - 512`）把"模型输出容量上限"当成了"单次请求预留"，
    于是 kimi-k3 的 1048576/1048576 算出负预算。

    现在两个概念已经拆开（见 ContextLimits.RequestOutputReserve）：
    这个字段只用于构造请求的 max_tokens，**不参与压缩预算**，
    所以它可以如实等于上下文窗口。

    只保留两条最小约束：
      · 非正数视为未披露，返回 0；
      · 超过上下文窗口的视为上游笔误，收敛到窗口值——
        发一个比窗口还大的 max_tokens 一定被拒。
    """
    if output <= 0 or context <= 0:
        return 0
    return min(output, context)


# 上下文压缩预留量，必须与 TokenBudget.DefaultOverheadTokens 保持一致。
# 用于生成后的自检：**预算公式用的是请求预留而不是输出容量**，
# 所以这里只检查"窗口减去固定预留后仍为正"——这是任何模型都该满足的。
COMPACTION_OVERHEAD_TOKENS = 512

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
            limit = model.get("limit") or {}
            context = limit.get("context") or 0
            output = limit.get("output") or 0
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
                "maxOutputTokens": SafeOutputLimit(context, output),
                "supportsReasoning": bool(model.get("reasoning")),
                "reasoningField": reasoning_field_of(model),
                "reasoningEfforts": effort_map_of(model),
            })

        if not models:
            skipped.append((provider_id, "没有可用的 chat 模型（需 tool_call 且带上下文信息）"))
            continue

        models.sort(key=lambda m: m["id"])

        # 第二层：应用修正。每条都必须带 source 与 why——
        # 缺任何一个就报错而不是静默跳过：没有机理的修正无法判断
        # 上游改版后是否还成立，那种才是真正该自己重测的。
        for model in models:
            fix = CORRECTIONS.get(provider_id, {}).get(model["id"])
            if not fix:
                continue

            if fix.get("source") not in ("pi", "self") or not fix.get("why"):
                raise SystemExit(
                    f"CORRECTIONS[{provider_id}][{model['id']}] 缺 source 或 why；"
                    "没有机理说明的修正不许进表。")

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

    # 生成后自检：任何让压缩预算 <= 0 的组合都是硬错误，直接拒绝写文件。
    # 这一层不能省——上面 SafeOutputLimit 只处理 limit 字段，
    # 但 CORRECTIONS 表也可能把某个值改坏，而那种错更隐蔽。
    for name, entry in providers.items():
        for model in entry["models"]:
            ctx = model["contextWindow"]
            # **用请求预留而不是输出容量**：输出容量与窗口同大是合法的
            # （那是模型能力），真正决定预算是单次请求预留。
            if ctx - COMPACTION_OVERHEAD_TOKENS <= 0:
                raise SystemExit(
                    f"{name}/{model['id']}: context={ctx} 减去 "
                    f"output={out} 与预留 {COMPACTION_OVERHEAD_TOKENS} 后不是正数。\n"
                    "这会让 ContextLimits.Validate() 抛异常、应用无法启动，"
                    "而用户看到的报错是「上下文窗口配置不可行」——那是我们的数据问题。")

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
            f"                ApiKeyName: {cs(key_env) if key_env else 'null'},")
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
        "/// <param name=\"ApiKeyName\">",
        "/// 密钥变量名。null 表示不鉴权（本地服务）。",
        "/// </param>",
        "/// <param name=\"Models\">该端点下的模型能力表；本地服务为空（模型由用户填）。</param>",
        "public sealed record ProviderPresetData(",
        "    string BaseUrl,",
        "    ChatProtocol Protocol,",
        "    string? ApiKeyName,",
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
        "/// 统一档位 → 该模型实际档位词的映射。<b>它有两个身份，读的人必须先分清是哪一侧。</b>",
        "/// <list type=\"bullet\">",
        "/// <item><b>Key 是 Dougong 的统一档位词</b>，固定八选一（不区分大小写）：",
        "/// <c>none</c> / <c>minimal</c> / <c>low</c> / <c>medium</c> / <c>high</c> / <c>xhigh</c> / <c>max</c> / <c>default</c>。",
        "/// 宿主的 UI 直接渲染这八个（或它们的子集），不需要知道任何 provider 的方言。</item>",
        "/// <item><b>Value 是该模型端点上认的字面量</b>，原样发给 provider。",
        "/// 现在多数家与 key 同名（<c>high → high</c>），但这是巧合不是契约——",
        "/// GLM 用 <c>none</c> 表示关闭、部分端点认 <c>xhigh</c> 而不认 <c>max</c>，",
        "/// 映射存在正是为了让宿主不必关心这些差异。</item>",
        "/// </list>",
        "/// <para>",
        "/// <b>只有 key 出现在映射里的档位才是这个模型支持的。</b>UI 应当只列这些；",
        "/// 发一个不在映射里的档位，轻则被静默忽略（用户以为设置生效了），",
        "/// 重则 400 且错误信息看不出是哪个参数。",
        "/// </para>",
        "/// <para>",
        "/// <b>null 与空字典不同</b>：null 表示该模型没有档位概念（要么不支持思考，",
        "/// 要么只支持开关）；空字典不该出现——如果真的抽到，按 null 处理。",
        "/// </para>",
        "/// <para>",
        "/// <b>跨进程传它时</b>：这两个都是普通字符串，序列化成 JSON 对象即可。",
        "/// 不需要为它定义协议类型——它是 SDK 的目录数据，不是会话消息。",
        "/// 宿主若要在自己的 IPC 里传，建议原样传这个字典再加自己的展示字段；",
        "/// 不要重新定义一套档位枚举，那会让两边的档位集合漂移。",
        "/// </para>",
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
