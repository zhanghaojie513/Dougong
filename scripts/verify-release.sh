#!/usr/bin/env bash
# 发布后验证：**从 nuget.org 下载实际包，核对内容与行为**。
#
# 为什么需要这一步：`dotnet pack` 成功、本地测试全绿，只能说明
# "我构建出来的东西是对的"。用户装到的是 nuget.org 上的那个包——
# 两者是不同的判断对象。这个仓库已经因此吃过一次教训：
# 包是从未提交的工作区打出来的，仓库与包对不上。
#
# 它还验证一个本地测不到的东西：**索引是否真的生效**。
# 推送成功与"能被 -flatcontainer 查到"之间有几十秒到十几分钟的间隔，
# 期间装包会失败。
#
# 用法：
#   bash scripts/verify-release.sh                    # 验证当前版本
#   bash scripts/verify-release.sh 0.1.15-alpha.1      # 验证指定版本
#   bash scripts/verify-release.sh --wait             # 等索引生效再验证
#
# 不需要 NUGET_API_KEY：只读公开源。
set -euo pipefail

DOTNET="${DOTNET:-dotnet}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

SOURCE="https://api.nuget.org/v3-flatcontainer"
DEFAULT_VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props | head -1)"

WAIT=0
VERSION=""
for arg in "$@"; do
  case "$arg" in
    --wait) WAIT=1 ;;
    *) VERSION="$arg" ;;
  esac
done
VERSION="${VERSION:-$DEFAULT_VERSION}"

if [ -z "$VERSION" ]; then
  echo "无法确定版本号：Directory.Build.props 里没有 <Version>" >&2
  exit 1
fi

# 与 publish-nuget.sh 同一份包列表。两处不一致会让"发出去的包"与
# "验证的包"不是同一批，而那正是这一步要防的事。
PACKAGES=(HostProtocol Kernel Agent Hosting Ai Mcp Acp Cli)

echo "== 验证 $VERSION =="
echo

# ── 1. 索引可用性 ────────────────────────────────────────────────
# 推送成功 ≠ 能查到。这一步失败时给出等待建议，而不是笼统报错。
echo "== 1. 检查索引是否生效 =="
missing=0
for pkg in "${PACKAGES[@]}"; do
  lower="$(echo "dougong.$pkg" | tr '[:upper:]' '[:lower:]')"
  printf '  %-14s ' "$pkg"
  if curl -sf --max-time 30 "$SOURCE/$lower/index.json" \
       | grep -q "\"$VERSION\""; then
    echo "已上架"
  else
    echo "未找到"
    missing=$((missing + 1))
  fi
done

if [ "$missing" -gt 0 ]; then
  if [ "$WAIT" -eq 1 ]; then
    echo
    echo "有 $missing 个包还没索引到。每 30 秒重试一次，最多 20 次…"
    for attempt in $(seq 1 20); do
      sleep 30
      missing=0
      for pkg in "${PACKAGES[@]}"; do
        lower="$(echo "dougong.$pkg" | tr '[:upper:]' '[:lower:]')"
        curl -sf --max-time 30 "$SOURCE/$lower/index.json" \
          | grep -q "\"$VERSION\"" || missing=$((missing + 1))
      done
      echo "  第 $attempt 次：还缺 $missing 个"
      [ "$missing" -eq 0 ] && break
    done
  fi

  if [ "$missing" -gt 0 ]; then
    echo
    echo "还有 $missing 个包查不到。索引通常需要 10–15 分钟；" >&2
    echo "稍后重跑本脚本，或加 --wait 让它自己等。" >&2
    exit 1
  fi
fi

echo
echo "== 2. 下载并核对包内容 =="

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# 只验 Ai 与 HostProtocol：它们承载了"公开 API 有没有真的推上去"这件事。
# 其余包打不开包不会有独立结论——它们要么引用这两个，要么只是工具。
CHECK_PACKAGES=(Ai HostProtocol)

