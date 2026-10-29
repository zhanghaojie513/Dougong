from pathlib import Path
from xml.etree import ElementTree as ET
from PIL import Image, ImageChops
import base64
import subprocess
import sys
import zipfile

ROOT=Path(__file__).parent
SOURCE=ROOT.parent/'dougong-monochrome'
SCRIPTS=Path('C:/Users/haiha/.agents/skills/logo-design/scripts')
for d in ['svg','icons','png']:
    (ROOT/d).mkdir(exist_ok=True)

def paths(name):
    root=ET.parse(SOURCE/'svg'/f'{name}.svg').getroot()
    return ''.join(f'<path d="{p.attrib["d"]}"/>' for p in root.findall('.//{http://www.w3.org/2000/svg}path'))

def icon(light=False,size=None):
    small=size is not None and size<=32
    mark=paths(f'app-icon-{size}' if small else 'app-icon')
    if small:
        bg,fg=('#EFEFEF','#161616') if light else ('#161616','#F3F3F3')
        content=f'<rect x="8" y="8" width="240" height="240" rx="52" fill="{bg}"/><g fill="{fg}">{mark}</g>'
    else:
        stops=[('#FCFCFC','0%'),('#EFEFEF','48%'),('#DADADA','100%')] if light else [('#383838','0%'),('#1D1D1D','43%'),('#090909','100%')]
        fg1,fg2=('#303030','#111111') if light else ('#FAFAFA','#DCDCDC')
        edge='#000000' if light else '#FFFFFF'
        top,bottom=('.16','.05') if light else ('.40','.07')
        defs=f'''<defs>
<linearGradient id="body" x1="24" y1="8" x2="224" y2="256" gradientUnits="userSpaceOnUse">{''.join(f'<stop offset="{o}" stop-color="{c}"/>' for c,o in stops)}</linearGradient>
<linearGradient id="face" x1="64" y1="48" x2="176" y2="210" gradientUnits="userSpaceOnUse"><stop stop-color="{fg1}"/><stop offset="1" stop-color="{fg2}"/></linearGradient>
<linearGradient id="edge" x1="40" y1="8" x2="210" y2="248" gradientUnits="userSpaceOnUse"><stop stop-color="{edge}" stop-opacity="{top}"/><stop offset=".46" stop-color="{edge}" stop-opacity=".10"/><stop offset="1" stop-color="{edge}" stop-opacity="{bottom}"/></linearGradient>
<filter id="relief" x="-10%" y="-10%" width="120%" height="120%" color-interpolation-filters="sRGB"><feDropShadow dx="0" dy="1.2" stdDeviation=".7" flood-color="#000000" flood-opacity="{'.12' if light else '.5'}"/></filter>
</defs>'''
        relief=' filter="url(#relief)"' if size is None or size>=64 else ''
        content=defs+'<rect x="8" y="8" width="240" height="240" rx="52" fill="url(#body)"/><rect x="8.5" y="8.5" width="239" height="239" rx="51.5" fill="none" stroke="url(#edge)" stroke-width="1"/>'+f'<g fill="url(#face)"{relief}>{mark}</g>'
    return f'<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256" role="img"><title>Dougong — 黑白质感版</title>{content}</svg>'

for p in (SOURCE/'svg').glob('*.svg'):
    if not p.stem.startswith('app-icon'):
        (ROOT/'svg'/p.name).write_text(p.read_text(encoding='utf-8'),encoding='utf-8')
(ROOT/'svg'/'app-icon.svg').write_text(icon(),encoding='utf-8')
(ROOT/'svg'/'app-icon-light.svg').write_text(icon(True),encoding='utf-8')
for s in [16,24,32,48]:
    (ROOT/'svg'/f'app-icon-{s}.svg').write_text(icon(size=s),encoding='utf-8')

def image(path,attrs=''):
    mime='image/svg+xml' if path.suffix=='.svg' else 'image/png'
    return f'<img src="data:{mime};base64,{base64.b64encode(path.read_bytes()).decode()}" {attrs}>'

