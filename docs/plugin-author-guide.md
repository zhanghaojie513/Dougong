# 插件作者指南

> **这份文档的目标**：读完它，不看源码就能写出一个能装上、能调试、知道什么时候会失败的插件。
>
> **它不是什么**：不是 API 参考（那看 `architecture.md` §3），不是设计文档（那看 `design.md`）。
> 遇到本文没说清的地方，是**本文的缺陷**，请按文末的方式反馈。
>
> **如果你还没写过 .NET 或没写过代码**：先看 [getting-started.md](getting-started.md)。
> 那份从"装哪个软件"讲起，走完它再回来读本文的 §1。

---

## 0. 先搞清楚你在写什么

Dougong 的插件不是"扩展点回调"，而是**运行能力的提供方**。一个插件能做三件事：

| 做什么 | 用什么 | 例子 |
|---|---|---|
| 提供一种能力给别人用 | `context.Services.Register<T>(...)` | 存储、模型连接、审批器 |
| 向别人要一种能力 | `PluginManifest` 里声明 `ServiceRequirement` | "我需要 `IAgentStore`" |
| 订阅/发布事件 | `context.Events` | 状态通知（不是授权依据） |

**插件不需要停用方法**。停用 = 释放 `PluginContext` 作用域，你登记在
`RegisterForCleanup` 里的回调会被执行。这是刻意的：少一个可以忘记实现的接口。

---

## 1. 最小可运行插件（照抄即可）

### 1.1 工程文件

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <!-- 必须：动态加载要求程序集可独立解析 -->
    <EnableDynamicLoading>true</EnableDynamicLoading>
    <IsPackable>false</IsPackable>

    <!-- 必须显式开启。Dougong 仓库根目录的 Directory.Build.props 会提供它们，
         但你的插件工程不在那个目录下，不写就会缺 usings 与可空注解，
         表现为"找不到 DateTimeOffset / CancellationToken"。 -->
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <!-- 关键：内核由宿主提供，插件不能自带一份。
         否则插件会加载到自己的 Dougong.Kernel.dll，
         跨 ALC 的类型同一性断裂，服务注册会"成功"但宿主解析不到。 -->
    <ProjectReference Include="..\..\src\Dougong.Kernel\Dougong.Kernel.csproj">
      <Private>false</Private>
      <ExcludeAssets>runtime</ExcludeAssets>
    </ProjectReference>
  </ItemGroup>
</Project>
```

> **这两个设置缺了会怎样**：不设 `EnableDynamicLoading` 时，依赖程序集不会
> 复制到输出目录，加载时报"找不到程序集"。不设 `Private=false` 时，
> 内核程序集被复制一份，插件与宿主用的是两个不同的 `IPlugin` 类型，
> 加载时报"类型不实现 IPlugin"。

### 1.2 插件类

```csharp
using Dougong.Kernel;

public sealed class MyPlugin : IPlugin
{
    // 同一个内核里 Id 必须唯一。版本用于审批绑定与诊断。
    public PluginManifest Manifest { get; } =
        PluginManifest.Create("my.plugin", "1.0.0");

    public ValueTask ActivateAsync(PluginContext context, CancellationToken cancellationToken)
    {
        // 注册一个服务：别的插件/宿主按类型解析到它。
        context.Services.Register<IClock>(new SystemClock());

        // 登记清理：作用域关闭时按登记的逆序执行。
        context.RegisterForCleanup(_ =>
        {
            // 释放你持有的非托管资源、后台任务、连接。
            return ValueTask.CompletedTask;
        });

        return ValueTask.CompletedTask;
    }
}

public interface IClock { DateTimeOffset Now { get; } }

// 上面用到的实现。真实项目里换成你自己的；这个只是让示例能独立编译。
public sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.UtcNow;
}
```

### 1.3 服务契约必须放在共享程序集里

上面那段代码有个**隐藏前提**：`IClock` 定义在插件程序集内。如果宿主也想解析它，
宿主编译期根本看不到这个类型。

**规则**：宿主要用的服务契约，必须放在一个**双方都引用、且都不复制到输出**的
独立程序集里。仓库里的 `samples/Dougong.Sample.Shared` 就是这个角色。

```xml
<!-- 契约程序集：谁引用它都不复制 -->
<ProjectReference Include="..\MyContracts\MyContracts.csproj">
  <Private>false</Private>
  <ExcludeAssets>runtime</ExcludeAssets>
