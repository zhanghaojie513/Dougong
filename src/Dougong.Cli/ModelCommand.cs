using Dougong.Ai;
using Dougong.Cli.Terminal;

namespace Dougong.Cli;

/// <summary>
/// <c>dougong model</c>：交互式选择 provider 与模型，并写回配置文件。
/// </summary>
/// <remarks>
/// <b>为什么是独立子命令而不是 REPL 里的 <c>/model</c></b>：两者改的东西不同。
/// <c>/model</c> 只换本次会话的连接器，退出即失效——那是"临时试一个端点"。
/// 这个命令改的是**配置文件**，是持久决定。把它们合成一个入口，
/// 用户就分不清"我刚才改的到底存没存"。
/// <para>
/// <b>为什么不自动探测可用模型</b>：那要调 <c>/v1/models</c>，需要密钥、
/// 需要网络，而且各家的返回形状并不统一。配模型是低频动作，一次真实报错
/// （404 model not found）比一个可能过时的列表更有用。所以这里让人填，
/// 但把"当前值"和"怎么填"讲清楚。
/// </para>
/// </remarks>
public static class ModelCommand
{
    /// <summary>执行命令，返回进程退出码。</summary>
    /// <param name="arguments">命令参数（不含 "model" 本身）。</param>
    public static int Run(string[] arguments)
    {
        // 非交互式形式：dougong model --set <provider> --model <name>
        // 既给脚本用，也给"我知道自己要用什么"的人用。
        string? setProvider = null;
        string? setModel = null;
        var makeDefault = false;
        var list = false;

        for (var i = 0; i < arguments.Length; i++)
        {
            switch (arguments[i])
            {
                case "--set" when i + 1 < arguments.Length:
                    setProvider = arguments[++i];
                    break;
                case "--model" when i + 1 < arguments.Length:
                    setModel = arguments[++i];
                    break;
                case "--default":
                    makeDefault = true;
                    break;
                case "--list" or "-l":
                    list = true;
                    break;
                case "--help" or "-h":
                    PrintHelp();
                    return 0;
                default:
                    Console.Error.WriteLine($"未知参数：{arguments[i]}");
                    Console.Error.WriteLine("用法：dougong model [--set <provider> [--model <名字>] [--default]] [--list]");
                    return 2;
            }
        }

        var path = ProviderConfigFile.DefaultPath;
        var loadError = ProviderConfigFile.TryLoad(path, out var catalog);

        if (loadError is not null)
        {
            Console.Error.WriteLine($"配置有问题：{loadError}");
            Console.Error.WriteLine("先修好再改。运行 dougong --doctor 查看完整诊断。");
            return 2;
        }

        if (list || setProvider is not null)
            return RunNonInteractive(path, catalog, setProvider, setModel, makeDefault);

        return RunInteractive(path, catalog);
    }

    private static int RunNonInteractive(
        string path, ProviderCatalog? catalog, string? providerName, string? model, bool makeDefault)
    {
        if (providerName is null)
        {
            PrintProviderTable(catalog);
            return 0;
        }

        // 换 provider 但没给模型时，沿用该 provider 已有的模型名。
        // 不给的话就变成"换过去但用不了"，而那要到下次对话才发现。
        var existing = catalog?.Find(providerName);
        var target = model ?? existing?.Model;

        if (string.IsNullOrWhiteSpace(target))
        {
            Console.Error.WriteLine(
                $"provider '{providerName}' 还没有模型名。请用 --model 指定。");
            return 2;
        }

        var result = ProviderConfigWriter.UpdateProvider(
            path, providerName, target, makeDefault || catalog is null);

        if (!result.Succeeded)
        {
            Console.Error.WriteLine(result.Error);
            return 1;
        }

        Output.Line($"已写入 {path}");
        Output.Line($"  provider: {providerName}");
        Output.Line($"  model   : {target}");
        if (makeDefault || catalog is null)
            Output.Line("  （已设为 default_provider）");
        if (result.BackupPath is not null)
            Output.Line($"  备份    : {result.BackupPath}");
        Output.Line();
        Output.Line("下一步：dougong --doctor 确认配置可用。");

        return 0;
    }

