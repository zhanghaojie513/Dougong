using System.IO.Compression;
using System.Text;

namespace Dougong.Agent.Comparison;

/// <summary>
/// 品牌资产的确定性检查。
/// </summary>
/// <remarks>
/// <b>为什么品牌素材需要检查</b>：它们是**构建产物**（`assets/brand/build.py`
/// 生成），但生成完就进了仓库。如果某次只改了脚本没重跑、或者手工替换了
/// 一两个文件，PNG 的尺寸、透明角、ICO 的帧数就可能与文档不符——
/// 而这类问题在 GitHub 页面上看起来"就是有点糊"，没人会去量。
/// <para>
/// 这里只验证**可客观判定的事实**（尺寸、alpha、帧数、文件存在），
/// 不评判美观。观感需要人看，见 `assets/brand/README.md` §十一。
/// </para>
/// </remarks>
public static class BrandChecks
{
    /// <summary>应用图标应当存在的尺寸。</summary>
    private static readonly int[] IconSizes = [16, 24, 32, 48, 64, 128, 256, 512, 1024];

    public static Task<int> RunAsync()
    {
        var failures = 0;
        failures += Check("brand-icons-all-sizes-present", IconsPresent);
        failures += Check("brand-icons-are-square-with-alpha", IconsSquareWithAlpha);
        failures += Check("brand-ico-frames-match-sizes", IcoFrames);
        failures += Check("brand-primary-symbols-exist", PrimarySymbolsExist);
        failures += Check("brand-readme-lists-existing-files", ReadmeMatchesFiles);
        failures += Check("readme-logo-matches-brand-icon", ReadmeLogoIsCurrent);

        Console.WriteLine(failures == 0
            ? "BRAND: ALL PASS"
            : $"BRAND: {failures} FAILURE(S)");
        return Task.FromResult(failures);
    }

    /// <summary>九个尺寸一个都不能少。</summary>
    /// <remarks>
    /// 少一个尺寸的后果很具体：Windows 桌面会拿大图硬缩，边缘发虚；
    /// 而"少一个"在目录列表里不显眼。
    /// </remarks>
    private static string? IconsPresent()
    {
        var dir = Path.Combine(BrandRoot(), "icons");
        if (!Directory.Exists(dir)) return $"找不到 {dir}";

        var missing = IconSizes
            .Select(size => $"dougong-{size}.png")
            .Where(name => !File.Exists(Path.Combine(dir, name)))
            .ToArray();

        return missing.Length == 0
            ? null
            : $"缺少应用图标：{string.Join(", ", missing)}";
    }

    /// <summary>每个图标必须是正方形且带 alpha 通道（四角要能透明）。</summary>
    /// <remarks>
    /// 非正方形会被拉伸变形；没有 alpha 通道则四角变成实心白块，
    /// 在深色任务栏上很刺眼。这两点都直接从 PNG 头读，不需要解码像素。
    /// </remarks>
    private static string? IconsSquareWithAlpha()
    {
        var dir = Path.Combine(BrandRoot(), "icons");

        foreach (var size in IconSizes)
        {
            var path = Path.Combine(dir, $"dougong-{size}.png");
            if (!File.Exists(path)) continue;   // 由 IconsPresent 报

            var header = ReadPngHeader(path);
            if (header is null) return $"{Path.GetFileName(path)} 不是有效 PNG";

            var (width, height, colorType) = header.Value;

            if (width != height)
                return $"{Path.GetFileName(path)} 不是正方形：{width}×{height}";

            if (width != size)
                return $"{Path.GetFileName(path)} 文件名写 {size}，实际 {width}px";

            // colorType 4 = 灰度+alpha，6 = RGBA。3（调色板）也可能带 tRNS，
            // 但应用图标必须逐像素透明，所以只接受 4/6。
            if (colorType is not (4 or 6))
                return $"{Path.GetFileName(path)} 没有 alpha 通道（colorType={colorType}）";
        }

        return null;
    }

    /// <summary>ICO 的帧数必须等于文档承诺的七个尺寸，且每帧尺寸对得上。</summary>
    private static string? IcoFrames()
    {
        var path = Path.Combine(BrandRoot(), "icons", "dougong.ico");
        if (!File.Exists(path)) return "缺少 icons/dougong.ico";

        var data = File.ReadAllBytes(path);
        if (data.Length < 6) return "ICO 文件过短";

        var count = data[4] | (data[5] << 8);
        int[] expected = [16, 24, 32, 48, 64, 128, 256];

        if (count != expected.Length)
            return $"ICO 有 {count} 帧，文档说 {expected.Length} 帧";

        for (var i = 0; i < count; i++)
        {
            var offset = 6 + (i * 16);
            var width = data[offset] == 0 ? 256 : data[offset];
            if (width != expected[i])
                return $"ICO 第 {i} 帧是 {width}px，应为 {expected[i]}px";
        }

        return null;
    }

