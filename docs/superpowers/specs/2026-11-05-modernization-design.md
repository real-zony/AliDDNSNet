# AliDDNSNet 现代化改造设计

日期：2026-11-05  
状态：用户已批准并完成核心实现及分域复审；第 2 节保留实施前调查快照，后续实测与环境边界见第 13 节。

## 1. 目标与已确认决策

用户要求：

1. 更新所有依赖到最新版本。
2. 优化代码结构。
3. 修复 GitHub Issue #52（启动入口异常）与 #11（AAAA / IPv6 支持）。
4. 优化 CLI 样式，可以使用 Spectre.Console。

用户已确认：

- 运行基线升级为 **.NET 10 LTS**，先审阅设计，再实施。
- 采用 **方案 A：保留 System.CommandLine，Spectre.Console 专注终端显示**。

成功标准：升级的不只是包号，而是一个仍可按原方式运行、能正确更新 IPv4 / IPv6、发布后也能正常启动的 DDNS 工具。未批准的实现细节均为下文提出的工程选择，而非用户已有要求。

## 2. 现状与证据

- 主项目和测试项目目前目标均为 `net8.0`；生产依赖包含 System.CommandLine beta、Hosting alpha、Newtonsoft.Json 与 Nito 系列。测试项目的 xUnit PackageReference 存在嵌套错误。
- `Program.cs:18-232` 混合参数解析、全局状态、配置输出、定时调度、DNS 更新和交互；命令读取使用递归，定时回调通过 AsyncContext 同步等待异步操作。
- `Program.cs:140-200` 对所有记录使用同一个公网 IP，匹配时仅比较 RR，不比较 Type；记录为空时仍读取 `record.SubName`。
- `NetworkHelper.cs:10-29` 返回未经 IP 地址族校验的 HTTP 响应正文；每次调用新建 HttpClient。
- `ApiRequestTool.cs:14-23` 只请求一次解析列表，没有处理分页；请求使用 HTTP，异常被转换为空字符串，之后才发生 JSON 解析错误。
- 当前代码已改用 System.CommandLine，但最新 GitHub Release `v0.0.5` 的源码仍使用 McMaster 的 `ExecuteAsync<Program>` 和按约定发现的 `OnExecuteAsync`。其调用方式与 #52 的堆栈一致；当前 `scripts/build.sh` 和该标签下的 `scripts/publish/publish.sh` 均启用了 PublishTrimmed。不过，历史脚本的打包路径仍是 net5.0，不能据此确定下载包的实际构建过程。**裁剪导致入口被删除仍是待复现假设，不能把它写成已确认根因。**
- Dockerfile 强制使用 amd64 基础镜像，而发布工作流声明 amd64、arm64；CI、Dockerfile 和构建脚本均绑定 .NET 8。
- 当前机器只检测到 .NET 9 SDK。尝试不还原依赖运行现有 `--help`，因缺少 `obj/project.assets.json` 而失败；尚无当前源码启动成功或失败入口的实测结论。

## 3. 方案比较

### A：轻量职责拆分（用户已选择）

保留 System.CommandLine，升级稳定版并采用显式处理函数；Spectre.Console 只负责显示。保留单文件、自包含、裁剪发布，实际运行发布文件验证。业务代码按职责组织，但不增加生产项目数量。

优点：沿用当前技术路线，减少反射绑定与不必要依赖，适合小型工具。代价：参数解析和富文本输出来自两个库，需要明确各自边界。

### B：统一迁移 Spectre.Console.Cli（未选择）

参数、帮助和命令执行全部迁移 Spectre.Console.Cli，业务仍按职责拆分；默认不裁剪发布。

优点：命令定义和显示统一生态。代价：增加一次参数框架迁移，保守的不裁剪发布会增大分发包。

不采用仅更新版本号而保留现有业务结构的方案：它不能解决地址族混用、空引用及执行生命周期的问题。也不引入多层项目、Generic Host、插件系统或通用仓储框架。

## 4. 范围与兼容边界

### 保留的外部行为

- 可执行文件名 `AliCloudDynamicDNS`。
- `-f / --file` 指定配置文件；默认读取当前工作目录的 `settings.json`。
- `-i / --interval` 单位为秒；省略或为 0 时执行一次；非零最小为 30 秒。
- 周期模式启动后立即执行一次，然后持续运行。
- 既有 JSON 字段名和已有自定义地址源含义保持不变，继续接受示例中的注释、大小写不敏感属性及尾随逗号。配置中的 `SubDomains[].Interval` 仍是 DNS TTL，不与命令行周期混淆。
- 在交互终端中保留 `ip`、`config`、`update`、`cls`、`exit`，忽略命令大小写。
- 现有发布目标：win-x64、linux-x64、osx-x64、linux-arm、linux-arm64；Docker 保留 linux/amd64 和 linux/arm64。

