# LMLoader

**Godot 4 / C# 的类 BepInEx 模组加载器**(LGPL-3.0-or-later)。游戏开发者一行代码接入;模组作者用常规 Godot/C# 习惯做模组。

> 当前状态:内嵌版 v1.0。

## 为什么需要 LMLoader(Godot 原生 addon 差在哪)

Godot 原生的"模组"通常是直接塞进工程的 addon——耦合于具体游戏工程,没有独立的依赖声明、
没有统一的发现与加载顺序、没有跨模组 API 约定,更无法对接 Steam Workshop / Thunderstore
这样的分发生态。玩家侧"装一个模组"在原生 addon 世界里等于"改游戏工程"。

LMLoader 把这些生态层能力补齐:

- **依赖管理**:mod.json 声明依赖与版本区间(`^` `~` `>=`),拓扑排序、循环整批拒绝、
  版本冲突可诊断;依赖只声明在清单,不做代码双声明。
- **统一分发**:同一份模组目录 = 原生格式;Thunderstore 包(带 mod.json)解压即用;
  Steam Workshop 订阅目录自动发现。玩家"订阅即用",作者零适配。
- **隔离与稳定**:模组装进独立 ALC,loader 供给公共库;单模组失败不杀游戏、
  级联只跳过硬依赖者;HarmonyX 由模组自带,导出构建 patch 全通。
- **开箱能力**:per-mod 配置文件(`user://configs/<uid>.toml`,改文件即生效)、
  分级日志与日志窗口、模组挂载点与场景事件桥。

明确不做(v1):Unload/热重载/模组动态开关(重启生效是唯一可靠路径)、沙箱、反作弊对抗、
移动端 AOT patch。

## 快速开始

### 游戏接入(一行)

```bash
dotnet add package LMLoader.Embedded
```

```csharp
// 主场景 _Ready 中:
var loader = LMLoaderEmbedded.Initialize(GetTree(), l =>
{
    l.GameId = "com.my.game";
    l.GameAssemblyResolver = n => n.Name == "MyGame" ? typeof(EntryPoint).Assembly : null;
});
loader.BootCompleted += () => GD.Print(loader.LastLoadResult?.SummaryText);
```

### 写一个模组

```bash
dotnet add package LMLoader.Api   # 模组工程唯一引用
```

```csharp
public class MyModule : LmModule   // 清单 entry.modules[].type = 完整命名空间全名
{
    public override void OnLoad()
    {
        var patcher = new Harmony(Uid);              // HarmonyX 由模组自带
        Logger.Info("loaded");
    }
}
```

`mods/MyMod/` 下放 `com.author.mymod.mod.json` + `MyMod.dll` 即可被自动发现。
完整指引:[mod 开发指南](docs/mod-dev-guide.md) · [API 参考](docs/api-reference.md) ·
[mod.json 规范](docs/mod-json-schema.md) · [配置详解](docs/mod-config.md)。

## 包一览(v1.0)

| 包 | 用途 |
|---|---|
| `LMLoader.Embedded` | 游戏宿主接入(传递 Core/GodotBridge/UI) |
| `LMLoader.Api` | 模组工程唯一引用(小而稳定,随 loaderVersion 语义化) |
| `LMLoader.Core` / `GodotBridge` / `UI` | 分包依赖,宿主可单独消费 |
| `LMLoader.Cli` | 作者工具:lint / pack-pck / pack-ts(源码随仓库,后续独立发包) |

## 仓库结构

- `src/` — Api / Core / GodotBridge / Embedded / UI / Distribution(Thunderstore、SteamWorkshop)
- `tools/LMLoader.Cli` — mod 作者命令行
- `samples/SampleGame.Embedded` — 可运行样例(单模组深验证 + 双模组协作,headless 冒烟可断言)
- `tests/` — 306 例单测(含 Roslyn 运行时编译真实模组 dll 的集成夹具)

## 开发

```bash
dotnet build && dotnet test        # 全绿为进阶门槛
# Godot 双环境冒烟(编辑器 + 导出)与打包命令见 AGENTS.md「Godot 运行时验证命令」
```

## 许可

LGPL-3.0-or-later(见 [LICENSE](LICENSE))。模组生态友好:动态链接使用 loader 不要求开源你的模组。
