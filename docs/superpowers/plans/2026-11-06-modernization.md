# AliDDNSNet Modernization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 完成最新稳定依赖升级、轻量结构整理、Issue #52/#11 修复，以及同时适合交互终端和后台运行的 CLI。

**Architecture:** 单应用项目、单测试项目；System.CommandLine 显式绑定命令，Spectre.Console 仅显示。配置、IP 获取、AliDNS 协议、更新决策与生命周期有明确边界；使用内置异步、取消及 JSON 源生成，不增加通用宿主或云 SDK。

**Tech Stack:** .NET 10 LTS、System.CommandLine、Spectre.Console、System.Text.Json、HttpClient、xUnit v3 最新稳定代际、VSTest、Coverlet collector。

**Spec:** `docs/superpowers/specs/2026-11-05-modernization-design.md`（用户已批准）。

**执行状态：** 用户已确认本计划并完成核心实现与分域复审。下文保留原实施步骤，实际结果与环境限制记录在设计文档第 13 节；整分支审阅和本地分支交付仍由主会话收尾。

## Global Constraints

- 运行基线 **.NET 10 LTS**；保留一个应用项目和一个测试项目。
- “最新”指最新稳定版，不采用 alpha、beta、preview 或 RC。
- 保留 `AliCloudDynamicDNS`、`-f / --file`、`-i / --interval`；周期单位秒，0 表示单次，非零最小 30 秒。
- 默认读取当前工作目录的 `settings.json`；保留注释、尾随逗号、大小写不敏感 JSON 属性，以及原 TTL 字段 `SubDomains[].Interval`。
- A 来源：显式 `PublicIpServer`，否则 `https://api.ipify.org`；AAAA 来源：显式 `PublicIpv6Server`、显式 `PublicIpServer`、`https://api6.ipify.org`，按此顺序。
- 只更新 A/AAAA；未知/空 Type 在写入前作为配置错误终止。匹配 RR/Type 忽略大小写；多条同键记录报歧义，缺失记录不自动创建。
- 保留 `ip/config/update/cls/exit`；无 TTY 不要求输入，EOF 不空转，定时/手动更新不重叠。
- 成功退出 0，参数/配置/更新失败非零，取消 130；周期中的单轮失败不停止下一正常周期。`exit` 是正常退出，返回 0。
- 保留裁剪、自包含、单文件发布；不启用 Native AOT，不压制裁剪警告掩盖缺陷。
- 保留 win-x64、linux-x64、osx-x64、linux-arm、linux-arm64；Docker 保留 linux/amd64、linux/arm64。
- 不打印密钥、完整 AccessId 或签名 URL；不把真实配置送进分发包和 Docker 构建上下文。
- 不新增重试框架、遥测、热重载、插件、多 DNS 厂商、自动建删记录或解析线路配置。
- 不修改线上 DNS 完成测试，不使用用户真实凭据；不得把交叉发布或测试替身描述为目标平台/线上实测。
- 代码变更由编码子代理完成，主会话拥有合同、集成和验收；所有子代理跳过 build/test/lint/formatter，检查集中由主会话在变更静止的检查点运行。
- 测试用 FakeTimeProvider 和 TaskCompletionSource 控制顺序；不依赖任意 Sleep、真实公网或跨用例共享的可变环境。先等待“操作已进入”的信号，再断言阻塞/并发状态。

## Review Focus

1. 旧 AAAA-only 自定义 `PublicIpServer` 不能被新默认值覆盖；双栈覆盖优先级必须正确——Task 2 的来源选择行为测试。
2. IPv6 的不同文字表示与错误地址族不能导致误写——Task 3 的实际更新决策测试。
3. 阻塞网络、待输入、stdin EOF 时取消仍应结束，EOF 不能触发忙等——Task 4 的生命周期测试与实际进程检查。
4. 29 秒边界、30 秒和较大 uint 周期不得出现 int 毫秒溢出或提前更新——Task 4 的参数冒烟与假时间测试。
5. API/域名文本中的方括号不能被解释为 markup，错误响应也不能泄露凭据——Task 4 的输出安全行为测试及 Task 5 的产物检查。

---

## 文件与接口归属

保留现有 `AliCloudDynamicDNS` 根命名空间，新增目录与命名空间一致。内部实现辅助类型可进一步局部分拆，但不得另建生产项目。