### 明确不做

- 不新增 Web UI、Windows 服务安装器、系统托盘、多 DNS 厂商、自动建记录或删除记录。
- 不新增重试框架、遥测、热重载、配置迁移层、IPv6 网卡选择器或解析线路配置。
- 不把 Native AOT 当成本次交付目标；裁剪、自包含、单文件与 Native AOT 是不同要求。
- 不使用真实用户凭据修改线上 DNS 来完成自动验收。

## 5. 依赖与结构

### 依赖策略

- 应用、测试、SDK 选择、CI 与 Docker 一起迁移 .NET 10。SDK 的明确版本在实施时根据官方稳定发行数据确定。
- “最新”统一指查询时可用的最新**稳定版**，不采用 alpha、beta、preview 或 RC。
- 已查询到 System.CommandLine 最新稳定版为 `2.0.14`，Spectre.Console 为 `0.57.2`；实施时重新核对并固定具体版本，不使用浮动版本。
- JSON 改为内置 System.Text.Json，并通过源生成上下文支持裁剪；配置兼容性必须通过行为测试证明。
- 用正常的 async / await、CancellationToken 和可取消延迟替换 StrongTimer 与 Nito 桥接。迁移全部调用点后移除旧实现、Newtonsoft.Json、Nito、Hosting 以及确实不再需要的显式框架包；不保留空壳或兼容别名。
- 测试依赖升级到当前稳定代际，修复项目 XML。只保留实际使用的断言、替身及覆盖率依赖，避免同时保留两套无用途的覆盖率接入。
- GitHub Actions 与容器基础镜像也纳入版本更新，不只修改 csproj。

### 职责划分

继续保留一个应用项目和一个测试项目，以目录和小型类型区分职责：

- **入口与 CLI**：声明参数，显式绑定异步处理函数，构造依赖，把结果映射为退出码。不执行 DNS 业务。
- **配置**：读取 JSON，提供本次运行使用的配置对象及清楚的配置错误；不再持有可变静态全局配置。
- **公网 IP 获取**：接收 HttpClient 与地址源，返回解析后的 IPAddress；负责 HTTP 结果与地址族检查。
- **AliDNS 客户端**：管理请求签名、HTTPS 请求、分页、类型化响应和 API 错误。保留现有 AliDNS RPC 协议，不为了重构引入整套云 SDK。
- **更新服务**：决定需要哪些地址族，匹配远程记录，比较 IP，更新并产生结果；不直接操作 Console。
- **运行器与终端显示**：运行器负责单次/周期/退出和更新串行化；显示组件通过 Spectre.Console 呈现配置、状态和错误。

由入口显式构造少量依赖即可；接口只用于真正需要替换的边界，不为每个类建立接口。HTTP 连接可复用，请求和响应按作用域释放，取消信号传至网络调用。

## 6. Issue #11：IPv4 / IPv6 的完整行为

### 配置

保留 `PublicIpServer` 作为用户显式指定的通用公网地址源，新增可选的 `PublicIpv6Server` 专门覆盖 AAAA 的来源。来源选择规则如下：

- A：显式 `PublicIpServer`；未配置时使用 IPv4 专用的 `https://api.ipify.org`。
- AAAA：优先显式 `PublicIpv6Server`，其次是显式 `PublicIpServer`；两者均未配置时使用 IPv6 专用的 `https://api6.ipify.org`。
- 必须区分“用户显式配置”与“程序使用默认值”，不能先填入 IPv4 默认地址后错误地让 AAAA 继承它。
- 旧 AAAA-only 配置若通过 `PublicIpServer` 指定 IPv6 服务，该服务继续生效。混合 A/AAAA 且通用来源只能返回 IPv4 时，应显式填写 `PublicIpv6Server`；地址族不符时报出配置建议，不暗中改用其他服务。

同一份配置可同时包含 A 和 AAAA，例如：

```json
{
  "AccessId": "your-access-id",
  "AccessKey": "your-access-key",
  "MainDomain": "example.com",
  "PublicIpServer": "https://api.ipify.org",
  "PublicIpv6Server": "https://api6.ipify.org",
  "SubDomains": [
    { "Type": "A", "SubDomain": "home", "Interval": 600 },
    { "Type": "AAAA", "SubDomain": "home", "Interval": 600 }
  ]
}
```