</ProjectReference>
```

宿主侧还需要登记它（否则插件加载时会因"找不到契约程序集"失败）：

```csharp
using var contract = kernel.RegisterSharedContract(typeof(IClock).Assembly);
```

**不用共享程序集行不行**：可以，但那一半能力就用不了——宿主只能通过
`PluginSummary` 看到插件存在，不能按类型解析它的服务。如果你的插件只是
提供工具（走 `IAgentTool`），不需要这一步；如果它提供的是**宿主会用的服务**，
就必须共享契约。

### 1.4 宿主侧加载

```csharp
await using var kernel = new DougongKernel();
var handle = await kernel.LoadAsync("path/to/MyPlugin.dll");

// handle.State 为 Failed 时不抛异常——必须检查。
// Blocked 表示"依赖未满足"，Failed 表示"尝试过但失败了"。
if (handle.State is not PluginState.Active)
{
    // 订阅状态变化拿诊断（见 §5）
}

await kernel.UnloadAsync(handle.Id);
```

---

## 2. 常见任务

### 2.1 声明我需要另一个插件提供的服务

```csharp
public PluginManifest Manifest { get; } = PluginManifest.CreateWithDependencies(
    "my.consumer", "1.0.0",
    [new ServiceRequirement(typeof(IClock))],   // 必需
    []);                                        // 插件级依赖（一般不用）
```

没有提供方时，你的插件进入 `Blocked`（**不是 Failed**）。提供方后来出现，内核会
重新评估并激活你——不需要你做任何事。

**可选依赖**用 `new ServiceRequirement(typeof(IX), Optional: true)`。
缺失时不阻塞你，但提供方出现时仍会通知重评估。

### 2.2 在激活期间使用别的服务

激活回调里可以直接用 `context.Services.Resolve<T>()`——它包含本代次的暂存注册。

**运行期**（激活之后）不能用 `Services`，必须用 `context.Resolver`，
它返回一个**代次绑定的引用**：

```csharp
var clockRef = context.Resolver.Resolve<IClock>();
if (clockRef is not null)
{
    await clockRef.InvokeAsync((clock, ct) =>
    {
        // 在这里用 clock。委托返回前必须结束工作。
        return ValueTask.CompletedTask;
    });
}
```

**为什么不能直接把 `clock` 存字段**：提供方被停用或替换后，你手里的实例就过期了。
`InvokeAsync` 每次重新校验代次并计入在途计数——这是"停用时会等你用完"的唯一保证。

### 2.3 订阅事件

```csharp
context.Events.Subscribe<MyEvent>(e => { /* 处理 */ });
```

订阅归作用域管理，停用时自动退订。**事件不是授权依据**——不要用它来
判断"某个操作是否被允许"，那类判断走工具审批。

---

## 3. 生命周期：你的代码什么时候被调用

```
                    ┌── ActivateAsync
                    │      ↓
Registered → Activating → Active → Deactivating → Stopped
                    │                      ↓
                    └── 失败 → Failed      └── 清理失败/超时 → Failed（隔离）
