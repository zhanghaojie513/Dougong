# 从零开始：第一次动手

> **这份文档给谁**：没写过 .NET、甚至没写过代码，但想跑起来看看、然后写一个自己的插件的人。
>
> **它和 [plugin-author-guide.md](plugin-author-guide.md) 的区别**：那份假设你懂 C#、
> 知道什么是 csproj。这份假设你**什么都不懂**——每条命令都写全，每步都告诉你
> "应该看到什么"，看到别的就是没成功。
>
> **不懂开发也能走完第 0–4 节**。第 5 节开始要写代码，那时你需要一点耐心，但不需要基础。

---

## 0. 先装两样东西

你只需要装这两样。其余都由它们自动处理。

### 0.1 .NET SDK

1. 打开 <https://dotnet.microsoft.com/download/dotnet/10.0>
2. 下载 **SDK 10.0.x**（不是 Runtime，不是 ASP.NET Core Runtime——必须是 SDK）
3. 安装，一路下一步
4. **关掉所有已经打开的终端窗口**（不关的话新装的命令找不到）

验证。打开一个新的终端（Windows 上是"终端"或 PowerShell），输入：

```bash
dotnet --version
```

**应该看到**：`10.0.402` 或更高的 `10.0.x`。

**如果看到** `command not found` 或 `'dotnet' 不是内部或外部命令`：
说明没装成功，或者没重开终端。重开一个再试；还不行就重新装一遍。

### 0.2 Git

1. 打开 <https://git-scm.com/downloads>
2. 下载你系统的版本，安装，一路下一步
3. 同样**关掉并重开终端**

验证：

```bash
git --version
```

**应该看到**：`git version 2.x.x`。

### 0.3 一个能看代码的工具（可选，但强烈建议）

Visual Studio Code：<https://code.visualstudio.com/>

装完后在它的扩展市场里搜 `C#`，装微软官方那个（作者是 Microsoft）。
不装也能走完本文，只是看代码会不方便。

---

## 1. 把项目拿到本地

打开终端，逐条运行（每行回车一次）：

```bash
cd ~
git clone https://github.com/zhanghaojie513/Dougong.git dougong
cd dougong
```

**应该看到**：`Cloning into 'dougong'...` 然后一堆进度，最后回到命令提示符。

**如果卡在 `Cloning into` 不动超过 2 分钟**：网络问题，按 Ctrl+C 中断，
换一个时间再试。这个仓库在国内访问 GitHub 经常慢。

进去之后确认东西都在：

```bash
ls
```

**应该看到**（顺序可能不同）：`README.md`、`src`、`tests`、`samples`、`docs`、
`scripts`、`Dougong.slnx` 等。

**如果没有 `Dougong.slnx`**：说明你在错误的目录里。重新 `cd ~/dougong` 再 `ls`。

---

## 2. 第一次构建

```bash
dotnet build Dougong.slnx -c Release
```

**第一次会下载很多依赖，可能 3–10 分钟**，屏幕滚动是正常的。

**成功的标志**（最后几行）：

```
    0 个警告
    0 个错误
```

或者英文：`0 Warning(s)` / `0 Error(s)`。

**如果看到红色的 `error`**：把它完整复制下来。这属于项目问题，不是你的操作问题。

**如果看到 `MSB3101`（`AssemblyReference.cache already exists` 之类），或者
内存不足 / `OutOfMemoryException`**：先运行

```bash
dotnet build-server shutdown
```

再重新构建一次。这两类都是本机残留的构建缓存与新构建抢写同一文件，
不是代码问题。**全新 clone 一般不会遇到**；如果你是从别人的工作副本或
已有目录开始，比较常见。改完只出现 `0 个警告 / 0 个错误` 就算通过——
不必追问那一次警告为什么出现。

---

## 3. 第一次跑测试

```bash
dotnet test Dougong.slnx -c Release --no-build
```

**应该看到**（大约 5 秒后）：

```
总计: 187
  失败: 0
  成功: 187
```

**关键是「失败: 0」**，总数随项目增长会变（写这段时是 187）。
只要失败是 0，就是通过。

**如果失败数不是 0**：先不要怀疑自己。把输出完整贴出来。

> **为什么要你先跑测试**：这 187 项检查验证的是项目自己没坏。
> 如果你在改动之前它就红了，那后面所有问题都跟你无关——先排除这个前提，
> 能省掉大量"是不是我弄坏了"的自我怀疑。

---

## 4. 跑一次完整的自检（可选，约 5 分钟）

```bash
bash scripts/all-checks.sh
```

> **Windows 用户**：如果你用的是 Git Bash（装 Git 时自带的那个），这条命令可以直接用。
> 在 PowerShell 或 CMD 里要用 `bash scripts/all-checks.sh`，前提是 Git 装的时候带了 bash。

**应该看到**最后是：

```
ALL SUITES PASS
```