新增字段和默认地址源是本设计的工程选择。采用外部 IPv6 地址源意味着获取本机访问该服务时使用的 IPv6，并不推断局域网其他设备的地址。

### 更新规则

配置的更新目标仅支持 A 和 AAAA，Type 忽略大小写并规范化。其他类型、空类型属于配置错误，在任何网络写入前终止本次运行，不能把 IP 当成 CNAME、MX 等记录值，也不能静默跳过后返回成功。RR 按 DNS 名称的大小写不敏感语义匹配。

1. 每轮只获取配置实际需要的地址族，每个地址族至多获取一次。只有 A 时不访问 IPv6 服务；只有 AAAA 时不要求 IPv4 可用。
2. HTTP 必须成功；去掉响应首尾空白后用 IPAddress 解析。A 仅接受 IPv4，AAAA 仅接受 IPv6；不把 HTML、错误正文或 IPv4 文本写入 AAAA。
3. 用 `(RR, Type)` 精确匹配配置与远程记录，记录模型必须包含 Type；精确匹配不表示区分 DNS 名称大小写。按 AliDNS 分页读取完整列表，避免第一页之外的记录被误判缺失。
4. 地址用 IPAddress 语义比较，IPv6 的等价缩写不触发无意义更新。更新时使用标准地址文本及配置中的类型和 TTL，并显式保留远程记录的 Line；AliDNS 的更新接口省略 Line 会使用 default，不能因此改变原有解析线路。
5. 缺少记录时报告配置中的域名与类型，不解引用空对象，不自动创建记录。
6. 若同一 RR、Type 有多个远程记录，报告目标存在歧义，不任意选择一条或批量改变不同线路的记录；其他配置继续处理。本次不扩展解析线路配置。
7. 某个地址族获取失败，不阻止另一地址族继续；单条 API 更新失败不抹掉其他结果。单次模式只要有配置目标失败，整体退出码非零。
8. 取消不被转换为空响应或普通业务失败；周期模式的瞬时失败在下一正常周期再尝试，不增加额外重试机制。

## 7. Issue #52：入口与发布产物

- 先用可获取的 `v0.0.5` 发布包或等价的受控发布复现，检查 McMaster 的反射入口与裁剪的实际关系。该 Issue 未注明具体版本，不能声称已证明报告者使用的是哪个文件。
- 当前代码去掉 NamingConventionBinder，使用稳定版 System.CommandLine 的显式异步处理函数及取参 API。参数名不再依赖方法名/参数名反射约定。
- JSON 使用源生成上下文，避免修好 CLI 入口后在裁剪发布的配置读取阶段再次失败。
- 发布脚本明确发布应用 csproj，不把整个测试解决方案当作应用打包目标。同步框架目录、版本参数和产物名称。
- 保留裁剪，但不靠全局关闭分析器、压制警告或整包保留旧反射入口掩盖问题；实际使用到的 Spectre 显示路径也进入裁剪产物冒烟测试。
- 验证必须包含发布后的 `--help`、正常执行入口、配置读取与错误退出，不能以开发态 `dotnet run --help` 代替。
- 分发包和 Docker 构建上下文只包含配置示例，不携带真实 `settings.json` 或密钥；单靠 .gitignore 不构成发布排除。

## 8. 生命周期与退出语义

- 单次模式真正 await 一轮更新结束，再返回结果。不启动计时器后马上停止，不在深层方法调用 Environment.Exit。
- 周期模式保持现有“本轮自动更新完成后，再等待指定秒数”的节奏；不是固定频率追赶任务。使用可取消的异步等待。
- 定时更新和手动 `update` 共用串行执行边界，不产生并发 DNS 写入。
- 有交互能力时才读取控制台命令；stdin EOF 后停止读取，不递归空转，也不因此停止后台周期任务。
- 重定向或容器后台场景不要求 TTY、不等待菜单选择；`exit` 与 Ctrl+C 结束运行，传递取消并释放资源。
- 退出码：成功（含无需更新）为 0；参数、配置、网络、API 或部分目标失败为非零；用户取消按常见 CLI 惯例返回 130。周期中某轮失败只记录该轮结果，继续下一正常周期。

## 9. CLI 行为与样式方向

这不是全屏 TUI，不引入与后台更新竞争输出的常驻动画。

