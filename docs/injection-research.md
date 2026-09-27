# 注入版前置调研报告(阶段 8 前置,2026-09-27)

> 《问题清单》P0-3 要求的最小调研报告:对"钩 hostfxr/nethost、借主程序集跑 ModuleInitializer、
> 改写 runtimeconfig.json"三条路线的可行性评估。基线:Godot 4.7.2 stable / .NET 8。
> 结论供阶段 8 路线拍板(D20)与 spike 设计使用;本文为技术事实,拍板记录届时入《设计决策》。

## 一、结论(TL;DR)

- **进程内只能有一个 CoreCLR,但官方支持"附加"**:对已初始化运行时的进程再次调用
  `hostfxr_initialize_for_runtime_config` 返回 `Success_HostAlreadyInitialized (0x1)` 并给出可用
  context——注入版的一切动作都是"附加并借道",与"注入版共享同一 Core 内核"的架构预期吻合。
- **主路线(推荐):同名转发 hostfxr.dll**。Godot 导出模板在游戏目录探测 hostfxr——提供同名
  转发 dll(Doorstop 式,零内存 hook),在真初始化返回后用 `hdt_load_assembly_and_get_function_pointer`
  首跳 LMLoader bootstrap,再把 Godot 误判为失败的非零成功码规范化后交还。
- **兜底路线:Cecil 注入 ModuleInitializer 进游戏主程序集**。机制最简单、不依赖 hostfxr 内部
  行为,代价是游戏更新需重注入、R2R 预编译需剥离。
- **排除路线:改写 runtimeconfig.json**——自包含导出根本不走 runtimeconfig 入口
  (走 `hostfxr_initialize_for_dotnet_command_line`),该注入面仅对框架依赖形态有效。
- **现状:面向 Godot C# 游戏的第三方注入式加载器在公开生态中不存在**。GDWeave/GDPatch 证明
  代理 dll 注入在 Godot 上可行,但它们只做 GDScript 补丁;C# 侧是空白——先发优势与空白风险并存。

## 二、关键事实(源码级实证)

以下事实来自对 godotengine/godot **4.7.2-stable** `modules/mono` 的精读(源码链接见文末)与
本机导出产物交叉验证:

1. **Godot 自己就是 native host**:不链接 nethost,运行时 `LoadLibraryExW` 动态加载 hostfxr,
   仅解析 4 个符号(initialize_for_runtime_config / initialize_for_dotnet_command_line /
   get_runtime_delegate / close)。hostfxr 不被静态链接。
2. **两条启动流**:
   - 编辑器:`hostfxr_initialize_for_runtime_config(<exe旁>/GodotSharp/Api/…/GodotPlugins.runtimeconfig.json)`
     → delegate 加载 `GodotPlugins.dll` → `GodotPlugins.Main.InitializeFromEngine`;
     游戏程序集装入**可收集的** `GodotPlugins.PluginLoadContext`(编辑器热重载用)。
   - 导出游戏:`hostfxr_initialize_for_dotnet_command_line(argv[0] = <游戏>.dll)` → delegate 取
     `GodotPlugins.Game.Main.InitializeFromGameProject`(由 Godot.SourceGenerators 以源码形式
     注入游戏程序集)→ 游戏主程序集按 hostfxr 语义落入**默认 ALC**。
   - 降级链:hostfxr 失败 → 直调 coreclr_initialize → MonoVM 兼容 → NativeAOT 原生库。
3. **导出布局**:全部托管文件(含 hostfxr.dll/hostpolicy.dll/coreclr.dll/clrjit.dll)平铺在
   `data_<项目名>_<平台>_<arch>/`;runtimeconfig 叫 `<游戏名>.runtimeconfig.json` 与 dll 并排;
   self-contained 用 `includedFrameworks` 钉死版本;`MetadataUpdater.IsSupported=false`
   (导出环境无热重载能力,静态织入不受影响)。
4. **单实例语义(官方)**:"Only one runtime can be loaded inside a single process";二次
   `initialize_for_runtime_config` 若兼容返回 0x1 + 有效 context。Godot 自己的代码也依赖该行为。
   无任何 API 能取回已存在 host 句柄(`coreclr_get_active_host` 提案 4 年未实现,已关闭);
   但 hostfxr 源码确认:null handle + 进程内已有 active context 时 `get_runtime_delegate` 可用。