这一条跑完，你就验证了整个项目——不只是单元测试，还包括协议、会话、审批、
资源锁等全部自检套件。**这是本文里耗时最长的一步，但也是最能让你放心的一步。**

> **只认最后一行是不是 `ALL SUITES PASS`。** 中间每一行是一个套件的名字与结果，
> 套件数量会随项目增长，本文不写死那个数字——**你看到的行数与你手上的版本有关，
> 不是你在前面某步做错了什么。**

---

## 5. 看一个已经能跑的样例

不用写任何代码，先看成品跑起来是什么样：

```bash
cd samples/m1-minimal-agent
dotnet run
```

**关键**：这个样例**不需要 API Key**，它用一个"假的模型"跑完了完整循环
（模型说"我要读文件" → 审批 → 真的读 → 结果回填 → 模型给答复）。
所以它不花钱，也不会连任何外部服务。

**应该看到**：一轮完整的工具调用过程，最后有类似"完成"的结论。

看完回到项目根目录：

```bash
cd ../..
```

---

## 6. 写你自己的第一个插件

从这里开始要**写代码**了。不用怕——下面是可以整段复制的。

### 6.1 先读那份详细的

现在去读 [plugin-author-guide.md](plugin-author-guide.md) 的 §1。
那里每一段代码都有解释。**读完 §1 就够写出第一个插件了**，后面的章节等你遇到
对应问题时再回来查。

### 6.2 建目录并写文件

在项目根目录下：

```bash
mkdir -p samples/my-first-plugin
```

在 `samples/my-first-plugin/` 里建两个文件。

**文件一：`MyFirstPlugin.csproj`**

把 [plugin-author-guide.md §1.1](plugin-author-guide.md) 的那个 XML 代码块
**整段**复制过来（从 `<Project Sdk=` 到 `</Project>`）。

**文件二：`MyPlugin.cs`**

把 [plugin-author-guide.md §1.2](plugin-author-guide.md) 的那个 C# 代码块
**整段**复制过来。

> **整段的意思是——三个类型都要。** §1.2 的代码块里有三个类型：
> `MyPlugin`、`IClock`、`SystemClock`。只复制看起来"主要"的 `MyPlugin`
> 会报 `CS0246: 未能找到类型或命名空间名"SystemClock"`（或 `IClock`）。
> 那三个类型放在同一个文件里就是为了让你一次复制完。

### 6.3 编译

```bash
dotnet build samples/my-first-plugin -c Release
```

**应该看到**：`0 个警告` / `0 个错误`。

**如果看到 `CS0246`，说某个类型找不到**（`SystemClock`、`IClock`、`Dougong.Kernel`）：

| 找不到的是什么 | 原因 | 做法 |
|---|---|---|
| `SystemClock` 或 `IClock` | 只复制了 §1.2 的一部分 | 回到 §1.2，把**整个代码块**重新复制一遍 |
| `Dougong.Kernel` / `IPlugin` | csproj 里的 `<ProjectReference>` 路径不对 | 见下 |

`<ProjectReference>` 的路径是**相对 csproj 自己**的。你的工程在
`samples/my-first-plugin/`，内核在 `src/Dougong.Kernel/`，所以要走两级上去：

```
..\..\src\Dougong.Kernel\Dougong.Kernel.csproj
```

数一下 `..` 的个数：`samples/my-first-plugin` → `samples` → 仓库根，
正好两级。少写一级就会找不到。

### 6.4 让内核真的加载你自己的插件

编译出来只是文件，还不算"能装上"。这一步让内核真的把它加载起来。

```bash
dotnet run --project spike/Dougong.Agent.Comparison \
  -- load-plugin samples/my-first-plugin/bin/Release/net10.0/MyFirstPlugin.dll
```

**应该看到**：

```
加载：...\MyFirstPlugin.dll

状态：Active

成功：内核认这个插件，且激活完成。
卸载：成功
```

看到 `状态：Active`，你的插件就是真的能装上了——内核认它、激活完成、还能卸载干净。

**如果 `状态` 不是 `Active`**，命令会打印诊断，包括哪个阶段失败、下一步检查什么。
最常见的三类原因（命令里也会列出来）：

| 报什么 | 原因 |
|---|---|
| 类型不实现 `IPlugin` | csproj 里 `Dougong.Kernel` 的引用漏了 `<Private>false</Private>` |
| 找不到程序集 | 漏了 `EnableDynamicLoading` |
| `Blocked` + 提示缺某个服务 | 插件声明了服务依赖但没人提供（这不是失败） |

> **如果你在别处见过 `kernel-wiring`，想让跑一下**
>
> ```bash
> dotnet run --project spike/Dougong.Agent.Comparison -- kernel-wiring
> ```
>
> 那是内核自身的接线自检，最后一行是 `KERNEL-WIRING: ALL PASS`。
> 它**不加载你的插件**，验证的是内核没坏——所以它不能代替上面这一步。
>
> 跑它时开头会有一行很长的"内核成员调用：..."，那是 `bare-hold` 检查的
> 调试输出，正常现象，不是错误。

