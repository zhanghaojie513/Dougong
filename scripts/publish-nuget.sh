#!/usr/bin/env bash
# 发布到 nuget.org。**按依赖顺序逐个推，失败即停。**
#
# 为什么要按顺序：包里声明了 ProjectReference 生成的依赖（例如
# Dougong.Hosting 依赖 Dougong.Kernel 与 Dougong.Agent）。nuget.org 在接收
# 包时会解析这些依赖，被依赖的包若尚未存在，推送会失败。
#
# 用法：
#   export NUGET_API_KEY='你的key'      # 不要写进任何文件
#   bash scripts/publish-nuget.sh --dry-run   # 先看会推什么
#   bash scripts/publish-nuget.sh             # 真推
#
# **这个脚本不读、不打印、不保存 key。** 只从环境变量取，且失败时不回显它。
#
# 一次教训（2026-10-28）：终检时 `rm -rf artifacts/final` 后只重打了 Cli，
# 结果用户按发布命令跑时第一个包就找不到。现在脚本自己先打包，
# 不依赖 artifacts/ 里残留的东西。
set -euo pipefail

DOTNET="${DOTNET:-dotnet}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

# 版本号从 Directory.Build.props 读，不在这里写死。
# 写死过一次：改版本时只改了 props，脚本仍在推旧版本号。
DEFAULT_VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props | head -1)"
VERSION="${VERSION:-$DEFAULT_VERSION}"

if [ -z "$VERSION" ]; then
  echo "无法从 Directory.Build.props 读出 <Version>" >&2
  exit 1
fi
OUT="artifacts/final"
SOURCE="https://api.nuget.org/v3/index.json"

# 依赖顺序，不可调整：Hosting 依赖 Kernel+Agent；Cli 依赖其余全部。
PACKAGES=(Kernel Agent Hosting Ai Mcp Acp Cli)

DRY_RUN=0
[ "${1:-}" = "--dry-run" ] && DRY_RUN=1

if [ "$DRY_RUN" -eq 0 ] && [ -z "${NUGET_API_KEY:-}" ]; then
  echo "缺少 NUGET_API_KEY 环境变量。" >&2
  echo "  export NUGET_API_KEY='你的key'" >&2
  echo "（key 从 https://www.nuget.org/account/apikeys 获取）" >&2
  exit 2
fi

echo "== 1. 打包（总是重打，不复用残留产物）=="
rm -rf "$OUT"
"$DOTNET" pack Dougong.slnx -c Release -o "$OUT" > /tmp/dougong-pack.log 2>&1 || {
  echo "打包失败，详见 /tmp/dougong-pack.log" >&2
  tail -20 /tmp/dougong-pack.log >&2
  exit 1
}

echo "== 2. 校验 7 个包都在 =="
missing=0
for p in "${PACKAGES[@]}"; do
  f="$OUT/Dougong.$p.$VERSION.nupkg"
  if [ ! -f "$f" ]; then
    echo "  缺失：$f" >&2
    missing=1
  else
    printf "  %-10s %8d B\n" "$p" "$(stat -c%s "$f")"
  fi
done
[ "$missing" -eq 0 ] || exit 1

if [ "$DRY_RUN" -eq 1 ]; then
  echo
  echo "== dry-run：以上 7 个包会被推到 $SOURCE（顺序如下）=="
  printf '  %s\n' "${PACKAGES[@]}"
  echo
  echo "确认无误后去掉 --dry-run 再跑一次。"
  exit 0
fi

echo
echo "== 3. 按依赖顺序推送 =="
for p in "${PACKAGES[@]}"; do
  printf '  %-10s ' "$p"
  if "$DOTNET" nuget push "$OUT/Dougong.$p.$VERSION.nupkg" \
       --api-key "$NUGET_API_KEY" --source "$SOURCE" > /tmp/dougong-push.log 2>&1; then
    echo "已推送"
  else
    echo "失败"
    # 只打印错误的摘要，不打印命令行（其中含 key）。
    grep -iE "error|forbidden|409|401|conflict" /tmp/dougong-push.log | head -5 >&2
    echo >&2
    echo "已推送的包会留在 nuget.org 上（不能删除，只能 unlist）。" >&2
    echo "修好问题后从失败的这个包继续，不必重推前面成功的。" >&2
    exit 1
  fi
done

echo
echo "全部推送完成。等 10–15 分钟索引刷新后验证："
echo "  dotnet nuget locals http-cache --clear"
echo "  dotnet add package Dougong.Agent --version $VERSION"
echo "  dotnet tool install --global Dougong.Cli --version $VERSION"
