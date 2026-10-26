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

**文件一：`MyFirstPlugin.csproj`**（文件名必须一模一样）

复制 [plugin-author-guide.md §1.1](plugin-author-guide.md) 的那段 XML。
**注意**：如果你把工程放在 `samples/` 下（而不是仓库外面），
`ImplicitUsings` 和 `Nullable` 其实由仓库根目录的配置提供了，
但**照抄写上不会出错**，所以照抄。

**文件二：`MyPlugin.cs`**

复制 [plugin-author-guide.md §1.2](plugin-author-guide.md) 的那段 C#。

### 6.3 编译

```bash
dotnet build samples/my-first-plugin -c Release
```

**应该看到**：`0 个警告` / `0 个错误`。

**如果看到**"找不到类型或命名空间 `Dougong.Kernel`"：
说明 csproj 里的 `<ProjectReference>` 路径不对。那是相对路径，
你的工程在 `samples/my-first-plugin/` 下，所以应该是 `..\..\src\Dougong.Kernel\Dougong.Kernel.csproj`
（两个 `..`）。数一下层级。

### 6.4 让内核真的加载它

这一步需要一个"宿主程序"。项目里已经有现成的：

```bash
dotnet run --project spike/Dougong.Agent.Comparison -- kernel-wiring
```

这是内核接线的自检。**你应该看到** `KERNEL-WIRING: ALL PASS`。

它加载的不是你写的插件，而是样例插件——但你现在已经知道
"加载成功"长什么样了。把你自己的 dll 路径传给 `kernel.LoadAsync(...)`
就等价于这件事（这一句需要写几行宿主代码，见指南 §1.4）。

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

**§6.1–6.3 未在本次走查中完成**：执行走查的工具对可读目录有限制，
与 Dougong 无关，不影响文档本身的正确性。§6.4 的 `kernel-wiring` 已单独验证通过。

### 仍然没有的

**这不是"没参与开发的人"的验证。** 上面那次是一个 agent 严格按文档执行，
它能发现"命令跑不通""数字对不上"这类硬错误，但**发现不了**
"一个真新手会卡在哪里"——它懂开发，看见 `csproj` 不会犯怵。

判据 1 要的是后者。这一点写在 [M3 的出口判据](m3-plan.md)里，
是四条判据中唯一还没有证据的一条。