---

### 6.5 写一个模型能调用的工具（进阶）

§6 那个插件是**内核插件**：它给宿主提供能力，模型看不见它。

**工具插件不一样**：模型能主动调用它。如果你想让模型多会一件事（查天气、算汇率、
查数据库），写的就是这种。

完整教程在 [plugin-author-guide.md §2.5](plugin-author-guide.md)，
这里有可运行的最小版本。

#### 6.5.1 建工程

跟 §6.2 一样的位置和方式，只是多引一个项目（工具契约在 Agent 层）：

```bash
mkdir -p samples/my-first-tool
```

**文件一：`MyFirstTool.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <EnableDynamicLoading>true</EnableDynamicLoading>
    <IsPackable>false</IsPackable>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Dougong.Agent\Dougong.Agent.csproj">
      <Private>false</Private>
      <ExcludeAssets>runtime</ExcludeAssets>
    </ProjectReference>
  </ItemGroup>
</Project>
```

**文件二：`WordCountTool.cs`**——整段复制
[plugin-author-guide.md §2.5.1](plugin-author-guide.md) 的那个 C# 代码块。

#### 6.5.2 编译并让宿主加载它

```bash
dotnet build samples/my-first-tool -c Release
dotnet run --project spike/Dougong.Agent.Comparison \
  -- load-tool samples/my-first-tool/bin/Release/net10.0/MyFirstTool.dll
```

**应该看到**：

```
加载：...\MyFirstTool.dll

工具：word_count（版本 1.0.0）
权限：已知、无访问 —— 这类工具由宿主决定是否跳过审批
调度：Serial（未声明资源键）
调用：{"text":"hello world"}
审批：请求了 1 次（默认行为；宿主可包 PermissionPolicyApprover 让无访问工具跳过）
结果：{"characters":11}

成功：宿主认这个工具，而且能被派发。
```

看到 `成功`，你的工具就被宿主接受、也确实能被调用了。

**如果报错**，命令会给可操作的原因。最常见的两个：

| 报什么 | 原因 |
|---|---|
| 找不到 `IAgentTool` | csproj 引的是 `Dougong.Kernel` 而不是 `Dougong.Agent` |
| schema 不合法 | `InputSchema` 不是合法 JSON Schema——注意必须 `required` 里列出的字段也在 `properties` 里 |

---

## 7. 遇到问题时怎么问

**先做这四件事**：

1. **把完整报错贴出来**，不要只说"报错了"。红色文字从第一行到最后一行。
2. **说清你运行的是哪条命令**，以及你当时在哪个目录（`pwd` 的输出）。
3. **说明"应该看到什么"和"实际看到什么"** 的差别。
4. **给出你所在副本的版本**：

   ```bash
   git log --oneline -1
   ```

   这一条不是形式主义。文档和代码会持续变化，**你读到的文档可能与你手上的代码不是同一版**——
   而这恰好是一次真实卡住的直接原因：一位读者报"指南里的示例少了 `SystemClock`"，
   查下来是那份副本还没更新，指南本身早已补上。

   如果是用 `dotnet tool` 装的 CLI，再附上：

   ```bash
   dougong --version
   ```

本文档每一节都写了"应该看到什么"。如果实际不是那样，那就是问题所在。

**如果是本文档说错了或者漏了**：那是文档的缺陷，请直接指出是哪一节。
按本文走一遍发现的问题，比任何猜测都有价值。

**如果你不确定该贴什么**，直接跑：

```bash
dougong --report
```

它输出的内容不含密钥与你的对话内容，可以直接贴出来。

---

## 8. 这份文档有多可靠

**已经做到的**：本文每一节里写出的命令与"应该看到什么"，都实际执行过；
被真人读者走过一遍，走出的问题都修了。

**已知的边界**：

| 范围 | 验证程度 |
|---|---|
| §0–§5（装环境、构建、测试、自检、跑样例） | 命令实际执行过；有人走到过这里 |
| §6.1–6.4（写第一个插件、编译、让内核加载它） | 命令实际执行过；**有真人走完且未报错** |
| §7（遇到问题怎么问） | 无技术操作，无需验证 |
| §8（本节） | 就是本节的说明本身 |

**仍然可能不准的地方**：

- 数字类描述（测试项数、套件数）会随项目增长而过时。本文已尽量避免写死，
  但若你看到的与实际不符，以实际输出为准。
- 只在 Windows + Git Bash 下完整走过。Linux / macOS 的命令差异没有验证过。
- 只有极少数真人走过，样本量很小。"别人都能走通"这句话本文不敢说。

**如果你卡住了**：那不是你的问题，是本文的缺口。请按 §7 的方式报出来——
具体在哪一节、哪条命令、看到什么与预期差在哪。

> 维护者的验证记录（每次走查的时间、发现的问题、修了什么）在
> [status.md](status.md) 的进度日志里。那是对账用的，不需要读来使用本项目。