| 归属 | 修改/新增 | 责任 |
| --- | --- | --- |
| 主会话 | 本设计/计划、临时复现与验收工具 | 需求、基线、集成、验证、最后同步 README/示例 |
| DNS 实现者（Task 2–3） | `Configuration/ConfigurationModel.cs`、`SubDomainRecord.cs`、新 `ConfigurationLoader.cs`、`ConfigurationJsonContext.cs` | 配置模型、加载及源生成 |
| DNS 实现者 | 新 `Networking/PublicIpProvider.cs` | 地址源选择、HTTP 获取及地址族检查 |
| DNS 实现者 | 新 `AliCloud/AliDnsClient.cs`、`AliCloud/Models/AliDnsResponses.cs`；修改 `AliCloudRecordModel.cs`、`ApiRequest/ApiRequestParameters.cs` | HTTP、签名、类型化响应、分页 |
| DNS 实现者 | 新 `Updating/DnsUpdater.cs`、`Updating/UpdateResult.cs` | 按目标生成 Updated/Unchanged/Failed 结果 |
| DNS 实现者 | 新 `ConfigurationTests.cs`、`PublicIpProviderTests.cs`、`AliDnsClientTests.cs`、`DnsUpdaterTests.cs`（测试项目内） | 对应不变量及协议行为 |
| CLI 实现者（Task 4） | `Program.cs`；新 `Cli/RunOptions.cs`、`Cli/CliApplication.cs`、`Cli/ConsolePresenter.cs`、`Runtime/ApplicationRunner.cs` | 显式入口、生命周期、显示 |
| CLI 实现者 | 新 `ApplicationRunnerTests.cs`、`ConsolePresenterTests.cs`（测试项目内） | 并发、等待、退出和输出安全 |
| 发布实现者（Task 5） | 两个 csproj、根 `global.json`、Dockerfile、应用目录 `.dockerignore`、`scripts/build.sh`、两个既有 workflow | 依赖、构建、发布和 CI |

退役归属：DNS 实现者删除 `Configuration/ConfigurationHelper.cs`、`Utility/NetworkHelper.cs`、`AliCloud/ApiRequest/ApiRequestTool.cs`、旧 `NetworkHelperTests.cs`；CLI 实现者删除 `Threading/StrongTimer.cs`、`Utility/ConsoleHelper.cs`。替代代码及调用方一起切换，不交付兼容别名/空壳。现有未知用户改动不覆盖。

### 跨任务合同（命名与签名固定）

这些是组件合同，不是要求先提交占位实现。接口与实现可在同一职责文件内；仅三个被消费的行为边界需要接口。

```csharp
// Configuration；模型保留既有 JSON 字段，新增 string? PublicIpv6Server。
public static Task<ConfigurationModel> ConfigurationLoader.LoadAsync(string filePath, CancellationToken cancellationToken);
public sealed record SubDomainRecord(string Type, string SubDomain, int Interval);

// Networking；具体 PublicIpProvider(HttpClient httpClient, ConfigurationModel configuration)。
public interface IPublicIpProvider {
    Task<IPAddress> GetAsync(AddressFamily addressFamily, CancellationToken cancellationToken);
}

// AliCloud；具体 AliDnsClient(HttpClient httpClient, string accessId, string accessKey)。
public interface IAliDnsClient {
    Task<IReadOnlyList<AliCloudRecordModel>> GetRecordsAsync(string mainDomain, CancellationToken cancellationToken);
    Task UpdateRecordAsync(AliCloudRecordModel record, IPAddress address, SubDomainRecord target, CancellationToken cancellationToken);
}
// AliCloud.Models：沿用已有类型名，增加实际协议需要的类型和线路。
public sealed record AliCloudRecordModel(string SubName, string RecordId, string Value, string Type, string Line);

// Updating；具体 DnsUpdater(ConfigurationModel configuration, IAliDnsClient dnsClient, IPublicIpProvider ipProvider)。
public interface IDnsUpdater {
    Task<UpdateResult> UpdateAsync(CancellationToken cancellationToken);
}
public enum RecordUpdateStatus { Updated, Unchanged, Failed }
public sealed record RecordUpdateResult(string SubDomain, string Type, string? RecordId, string? OldValue, string? NewValue, RecordUpdateStatus Status, string? Error);
public sealed record UpdateResult(IReadOnlyList<RecordUpdateResult> Records, string? Error = null) {
    public bool IsSuccess { get; } // 由全局 Error 和逐目标状态计算；不是可独立赋值的状态。
}

// Cli
public sealed record RunOptions(string? FilePath, uint IntervalSeconds);
public static RootCommand CliApplication.CreateRootCommand(Func<RunOptions, CancellationToken, Task<int>> execute);
```

