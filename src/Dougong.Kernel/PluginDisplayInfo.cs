namespace Dougong.Kernel;

/// <summary>
/// 插件的展示元信息。**全部可选**——缺失时宿主回退到
/// <see cref="PluginId.Value"/>，不应该因为没填而拒绝加载。
/// </summary>
/// <remarks>
/// <b>命名用 <c>Title</c> 而不是 <c>DisplayName</c></b>：与 dsh 的 <c>meta.title</c>
/// 对齐，且 <c>Title</c>/<c>Description</c> 是成套的 UI 语义
/// （列表显示 Title，详情显示 Description）。
/// <para>
/// <b>不做多语言</b>：dsh 有 <c>locale/&lt;lang&gt;.json</c> 是因为它有外部插件生态，
/// 本项目一个外部作者都没有。多语言要定语言协商、回退链、文件布局——
/// 在没有真实需求时冻结格式，正是 host-layering-decision §4.4 反对的做法。
/// 真有作者提出时加一个 <c>IReadOnlyDictionary&lt;string,string&gt;? LocalizedTitles</c>
/// 即可，不改结构。
/// </para>
/// </remarks>
public sealed record PluginDisplayInfo
{
    /// <summary>显示名。列表页用它代替 PluginId。</summary>
    public string? Title { get; init; }

    /// <summary>一句话说明这个插件做什么。</summary>
    public string? Description { get; init; }

    /// <summary>作者或组织名，用于"这插件是谁的"这类判断。</summary>
    public string? Author { get; init; }

    /// <summary>三个字段全空。</summary>
    /// <remarks>
    /// 提供它是为了让"没有展示信息"与"有展示信息但都是空串"能区分：
    /// 前者应当是 <c>null</c>，后者说明插件作者填错了。
    /// </remarks>
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Title)
        && string.IsNullOrWhiteSpace(Description)
        && string.IsNullOrWhiteSpace(Author);
}
