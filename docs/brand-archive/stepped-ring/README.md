# 已废弃：阶梯环方案

> **这不是当前标志。** 当前标志见 [assets/brand/README.md](../../assets/brand/README.md)。

这里的素材属于 **阶梯环**方案（玄黑底 `#0E0E11` + 米白线 `#F5F1E8`，
六个尺寸变体、立体桌面图标），于 2026-10-29 被全面替换。

## 为什么留着

Logo 一共换了四轮：斗拱具象 → D 字标 → 阶梯环 → 合构。
每一轮被否的理由都记在 [m3-dev-journey.md](../../docs/journey/m3-dev-journey.md)，
如果删掉图，那些理由就变成无法复核的口头说法。

## 目录内容

```
SPEC-historical.md  当时的规范原文（描述的是阶梯环，不是现在这套）
icon-primary.svg    玄底 + 米白线，主图标
icon-inverse.svg    无圆角底板版
icon-flat.svg       白底印刷版
icon-mono-white.svg 深色底纯白线
icon-small.svg      16–48px 专用（台阶减到 2 级）
icon-desktop.svg    桌面图标，前后分层立体
png/                八个尺寸（主图标）
png-desktop/        五个尺寸（立体版）
board.png           场景展示
desktop-context.png 桌面环境截图
typography-comparison.png  字重对比
```

**不要从这里取用任何文件。** 它的唯一用途是追溯。

## 当时的规范里有一处仍成立

那段关于**应用图标立体效果要三个参数配合**的记录——
底板径向提亮、投影偏移 `dy=10`、前层暖光 18%——讲的是"黑底叠黑投影等于没有投影"。
这个结论在新方案（石墨渐变 + 柔白构件 + 轻微边缘光）里同样适用，
新素材的 `build.py` 沿用了同一套光照参数。
