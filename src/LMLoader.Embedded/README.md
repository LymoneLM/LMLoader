# LMLoader.Embedded

Godot/C# 模组加载器(LMLoader)的内嵌版引导包:游戏工程引用本包后,在入口代码中一行调用即可完成接入。

## 接入方式(一行代码)

```csharp
// 在你的主场景/Autoload 脚本中:
public override void _Ready()
{
    LMLoaderEmbedded.Initialize(GetTree(), loader => loader.GameId = "com.my.game");
}
```

模组放置于游戏目录 `mods/`(或通过 `loader.ModsRootOverride` 指定),每个模组含 `*.mod.json` 清单、
模组 DLL 与可选 pck。规范见仓库 `docs/mod-json-schema.md`。

## 运行期访问

- `loader.Services` —— 跨模组服务注册表(D4)
- `loader.LastLoadResult.SummaryText` —— 加载汇总报告
- `loader.GetModMountPoint(modUid)` —— 模组挂载点(D5)
- `LmScene.NodeAdded / NodeRemoved / SceneChanged` —— 场景级事件桥(D5)

## 许可

LGPL-3.0-or-later。