`ConfigurationModel` 的 AccessId、AccessKey、MainDomain 为字符串，来源字段为 nullable 字符串，SubDomains 为 `IReadOnlyList<SubDomainRecord>`；对外不保留可变静态配置。未知 JSON 属性继续忽略。配置加载失败使用带安全字段/路径信息的 `InvalidDataException`（I/O 异常可向入口传播），不包含原始密钥值。

`ConsolePresenter(IAnsiConsole console, bool interactive, ConfigurationModel? configuration = null)` 提供 `WriteConfiguration(ConfigurationModel,uint)`、`WriteIp(AddressFamily,IPAddress)`、`WriteUpdateResult(UpdateResult,bool includeUnchanged)`、`WriteError(string)`、`WriteCommandHelp()`、`Clear()`。`ApplicationRunner(ConfigurationModel,IDnsUpdater,IPublicIpProvider,ConsolePresenter,TextReader,bool interactive,TimeProvider? timeProvider = null)` 提供 `Task<int> RunAsync(uint intervalSeconds,CancellationToken cancellationToken)`。只有运行器持有定时/手动更新的串行边界，更新服务不再重复加同一层锁。

## Task 1：可执行基线、版本与工作区前提

**Files:** 只读当前应用/测试项目、已定位的调用点及旧发布包；SDK 和诊断工具放在工作区之外。此任务不编写产品代码。

**Consumes:** 已批准设计和当前源码。  
**Produces:** 实际使用的 SDK 路径、稳定版本清单、两个问题的可核对基线，以及后续任务使用的独立工作区。

- [ ] **Step 1：执行前读取 using-git-worktrees，选择隔离工作区。** 保留当前设计/计划和用户工作；不自动推送、发 Release 或改系统级配置。所有工作者使用同一明确工作区及上述非重叠归属。
- [ ] **Step 2：安装局部 .NET 10 SDK。** 官方元数据当前给出 SDK `10.0.403`、运行时 `10.0.14`；下载对应系统归档，核对元数据 SHA-512，然后运行该路径的 `dotnet --info`。不能把当前机器的 .NET 9 当成验证 .NET 10 的替代品。
- [ ] **Step 3：冻结依赖选择。** 下表为已核对的当前稳定版本；实际安装前若官方已有更新稳定补丁，使用更新版并同步全体合同。来源均是 NuGet flat-container 索引/包元数据及官方 .NET release metadata。

| 用途 | 包/SDK | 版本 |
| --- | --- | --- |
| SDK | .NET SDK | 10.0.403 |
| 生产 | System.CommandLine | 2.0.14 |
| 生产 | Spectre.Console | 0.57.2 |
| 测试 | xunit.v3.mtp-off | 4.0.2 |
| 测试 | xunit.runner.visualstudio | 4.0.1 |
| 测试 | Microsoft.NET.Test.Sdk | 18.11.0 |
| 测试 | coverlet.collector | 10.1.0 |
| 测试时钟 | Microsoft.Extensions.TimeProvider.Testing | 10.11.0 |

选择 `xunit.v3.mtp-off` 是同代框架的官方 VSTest 变体，不是降级。xUnit 4 默认 MTP v2 在 .NET 10 下不能沿用 VSTest target；本项目保留 VSTest，避免同时迁移第二套测试运行入口。删除旧 xunit、Shouldly、NSubstitute、coverlet.msbuild；断言用 xUnit 自带 API，HTTP 在传输边界控制，时钟使用 FakeTimeProvider。

