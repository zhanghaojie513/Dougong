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
资源锁等全部自检套件（当前 36 套，数量会随项目增长）。
**这是本文里耗时最长的一步，但也是最能让你放心的一步。**

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

> **顺便认识一下 `kernel-wiring`**
>
> ```bash
> dotnet run --project spike/Dougong.Agent.Comparison -- kernel-wiring
> ```
>
> 那是内核自身的接线自检（12 项），跑完最后一行是 `KERNEL-WIRING: ALL PASS`。
> 它和你的插件无关，是验证内核没坏。
>
> **注意它开头会打一行很长的"内核成员调用：..."**——那是 `bare-hold` 检查的
> 调试输出，属于正常现象，不是错误。

---

## 7. 遇到问题时怎么问

**先做这三件事**，能解决 90% 的问题：

1. **把完整报错贴出来**，不要只说"报错了"。红色文字从第一行到最后一行。
2. **说清你运行的是哪条命令**，以及你当时在哪个目录（`pwd` 的输出）。
3. **说明"应该看到什么"和"实际看到什么"** 的差别。

本文档每一节都写了"应该看到什么"。如果实际不是那样，那就是问题所在。

**如果是本文档说错了或者漏了**：那是文档的缺陷，请直接指出是哪一节。
按本文走一遍发现的问题，比任何猜测都有价值。

---

## 8. 这份文档是怎么验证的

写完之后，本文里**每一条命令都实际跑过一遍**，不是"看起来对"：

| 验证项 | 结果 |
|---|---|
| `samples/m1-minimal-agent` 里直接 `dotnet run` | 通过，输出"样例通过：工具经 dispatcher 执行" |
| 照抄 §6.2 的 csproj + §1.2 的代码，在**仓库外**编译 | 通过（0 警告 0 错误） |
| 同样的文件放在 `samples/` 下、用 `..\..\src\...` 相对路径 | 通过 |
| `-- kernel-wiring` 自检 | `KERNEL-WIRING: ALL PASS` |

### 2026-10-26：第一次"按文档走"的实测

一个独立的 agent 被要求**严格照本文走、不跳过**，结果如下：

| 节 | 文档预期 | 实际 | 结论 |
|---|---|---|---|
| §0.1 `dotnet --version` | ≥ 10.0.402 | `10.0.402` | 一致 |
| §0.2 `git --version` | 2.x | `2.55.0.windows.5` | 一致 |
| §1 目录检查 | 有 `Dougong.slnx` 等 | 全部存在 | 一致 |
| §2 构建 | `0 警告 / 0 错误` | 首次 1 个 `MSB3101`，清理后 0/0 | **文档当时没写这条** |
| §3 测试 | 187 总计 / 0 失败 | 187 / 0 | 完全一致 |
| §4 自检 | `ALL SUITES PASS` | 通过 | **但套数写错（34 vs 实际 36）** |
| §5 样例 | "样例通过：工具经 dispatcher 执行" | 一字不差 | 完全一致 |
| §6.4 `kernel-wiring` | `KERNEL-WIRING: ALL PASS` | 一致 | 通过 |

**据此已修两处**：套数改为"当前 36 套"（并说明会随项目增长），
§2 补上 `MSB3101` 的处理办法。

### 2026-10-26（第二次）：真人走查

一位真实读者走到 §6.3 时卡住，报：

```
MyPlugin.cs(12,47): error CS0246: 未能找到类型或命名空间名"SystemClock"
```

两个原因，都真实：

1. **他读的是旧副本**，当时指南里确实缺 `SystemClock`（已在 #9 补上）。
2. **§6.2 的指令有歧义**：原本只写"复制 §1.2 的那段 C#"，
   而那段里有三个类型（`MyPlugin` / `IClock` / `SystemClock`）。
   读者只复制看起来"主要"的那个，就必报 `CS0246`。

已把 §6.2 改成"整段复制"并明确点出三个类型，§6.3 补了
`CS0246` 的分类对照表（找不到哪个类型 → 是哪一步做错了）。

**验证方式**：按新文档原样从指南里提取代码块生成两个文件，
`dotnet build` 通过（0 警告 0 错误）。

### 2026-10-26（第三次）：真实读者走到 §6.3

第一次有真人在本文档上卡住并报出确切错误（见上）。这正是判据 1 要的证据形式：
不是"作者觉得写清楚了"，而是"读者在哪一行停下"。

**已经修的就是这一处。** 但一次走查只覆盖到 §6.3，§7–§8 还没有真人走过。

### 顺带补的：§6.4 原来做不到"验证你自己的插件"

走查暴露出 §6.4 只教读者跑 `kernel-wiring`（内核自身的自检），
而那**加载的不是你的插件**。也就是说 §6 的最后一环是断的：
读者能编译出 dll，却没有任何办法确认内核认不认它。

已补 `-- load-plugin <你的 dll>`：走生产加载路径，打印状态与诊断，
验证卸载，失败时列出最常见的三类配置错误。§6.4 已改写为使用它。

对应检查项 `-- load-plugin-checks` 会**从指南里提取代码块**生成插件再加载——
这样"指南里的代码抄下来不能用"会被自动发现（这类问题刚发生过一次）。

**所以判据 1 仍然是"部分"。** 判断依据：
[getting-started.md](getting-started.md) 到 §6.4 已有真人验证，
但"一个没参与开发的人从零走完全程、全程无需提问"还没有。
这一点写在 [M3 的出口判据](m3-plan.md)里。
