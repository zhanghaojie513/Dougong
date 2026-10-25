#!/usr/bin/env bash
# 用真实模型跑 Skill L1 验收 1（记忆启用前后在留出任务上的偏好遵循率对照）。
#
# 三种给凭证的方式，按优先级：
#   1. 已经在环境里 export 了 → 直接用
#   2. 交互式输入 → 脚本内 read -s（不回显），只在本进程生效，不写任何文件
#   3. 从其它进程读取 → 自己 export 后重新运行本脚本
#
# 为什么不做"从配置文件读密钥"：密钥一旦落盘就会进 git、进备份、进日志。
# 本项目从 M1 起就只从环境变量读（见 docs/m1-candidate-comparison.md）。
#
# 用法：
#   bash scripts/run-l1-effect.sh
set -uo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO" || exit 1

need() {
  local name="$1" hint="$2"
  if [ -n "${!name:-}" ]; then
    return 0
  fi

  printf '  %s（%s）: ' "$name" "$hint"
  # -s 不回显：密钥不会出现在终端历史或截图里。
  local value
  read -rs value
  echo
  if [ -z "$value" ]; then
    echo "未输入 $name，中止。"
    exit 2
  fi
  export "$name=$value"
}

echo "== Skill L1 验收 1：真实模型对照 =="
echo "需要三个环境变量。未设置的会提示输入（不回显、不落盘）。"
echo

need DOUGONG_PROVIDER_BASE    "例如 https://api.example.com/v1"
need DOUGONG_PROVIDER_MODEL   "例如 deepseek-v4.1-flash"
need DOUGONG_PROVIDER_KEY     "密钥，只在本进程内使用"

# 协议：默认 openai 兼容；Anthropic 兼容端点设 DOUGONG_PROVIDER_PROTOCOL=anthropic
: "${DOUGONG_PROVIDER_PROTOCOL:=openai}"
export DOUGONG_PROVIDER_PROTOCOL

echo
echo "  base   = $DOUGONG_PROVIDER_BASE"
echo "  model  = $DOUGONG_PROVIDER_MODEL"
echo "  proto  = $DOUGONG_PROVIDER_PROTOCOL"
echo "  key    = （已设置，长度 ${#DOUGONG_PROVIDER_KEY}）"
echo
echo "本脚本会产生 20 次模型调用（10 任务 × 有记忆/无记忆两组），按你的 provider 计费。"
printf '继续？[y/N] '
read -r confirm
case "$confirm" in
  y|Y) ;;
  *) echo "已取消。"; exit 0 ;;
esac

echo
dotnet build spike/Dougong.Agent.Comparison -c Release -v q --nologo || exit 1
dotnet run --project spike/Dougong.Agent.Comparison -c Release --no-build -- l1-effect
