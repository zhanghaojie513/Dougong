using System.Text.Json;
using Dougong.Ai;

namespace Dougong.Cli;

/// <summary>
/// 从 OpenAI 兼容端点拉取可用模型名。
/// </summary>
/// <remarks>
/// <b>为什么值得做</b>：手打模型名是最容易出错的一步（<c>deepseek-chat</c> 打成
/// <c>deepseek-chart</c>），而错误要到第一次对话才以 404 的形式出现，
/// 那时看不出是拼写问题。列出来就能点选。
/// <para>
/// <b>为什么失败不算错误</b>：很多端点不实现 <c>/v1/models</c>，或要求额外权限。
/// 拉不到就退回手填——那才是常态，不是异常。
/// </para>
/// <para>
/// <b>只读、不重试、超时短</b>：这是配置向导里的一步，卡住三十秒比拿不到列表更烦人。
/// 失败路径全部返回空列表，从不抛。
/// </para>
/// </remarks>
public static class ModelDiscovery
{
    /// <summary>查询可用模型。失败或无结果时返回空列表，**从不抛异常**。</summary>
    /// <param name="baseUrl">端点地址，例如 <c>https://api.deepseek.com</c>。</param>
    /// <param name="keyEnv">密钥所在的环境变量名；null 表示不带鉴权。</param>
    public static IReadOnlyList<string> TryListModels(string baseUrl, string? keyEnv)
    {
        try
        {
            return ListModelsAsync(baseUrl, keyEnv).GetAwaiter().GetResult();
        }
        catch
        {
            // 任何异常都视为"拉不到"：向导不该因为端点不支持而中断。
            return [];
        }
    }

    private static async Task<IReadOnlyList<string>> ListModelsAsync(string baseUrl, string? keyEnv)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)) return [];

        var modelsUrl = new Uri(
            uri.ToString().TrimEnd('/') + "/models",
            UriKind.Absolute);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        using var request = new HttpRequestMessage(HttpMethod.Get, modelsUrl);

        // 与 ChatClientFactory 同一来源顺序：存储优先，环境变量兜底。
        // 这里若只查环境变量，用户存了凭据文件后"拉取模型列表"会拿不到数据，
        // 而对话本身是好的——那种不一致最难查。
        if (keyEnv is { Length: > 0 })
        {
            var key = CredentialFile.TryRead(keyEnv);

            if (!string.IsNullOrWhiteSpace(key))
            {
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            }
        }

        using var response = await http.SendAsync(request).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return [];

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        // 形状是 `{ "data": [ { "id": "..." }, ... ] }`（OpenAI 规范）。
        // 不为此引入强类型：只需要里面的 id，而多一层类型反而要处理
        // 各家额外字段差异。
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var names = new List<string>();
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String
                && id.GetString() is { Length: > 0 } name)
            {
                names.Add(name);
            }
        }

        // 排序后去重：端点给的顺序不稳定，而用户按字母找比按返回顺序找快。
        return names
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