- [ ] **Step 4：建立修复前证据。** #52 优先检查现有 v0.0.5 发布包，在没有真实配置/凭据的隔离目录观察入口；必要时对同一旧源码比较受控裁剪/不裁剪发布。#11 用受控响应覆盖同名 A/AAAA、缺失记录和 IPv4 正文进入 AAAA 的路径。基线使用真实旧产物或实际旧代码，不复制一段自写的“旧逻辑”冒充复现。已知报错作为事实，不为怀疑报告而反复运行。
- [ ] **Step 5：记清基线边界。** 当前 `--no-restore --help` 已因资产缺失失败；缺包/编译错误不算目标回归的红灯。若旧源码不能直接执行，隔离修复其构建前提或使用旧产物；保留无法识别原始二进制的事实，不把新代码与旧发行来源混为一谈。

## Task 2：配置加载与按地址族获取公网 IP

**Files:** 文件归属表中 Configuration、Networking、ConfigurationTests、PublicIpProviderTests；删除旧配置全局状态及 NetworkHelper 测试。

**Consumes:** HttpClient、系统 JSON、Task 1 的目标框架。  
**Produces:** `ConfigurationLoader.LoadAsync`、配置模型、`IPublicIpProvider`/`PublicIpProvider`，按上述合同供 Task 3/4 使用。

- [ ] **Step 1：先写行为回归。** `ConfigurationTests.ReadsCommentedLegacyConfiguration` 使用带注释、尾随逗号和小写属性名的 JSON；`RejectsUnsupportedTypeBeforeUse` 使用 MX；`PublicIpProviderTests.PreservesExplicitLegacyIpv6Source` 使用只有 AAAA 的旧配置；`Ipv6OverrideWinsOverSharedSource` 同时提供两字段；`RejectsWrongFamily` 使用 IPv4 正文作为 AAAA 来源；`RejectsHttpErrorBody` 使用 HTTP 503/HTML。测试使用临时配置文件与受控 HttpMessageHandler，不访问公网。

```csharp
// 核心断言：A/AAAA 的实际来源响应不同，断言结果而不只检查转发参数。
Assert.Equal("AAAA", configuration.SubDomains[0].Type);
Assert.Equal(IPAddress.Parse("2001:db8::11"), legacyIpv6Address);
Assert.Equal(IPAddress.Parse("2001:db8::22"), overriddenIpv6Address);
await Assert.ThrowsAsync<InvalidDataException>(() => loadUnsupportedType);
await Assert.ThrowsAsync<InvalidDataException>(() => getWrongFamily);
```

- [ ] **Step 2：主会话取得对应失败证据。** 回归可以在基线兼容入口上执行，再在迁移时保留相同外部断言；新 API 尚不存在造成的编译错误不算行为失败。子代理不运行检查工具。
- [ ] **Step 3：实现配置加载。** 使用源生成 `ConfigurationJsonContext`，保留设计中的 JSON 读取能力；校验必要凭据/域名、非空目标和 A/AAAA 类型，来源 URI 只允许 HTTP/HTTPS。错误指出字段/索引但不含密钥原文。不把来源缺省值提前填进模型，不改变 `@`、`*`、多级 RR 的含义。
- [ ] **Step 4：实现 PublicIpProvider。** 依合同选择来源、复用传入的 HttpClient、检查状态码、Trim 首尾空白、IPAddress.TryParse 后校验 AddressFamily，返回类型化地址。取消继续传播；不返回空串伪装成功，不对错误地址族自动换源。
- [ ] **Step 5：迁移后检查。** 主会话运行 `dotnet test AliCloudDynamicDNS.Test/AliCloudDynamicDNS.Test.csproj --filter "FullyQualifiedName~ConfigurationTests|FullyQualifiedName~PublicIpProviderTests"`；预期用例全通过，未知类型/错误地址族不产生可写入地址。

## Task 3：AliDNS 协议与完整双栈更新

**Files:** 文件归属表中 AliCloud、Updating、AliDnsClientTests、DnsUpdaterTests；替代并删除旧 ApiRequestTool。

**Consumes:** Task 2 的 ConfigurationModel、SubDomainRecord、IPublicIpProvider。  
**Produces:** `IAliDnsClient`/`AliDnsClient`、`IDnsUpdater`/`DnsUpdater` 与结果类型。