ladder=''.join(f'<div>{image(ROOT/"svg"/(f"app-icon-{s}.svg" if s<=48 else "app-icon.svg"),f"width=\"{s}\" height=\"{s}\"")}<span>{s} px</span></div>' for s in [16,24,32,48,64])
html='''<!doctype html><html lang="zh-CN"><meta charset="utf-8"><title>Dougong 黑白质感版</title><style>
*{box-sizing:border-box}body{margin:0;padding:38px 46px;background:#EFEFEF;color:#171717;font:16px 'Segoe UI','Microsoft YaHei',sans-serif}header{display:flex;justify-content:space-between;align-items:baseline;margin-bottom:24px}h1{margin:0;font-size:27px}header p{margin:0;color:#555;font-size:14px}.hero{display:grid;grid-template-columns:.8fr 1.2fr 1fr;gap:20px}.panel{height:350px;display:flex;flex-direction:column;align-items:center;justify-content:space-between;padding:24px;background:#fff;border:1px solid #D6D6D6;border-radius:18px}.panel p{align-self:flex-start;margin:0;font-size:14px}.panel img{width:260px;height:260px}.before img{width:210px;height:260px}.primary{background:#161616;color:#fff;border-color:#161616}.bottom{display:grid;grid-template-columns:1fr 1fr;gap:20px;margin-top:20px}.bottom section{background:white;border:1px solid #D6D6D6;border-radius:18px;padding:24px;height:195px}.sizes{display:flex;gap:25px;align-items:flex-end;margin-top:20px}.sizes>div{display:flex;flex-direction:column;align-items:center;gap:10px;min-width:40px}.sizes span{font-size:12px;color:#555}.bottom p{margin:0;font-size:14px}.identity img{width:100%;height:100px;object-fit:contain;margin-top:10px}footer{font-size:13px;line-height:1.8;color:#555;margin-top:20px}
</style><header><h1>Dougong / 黑白质感精修</h1><p>修改版结构 · 石墨表面 · 柔白构件 · 克制边缘光</p></header><main class="hero"><section class="panel before"><p>上一版 / 纯黑纯白</p>'''
html+=image(SOURCE/'svg'/'app-icon.svg')+'</section><section class="panel primary"><p>本版 / 石墨黑</p>'+image(ROOT/'svg'/'app-icon.svg')+'</section><section class="panel"><p>反色 / 雾白</p>'+image(ROOT/'svg'/'app-icon-light.svg')+'</section></main><div class="bottom"><section><p>真实像素尺寸 · 小图标降低材质强度</p><div class="sizes">'+ladder+'</div></section><section class="identity"><p>产品主标志保持纯色，适合界面与文档</p>'+image(ROOT/'svg'/'horizontal.svg')+'</section></div><footer>质感集中在应用图标的表面与边缘。保留已确认的轮廓、斜面接口和留白。<br>图标四角透明；16–32 px 使用平面灰阶与像素校正，48 px 保留渐变但关闭投影，64 px 起加入轻微浮起感。</footer></html>'
html=html.replace('.before img{width:210px;height:260px}', '.before img{width:260px;height:260px}')
(ROOT/'质感对照.html').write_text(html,encoding='utf-8')

def run(*args):
    subprocess.run([sys.executable,str(SCRIPTS/'render_png.py'),*map(str,args)],check=True)

run(ROOT/'质感对照.html','-o',ROOT/'质感对照.png','--width',1600,'--height',850)
if '--preview-only' in sys.argv:
    sys.exit(0)

for s in [16,24,32,48,64,128,256,512,1024]:
    src=f'app-icon-{s}' if s<=48 else 'app-icon'
    run(ROOT/'svg'/f'{src}.svg','-o',ROOT/'icons'/f'dougong-{s}.png','--size',s)
for name in ['symbol','symbol-white','horizontal','horizontal-white','app-icon-light']:
    dims=['--width',1236,'--height',384] if name.startswith('horizontal') else ['--size',1024]
    run(ROOT/'svg'/f'{name}.svg','-o',ROOT/'png'/f'{name}.png',*dims)

sizes=[16,24,32,48,64,128,256]
frames=[Image.open(ROOT/'icons'/f'dougong-{s}.png').convert('RGBA') for s in sizes]
frames[-1].save(ROOT/'icons'/'dougong.ico',format='ICO',sizes=[(s,s) for s in sizes],append_images=frames[:-1])
ico=Image.open(ROOT/'icons'/'dougong.ico')
assert ico.ico.sizes()=={(s,s) for s in sizes}
for s,frame in zip(sizes,frames):
    assert ImageChops.difference(ico.ico.getimage((s,s)).convert('RGBA'),frame).getbbox() is None
for p in [*(ROOT/'icons').glob('*.png'),*(ROOT/'png').glob('*.png')]:
    im=Image.open(p).convert('RGBA')
    r,g,b,a=im.split()
    assert ImageChops.difference(r,g).getbbox() is None
    assert ImageChops.difference(g,b).getbbox() is None
    assert im.getpixel((0,0))[3]==0

out=ROOT.parent/'dougong-monochrome-refined-logo-kit.zip'
with zipfile.ZipFile(out,'w',zipfile.ZIP_DEFLATED) as z:
    for p in ROOT.rglob('*'):
        if p.is_file() and p.name!='build.py':
            z.write(p,Path('dougong-monochrome-refined')/p.relative_to(ROOT))
print('Verified neutral grayscale, transparent corners and ICO native frames;',out)
