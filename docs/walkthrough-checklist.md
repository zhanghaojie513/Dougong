# 走查清单

> **这份文件的用途**：把"照文档走一遍"变成可逐条勾选、可记录结果的清单。
> 走查者不需要理解项目，只需要照做并记录"实际看到了什么"。
>
> **两条路，选一条**。路径 A 不需要模型 key，约 20 分钟；路径 B 需要，约 10 分钟。
> 建议先走 A——它覆盖的 `plugin-author-guide.md` 刚改过，还没被真人验过。
>
> **怎么记**：每条后面有 `[ ]`，走通了写 `✅`，卡住了写 `❌` 并**记下屏幕上的原话**
> （不要转述、不要归纳）。卡住的地方就是文档的缺口，不是你的问题。

---

# 路径 A：写一个插件（不需要模型 key，约 20 分钟）

对应文档：[docs/getting-started.md](getting-started.md) §1 → §2 → §7

## A1. 准备

```bash
git clone https://github.com/zhanghaojie513/Dougong.git dougong
cd dougong
```

- [ ] 命令成功，`dougong` 目录出现

## A2. 构建

```bash
dotnet build Dougong.slnx -c Release
```

**应该看到**：`0 个警告` / `0 个错误`

- [ ] 构建成功
- [ ] 记录耗时：______ 分钟（文档说"3–10 分钟"，实际是多少？）

## A3. 建插件目录

```bash
mkdir -p samples/my-first-plugin
```

- [ ] 目录建好

## A4. 写两个文件

打开 [docs/plugin-author-guide.md](plugin-author-guide.md)：

- [ ] 把 **§1.1** 的 XML 代码块**整段**复制进 `samples/my-first-plugin/MyFirstPlugin.csproj`
- [ ] 把 **§1.2** 的 C# 代码块**整段**复制进 `samples/my-first-plugin/MyPlugin.cs`

> **"整段"指三个类型都要**：`MyPlugin`、`IClock`、`SystemClock`。
> 只复制看起来主要的那个会报 `CS0246`。

- [ ] 两个文件都写好了，各自内容完整

## A5. 编译插件

```bash
dotnet build samples/my-first-plugin -c Release
```

**应该看到**：`0 个警告` / `0 个错误`

- [ ] 编译成功

**如果报错**，先看是不是这两类（文档里有对照表）：

- `CS0246` 找不到 `SystemClock` / `IClock` → §1.2 没复制全
- 找不到 `Dougong.Kernel` → csproj 里的 `../..` 层级写错了

实际报错（原话）：________________________________

## A6. 让内核加载它

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

- [ ] 看到 `状态：Active`
- [ ] 看到 `卸载：成功`

实际输出（原话）：________________________________

---

# 路径 B：装工具跑真实任务（需要模型 key，约 10 分钟）

对应文档：[docs/getting-started.md](getting-started.md) §0.4 → §6

> **前置条件**：一个**有余量**的模型 key。会用到的三个值：
> 服务地址、模型名、密钥。缺一个就会卡在 B3。

## B1. 装工具

```bash
dotnet tool install --global Dougong.Cli --version 0.1.0-alpha.1
dougong --help
```

> **必须带 `--version`**。不带的话会报"找不到 dougong.cli"——新包在
> nuget.org 的搜索索引里还没出现，而不带版本号正是走搜索索引。

**应该看到**：帮助信息，列出 `--doctor`、`--report`、`--version` 等命令

- [ ] 安装成功
- [ ] `dougong --help` 有输出

## B2. 设三个环境变量

**先确认你的终端是哪一种，写法不一样。**

Git Bash / WSL / macOS / Linux：

```bash
export DOUGONG_PROVIDER_BASE=https://你的地址
export DOUGONG_PROVIDER_MODEL=你的模型名
export DOUGONG_PROVIDER_KEY=你的密钥
```

Windows cmd（**不要加引号**）：

```bat
set DOUGONG_PROVIDER_BASE=https://你的地址
set DOUGONG_PROVIDER_MODEL=你的模型名
set DOUGONG_PROVIDER_KEY=你的密钥
```

Windows PowerShell（**必须加引号**）：

```powershell
$env:DOUGONG_PROVIDER_BASE = "https://你的地址"
$env:DOUGONG_PROVIDER_MODEL = "你的模型名"
$env:DOUGONG_PROVIDER_KEY = "你的密钥"
```

- [ ] 三个变量都设好
- [ ] 记录你用的是哪个终端：____________

## B3. 先自检

```bash
dougong --doctor
```

**应该看到**：6 项检查，每项标 `[通过]` / `[失败]` / `[警告]`，失败项下面有一行"下一步"。

- [ ] `运行时` 通过
- [ ] `provider 配置` 通过（**密钥只显示"已设置(N 字符)"，不显示内容**——确认这一点）
- [ ] `工作区` 通过
- [ ] `会话目录` 通过
- [ ] `provider 连通性` 通过或警告（不是失败）

实际结论行（原话）：________________________________

> **如果 `provider 连通性` 是警告**，把警告内容记下来。
> 常见原因：余额不足（402）、模型名不对、地址路径不对。

## B4. 跑一个真实任务

```bash
dougong
```

然后输入一句话，比如：

```
2+2 等于几？只回答数字。
```

- [ ] 程序启动，显示了模型名、工作区、会话文件路径
- [ ] 输入问题后收到回答
- [ ] 回答内容正确

实际回答（原话）：________________________________

## B5. 退出

输入 `/exit`（或按文档 §6.5 的方式退出）

- [ ] 正常退出，没有报错

---

# 走查之后

把这份清单里所有 `❌` 和记录的原话发回来。**每一条卡住的地方都是文档的缺口**，
之前一次走查找出 5 个，全部修了。

另外两个值得留意的点：

1. **上面任何一条"应该看到"与实际不符**，即使不影响继续，也值得记下来——
   那说明文档在骗人，比卡住更糟。
2. **有没有哪一步你觉得"这个我得猜"？** 需要猜就说明文档漏了东西。