- [ ] **Step 1：先写协议与业务回归。** 使用真实 AliDnsClient/DnsUpdater 加受控 HTTP 响应；测试替身只替代网络，不替代待验证的解析、匹配或更新决策。`AliDnsClientTests.ReadsLaterPages` 返回两页；`PreservesLineWhenUpdating` 返回非 default Line；`ReportsApiErrorWithoutSignedUrl` 返回结构完整的阿里云错误；`DnsUpdaterTests.MatchesSameNameByType` 配置 home，同时给远程 HOME/A 与 home/AAAA；`FetchesOnlyConfiguredFamilyOncePerCycle` 分别以 A-only、AAAA-only 的两个目标验证实际 HTTP 来源每轮只请求一次且没有访问另一族；另覆盖 missing、ambiguous、equivalent IPv6、IPv6 获取失败但 A 成功，以及第二条更新失败。

```csharp
// 独立协议期望；捕获的是生产 HTTP 请求编码后的字段，不是 mock 的回显。
Assert.Equal("record-v4", ipv4Update["RecordId"]);
Assert.Equal("192.0.2.10", ipv4Update["Value"]);
Assert.Equal("record-v6", ipv6Update["RecordId"]);
Assert.Equal("AAAA", ipv6Update["Type"]);
Assert.Equal("2001:db8::10", ipv6Update["Value"]);
Assert.Equal("telecom", ipv6Update["Line"]);
Assert.Equal(RecordUpdateStatus.Unchanged, equivalentIpv6.Status);
Assert.False(partialFailure.IsSuccess);
```

同一红灯阶段加入 `AliDnsClientTests.SignatureMatchesIndependentFixture`。固定 AccessId=`test-id`、AccessKey=`test-secret`、时间 `2026-11-06T00:00:00Z`、nonce=`fixed-nonce`；操作参数为 Action=`UpdateDomainRecord`、RecordId=`record-v6`、RR=`主 机+~`、Type=`AAAA`、Value=`2001:db8::10`、TTL=`600`、Line=`telecom`。协议固定 Format=`json`、SignatureMethod=`HMAC-SHA1`、SignatureVersion=`1.0`、Version=`2015-01-09`。

```csharp
// 期望已由独立 Python 标准库 hmac/hashlib/urllib 推导，未调用产品签名器。
Assert.Equal("PKwq4g+WEQUaRlUoPNm6ZuYqIOE=", decodedSignature);
Assert.Contains("RR=%E4%B8%BB%20%E6%9C%BA%2B~", signedQuery);
```

- [ ] **Step 2：主会话先核对失败分支。** #11 的红灯必须是错记录、错地址族、缺失目标崩溃等真实行为，不是人为损坏配置或测试替身本身。
- [ ] **Step 3：实现签名与类型化 HTTP。** `ApiRequestParameters` 改为无全局状态的静态签名工具，提供 `internal static string CreateQuery(string accessId,string accessKey,IReadOnlyDictionary<string,string> parameters,DateTimeOffset timestamp,string nonce)`。继续 AliDNS RPC 2015-01-09、GET、HMAC-SHA1、Ordinal 参数排序及 RFC3986 编码；数字和时间使用 invariant 格式。生产请求地址固定 HTTPS `alidns.aliyuncs.com`，不增加测试用公开 endpoint 配置。
- [ ] **Step 4：实现 DTO 源生成与分页。** `AliDnsResponses.cs` 定义列表（TotalCount/PageNumber/PageSize/DomainRecords.Record）、更新（RecordId）、错误（Code/Message/RequestId）及源生成上下文；记录包含 Type、Line。每页明确 PageNumber（从 1 开始）、PageSize=500，读完 TotalCount 所指范围；空/无进展页面不能无限循环或当成已完整读取。API/HTTP 错误产生可诊断异常，不包含签名 URL。响应 RecordId 缺失或不匹配视为失败。
- [ ] **Step 5：实现更新决策。** 每族每轮只获取一次；按 RR+Type 忽略大小写匹配；缺失/歧义产生 Failed，不写入；按 IPAddress 比较；更新保留 Line 与配置 TTL。地址族和单目标错误分别保留，另一族/其他目标继续；取消始终传播。列表获取失败用 UpdateResult.Error 表达，不能伪造每条成功。
- [ ] **Step 6：迁移后检查。** 主会话运行 `dotnet test AliCloudDynamicDNS.Test/AliCloudDynamicDNS.Test.csproj --filter "FullyQualifiedName~AliDnsClientTests|FullyQualifiedName~DnsUpdaterTests"`；预期所有状态、分页后的目标及实际更新字段正确，错误路径没有 DNS 写入。