    /// <summary>交互式：挑 provider，再填模型，然后写回。</summary>
    private static int RunInteractive(string path, ProviderCatalog? catalog)
    {
        Output.Line();
        Output.Line($"  配置文件：{path}");
        if (catalog is null)
        {
            Output.Line("  当前还没有任何 provider——下面会帮你建一份。");
        }
        else
        {
            Output.Line($"  当前选中：{catalog.SelectedName}"
                + $"（模型 {(string.IsNullOrWhiteSpace(catalog.Selected.Model) ? "未配置" : catalog.Selected.Model)}）");
        }
        Output.Line();

        var current = catalog?.SelectedName;
        var chosen = PromptProvider(catalog, current);
        if (chosen is null)
        {
            Output.Line("未改动。");
            return 0;
        }

        var existingModel = catalog?.Find(chosen)?.Model;
        var model = PromptModel(chosen, existingModel);
        if (model is null)
        {
            Output.Line("未改动。");
            return 0;
        }

        // 选中的 provider 就设成默认：这个命令的意图是"以后用这个"。
        var result = ProviderConfigWriter.UpdateProvider(path, chosen, model, setAsDefault: true);
        if (!result.Succeeded)
        {
            Console.Error.WriteLine(result.Error);
            return 1;
        }

        Output.Line();
        Output.Line($"已写入 {path}");
        Output.Line($"  provider: {chosen}（已设为 default_provider）");
        Output.Line($"  model   : {model}");
        if (result.BackupPath is not null)
            Output.Line($"  备份    : {result.BackupPath}");

        // 密钥状态必须当场说：配好了模型却跑不起来，最常见的原因就是这个。
        var presetKey = ProviderCatalog.Presets.TryGetValue(chosen, out var preset)
            ? preset.ApiKeyEnvironmentVariable
            : null;
        if (presetKey is { Length: > 0 }
            && Environment.GetEnvironmentVariable(presetKey) is not { Length: > 0 })
        {
            Output.Line();
            Output.Line($"⚠ 还差密钥：provider '{chosen}' 需要环境变量 {presetKey}，但它没有设置。");
            Output.Line($"  export {presetKey}='<你的密钥>'");
        }

        Output.Line();
        Output.Line("下一步：dougong --doctor 确认配置可用。");
        return 0;
    }

    /// <summary>列出可选 provider，读一行选择。返回 null 表示取消。</summary>
    private static string? PromptProvider(ProviderCatalog? catalog, string? current)
    {
        // 候选是"预设 + 配置里已有的"，去重后按名字排序。
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in ProviderCatalog.Presets.Keys) names.Add(name);
        if (catalog is not null)
            foreach (var provider in catalog.Providers) names.Add(provider.Name);

        var items = names.ToList();
        Output.Line("  选择 provider：");
        Output.Line();

        for (var i = 0; i < items.Count; i++)
        {
            var name = items[i];
            var configured = catalog?.Find(name);
            var model = configured is null || string.IsNullOrWhiteSpace(configured.Model)
                ? "未配置模型"
                : configured.Model;
            var marker = string.Equals(name, current, StringComparison.OrdinalIgnoreCase) ? "▶" : " ";
            Output.Line($"  {marker} {i + 1,2}. {name,-14} {model}");
        }
        Output.Line();
        Output.Line($"   0. 取消");

        while (true)
        {
            Output.Text($"选择 [1-{items.Count}]，回车取消: ");
            var input = Console.ReadLine();
            if (input is null) return null;
            input = input.Trim();
            if (input.Length == 0 || input == "0") return null;

            if (int.TryParse(input, out var index) && index >= 1 && index <= items.Count)
                return items[index - 1];

            // 直接敲名字也接受：列表长的时候比数序号快。
            var byName = items.FirstOrDefault(n =>
                string.Equals(n, input, StringComparison.OrdinalIgnoreCase));
            if (byName is not null) return byName;

            Output.Line($"  没有 {input} 这一项。");
        }
    }

    /// <summary>提示输入模型名。返回 null 表示取消。</summary>
    private static string? PromptModel(string provider, string? existing)
    {
        Output.Line();
        if (string.IsNullOrWhiteSpace(existing))
            Output.Text($"  模型名（例如 deepseek-chat）: ");
        else
            Output.Text($"  模型名（当前 {existing}，回车保持不变）: ");

        var input = Console.ReadLine();
        if (input is null) return null;

        input = input.Trim();
        if (input.Length == 0) return string.IsNullOrWhiteSpace(existing) ? null : existing;

        return input;
    }

    private static void PrintProviderTable(ProviderCatalog? catalog)
    {
        Output.Line($"  配置文件：{ProviderConfigFile.DefaultPath}");
        Output.Line();

        if (catalog is null)
        {
            Output.Line("  还没有任何 provider。");
            Output.Line("  直接跑 dougong model（不带 --list）会引导你建一份。");
            return;
        }

        foreach (var provider in catalog.Providers)
        {
            var active = string.Equals(
                provider.Name, catalog.SelectedName, StringComparison.OrdinalIgnoreCase);
            var model = string.IsNullOrWhiteSpace(provider.Model) ? "未配置模型" : provider.Model;
            var keyState = provider.ApiKeyEnvironmentVariable switch
            {
                null or "" => "无需密钥",
                { } n when Environment.GetEnvironmentVariable(n) is { Length: > 0 } => $"{n} 已设置",
                { } n => $"{n} 未设置",
            };

            Output.Line($"  {(active ? "▶" : " ")} {provider.Name,-16} {model,-22} {keyState}");
        }

        Output.Line();
        Output.Line("  改模型：dougong model      或  dougong model --set <名字> --model <模型>");
    }

    private static void PrintHelp()
    {
        Output.Line("dougong model —— 选择 provider 与默认模型，写回配置文件");
        Output.Line();
        Output.Line("  dougong model                          交互式选择（会写回配置文件）");
        Output.Line("  dougong model --list                   只看当前配置");
        Output.Line("  dougong model --set deepseek --model deepseek-chat");
        Output.Line("                                         直接设置，并设为默认");
        Output.Line();
        Output.Line("与 REPL 里的 /model 的区别：");
        Output.Line("  /model        只换本次会话，退出即失效");
        Output.Line("  dougong model 改配置文件，是持久决定");
    }
}
