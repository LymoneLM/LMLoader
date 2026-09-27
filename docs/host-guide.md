# 宿主接入指南(游戏开发者)

> 面向想把 LMLoader 内嵌进自己游戏的人。配套:[API 参考](api-reference.md) · [架构](architecture.md)。
> mod 作者侧见 [mod 开发指南](mod-dev-guide.md)。

## 一行接入

```bash
dotnet add package LMLoader.Embedded
```

```csharp
// 主场景 _Ready 中:
var loader = LMLoaderEmbedded.Initialize(GetTree(), l =>
{
    l.GameId = "com.my.game";
});
loader.BootCompleted += () => GD.Print(loader.LastLoadResult?.SummaryText);
```

`Initialize` 内部做了什么:创建引导节点 → 延迟入树(Godot 拒绝父节点布置期同步 AddChild)→
显式 `RunBoot` → 扫描 `res://mods` → 规划 → 挂载 pck → 执行模组生命周期 → `BootCompleted` 通知。

典型接入只需设置 `GameId`。`GameAssemblyResolver`(游戏程序集解析)由 Initialize 自动按
调用方程序集建立缺省值——模组 patch 游戏方法零配置即可用;多程序集游戏才需要显式覆盖
(其余选项见 [API 参考](api-reference.md) 的 LMLoaderAutoload 表)。

## gameId 约定

- 宿主设置 `GameId` 后,清单**显式声明**了 `gameId` 的模组必须匹配才加载;未声明的模组视为
  通用模组照常装载(D18)。
- 约定值建议用反向域名(如 `com.studio.gamename`),并写进游戏的 mod 文档。
- 不设置 `GameId`(空串)= 不过滤:任何模组都装载,适合开发期。

## 模组目录与部署

- 缺省扫描 `res://mods`(可用 `ModsRootOverride` 指向任意全局化路径);递归扫描 `*mod.json`,
  兼容 Workshop 深层订阅目录。
- **开发机排障第一课**:运行期加载的是 exe 旁的物理 mods 目录;编辑器里改了模组源码后,
  `res://mods` 与导出目录的副本是**手动部署**的——先确认磁盘上那份 dll 的时间戳,再怀疑框架
  (P4-1 实录:陈旧副本让新模组"看似失效",误诊半天)。
- 导出游戏:输出目录必须**预先存在**(Godot 不会自建,否则报"给定的导出路径不存在");
  导出后 `rm -rf build/mods && cp -r mods build/mods` 重新部署副本。

## Steam Workshop 接线

```csharp
var steamRoots = LMLoader.Distribution.SteamWorkshop.SteamLibraryLocator.LocateSteamRoots();
var libraries  = SteamLibraryLocator.EnumerateLibraryRoots(steamRoots);
var roots      = WorkshopModsScanner.GetScanRoots(libraries, "<你的AppId>");

var loader = LMLoaderEmbedded.Initialize(GetTree(), l =>
{
    l.GameId = "com.my.game";
    l.AdditionalModsRoots = roots;   // 与主根合并扫描;同 uid 副本主根优先
});
```

- 零 Steamworks SDK 依赖:纯目录扫描 `<Steam库>/steamapps/workshop/content/<AppId>/<条目id>/`,
  离线可用、Proton/Deck 直读。
- 条目内必须含 `*mod.json`;`gameId` 过滤兜底(通用模组不过滤)。

## 失败可见性(建议宿主做的事)

模组失败不杀游戏(D7 宽松传播),玩家唯一的内置入口是 F12 日志窗口与 `user://logs/LMLoader.log`。
建议宿主在 `BootCompleted` 里检查 `LastLoadResult` 并给出用户可见反馈:

```csharp
loader.BootCompleted += () =>
{
    var r = loader.LastLoadResult;
    if (r is { Plan.BatchRejected: true })
    {
        // 循环依赖整批拒绝:r.Plan.BatchRejectReason
    }
    else if (r is { Lifecycle.FailedCount: > 0 } or { Lifecycle.SkippedCount: > 0 })
    {
        // 首启弹窗/主菜单红点:r.SummaryText 是人可读汇总
    }
};
```

## 配置与日志落点

- 模组配置:`user://configs/<模组uid>.toml`,热重载随引导自动开启(Steam 环境游戏目录不可写,
  user:// 是硬约束,不要改道)。
- 日志:`user://logs/LMLoader.log` 追加写 + 内存环形缓冲(日志窗口数据源);`OpenLogWindow()` /
  F12 切换。

## 模组管理(启停开关,D19)

- 玩家/宿主经管理窗口(`loader.OpenModManager()`,或直接调 `ModManager` API)开关模组;
  状态落 `user://mod-state.toml`(与模组配置平级,模组更新不覆盖玩家选择)。
- 三级粒度:全局总开关(纯净模式,排障用)→ 整包开关(UI 对清单内全部 uid 批量写入)→ 单模组开关。
- **开关写入即时持久化,重启进程后生效**;loader 发 `ModStateChanged` 信号并暴露 `PendingRestart`
  ——何时重启(保存→退出→拉起进程)由游戏决定,例如主菜单挂"重启以应用模组更改"按钮:

```csharp
loader.ModStateChanged += e => GD.Print(e.Summary); // e.DisabledUids / e.GlobalEnabled
```

- 关闭某模组前可查询影响面:`manager.PreviewDisable(uid)` 返回将因硬依赖缺失而无法加载的
  传递依赖方(静态预览,不强制)。
- 同进程重跑加载**不是**应用变更的手段(共享 ALC 不释放,二次实例化 = 变相热切换)。

## 已验证边界

- Godot **4.7.2 stable / .NET 8** 编辑器与 Windows 导出构建双环境实证(patch 含 native→managed
  路径);其余 Godot 版本未测——升级或跨版本使用前请先在导出构建跑一次冒烟
  (样例工程 `samples/SampleGame.Embedded` 可直接复用)。
- 明确不做:v1 无 Unload/热重载/模组运行期开关(重启生效是唯一可靠路径)、无沙箱、
  不做移动端 AOT patch。
