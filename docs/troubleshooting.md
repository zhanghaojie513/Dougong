# 故障排查

> **先做一件事**：如果你已经能启动 CLI，运行
>
> ```bash
> dougong --doctor
> ```
>
> 它会逐项检查运行时、provider 配置、工作区、会话目录，并给每一项失败指出**下一步**。
> 下面这份表是它覆盖不到的情况。

---

## 一、这个文档的写法

每一条都来自**实际遇到过的问题**，不是推测。格式统一为：

- **现象**：你在屏幕上看到什么
- **原因**：为什么会这样
- **做法**：怎么解决
- **这一条是怎么发现的**：如果它原来是缺陷而不是配置问题

最后一条很重要：区分"项目有缺陷"和"你的环境有问题"，决定了你该改哪里。

---

## 二、构建与测试

### 2.1 `MSB3101`：`AssemblyReference.cache already exists`

**现象**

```
error MSB3101: Could not write state file "obj\...\AssemblyReference.cache".
```

**原因**：残留的 `obj` 目录与新的构建抢写同一个文件。多发生在从别人的工作副本、
或一个已经构建过的目录开始，而不是全新 clone。

**做法**

```bash
dotnet build-server shutdown
dotnet build Dougong.slnx -c Release
```

**这一条怎么发现的**：按 [getting-started.md](getting-started.md) 走查时遇到过。
第一次构建报 1 个警告，清理后是 0 警告 0 错误。文档最初没写这条。

### 2.2 `System.OutOfMemoryException`（出现在 MSBuild 内部）

**现象**

```
System.OutOfMemoryException: Exception of type 'System.OutOfMemoryException' was thrown.
   at System.Collections.Concurrent.ConcurrentDictionary...
```

**原因**：宿主内存耗尽，**不是代码问题**。并行构建多个项目时容易触发。

**做法**：先 `dotnet build-server shutdown`，关掉一些程序，再重试。
反复出现就改用串行构建：

```bash
dotnet build Dougong.slnx -c Release -m:1
```

**这一条怎么发现的**：全量回归时偶发。当时误以为是某个测试有 bug，
查下去才发现是宿主的 `fork` 也失败了。

### 2.3 只跑了部分测试 / `Zero tests ran`

**现象**：输出 `Zero tests ran` 或握手失败。

**原因**：本项目用 xUnit v3 + Microsoft.Testing.Platform，**不是 VSTest**。

**做法**：用

```bash
dotnet test Dougong.slnx -c Release --no-build --output Detailed
```

不要用 VSTest 的 `--logger`，在 MTP 下表现为握手失败。

---

## 三、运行与配置

### 3.0 `未知参数：xxx`

**现象**：

```
未知参数：--docter

可用参数：--help --version --doctor --report --acp --list
          --resume <会话>  --branch <会话>
```

退出码 2，不进入对话。

**原因**：参数拼错了。上面的例子把 `--doctor` 写成了 `--docter`。

**这条检查是 2026-10-28 加的，修的是一个静默失败**：在此之前所有参数都用
"包含就生效"的方式判断，拼错的参数被**忽略**，程序直接进入交互对话——
用户以为在跑自检，实际开了一个会话并写了日志文件。现在会明确报错。

**做法**：照上面列出的可用参数改正。要看完整说明跑 `dougong --help`。

### 3.1 `缺少 provider 配置`

**现象**：`缺少 provider 配置：没有任何可用的 provider`

**原因**：既没有配置文件，也没有设全三个环境变量。

**做法**：先跑 `dougong --doctor`，它会把缺什么和下一步一起打出来。

**推荐用配置文件**（可以配多个 provider）：`~/.dougong/config.toml`

```toml
default_provider = "deepseek"

[providers.deepseek]
base_url = "https://api.deepseek.com"
model    = "deepseek-chat"
key_env  = "DEEPSEEK_API_KEY"    # 变量名，不是密钥本身
```

```bash
export DEEPSEEK_API_KEY='<密钥>'   # 密钥只进环境变量
```

**或者全用环境变量**（优先级最高，会覆盖配置文件的选中项）：

