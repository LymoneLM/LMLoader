# LMLoader API 参考(v1.0 内嵌版)

> 配套文档:[mod.json 规范](mod-json-schema.md) · [模组配置](mod-config.md) · [mod 开发指南](mod-dev-guide.md)
> 本文按程序集划分,列出公开类型与成员语义;行为依据标注决策编号(D#)。

## 程序集布局

| 程序集 | 谁引用 | 说明 |
|---|---|---|
| `LMLoader.Api` | **模组工程唯一引用**(D13) | 基类/上下文/日志/服务/配置;零第三方依赖 |
| `LMLoader.Core` | 宿主、Bridge | 加载器内核;不引用 Godot、不引用 HarmonyX(D14) |
| `LMLoader.GodotBridge` | 宿主游戏 | Godot 专属:Autoload 引导、pck 挂载、SceneTree 事件 |
| `LMLoader.Embedded` | 宿主游戏 | 一行接入的引导封装 |
| `LMLoader.UI` | 宿主游戏(可选) | 日志窗口(分级查看 + 加载报告) |
| `LMLoader.Distribution.Thunderstore` | 打包工具链(可选) | Thunderstore manifest 适配(D16) |
| `LMLoader.Distribution.SteamWorkshop` | 宿主(可选) | Workshop 订阅目录发现(D15) |
| `LMLoader.Cli` | mod 作者命令行 | lint / pack-pck / pack-ts(6.4) |

---

## LMLoader.Api

### LmModule —— 模块基类(D3)

```csharp
public abstract class LmModule
{
    public LmModuleContext Context { get; }   // 加载器注入;未初始化时访问抛错
    public string Uid { get; }                // = Context.ModuleUid
    protected ILmLogger Logger { get; }
    protected ModConfig Config { get; }       // 加载器未提供配置系统时访问抛错

    protected void PublishService<T>(T instance) where T : class;   // D4 弱类型通道
    protected bool TryGetService<T>(out T instance) where T : class;

    public virtual void OnPreLoad();   // 读清单外信息、声明配置、发布服务
    public virtual void OnLoad();      // 主入口;HarmonyX 环境就绪,可 patch
    public virtual void OnPostLoad();  // 跨模组后置协调(全部模块 Load 完成后)
}
```

- 生命周期 v1 仅此三阶段;**无 Unload/热重载**,运行期开关以重启为唯一可靠路径(范围决策)。
- 模块由 mod.json `entry.modules[].type` 显式声明,反射实例化(要求公共无参构造)。
- 任一阶段异常仅导致本模块跳过后续阶段;硬依赖它的模块级联跳过,不影响无依赖模块(D7 宽松传播)。
- `PublishService` 建议在 OnPreLoad/OnLoad;`TryGetService` 在 OnPostLoad(全部注册完成后)。

### LmModuleContext —— 运行上下文(只读)

| 成员 | 说明 |
|---|---|
| `ModuleUid` | 模块 uid(mod.json 中该模块的 uid) |
| `ModUid` | 所属模组 uid(配置文件名、日志分组即它) |
| `ModDirectory` | 模组目录物理路径(dll 与自带依赖;pck 资源经挂载,不在此语义内) |
| `Logger` | per-mod logger(D6) |
| `Services` | `ServiceRegistry?`;加载器未提供时为 null |
| `Config` | `ModConfig?`;未提供配置系统时为 null |

### ILmLogger / LmLogLevel —— 日志(D6)

```csharp
public interface ILmLogger
{
    void Log(LmLogLevel level, string message, Exception? exception = null);
    void Trace(string message);   // DIM 便捷方法
    void Debug(string message);
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? exception = null);
}
```

`LmLogLevel`: `Trace = 0, Debug = 1, Info = 2, Warning = 3, Error = 4`。
日志按 modUid 分组输出;loader 自身为特殊模组 uid `LMLoader`。

### ServiceRegistry —— 跨模组服务(D4)

```csharp
public void Register<T>(string ownerUid, T instance) where T : class;  // 同类型重复注册:后者覆盖(所有者不变)
public bool TryGet<T>(out T instance) where T : class;                 // 最后注册者胜
public bool TryGet<T>(string ownerUid, out T instance) where T : class;
public IReadOnlyList<T> GetAll<T>() where T : class;                   // 全部注册者,按注册序
```

以**声明类型 T** 为键(泛型静态语义,非实例类型);强类型直引通道见 [mod 开发指南](mod-dev-guide.md)。

### LMLoader.Api.Config —— 配置(D11)

```csharp
public sealed class ModConfig
{
    public IReadOnlyList<ConfigEntryBase> Entries { get; }   // 声明序(测试/枚举用)

    public ConfigEntry<T> Bind<T>(
        string section, string key, T defaultValue,
        string? description = null, bool requiresRestart = false,
        AcceptableValueBase? acceptableValues = null) where T : notnull;
    // 同键重复 Bind 返回同实例;改类型重绑抛 InvalidOperationException;
    // PreLoad 合并完成后 Bind 抛错(必须在 OnPreLoad 声明)
}

public sealed class ConfigEntry<T> : ConfigEntryBase where T : notnull
{
    public T Value { get; set; }         // 读当前值;写触发 SettingChanged(未变更不触发)
    public T DefaultValue { get; }
    public event Action<ConfigEntry<T>>? SettingChanged;  // 含热重载写入;在后台线程触发
}

public abstract class ConfigEntryBase
{
    public string Section { get; }       // "" = 顶层裸键
    public string Key { get; }
    public string Description { get; }
    public bool RequiresRestart { get; } // true = 改动建议重启(值仍会热更新)
    public AcceptableValueBase? AcceptableValues { get; }
    public Type SettingType { get; }
    public object? BoxedDefaultValue { get; }
    public object? BoxedValue { get; }
}

public sealed class AcceptableValueRange<T> : AcceptableValueBase where T : struct, IComparable<T>
{
    public AcceptableValueRange(T minimum, T maximum);   // 越界钳制到边界
    public T Minimum { get; }  public T Maximum { get; }
    public T Clamp(T value);
}

public sealed class AcceptableValueList<T> : AcceptableValueBase where T : notnull
{
    public AcceptableValueList(params T[] values);       // 列表外值 → 回退默认(文件)/抛错(代码赋值)
    public IReadOnlyList<T> Items { get; }
    public bool Contains(T value);
}
```

- 支持的 T(v1 标量):`string`、`bool`、全部整型、`float/double/decimal`、`enum`(文件中写作名字符串)。
- 约束类型必须与绑定类型一致;默认值落在列表外 → `Bind` 即抛错。
- 线程模型:`SettingChanged`/`Value` 写入可能来自热重载后台线程——涉及主线程亲和的 API(如 Godot 节点)请自行调度。

---

## LMLoader.Core —— 宿主侧内核

### LoaderOptions(全部 `init`)

| 选项 | 缺省 | 说明 |
|---|---|---|
| `ModsRootPath`(required) | — | mods 主根,递归扫 `*mod.json` |
| `AdditionalModsRoots` | null | 附加扫描根(Workshop 订阅目录等);缺失静默跳过;同 uid 副本主根优先+告警 |
| `GameId` | `""` | 非空时与清单 `gameId` 比对,不匹配拒载(D9) |
| `ApiVersion` | Api 程序集版本 | loaderVersion 区间校验基线(阶段 5) |
| `SharedLibraries` | null | loader 额外供给模组的公共库;`LMLoader.Api` 自动纳入(D1) |
| `GameAssemblyResolver` | null | 游戏程序集解析器(P0-1:游戏装在宿主自身 ALC) |
| `AfterPlan` | null | 规划成功后回调(pck 挂载时机);整批拒绝时不调用 |
| `ConfigRootPath` | null | 配置根目录;null = 关闭配置系统(D11) |
| `Strict` | false | 任一失败即中止(D7 预留);v1 默认宽松 |
| `MinimumLogLevel` | `Info` | 最低日志级别 |

### ModManager

```csharp
public sealed class ModManager : IDisposable
{
    public ModManager(LoaderOptions options, LoggerRouter? loggerRouter = null, ServiceRegistry? serviceRegistry = null);
    public LoadResult LoadAll();            // 可重复调用(每次独立规划)
    public LoggerRouter LoggerRouter { get; }
    public ServiceRegistry Services { get; }
    public ConfigManager? Configs { get; }  // 未配置 ConfigRootPath 时为 null;StartHotReload() 开启热重载
    public void Dispose();                  // 释放热重载监听与模组 ALC
}
```

### LoadResult / LifecycleReport

```csharp
public sealed class LoadResult
{
    public IReadOnlyList<ModManifest> LoadedManifests { get; }
    public IReadOnlyList<string> ReadWarnings { get; }  // 未知字段、重复键等(宽容读入,D9)
    public LoadPlan Plan { get; }                       // Ordered / Skipped / BatchRejected / BatchRejectReason
    public LifecycleReport? Lifecycle { get; }          // 整批拒绝时为 null
    public string SummaryText { get; }                  // 人可读汇总表(D7)
}

public sealed class LifecycleReport
{
    public IReadOnlyList<ModuleRunResult> Results { get; }
    public bool StrictAborted { get; }
    public TimeSpan Duration { get; }
}

public sealed class ModuleRunResult
{
    public string ModuleUid { get; }  public string ModUid { get; }
    public bool Succeeded { get; }    public bool Skipped { get; }
    public bool PostLoadExecuted { get; }
    public LifecycleStage? FailedStage { get; }   // AssemblyLoad/Instantiation/PreLoad/Load/PostLoad
    public Exception? Exception { get; }  public string? Note { get; }
    public LmModule? Instance { get; }
}
```

### 日志

```csharp
public sealed class LoggerRouter
{
    public LoggerRouter(params ILogSink[] sinks);       // MinimumLevel 可 init
    public void AddSink(ILogSink sink);
    public ILmLogger GetLogger(string modUid);          // 同 uid 复用同实例;线程安全
}

public interface ILogSink { void Emit(LogEvent @event); }
public readonly record struct LogEvent(DateTimeOffset Timestamp, LmLogLevel Level, string ModUid, string Message, Exception? Exception);

public sealed class ConsoleLogSink : ILogSink;                    // 注入 TextWriter 供测试
public sealed class FileLogSink(string path, bool append = true) : ILogSink, IDisposable;
public sealed class RingBufferLogSink(int capacity = 512) : ILogSink;  // Snapshot() 取副本
```

---

## LMLoader.GodotBridge + LMLoader.Embedded —— Godot 宿主

### 一行接入(推荐)

```csharp
// 主场景 _Ready 中:
var loader = LMLoaderEmbedded.Initialize(GetTree(), l =>
{
    l.GameId = "com.game.identifier";
    l.GameAssemblyResolver = n => n.Name == "你的游戏程序集名" ? typeof(入口类型).Assembly : null;
});
loader.BootCompleted += () => { /* loader.LastLoadResult */ };
```

`Initialize(SceneTree tree, Action<LMLoaderAutoload>? configure = null)`:同步入树失败自动降级
CallDeferred(4.7.2 实证);延迟入树不触发 `_Ready`,引导走显式 `RunBoot`(幂等)。

### LMLoaderAutoload(引导节点,固定挂载 `/root/LMLoader`)

| 成员 | 说明 |
|---|---|
| `GameId` / `ApiVersion` / `ModsRootOverride` / `GameAssemblyResolver` | 入树前设置 |
| `BootCompleted` | 引导结束(成功/部分失败/无 mods);经 `LastLoadResult` 取结果 |
| `LastLoadResult` | `LoadResult?`;无 mods 目录时 null |
| `Services` | 跨模组服务注册表(D4) |
| `LogBuffer` | 内存环形日志(日志窗口数据源,D6) |
| `Configs` | 配置协调器;热重载已随引导自动开启 |
| `OpenLogWindow()` | 打开/切换日志窗口;**F12** 默认切换(4.6) |
| `GetModMountPoint(string modUid)` | `/root/LMLoader/Mods/<uid>` 挂载点(D5);仅加载成功的模组 |
| `RunBoot()` | 显式引导(幂等);延迟入树路径由 Initialize 自动调用 |

### LmScene —— SceneTree 事件桥(D5,静态门面)

```csharp
LmScene.NodeAdded += node => { ... };    // 场景树加节点
LmScene.NodeRemoved += node => { ... };
LmScene.SceneChanged += () => { ... };   // 当前场景切换(4.7 无参信号)
```

### PckMounter —— pck 挂载(D10)

`PckMounter.MountInPlanOrder(LoadPlan plan, ILmLogger logger)`:按模块拓扑序挂载
`resources.pck`(`ProjectSettings.LoadResourcePack`),后挂载覆盖先挂载;pck 早于逻辑加载。

---

## LMLoader.Cli —— mod 作者命令行

```
lmcli lint      --dir <mod目录>
lmcli pack-pck  --godot <godot可执行> --project <含project.godot的目录> --preset <导出预设名> --output <pck路径>
lmcli pack-ts   --dir <mod目录> --team <Thunderstore团队> [--output <zip>] [--deps-root <mods根>]
```

- `lint`:严格校验——未知字段、运行时会忽略的行为一律 error;磁盘一致性(入口/pck/icon);D16 别名预校验;退出码 0/1。
- `pack-pck`:包装 `godot --headless --export-pack`(D10);`GODOT_EXECUTABLE` 环境变量可代替 `--godot`。
- `pack-ts`:生成 Thunderstore 包(zip 根含 manifest.json;mod.json 一并打入,解压即原生格式,D16);
  依赖映射需 `--deps-root` 提供依赖模组清单。

## LMLoader.Distribution.SteamWorkshop —— Workshop 发现(D15)

```csharp
var steamRoots = SteamLibraryLocator.LocateSteamRoots();          // 注册表/默认路径
var libraries  = SteamLibraryLocator.EnumerateLibraryRoots(steamRoots); // + libraryfolders.vdf
var roots = WorkshopModsScanner.GetScanRoots(libraries, "480");   // 含 mod.json 的订阅条目
// roots 直接喂给 LoaderOptions.AdditionalModsRoots
```

目录扫描为唯一发现路径(零 SDK 依赖、离线可用、Proton/Deck 直读);ACF 机会性过滤孤儿,
解析失败自动放行;`gameId` 过滤(D9)兜底防扫入他游戏模组。