for pkg in "${CHECK_PACKAGES[@]}"; do
  lower="$(echo "dougong.$pkg" | tr '[:upper:]' '[:lower:]')"
  # lower 已经含 dougong. 前缀，不能再拼一次。
  url="$SOURCE/$lower/$VERSION/$lower.$VERSION.nupkg"
  printf '  %-14s ' "$pkg"

  if ! curl -sfL --max-time 120 "$url" -o "$WORK/$pkg.nupkg"; then
    echo "下载失败"
    exit 1
  fi

  mkdir -p "$WORK/$pkg"
  if ! unzip -qo "$WORK/$pkg.nupkg" -d "$WORK/$pkg"; then
    echo "解包失败"
    exit 1
  fi

  dll="$WORK/$pkg/lib/net10.0/Dougong.$pkg.dll"
  if [ ! -f "$dll" ]; then
    echo "包里没有 lib/net10.0/Dougong.$pkg.dll"
    exit 1
  fi
  echo "已下载（$(stat -c%s "$dll" 2>/dev/null || wc -c < "$dll") B）"
done

echo
echo "== 3. 反射核对公开类型 =="

# 用临时工程引真实 DLL，而不是靠字符串搜字节：
# 字符串可能出现在注释、资源或调试信息里，"能搜到"不等于"能用"。
PROBE="$WORK/probe"
mkdir -p "$PROBE"

cat > "$PROBE/probe.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="Dougong.Ai">
      <HintPath>$(cygpath -w "$WORK/Ai/lib/net10.0/Dougong.Ai.dll" 2>/dev/null || echo "$WORK/Ai/lib/net10.0/Dougong.Ai.dll")</HintPath>
    </Reference>
    <Reference Include="Dougong.HostProtocol">
      <HintPath>$(cygpath -w "$WORK/HostProtocol/lib/net10.0/Dougong.HostProtocol.dll" 2>/dev/null || echo "$WORK/HostProtocol/lib/net10.0/Dougong.HostProtocol.dll")</HintPath>
    </Reference>
  </ItemGroup>
</Project>
EOF

# 断言清单。**新增公开类型时在这里加一行**，否则"发上去了"这件事
# 没有检查——而这正是本脚本存在的理由。
cat > "$PROBE/Program.cs" <<'PROBE_CS'
using System.Reflection;

var failures = 0;

// **显式 LoadFrom**：只用 <Reference> 引用时程序集不会被预加载，
// AppDomain.GetAssemblies() 里查不到它——实测踩到过，6 条断言全报"无法加载"。
// 而那只说明加载方式错了，不是包有问题。
var probeDir = AppContext.BaseDirectory;
var aiPath = Path.Combine(probeDir, "Dougong.Ai.dll");
var protocolPath = Path.Combine(probeDir, "Dougong.HostProtocol.dll");

if (File.Exists(aiPath)) Assembly.LoadFrom(aiPath);
if (File.Exists(protocolPath)) Assembly.LoadFrom(protocolPath);

void Require(string typeName, string asmHint, params string[] members)
{
    var asm = AppDomain.CurrentDomain.GetAssemblies()
        .FirstOrDefault(a => a.GetName().Name == asmHint);
    if (asm is null)
    {
        Console.WriteLine($"FAIL 无法加载 {asmHint}（查找路径 {aiPath} / {protocolPath}）");
        failures++;
        return;
    }

    var type = asm.GetType(typeName);
    if (type is null)
    {
        Console.WriteLine($"FAIL 缺类型 {typeName}");
        failures++;
        return;
    }

    foreach (var member in members)
    {
        var found = type.GetProperty(member, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance) is not null
            || type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
                   .Any(m => m.Name == member);

        if (!found)
        {
            Console.WriteLine($"FAIL {typeName}.{member} 不存在");
            failures++;
        }
    }
}

// ── 断言清单 ──────────────────────────────────────────────────
// 只列"推上去之后用户能用"的东西。内部实现不在这里钉。
Require("Dougong.Ai.DougongHome", "Dougong.Ai",
    "Root", "ConfigPath", "CredentialPath", "SessionsDirectory", "MemoryDirectory",
    "Resolve", "Describe");

Require("Dougong.Ai.UserPreferences", "Dougong.Ai",
    "Shell", "Memory", "ShowReasoning", "MemoryDirectory");