```bash
export DOUGONG_PROVIDER_BASE=https://api.example.com/v1
export DOUGONG_PROVIDER_MODEL=<模型名>
export DOUGONG_PROVIDER_KEY=<密钥>
# 可选：Anthropic 兼容端点
# export DOUGONG_PROVIDER_PROTOCOL=anthropic
```

> 密钥只从环境变量读取，本项目的任何文件都不会保存它。
> 配置文件里只有密钥的**变量名**（`key_env`），写成密钥值会被硬拒。

**配置写错了会怎么报**：`--doctor` 会点名到具体键，不静默忽略。

| 现象 | 原因 |
|---|---|
| `key_env='sk-...' 看起来是一个密钥值而不是变量名` | 把密钥写进配置了，改成变量名 |
| `有未知的键 'api_key'` | 字段名拼错，支持 `base_url` / `wire_api` / `key_env` / `model` / `messages_path` |
| `base_url 不是合法的 http(s) 地址` | 缺 `https://` 前缀 |
| `选中的 provider 'x' 不存在` | `default_provider` 指向了没有定义的段 |
| `provider 'x' 没有配置 model` | 内置预设不带模型名，必须自己填 |

### 3.2 `找不到 dougong.cli`（安装 / 升级时）

**现象**：

```text
dotnet tool update --global Dougong.Cli
在 NuGet 源 https://api.nuget.org/v3/index.json 中找不到 dougong.cli。
```

**原因**：当前所有版本都带预发布标签（`-alpha.N`），而
`dotnet tool update`/`install` 默认只在**稳定版**里挑最新版。
稳定版列表为空，于是报"找不到"——**这个报错是误导**，包确实在 nuget.org 上。

**做法**：加 `--prerelease`。

```bash
dotnet tool update --global Dougong.Cli --prerelease
dougong --version
```

**为什么 `search` 也搜不到**：`dotnet tool search Dougong` 返回空。
实测 2026-10-30：nuget.org 的 `flatcontainer` 与 `registration` 索引都能列出全部版本
（返回 200），但搜索 API 的 `totalHits` 是 0。**不影响安装与升级**——那两个走
registration 索引。搜索索引的收录条件没有验证到，不猜。

**要装指定版本**（降级需额外 `--allow-downgrade`）：

```bash
dotnet tool update --global Dougong.Cli --version 0.1.2-alpha.1 --allow-downgrade
```

### 3.3 `401` / `403`：密钥无效或无权限

**现象**：`provider 返回 401：{"error":{"message":"Authentication Fails ..."}}`

**原因**：密钥失效、复制时多了空格、或者用了另一套订阅的密钥。**不是代码问题。**

**做法**

1. `dougong --doctor` 看 `provider 连通性` 一项的具体错误
2. 换一个已知可用的密钥用 `curl` 直接验证端点
3. 注意有些厂商有多个端点（普通/订阅版），**密钥与端点必须配对**

**这一条怎么发现的**：验收 1 第一次跑出"两组都是 0%"，看起来像"记忆无效"。
实际是密钥已失效——装置把 401 当成了"未遵循"。这是个假结论，已修：
现在请求失败会明确报错，不计入统计。

### 3.4 `429`：限流或余额不足

**现象**：`HTTP 429`，或 `已达到速率限制`。

**原因**：调用太密集，或账户余额不足。某些 provider 的免费额度耗尽也返回 429。

**做法**：等一会儿再试；或换一个 provider。

**这一条怎么发现的**：并行跑多家 provider 的验收时，同一家的两个协议同时跑会触发限流。
现在的矩阵脚本按 provider 分组，组内串行、组间并行。

### 3.5 连接被拒绝 / 超时

**现象**：`Failed to connect`、`Connection was reset`、`Recv failure`。

**原因**：网络问题，或端点地址写错。国内访问 GitHub 也经常这样。

**做法**

- 确认 `DOUGONG_PROVIDER_BASE` 可达：`curl -I <base>`
- **git 相关操作**（clone/push）如果反复失败，是默认的低速超时太短：

```bash
GIT_HTTP_LOW_SPEED_LIMIT=1 GIT_HTTP_LOW_SPEED_TIME=60 git fetch origin
```

### 3.6 会话目录不可写

**现象**：`无法创建或写入：<路径>`