```

**关键约束**

| 约束 | 后果 |
|---|---|
| `ActivateAsync` 里抛异常 | 本次注册回滚，状态 `Failed`，服务不留半提交 |
| 服务只能在激活期间注册 | 激活后调 `Services.Register` 会抛异常 |
| 清理回调必须响应取消令牌 | 否则清理超时 → 隔离，需重启宿主 |
| 清理回调不能重入内核 | 会抛 `InvalidOperationException` |
| 卸载只对动态加载的插件有效 | 静态注册的插件调 `UnloadAsync` 会抛 |

**每个阶段都有独立预算**（默认各 30 秒，宿主可配）。超时后宿主**停止等待**，
但**不会强制终止你的代码**——这是 .NET 的协作式取消，不是沙箱。

---

## 4. 五个最容易犯的错

### 4.1 忘了 `Private=false`

症状：加载时报"类型不实现 IPlugin"，或服务注册成功但宿主解析不到。
原因：插件加载了自己的一份内核程序集，`IPlugin` 是两个不同类型。
**诊断**：看 `PluginDiagnostic.Stage` 是否为 `Load`，`Hint` 会提示检查共享契约。

### 4.2 把服务实例存成字段

症状：提供方被替换后，你的插件还在用旧实例；停用时排空等不到。
**正确做法**：存 `IServiceReference<T>`，用时 `InvokeAsync`。

### 4.3 清理回调不同步取消

症状：停用要等满 `CleanupTimeout` 才结束，之后被隔离。
原因：清理里有 `Thread.Sleep`、`.Result`、或未取消的后台循环。

**注意一个反直觉点**：同步阻塞的清理回调会让**整个超时预算失效**——
`Task.Run` 之前的部分在调用线程上同步执行。这个问题在 0.1.0-alpha.1 之后
已修复，但你自己写回调时仍应避免同步阻塞。

### 4.4 在激活里启动未等待的后台任务

症状：停用后后台任务仍在跑，访问已释放的服务。
**正确做法**：用 `context.Lifetime` 关联，并在 `RegisterForCleanup` 里等它结束。

### 4.5 依赖声明写成插件级 `DependsOn` 而不是服务需求

`DependsOn` 只检查"那个插件是否 Active"，不解决**服务类型**的解析。
绝大多数情况你要的是 `ServiceRequirement`。

---

## 5. 出问题时怎么定位

内核的所有失败都带三段信息：**哪个插件、哪个阶段、下一步做什么**。

```csharp
using var subscription = kernel.SubscribeStateChanges(change =>
{
    if (change.To is not (PluginState.Failed or PluginState.Blocked)) return;

    var d = change.Diagnostic;
    Console.Error.WriteLine(
        $"[{change.PluginId}] {change.To} [{d?.Stage}] {change.Reason}");
    if (d?.Hint is { Length: > 0 } hint)
        Console.Error.WriteLine($"  建议：{hint}");
});
```

阶段与建议的对应：

| Stage | 意味着 | 建议指向 |
|---|---|---|
| `Load` | 程序集/契约解析失败 | `EnableDynamicLoading`、`RegisterSharedContract`、依赖是否随包发布 |
| `Activate` | `ActivateAsync` 抛了 | 看异常本身；跨程序集类型问题回到 Load |
| `Drain` | 在途调用没归零 | 忽略了取消令牌或启动了未等待的后台工作 |
| `Cleanup` | 清理回调超时或抛异常 | 检查是否可能永久阻塞 |
| `Unload` | 卸前复核不通过 | 只有动态插件可卸载；检查是否有外部强引用 |

**`Blocked` 不是失败**：它的 `Reason` 会点名缺哪个服务（例如
`未找到服务 IClock()。`）。看到它就说明你声明的依赖没人提供。

---

## 6. 写完之后自检

1. 插件能加载到 `Active` 吗？不能的话状态是 `Blocked` 还是 `Failed`？
2. 卸载后 `kernel.GetPluginsAsync()` 里还有它吗？（不该有）
3. 反复装卸 3 次，每次都正常吗？
4. 停用时清理回调执行了吗？有没有 `Thread.Sleep` 之类的同步阻塞？
5. 如果你的插件提供了服务：把另一个实现换上去，你的消费者还能工作吗？

---

## 7. 这份文档是怎么验证的

写完后按本文走了一遍，**不是读一遍判断"看起来对"**，而是真的建了工程、
真的编译、真的让内核加载。过程中发现并修掉了三个问题：

| 发现 | 修复 |
|---|---|
| 照抄 §1.1 的 csproj **编译不过**——缺 `ImplicitUsings`/`Nullable`，报"找不到 DateTimeOffset"。原因是 Dougong 仓库根的 `Directory.Build.props` 提供了这两个属性，但外部插件工程不在那个目录下 | 在 §1.1 显式写出并说明原因 |
| 服务契约定义在插件程序集里时，宿主无法按类型解析 | 补 §1.3 |
| 未说明"什么时候不需要共享契约" | 在 §1.3 末尾说明：只提供工具时不需要 |
| §1.2 的代码调用了 `new SystemClock()`，但全文没有定义这个类型——照抄必报 `CS0246: 未能找到类型或命名空间名"SystemClock"` | 在 §1.2 代码块末尾补上 `SystemClock` 的实现 |

验证结果：按修正后的指南建工程 → 编译通过 → 内核加载到 `Active` →
`UnloadAsync` 成功。

**这仍不是真人验证。** 我是作者，看得懂自己写的东西。判据 1 要求的是
"没参与开发的人能完成"，这一点只有真人能证。本文的自我审查只能排除
"照抄编译不过"这类硬错误。

## 8. 已知的文档缺口

以下几处仍需要看源码才能回答，如实列出：

1. **服务冲突怎么解**：同一服务有多个提供方时靠 `Register(priority:)` 选，
   但"什么时候用优先级、什么时候用 `ProviderId` 显式指定"文中没说清。
2. **`KernelStateView` 与 `SubscribeStateChanges` 该用哪个**：前者是
   快照+增量流（适合 UI），后者是同步回调（适合日志）。本文只用后者。
3. **写工具插件**：本文只讲内核插件。工具契约（`IAgentTool`、
   `ToolExecutionPlan` 的 `Reads`/`Writes`）属于 Agent 层，需要单独一篇。
4. **`architecture.md` §3 与实现不一致**：那里写的 `PluginContext.Config` /
   `Logger` / `Handle` 在实现中**不存在**。以本文 §3 的成员表为准。