## Task 4：显式 CLI、可取消运行器与 Spectre 显示

**Files:** `Program.cs`、新 Cli/Runtime 文件、ApplicationRunnerTests、ConsolePresenterTests；删除 StrongTimer、ConsoleHelper。

**Consumes:** Task 2/3 的公共合同与结果；System.CommandLine、Spectre.Console。  
**Produces:** `Program.Main(string[]) : Task<int>`、CliApplication、ApplicationRunner、ConsolePresenter。

- [ ] **Step 1：先写生命周期边界。** `ApplicationRunnerTests.SingleRunAwaitsCompletion` 用可控更新任务保证一轮结束前不返回；`SerializesManualAndScheduledUpdates` 控制首次更新阻塞并同时触发手动/时钟；`EofDoesNotStopScheduledUpdates` EOF 后推进假时钟；`CancellationInterruptsPendingInputAndNetwork` 使用取消可观测的边界；`LargeIntervalDoesNotOverflowOrFireEarly` 使用 `uint.MaxValue` 周期及假时间。断言运行结果/实际更新过程的并发上限，不测试构造函数或参数原样转发。

```csharp
Assert.False(singleRunTask.IsCompleted); // 更新仍被受控门阻塞时。
Assert.Equal(1, maximumConcurrentUpdates);
Assert.Equal(130, await cancelledRun);
Assert.Equal(0, await exitCommandRun);
Assert.Equal(1, completedUpdatesBeforeInterval); // 已执行启动轮，没有提前追加一轮。
```

- [ ] **Step 2：先写输出安全边界。** `ConsolePresenterTests.RendersExternalTextLiterallyAndMasksCredentials` 输入含方括号的外部消息及假密钥；`RedirectedOutputHasNoTerminalControls` 使用非交互 IAnsiConsole。断言原文字段语义、无明文密钥/完整 AccessId、无 ANSI 控制序列；不锁定日期、边框或完整文案。
- [ ] **Step 3：实现显式参数入口。** 用 Options、SetAction、ParseResult.GetValue 和 Parse(args).InvokeAsync；去掉 NamingConventionBinder。`--help` 不加载配置/访问网络；周期参数 0 或 >=30，uint 解析范围不再先转 int 毫秒。入口只加载配置、构造依赖并运行，错误返回退出码，不调用 Environment.Exit。
- [ ] **Step 4：实现 ApplicationRunner。** 一次运行直接 await；周期启动立即执行，自动轮结束后再延迟。TimeProvider 默认 System；较长周期用受支持的分段异步延迟配合单调已逝时间，不能溢出、提前执行或忙等。定时/手动共用一个串行入口；输入 EOF 后关闭输入循环但保留周期；输入等待不能阻止取消。`ip` 只查询配置涉及的族，不修改 DNS。利用 System.CommandLine 的取消令牌传播 Ctrl+C/SIGINT/SIGTERM，不另建宿主信号框架。
- [ ] **Step 5：实现 ConsolePresenter。** 交互时配置/记录表格与状态文本；非交互时逐行纯文本。保留文本命令及 cls；不做全屏动画、不使用反射式 TextPrompt 转换器或 WriteException 代替安全错误消息。IAnsiConsole 输出串行化；动态内容用 Text 或正确转义；NO_COLOR/重定向下无控制字符；凭据统一脱敏。
- [ ] **Step 6：主会话执行检查与真实 CLI。** 运行 `dotnet test AliCloudDynamicDNS.Test/AliCloudDynamicDNS.Test.csproj --filter "FullyQualifiedName~ApplicationRunnerTests|FullyQualifiedName~ConsolePresenterTests"`；再实际运行应用的 --help、-i 29、-i 30、缺失文件、周期/EOF/取消路径。使用 PTY 检查中文和窄终端下的 config/ip/update/cls/exit，不能仅凭 StringWriter 测试宣称样式已验收。

## Task 5：依赖落地、裁剪发布、Docker 与 CI