- 交互终端：用 Spectre.Console 的表格/分级文本呈现运行模式、周期、域名、记录类型及更新结果；成功、无需更新、失败状态有清楚区别。
- 继续使用简单文本命令，不强制选择菜单。帮助页保留标准命令行参数布局与示例。
- Docker、文件或管道重定向：稳定的逐行纯文本日志，无光标控制序列、进度动画或反复清屏。尊重终端能力及 `NO_COLOR`。
- 不输出 AccessKey，AccessId 脱敏；不打印含签名或凭据的请求 URL。外部内容按文本输出或正确转义，不能被当作 Spectre markup。
- 终端窄宽、无颜色、中文以及 IPv6 长文本仍应可读；具体列宽、色彩和排版由实现时的真实终端检查决定，不把文案和边框写成脆弱快照测试。

## 10. CI、Docker 与文档

- CI 用 .NET 10 执行测试和应用发布检查，相关 Actions 更新稳定版本。
- Docker 使用支持目标架构的 .NET 10 基础镜像，去掉硬编码 amd64。SDK 构建阶段固定运行于构建机架构，按目标架构交叉发布，不依赖 QEMU 执行 .NET SDK；运行阶段使用目标架构的镜像。保留容器入口 `-i 30`，无需 `-t` 也可持续运行。
- 二进制发布保持现有五种 RID；明确向脚本传入版本，手动工作流与 Release 事件各自使用有效 tag。
- README 与 `settings.json.example` 同步描述双栈字段、旧配置兼容方式、IPv6 网络前提、退出行为、运行时及构建方式。已有命令和容器示例不能继续要求 TTY 才能后台运行。
- 记录 Windows、Linux、macOS 及各架构的实际验证边界；交叉发布成功不等于已经在目标系统运行成功。
- 保留 RID 不代表继续支持所有旧 NAS/操作系统。README 说明 .NET 10 的 OS、libc 前提；例如 Linux Arm32 的 glibc 基线为 2.35。当前工作站不属于已核实的 .NET 10 官方支持平台清单，后续局部运行结果不能被描述为官方平台支持证明。

## 11. 验收要求

### 依赖与结构

- .NET 10 下还原、构建和既有测试运行成功；新增/保留依赖没有被遗漏的稳定版本升级，依赖移除与全部调用点迁移一致。
- 生产代码不再使用旧静态配置、递归交互、AsyncContext 同步桥或 StrongTimer。

### 双栈及错误路径

- A-only、AAAA-only、同名 A + AAAA 三种配置，选择正确地址源，写入正确 RecordId、Type 和 Value。
- IPv6 等价表示不更新；地址族不匹配不写入；一种地址族失败时另一种仍执行，并保留整体失败结果。
- 分页后的目标可被找到；目标不存在不会空引用；歧义目标不会被任意修改；API 错误保留可诊断信息且不泄露密钥。
- 注释/尾随逗号的旧配置与新增双栈配置在普通和裁剪构建中均可读取。
- 显式通用地址源继续用于旧 AAAA-only 配置，新增 IPv6 来源的优先级正确；未知记录类型在写入前被拒绝；更新保留既有解析线路。

### 启动、运行与显示

- 记录旧发布入口问题的复现证据或不能重现的具体边界；新裁剪单文件产物必须运行到真实处理函数。
- 实际运行 `--help`、无效参数、缺失配置、单次执行和周期模式，验证输出与退出码。
- 用受控 HTTP 端点/注入的真实 HTTP 处理路径贯穿参数、配置、地址获取、AliDNS 协议与更新流程，不碰线上 DNS；不能只证明 mock 方法被调用。
- 实际交互终端检查 `config`、`ip`、`update`、`cls`、`exit`、Ctrl+C；检查 EOF 和重定向下不会忙等、挂住或输出控制字符。
- 检查定时/手动更新不重叠；网络调用可取消。
- 在隔离构建目录中放入含唯一假密钥标记的配置，检查最终 ZIP 与最终镜像均不含该文件或标记，并确认配置示例仍在；不得使用真实凭据进行这项验收。
- 真实公网 IPv4/IPv6 地址源另做连接验证。若当前网络没有 IPv6 或无法运行 Docker/某个目标平台，明确报告边界，不能以测试替身冒充线上或跨平台运行成功。

永久测试只覆盖上述用户可见行为、边界和状态转换。删除现有只断言网络返回值非 null 的低价值测试，不把终端文案、内部类布局或转发细节固化为测试。

## 12. 实施前提与待批准工程选择

设计批准后才进入实施计划和代码变更。实施需要 .NET 10 SDK；优先使用可复现的局部 SDK 安装，不擅自修改系统级工具链。

需要用户在批准时一并审阅的工程选择：

