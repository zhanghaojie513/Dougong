#!/usr/bin/env bash
# 更新 ApiCompat 基线包。**发版时执行，不是每次提交都跑。**
#
# 为什么需要脚本：基线如果忘了更新，ApiCompat 会拿一个越来越旧的包比对，
# 于是真正的破坏性变更被淹没在"历史遗留差异"里。
#
# 为什么基线不与 artifacts/packages 共用目录：
# 那样"上一次 pack 的产物"会成为基线，基线跟着代码走，等于没有基线。
set -euo pipefail

DOTNET="${DOTNET:-dotnet}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

# 五个公开发布的 SDK 包。Cli 不在其中：它是工具（PackAsTool），
# 面向命令行用户而非 API 消费者，公开类型不是契约。
PACKAGES=(Dougong.Kernel Dougong.Agent Dougong.Hosting Dougong.Ai Dougong.Mcp Dougong.Acp)

echo "== 1. 临时关闭校验，从当前 HEAD 打包 =="
# 校验开着的话，pack 会因为"与基线不一致"失败——而我们正是要更新基线。
# 用命令行属性覆盖，避免改文件。
for pkg in "${PACKAGES[@]}"; do
  "$DOTNET" pack "src/$pkg" -c Release -p:EnablePackageValidation=false
done

echo
echo "== 2. 复制产物为基线 =="
mkdir -p artifacts/baseline
# 版本号从 Directory.Build.props 取，不靠 `ls | head -1` 猜。
#
# **这里曾经用 `ls *.nupkg | head -1`**，取到的是字典序第一个——
# 也就是最老的那个版本（0.1.0-alpha.1）。于是"更新基线"每次都在复制旧包，
# 基线永远停在最早版本，ApiCompat 一直在拿最老的包比对，
# 旧差异越积越多，真变更淹在里面。实测于 2026-10-29：
# 脚本报"已更新"，但六个基线包的版本仍是 0.1.0-alpha.1。
# 用 shell 参数展开而不是 sed 的捕获组：把反斜杠-1 写进脚本文本时，
# 经过任何一层转义处理都可能被吃成控制字符（实测踩到，VERSION 变成 0x01，
# 之后每步都找错文件）。参数展开没有这个问题。
VERSION_LINE="$(grep -m 1 '<Version>' Directory.Build.props)"
VERSION="${VERSION_LINE#*<Version>}"
VERSION="${VERSION%%</Version>*}"
VERSION="$(echo "$VERSION" | tr -d '[:space:]')"
if [ -z "$VERSION" ]; then
  echo "无法从 Directory.Build.props 读出 <Version>" >&2
  exit 1
fi

for pkg in "${PACKAGES[@]}"; do
  src="src/$pkg/bin/Release/$pkg.$VERSION.nupkg"
  if [ ! -f "$src" ]; then
    echo "找不到 $src——打包步骤是否失败？" >&2
    exit 1
  fi
  cp "$src" "artifacts/baseline/$pkg.nupkg"
  echo "  $pkg <- $(basename "$src")"
done

echo
echo "== 3. 验证：现在应与基线零差异 =="
for pkg in "${PACKAGES[@]}"; do
  "$DOTNET" pack "src/$pkg" -c Release --no-build 2>&1 | grep -E "error CP" && {
    echo "基线更新后仍有差异，检查是否漏了包" >&2
    exit 1
  }
  echo "  $pkg OK"
done

echo
echo "基线已更新。记得在 CHANGELOG.md 记录本次发版的 API 变化。"