**Files:** 两个 csproj、根 global.json、新 `AliCloudDynamicDNS/.dockerignore`、Dockerfile、scripts/build.sh、.github/workflows/unit-test.yaml、.github/workflows/docker-image.yaml。最终文档由主会话在冒烟验证后更新 README.md 和 settings.json.example。

**Consumes:** Task 1 版本表与已固定 CLI/配置合同。  
**Produces:** net10.0 构建、唯一测试运行方式、五 RID 发布、多架构容器、可重复的 CI 检查。

- [ ] **Step 1：更新项目文件。** 两项目目标 net10.0，启用 nullable；应用仅显式引用 System.CommandLine/Spectre.Console，并为内部生产签名器的回归测试配置 `InternalsVisibleTo` 给 `AliCloudDynamicDNS.Test`，不新增测试专用生产方法。测试设置 OutputType=Exe、IsTestProject=true，使用版本表测试包及 xUnit 内置断言。修复 PackageReference 嵌套，删除已迁移/无用途的 Newtonsoft、Nito、Immutable、Hosting、Shouldly、NSubstitute、旧 xunit、coverlet.msbuild。`global.json` 固定 SDK、allowPrerelease=false、rollForward=latestPatch，测试维持 VSTest，不开启 MTP。
- [ ] **Step 2：限定发布内容。** settings.json 不复制到输出/发布，settings.json.example 保留；.dockerignore 排除真实 settings、bin、obj、测试结果与其他非构建输入。排除规则针对实际构建 context，不能依赖仓库根 .gitignore。
- [ ] **Step 3：更新发布脚本。** 明确应用 csproj、net10.0、五个既有 RID、单文件/裁剪/自包含；验证版本参数非空，在自身创建的输出目录打包。脚本不能清理无关用户目录，不能把解决方案测试项目发布进去。保留可执行权限。
- [ ] **Step 4：更新 Docker 与 workflow。** SDK 阶段 FROM --platform=$BUILDPLATFORM，按 TARGETARCH 显式映射对应 linux-musl RID；使用 Task 1 对应的明确 SDK/runtime 镜像版本。容器继续采用框架依赖 DLL 发布（UseAppHost=false、self-contained=false，不启用裁剪），在目标架构的 .NET 10 Alpine runtime 镜像中运行，入口仍 -i 30；自包含/单文件/裁剪要求用于二进制分发，不能误施加到框架依赖容器发布上。GitHub Actions 在实施时读取各官方仓库最新稳定 Release 后锁定版本；修正 release/manual tag 来源，显式向 build.sh 传版本，保留发布到现有 ghcr.io 与 Release 附件的目的，不主动上传。测试 workflow 使用 collector 的 opencover 输出，移除旧 msbuild coverage 参数。
- [ ] **Step 5：把应用发布与入口冒烟加入 CI。** 至少原生 linux-x64 裁剪发布后实际运行帮助、缺失配置和带嵌套目标的配置校验路径；退出码与安全诊断必须符合合同。Windows/macOS 使用可运行的原生 runner 检查对应产物；Arm 交叉发布与原生运行证据分开报告，不以 QEMU 运行通过代替官方支持。
- [ ] **Step 6：主会话核对依赖闭包。** `dotnet restore AliCloudDynamicDNS.sln`、`dotnet list AliCloudDynamicDNS.sln package --outdated --include-transitive`、`dotnet list AliCloudDynamicDNS.sln package --vulnerable --include-transitive`。直接及传递旧项逐一处理；不能强行越过上游约束或悄悄忽略达不到“最新”的依赖，存在不可兼容约束时给出具体证据再决定。
- [ ] **Step 7：检查真实产物。** 在隔离副本中加入唯一假密钥标记，实际打包并检查 ZIP、最终镜像不含 settings.json/该标记，示例仍在；原生支持的目标逐个启动。交叉发布和缺少 Docker 的情况分别标明边界。
- [ ] **Step 8：冒烟通过后同步使用文档。** README、示例说明来源优先级、旧 AAAA 自定义来源、混合 A/AAAA、缺失/歧义/失败退出、无 TTY 运行、.NET 10/Arm32 glibc>=2.35 前提，以及真实验证过的构建/运行命令。删除因本次切换而失效的说明，不扩写无关功能。

## Task 6：集成验收与交付

**Files:** 不新增常驻测试项目；临时端到端 HTTP/CLI 驱动放在工作区之外。仅在发现真实缺陷时修正所属组件。