Require("Dougong.Ai.SecuritySwitches", "Dougong.Ai",
    "Current", "Shell", "Memory", "ShowReasoning", "MemoryDirectory");

Require("Dougong.Ai.CredentialFile", "Dougong.Ai", "DefaultPath", "TryRead", "Write");

Require("Dougong.Ai.ProviderConfigFile", "Dougong.Ai", "DefaultPath", "TryLoad");

Require("Dougong.HostProtocol.HostProtocolContract", "Dougong.HostProtocol",
    "Json", "RegisterDerivedType", "RegisteredExtensions");

Console.WriteLine(failures == 0 ? "API: ALL PASS" : $"API: {failures} FAILURE(S)");
return failures == 0 ? 0 : 1;
PROBE_CS

# 把被验的 DLL 拷贝到探针输出目录：LoadFrom 按 BaseDirectory 找。
# 不拷的话 AppContext.BaseDirectory 里没有它们，查找必然失败。
PROBE_OUT="$PROBE/bin/Release/net10.0"
mkdir -p "$PROBE_OUT"
cp "$WORK/Ai/lib/net10.0/Dougong.Ai.dll" "$PROBE_OUT/"
cp "$WORK/HostProtocol/lib/net10.0/Dougong.HostProtocol.dll" "$PROBE_OUT/"

if ! "$DOTNET" run --project "$PROBE/probe.csproj" -c Release 2>&1 | tail -20; then
  echo "反射核对失败" >&2
  exit 1
fi

if ! "$DOTNET" run --project "$PROBE/probe.csproj" -c Release 2>&1 | grep -q "API: ALL PASS"; then
  echo
  echo "公开 API 核对不通过：包里的内容与预期不符。" >&2
  exit 1
fi

echo
echo "== 4. 行为核对：DougongHome 的边界 =="

# 只钉一件已经在本地测过、但值得在**发布包上**再确认的事：
# 空值必须当未设置。dsh 的原文是 "a blank override never resolves the home
# to the current working directory"——做不到的话，DOUGONG_HOME="" 会让
# 程序把数据写进当前目录，而那是极难查的故障。
cat > "$PROBE/Program.cs" <<'BEHAVE_CS'
using System.Reflection;

// 与上一段同样需要显式加载：程序集不会因为 csproj 里有 Reference 就自动进来。
var aiPath = Path.Combine(AppContext.BaseDirectory, "Dougong.Ai.dll");
var asm = Assembly.LoadFrom(aiPath);
var home = asm.GetType("Dougong.Ai.DougongHome")!;
string Root() => (string)home.GetProperty("Root")!.GetValue(null)!;

var failures = 0;

void Expect(string label, string actual, string expected)
{
    if (actual != expected)
    {
        Console.WriteLine($"FAIL {label}: 实际 {actual}，应为 {expected}");
        failures++;
    }
}

var saved = Environment.GetEnvironmentVariable("DOUGONG_HOME");
try
{
    Environment.SetEnvironmentVariable("DOUGONG_HOME", null);
    var byDefault = Root();

    Environment.SetEnvironmentVariable("DOUGONG_HOME", "");
    Expect("空串当未设置", Root(), byDefault);

    Environment.SetEnvironmentVariable("DOUGONG_HOME", "   ");
    Expect("纯空白当未设置", Root(), byDefault);

    if (byDefault == Directory.GetCurrentDirectory())
    {
        Console.WriteLine("FAIL 默认 home 等于当前目录——数据会写进项目里");
        failures++;
    }
}
finally
{
    Environment.SetEnvironmentVariable("DOUGONG_HOME", saved);
}

Console.WriteLine(failures == 0 ? "BEHAVIOR: ALL PASS" : $"BEHAVIOR: {failures} FAILURE(S)");
return failures == 0 ? 0 : 1;
BEHAVE_CS

if ! "$DOTNET" run --project "$PROBE/probe.csproj" -c Release 2>&1 | tail -10; then
  echo "行为核对失败" >&2
  exit 1
fi

echo
echo "RELEASE-VERIFY: ALL PASS（$VERSION 已上架且内容符合预期）"