    /// <summary>主标志的两个极性与组合稿必须都在。</summary>
    /// <remarks>
    /// 缺了 `symbol-white.svg` 时，深色背景（README 的 dark 主题、视频片头）
    /// 就没法放标志——而它恰恰是最容易被忘记导出的那一个。
    /// </remarks>
    private static string? PrimarySymbolsExist()
    {
        string[] required =
        [
            Path.Combine("svg", "symbol.svg"),
            Path.Combine("svg", "symbol-white.svg"),
            Path.Combine("svg", "app-icon.svg"),
            Path.Combine("svg", "app-icon-light.svg"),
            Path.Combine("svg", "horizontal.svg"),
            Path.Combine("svg", "horizontal-white.svg"),
            Path.Combine("svg", "stacked.svg"),
            Path.Combine("svg", "wordmark.svg"),
            Path.Combine("png", "symbol.png"),
            Path.Combine("png", "symbol-white.png"),
        ];

        var root = BrandRoot();
        var missing = required.Where(r => !File.Exists(Path.Combine(root, r))).ToArray();

        return missing.Length == 0
            ? null
            : $"缺少品牌文件：{string.Join(", ", missing)}";
    }

    /// <summary>
    /// README 的文件清单里提到的每个路径都必须真实存在。
    /// </summary>
    /// <remarks>
    /// <b>这条防的是"替换素材后文档还在指旧文件名"</b>——2026-10-29 整目录换过
    /// 一次（阶梯环 → 黑白合构），旧文档里 11 个文件名全部失效。
    /// 只看文档看不出来，因为那些名字本身很合理。
    /// </remarks>
    private static string? ReadmeMatchesFiles()
    {
        var root = BrandRoot();
        var readme = Path.Combine(root, "README.md");
        if (!File.Exists(readme)) return "缺少 assets/brand/README.md";

        var text = File.ReadAllText(readme);
        var missing = new List<string>();

        // 抓形如 svg/xxx.svg、png/xxx.png、icons/xxx.png 的引用。
        foreach (var line in text.Split('\n'))
        {
            foreach (var token in line.Split([' ', '|', '`', '(', ')', '[', ']', ',', '\r'],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                if (!token.StartsWith("svg/", StringComparison.Ordinal)
                    && !token.StartsWith("png/", StringComparison.Ordinal)
                    && !token.StartsWith("icons/", StringComparison.Ordinal))
                {
                    continue;
                }

                // 通配写法 `icons/dougong-{16,24,...}.png` 逐个展开太脆，
                // 这类行只要求"目录里有匹配项"即可。
                if (token.Contains('{')) continue;

                // README 里有一块用框线字符画的文件清单（`icons/dougong-16.png  ┐`），
                // 那是给人看的排版，不是可点击的路径。含通配符或框线的 token 跳过。
                if (token.Contains('*') || token.Any(IsBoxDrawing)) continue;

                if (!File.Exists(Path.Combine(root, token))) missing.Add(token);
            }
        }

        return missing.Count == 0
            ? null
            : $"README 提到的文件不存在：{string.Join(", ", missing.Distinct())}";
    }

    /// <summary>
    /// README 顶部那张图必须就是当前主图标，不能还是上一版。
    /// </summary>
    /// <remarks>
    /// <b>这是"全面使用"最容易漏的一处</b>：`assets/logo.png` 是 GitHub 上
    /// 第一眼看到的东西，而它不在 `assets/brand/` 里，替换素材时容易忘。
    /// 判据是与 `icons/dougong-256.png` 逐字节相同——不是"看着像"。
    /// </remarks>
    private static string? ReadmeLogoIsCurrent()
    {
        var root = FindRepoRoot();
        var logo = Path.Combine(root, "assets", "logo.png");
        var icon = Path.Combine(BrandRoot(), "icons", "dougong-256.png");

        if (!File.Exists(logo)) return "缺少 assets/logo.png（README 顶部图）";
        if (!File.Exists(icon)) return "缺少 icons/dougong-256.png";

        var logoBytes = File.ReadAllBytes(logo);
        var iconBytes = File.ReadAllBytes(icon);

        if (!logoBytes.AsSpan().SequenceEqual(iconBytes))
        {
            return "assets/logo.png 与 icons/dougong-256.png 不一致"
                + "（README 顶部图还是旧版？重新复制一次）";
        }

        // 还要确认 README 真的引用了它。
        var readme = Path.Combine(root, "README.md");
        if (File.Exists(readme)
            && !File.ReadAllText(readme).Contains("assets/logo.png", StringComparison.Ordinal))
        {
            return "README.md 没有引用 assets/logo.png";
        }

        return null;
    }

    // ── 工具 ─────────────────────────────────────────────────

    private static string BrandRoot() => Path.Combine(FindRepoRoot(), "assets", "brand");

    /// <summary>框线字符（U+2500–U+257F）。README 的文件清单图用它连接条目。</summary>
    private static bool IsBoxDrawing(char c) => c is >= '─' and <= '╿';

    /// <summary>从 PNG 头读宽、高与色彩类型；不是 PNG 时返回 null。</summary>
    private static (int Width, int Height, int ColorType)? ReadPngHeader(string path)
    {
        using var stream = File.OpenRead(path);
        var header = new byte[33];

        if (stream.Read(header, 0, header.Length) < header.Length) return null;

        // 8 字节签名 + 4 长度 + "IHDR" + 宽(4) 高(4) 位深(1) 色彩类型(1)
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (!header.AsSpan(0, 8).SequenceEqual(signature)) return null;

        var width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
        var height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
        return (width, height, header[25]);
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Dougong.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("找不到仓库根目录（Dougong.slnx）");
    }

    private static int Check(string name, Func<string?> action)
    {
        try
        {
            var failure = action();
            if (failure is null) { Console.WriteLine($"PASS {name}"); return 0; }
            Console.WriteLine($"FAIL {name}: {failure}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}