5. **runtimeconfig 解释权全在 hostfxr**:Godot 不解析任何字段。`additionalProbingPaths` 只扩展
   deps 资产探测、**不进 TPA**;能扩 TPA 的是 additional deps(`DOTNET_ADDITIONAL_DEPS`),
   但自包含导出不走该入口。

## 三、先例现状与教训

- **无 C# 注入先例**:GitHub 全网检索 "godot doorstop"/"bepinex godot" 等均零命中;
  唯一被第三方注入过 mod 的 Godot 游戏是 WEBFISHING(GDWeave,GDScript 层,已归档)。
- **注入入口工程学已被验证可行**:GDWeave(winmm.dll 代理,Rust 薄入口 + C# 主体)与
  GDPatch("游戏目录放一个文件,删掉即还原",retour hook)证明代理 dll 进入 Godot 进程无障碍。
  Windows/Proton 交付要点:`WINEDLLOVERRIDES="winmm=n,b"`;Wine 下 bcryptprimitives mock。
- **GDWeave 弃坑根因 = 版本矩阵**(引擎小版本 × GodotSteam 变体 × 开发者 fork 导致 hook 点漂移),
  它只敢锁单游戏单版本。教训:注入版必须把"引擎版本检测 + hook 点注册表"作为一等设计,
  且 Core 层收敛为"文件格式解析 + 极少量引擎 hook 点"。
- **NativeAOT 是趋势性杀手**:Godot 已合并 NativeAOT 导出模板,gd_mono.cpp 已有 NativeAOT
  回退路径——AOT 游戏进程内没有 CoreCLR,托管注入全线失效(届时唯一路线退化为
  Doorstop-Il2Cpp 式自宿主 + 原生互操作,超出本项目 v2 范围)。注入版必须在引导期检测
  `has_feature("dotnet")` 并对 AOT 游戏明确拒绝。
- **薄原生壳 + C# 主体**的分层(GDWeave/VenusRootLoader 同构)与 LMLoader 现有架构天然吻合:
  native 侧只负责"进进程 + 借道 hostfxr 首跳",加载逻辑全部复用 Core。

## 四、三条路线评估(P0-3 原定三路)

### 路线 1|钩 hostfxr(原定路线 1)——**有条件可行,推荐主路线**

- 导出模板探测位置在**游戏目录内**(API assemblies 旁)→ 提供同名转发 hostfxr.dll,转发全部
  导出,零内存 hook,比 Detours/MinHook 稳(避开 hook 引擎内部函数的版本漂移问题)。
- 流程:转发 `initialize_for_dotnet_command_line` 拿到真 context → 用
  `hdt_load_assembly_and_get_function_pointer` 加载 LMLoader 注入 bootstrap(独立程序集,
  不进 TPA 也能被该 delegate 按绝对路径加载)→ 首跳注册依赖解析/挂载逻辑 → 把返回码规范化
  为 0 交还 Godot。
- **必处理的坑**:Godot 把 hostfxr 的非零成功码(0x1/0x2)当失败检查(修复 PR
  godotengine/godot#115654 已归档未合并)——hook 层必须把 0x1 规范化为 0,否则 Godot 直接
  报错退出。
- 风险:依赖"探测位置在游戏目录"这一导出模板行为(4.7.2 实证);引擎未来改变探测逻辑需跟进;
  玩家误装到非 .NET 游戏 → 转发层需无 .NET 特征时静默直通。

### 路线 2|Cecil 注入 ModuleInitializer(原定路线 2)——**有条件可行,兜底路线**

- 机制成熟(Fody 生态同型);触发时机天然(GodotPlugins 加载游戏主程序集即触发 initializer);
  .NET 8 运行时不验证强名称,签名非障碍。
- 成本:游戏更新覆盖需重注入(交付层需做增量检测);R2R 预编译代码与改后 IL 失配需剥离;
  编辑器热重载会 double-load 主程序集(initializer 可能跑两次,需幂等);带完整性校验的游戏会暴露。
- 与路线 1 的关系:互为备份。路线 1 对"游戏目录可写"敏感(部分游戏装在受保护目录),
  路线 2 对此不敏感但侵入游戏文件——两者可按目标游戏环境二选一。

### 路线 3|改写 runtimeconfig.json(原定路线 3)——**排除**

- 自包含导出走 `initialize_for_dotnet_command_line`,不以 runtimeconfig 为入口;
  `additionalProbingPaths` 不进 TPA;无知名成功案例。该注入面仅对框架依赖形态有意义,
  而 Godot 导出默认自包含。**从路线清单移除**,仅在"框架依赖形态的特殊场景"下保留观察。

### 路线外发现|DOTNET_STARTUP_HOOKS——**不可依赖**

hostfxr 拿函数指针直接调 GodotPlugins/Game 入口,不走 Main 执行路径,startup hooks 不触发。
不押注。

## 五、约束与风险(设计文档补账,对应 P0-3)

1. 单进程单 CoreCLR:注入版**永远不初始化新运行时**,只附加;注入 bootstrap 与游戏共享
   默认 ALC 的宿主语义(导出流游戏程序集在默认 ALC——注入 bootstrap 也在默认 ALC,
   与内嵌版的独立模组 ALC 设计并存,Core 内核复用成立)。
2. 适用面:仅 CoreCLR 导出(默认导出形态);NativeAOT 导出明确不支持,引导期检测并拒绝。
3. 版本矩阵:hook 点收敛为"hostfxr 转发"一项(路线 1),比 GDWeave 的引擎函数 hook 面
   小一个数量级;仍需引擎版本检测与注册表机制。
4. 合规与对抗:不做反作弊对抗(v1 既定范围红线);游戏完整性校验会导致路线 2 暴露、
   路线 1 的文件新增也可能被检——文档明示"注入版不提供对抗承诺"。

## 六、阶段 8 spike 计划(建议)

1. **S1 转发 hostfxr**:手写转发 dll(或 sdForwarder 类工具生成),对样例导出构建
   (`SampleGame.Embedded`)完成:直通(无注入)启动正常 → 注入 bootstrap 首跳
   (打印标记)→ 返回码规范化后 Godot 正常起游戏。
2. **S2 首跳进 Core**:bootstrap 内取 `GameAssemblyResolver` 语义等价物,验证注入侧能否
   复用 Core 的 `ModManager`(共享 ALC 语义下挂 mods 目录扫描)。
3. **S3 降级矩阵**:hostfxr 缺失/AOT 导出/非 .NET 游戏三种环境的静默直通验证。
4. spike 工程独立于仓库(参照 P0-1 的 `godot-test` 惯例),结论回填本报告与 D20。

## 附|主要证据链接

- Godot 4.7.2 源码:`modules/mono/mono_gd/gd_mono.cpp`、`modules/mono/glue/GodotSharp/GodotPlugins/Main.cs`、
  `PluginLoadContext.cs`、`editor/Godot.NET.Sdk/Godot.SourceGenerators/GodotPluginsInitializerGenerator.cs`、
  `thirdparty/hostfxr.h`(github.com/godotengine/godot/tree/4.7.2-stable)
- 单实例/附加语义:learn.microsoft.com/dotnet/core/tutorials/netcore-hosting(Limitations);
  github.com/dotnet/runtime/issues/99883、#68388、#56896、DNNE#115
- hostfxr 成功码误判坑:github.com/godotengine/godot/pull/115654;Godot 依赖附加语义:PR #123251
- additional deps:host-probing.md / additional-deps.md(github.com/dotnet/runtime/docs/design/features)
- 先例:GDWeave(github.com/NotNite/GDWeave,归档)、GDPatch(github.com/GDPatch/GDPatch)、
  UnityDoorstop(github.com/NeighTools/UnityDoorstop)、VenusRootLoader(Unity 案例)、
  注入首跳链条先例:NosSmooth.Local(github.com/Rutherther/NosSmooth.Local)
- NativeAOT 导出:godotengine/godot/pull/97901