**原因**：会话日志是**提交屏障**的落点。它不可写时，任何操作都无法继续——
这不是降级，是硬失败。

**做法**：`DOUGONG_SESSION_DIR` 指向一个可写目录。

---

## 四、插件加载

### 4.1 `类型不实现 IPlugin`

**现象**：插件加载失败，诊断里 `Stage=Load`。

**原因**：插件自带了一份 `Dougong.Kernel.dll`。跨 ALC 的类型同一性断裂，
插件与宿主眼里的 `IPlugin` 是两个不同类型。

**做法**：插件工程里引用内核必须这样写：

```xml
<ProjectReference Include="..\..\src\Dougong.Kernel\Dougong.Kernel.csproj">
  <Private>false</Private>
  <ExcludeAssets>runtime</ExcludeAssets>
</ProjectReference>
```

**这一条怎么发现的**：`docs/plugin-author-guide.md` §4.1 记录了它；
按指南走查时确认过「不设 `Private=false` 会报这个错」。

### 4.2 状态 `Blocked` 而不是 `Failed`

**现象**：插件停在被阻塞状态，原因写着"未找到服务 X"。

**原因**：`Blocked` **不是失败**——它表示声明的服务需求没人提供。

**做法**：检查 `ServiceRequirement` 里的类型，确认有插件提供它；
提供方后来出现时内核会自动重新评估并激活，不需要重启。

### 4.3 停用要等满 30 秒才结束

**现象**：清理回调执行完之前停用就卡住，最后超时被隔离。

**原因**：清理回调里有 `Thread.Sleep`、`.Result`、或没响应取消令牌的后台循环。

**做法**：清理回调必须响应 `CancellationToken`。

**这一条怎么发现的**：这是**真实缺陷**，不是使用问题。
`CompleteDisposalAsync` 曾直接调用 `DisposeCoreAsync`，而后者在第一个 `await`
之前是同步执行的——同步阻塞的回调会让 `CleanupTimeout` 完全失效。
实测 `CleanupTimeout=150ms` 时停用用了 3028ms。改 `Task.Run` 启动后降到 180ms。

---

## 五、如何拿到更多信息

### 5.1 分级日志

```bash
DOUGONG_LOG=debug dougong
```

| 值 | 输出 |
|---|---|
| `error` | 只有错误 |
| `warn`（默认） | 错误 + 警告，与不设此变量时**完全一致** |
| `info` | 额外输出启动事实：会话、工作区、存储等级、内核接线、压缩、MCP 数量 |
| `debug` | 额外输出每轮开始/结束与工具派发细节 |

日志一律写 stderr。**不会打印密钥**，也不会打印用户输入的原文（只记长度）。

无法识别的值会明确警告并按 `warn` 运行，不会静默忽略。

### 5.2 内核插件的失败诊断

插件失败时 stderr 会有三段信息：哪个插件、哪个阶段、下一步做什么。

```
[kernel] 插件 'my.plugin' 进入 Failed [Activate]：...
  详情：...
  建议：检查 ActivateAsync 抛出的异常...
```

阶段与建议的对应见 [plugin-author-guide.md](plugin-author-guide.md) §5。

### 5.3 报告问题时要带什么

**最快的方式**：直接跑

```bash
dougong --report
```

它会输出一份可以整段粘贴的诊断包：版本与 commit、平台、provider 配置形态、
最近会话的元信息（文件名/大小/记录数）、其它开关状态。

**这份输出已确认不含密钥与用户内容**——密钥只显示「是否设置 / 长度 /
是否含首尾空白」，会话只给元信息不读内容。所以可以直接贴到公开 issue。

配合使用：

1. `dougong --report` 的完整输出
2. `dougong --doctor` 的输出（逐项检查与建议）
3. `DOUGONG_LOG=info` 的启动几行
4. 完整报错文本，从第一行到最后一行
5. 你运行的确切命令，以及当时的工作目录

---

## 六、这份文档的边界

它只覆盖**实际遇到过**的问题。没遇到的写不上来——编出来的条目看起来更全，
但会给出错误的排查方向。

如果你遇到的问题不在上面，那是本文的缺口。按 §5.3 收集信息后提出来，
比"再想想还有什么可能"有用得多。
