import pathlib

base = pathlib.Path('artifacts/brand-character/yungong-refined/text')
out = []

for name in ['full', 'compact', 'ascii']:
    text = (base / f'yungong-{name}.txt').read_text(encoding='utf-8')
    lines = text.split('\n')
    while lines and lines[-1] == '':
        lines.pop()
    out.append((name, lines))

def cs_lines(name, lines):
    # 用 verbatim string 逐行，避免转义问题
    body = ',\n'.join('        @"' + l.replace('"', '""') + '"' for l in lines)
    return f'    private static readonly string[] {name.capitalize()}Art =\n    [\n{body},\n    ];\n'

header = '''namespace Dougong.Cli.Terminal;

/// <summary>
/// 云拱字符画：终端欢迎区使用的品牌形象。
/// </summary>
/// <remarks>
/// <b>三档来自品牌素材，不是这里手画的</b>：源文件在
/// <c>artifacts/brand-character/yungong-refined/text/</c>，
/// 由 <c>scripts/gen-yungong-art.py</c> 生成到这个类。
/// 手抄一次就会和源文件漂移，而那类漂移没人会发现——终端图形本来就不好对账。
///
/// <para>
/// <b>每行左侧的字面空格很重要</b>：形象是左对齐的，各行宽度不同（17–30）。
/// <b>不能按行补右侧空格</b>——补齐会把 18 行都变成 30 列，
/// 视觉上右侧多出一堵空白墙（居中排时尤其难看）。原样输出。
/// </para>
///
/// <para>
/// <b>盲文字形一档需要终端字体支持</b>。实测传统 conhost（未设 WT_SESSION）
/// 也能正常显示，所以 <c>ascii</c> 档只在显式降级时用——
/// 判据见 <see cref="Banner"/>。
/// </para>
/// </remarks>
public static class YungongArt
{
'''

footer = '''}
'''

parts = [header]
for name, lines in out:
    parts.append(cs_lines(name, lines))

parts.append(footer)
pathlib.Path('src/Dougong.Cli/Terminal/YungongArt.cs').write_text('\n'.join(parts), encoding='utf-8')
print("generated YungongArt.cs")
for name, lines in out:
    print(f"  {name}: {len(lines)} rows, max width {max(len(l) for l in lines)}")

# 供重建时校验：生成结果必须与源文件逐行一致。
print("\nRun from repo root: python scripts/gen-yungong-art.py")