**Consumes:** Task 2–5 的完整改动。  
**Produces:** 回归、真实命令、产物和平台边界证据，以及审阅后的完整交付。

- [ ] **Step 1：主会话进行合同检查。** 检查跨任务类型/调用方、旧实现退役、真实配置排除和文档一致性。C# LSP 当前未配置，已用源码检索完成旧引用定位；若执行时服务可用，导出符号变更先查询 references。
- [ ] **Step 2：一次完整构建与测试。** 执行 `dotnet build AliCloudDynamicDNS.sln -c Release --no-restore` 和 `dotnet test AliCloudDynamicDNS.sln -c Release --no-build --collect:"XPlat Code Coverage" --results-directory TestResults -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=opencover`；要求无失败、所有相关行为有可观测断言，不保留只断言非 null 的旧网络测试。
- [ ] **Step 3：运行受控端到端场景。** 用真实 CliApplication、配置加载、PublicIpProvider、AliDnsClient、DnsUpdater 及运行器，注入仅改写传输目标到本地受控服务器的 HttpClient；验证线上的请求协议形状、Type/RecordId/Value/Line、实际退出状态和输出。单独实际启动产品发布文件验证入口/JSON/显示。不得把前者冒称为直接用产品发布文件完成了线上 DNS 更新。
- [ ] **Step 4：运行发布后的程序。** 例如 `dotnet publish AliCloudDynamicDNS/AliCloudDynamicDNS.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=true`，实际调用生成的 exe；其他宿主用各自 RID。检查帮助、参数错误、配置错误及单次/周期相关路径，保留 #52 的新旧差异证据。
- [ ] **Step 5：验证真实环境边界。** 连接默认 IPv4/IPv6 地址源，观察是否确实能获得对应地址族；检查 Docker、不同原生平台、TTY、重定向/EOF 和取消。网络或平台不可用时明确输出实际尝试和限制，不把替身结果当作成功证据。
- [ ] **Step 6：请求最终代码审阅并处理实际问题。** 使用 requesting-code-review；主会话核实审阅结论，修正后运行覆盖修正路径的验证。保留一个集成所有者，不让审阅者与实现者相互覆盖共享文件。
- [ ] **Step 7：交付。** 使用 verification-before-completion，逐项对照设计与用户四项要求，只报告实际执行过的结果、剩余外部平台限制与配置用法。完成且测试通过后再按 finishing-a-development-branch 处理分支；不自动推送、发版或关闭 GitHub Issue。

## 执行顺序与检查点

1. 主会话完成 Task 1 和修复前行为证据；新行为回归先写，依赖/编译前提失败不当作红灯。
2. DNS、CLI、发布三个非重叠实现域可按固定合同并行工作；DNS 实现者在内部按 Task 2→3 顺序推进。项目文件只有发布实现者修改，入口只有 CLI 实现者修改。
3. 编码子代理不启动构建/测试。主会话在相应代码静止、具备可运行依赖的检查点执行计划中的目标验证；集中集成后跑全套和真实程序，不对半成品反复构建。
4. 每个实现域先检查设计符合性，再检查代码质量；全体合入后仍有 Task 6 的独立整体验收。测试先行与原问题红灯必须保留证据，不能以最终一次绿灯倒推曾经验证过失败。
5. 文档、临时工具处理和分支集成由主会话统一拥有；不在并行任务中自动提交/推送。用户确认本计划后才开始产品实现。

## 计划自检记录

- 需求覆盖：依赖→Task 1/5，结构→Task 2/3/4，#11→Task 2/3，#52→Task 1/4/5/6，CLI→Task 4，实际验收→Task 6。
- 步骤：列出具体文件、合同、输入场景、断言和命令；辅助代码体留给实现者，没有第二套通用框架。
- 类型：来源/客户端/更新器三个接口，RecordUpdateResult/UpdateResult 供显示与运行器消费；所有签名只在合同处定义。
- 风险：五类 Review Focus 分别在 Task 2、3、4、5 有验证归属，不用措辞快照或 mock 回显代替行为。
- 篇幅：以必要决策和边界为主，不把实现代码写进计划；自检和用户确认均已完成，执行结果见设计文档第 13 节。
