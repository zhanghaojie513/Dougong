#!/usr/bin/env bash
# 运行全部确定性自检套件。
#
# 为什么需要这个脚本：spike 不在 Dougong.slnx 里，`dotnet build Dougong.slnx`
# 不会重建它。做反证后若忘记单独重建 spike，跑的是旧 DLL，
# 会得到"修复有效"或"仍然失败"的假结论。本项目已因此白费至少四次排查。
set -euo pipefail

DOTNET="${DOTNET:-dotnet}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

echo "== 构建（solution + spike）=="
# solution 必须先构建：后面的 pack 用 --no-build，
# 不重建就会拿旧 DLL 做 API 比对，得到"零差异"的假结论。
"$DOTNET" build Dougong.slnx -c Release
"$DOTNET" build spike/Dougong.Agent.Comparison -c Release

echo
echo "== 内核测试 =="
"$DOTNET" test Dougong.slnx -c Release --no-build

echo
echo "== API 兼容检查（pack 时由 ApiCompat 执行）=="
# 为什么必须在这里跑：ApiCompat 只在 pack 阶段生效，
# `dotnet build` 与 `dotnet test` 都不会触发它。
# 少了这一步，破坏公开 API 的改动会一路绿灯到提交之后才被发现——
# 本项目的基线就在 3.4 之后腐化过一次（改了 PluginDiagnostic 却没更新基线）。
if ! pack_output=$("$DOTNET" pack Dougong.slnx -c Release --no-build 2>&1); then
  echo "$pack_output" | grep -E "error CP" | head -5
  echo "API 兼容检查失败：详见上方 error CP****。"
  echo "  有意变更 → scripts/update-api-baseline.sh 并更新 CHANGELOG.md"
  exit 1
fi
echo "API: ALL PASS"

echo
echo "== 自检套件 =="
# tool-package-checks 需要打包产物，所以先打一次。
# 它验证的是"别人装了能不能用"，而清包内容与 --help 行为只有打包后才存在。
echo "== dotnet tool 打包（供 tool-package-checks 使用）=="
"$DOTNET" pack src/Dougong.Cli -c Release --no-build -o ./artifacts/tool -v q
echo

suites="surface parity acp elicitation elicitation-process kernel-wiring kernel-path kernel-process bare-hold workspace-lock acp-plan replaceability telemetry recheck token schema tool-replacement mcp mcp-reload reachability approval-log wiring wire session usage shell failure store approval fileversion compaction contract skill-l1 l1-harness memory-cli context-hygiene doctor provider context-usage credential filesystem host-protocol host-protocol-extension prompt search event-order timeout shell-policy report apicompat terminal overflow brand load-plugin-checks tool-plugin-checks tool-package-checks"

failed=0
failed_names=""
for suite in $suites; do
  printf '%-18s ' "$suite"
  if ! output=$("$DOTNET" run --project spike/Dougong.Agent.Comparison \
        -c Release --no-build -- "$suite" 2>&1); then
    failed=$((failed + 1))
    failed_names="$failed_names $suite"
    # **失败时必须把每条 FAIL 打出来。**
    # 原先只 echo tail -1，于是 CI 日志里永远只有 "N FAILURE(S)"，
    # 看不到是哪一条——2026-10-31 查 ubuntu 上的 doctor 失败时，
    # 完整日志里翻不到任何单条 FAIL，只能靠猜。
    # 汇总行不够用，失败详情才是排查的起点。
    echo "$output" | grep -E '^FAIL ' || true
  fi
  echo "$output" | tail -1
done

echo
if [ "$failed" -eq 0 ]; then
  echo "ALL SUITES PASS"
else
  echo "FAILED SUITES:$failed_names"
  exit 1
fi

echo
echo "== 单文件发布 =="
# 单文件不是可选玩法：I0 的退出条件要求"别人装得上"，而自包含单文件发布
# 是 .NET 工具最常见的分发形态之一。
#
# 这条检查存在的原因是它曾经**编译失败**：PluginLoadContext 用
# typeof(object).Assembly.Location 推共享框架目录，而单文件下该属性恒为空
# （IL3000 → 本仓库 TreatWarningsAsErrors → error）。
#
# 光能编译不够：内核在单文件宿主里必须真的能加载与卸载插件，
# 否则"发布成功"只是把一个坏的 exe 交出去。所以发布后跑探针验证行为。
sf_out="$ROOT/artifacts/singlefile-probe"
rm -rf "$sf_out"
sf_rid="$("$DOTNET" --info | awk '/RID:/{print $2; exit}')"
if ! "$DOTNET" publish samples/singlefile-probe -c Release -r "$sf_rid" \
       -o "$sf_out" > /tmp/sf-publish.log 2>&1; then
  echo "singlefile        FAIL 发布失败"
  grep -E "error" /tmp/sf-publish.log | head -5
  exit 1
fi

probe_exe="$sf_out/SingleFileProbe"
[ -f "$probe_exe.exe" ] && probe_exe="$probe_exe.exe"
plugin_dll="$ROOT/samples/Dougong.Sample.Plugin/bin/Release/net10.0/Dougong.Sample.Plugin.dll"
if ! "$probe_exe" "$plugin_dll" > /tmp/sf-probe.log 2>&1; then
  echo "singlefile        FAIL"
  tail -8 /tmp/sf-probe.log
  exit 1
fi
echo "singlefile        $(tail -1 /tmp/sf-probe.log)"

# 反证：默认不跑（要反复重建，约 10 分钟），但必须能一键跑。
# 全绿只说明"没发现失败"，反证才说明"检查确实在验证该行为"。
if [ "${RUN_COUNTERPROOFS:-0}" = "1" ]; then
  echo
  echo "== 反证 =="
  bash scripts/skill-l1-counterproofs.sh
fi