- `PublicIpServer` 保留通用自定义来源含义，新增 `PublicIpv6Server` 优先覆盖 AAAA；没有显式来源时才采用 ipify 的分地址族端点。
- 同名同类型多记录视为歧义，不自动扩展到多个解析线路。
- DDNS 更新目标限定为 A/AAAA；其他记录类型在写入前作为配置错误拒绝。
- 单次更新的部分失败不再以成功退出掩盖；无 TTY 时只提供后台日志。
- 不新增大型框架、云 SDK 或 Native AOT；保留并验证裁剪发布。

其余内部类型名、文件划分、可测边界的最小接口和测试工具选择留给实施者，但不得改变以上外部契约。

## 参考

- Issue #52：https://github.com/real-zony/AliDDNSNet/issues/52
- Issue #11：https://github.com/real-zony/AliDDNSNet/issues/11
- v0.0.5 入口：https://github.com/real-zony/AliDDNSNet/blob/v0.0.5/AliCloudDynamicDNS/Program.cs
- .NET 支持策略：https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core
- System.CommandLine：https://learn.microsoft.com/en-us/dotnet/standard/commandline/
- System.CommandLine 版本：https://api.nuget.org/v3-flatcontainer/system.commandline/index.json
- Spectre.Console：https://spectreconsole.net/
- Spectre.Console 版本：https://api.nuget.org/v3-flatcontainer/spectre.console/index.json
- v0.0.5 发布脚本：https://github.com/real-zony/AliDDNSNet/blob/v0.0.5/scripts/publish/publish.sh
- ipify 地址族约定：https://www.ipify.org/
- AliDNS 分页与记录字段：https://help.aliyun.com/en/dns/api-alidns-2015-01-09-describedomainrecords
- AliDNS 更新与 Line 默认值：https://help.aliyun.com/zh/dns/api-alidns-2015-01-09-updatedomainrecord
- .NET 10 平台与 QEMU 限制：https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md

## 13. 实施验证记录

- 已升级到 .NET SDK 10.0.403 / 运行时 10.0.14，保留的直接与传递 NuGet 依赖均经检查，无可用稳定更新或已报告漏洞。
- 已用同一 v0.0.5 源码对照裁剪/不裁剪发布：不裁剪能进入配置处理，裁剪出现与 #52 一致的缺少 OnExecute 异常；可下载旧发布包也复现。此结论不推断报告者未注明的二进制身份。新版显式入口和 JSON 源生成的实际裁剪发布/解压产物入口冒烟通过。
- #11 修复前实际旧程序会把同名 A/AAAA 都写到 A 的 RecordId，且缺失记录触发空引用；新版受控完整链路按记录类型写入正确 ID/地址，保留线路，处理分页及部分失败。
- 完整回归 94 项通过，0 失败、0 跳过，无编译警告；另有 17 个受控 HTTP/CLI 场景通过，签名由独立实现验证，覆盖真实 30 秒周期、取消、地址源兼容及错误分支。
- 审阅发现并经 RED/GREEN 修正了响应正文超时边界、HTTP 超时与用户取消的区分，以及区域性正号的参数校验问题。分域复审未发现修复引入的新缺陷。
- 整分支审阅发现旧 BOM 配置兼容性遗漏：修复前 UTF-16/UTF-32 四种大小端用例失败、UTF-8 BOM 通过。修复后保留 UTF-8 直接流式路径，只对其他 BOM 编码转码；五种真实编码文件及裁剪发布产物的 UTF-32 BOM 配置读取均通过。
- BOM 探测对不可寻址配置流不回退 Position，而是仅重放已读取的非 BOM 前缀；Windows 真实命名管道的四种编码路径完成失败前/通过后的回归，Unix FIFO 用例交由对应原生 CI 执行。
- 五种 RID 实际发布和 ZIP 打包成功，无裁剪警告；ZIP 根条目及唯一假密钥标记排除检查通过，Windows 解压产物已实际启动。ZIP 改用 SDK 的 ZipDirectory，并以 .gitattributes 固定 Bash 脚本 LF，避免增加外部归档工具依赖。
- Windows 生成的 ZIP 没有 Unix 执行位；Unix 解压后需要 chmod +x。Ubuntu Release job 在 Unix 上生成、解压并运行产物，保留源文件模式；本机交叉发布不等于目标系统原生执行。
- 实际 Windows 原生终端已检查中文表格、脱敏、命令交互、正常退出与 60 列布局。独立 Windows CTRL_C_EVENT 测试观察到原生进程退出码 130。
- 未执行 Docker 构建/运行（本机没有 Docker），未在 Linux/macOS/Arm 上原生执行。本机 DNS 无法解析 IPv6 专用默认源的 AAAA，未声称完成真实公网 IPv6 连通验证；受控 IPv6 数据路径已验证。没有使用真实用户凭据写入线上 DNS。

