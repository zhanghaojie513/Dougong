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

PACKAGES=(Dougong.Kernel Dougong.Agent)

echo "== 1. 临时关闭校验，从当前 HEAD 打包 =="
# 校验开着的话，pack 会因为"与基线不一致"失败——而我们正是要更新基线。
# 用命令行属性覆盖，避免改文件。
for pkg in "${PACKAGES[@]}"; do
  "$DOTNET" pack "src/$pkg" -c Release -p:EnablePackageValidation=false
done

echo
echo "== 2. 复制产物为基线 =="
mkdir -p artifacts/baseline
for pkg in "${PACKAGES[@]}"; do
  src=$(ls "src/$pkg/bin/Release/$pkg".*.nupkg | head -1)
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
