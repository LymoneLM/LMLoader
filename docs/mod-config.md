# 模组配置(mod.json 之外的运行时配置)

> 状态:阶段 4 实装(任务 4.1–4.4,决策 D11)。面向模组作者;宿主接入见文末。

## 快速上手

在模块的 `OnPreLoad` 中声明配置项,`OnLoad` 起读到的即为合并后的最终值:

```csharp
using LMLoader.Api;
using LMLoader.Api.Config;

public class MyModule : LmModule
{
    private ConfigEntry<int> _multiplier = null!;

    public override void OnPreLoad()
    {
        _multiplier = Config.Bind(
            "Patch", "Multiplier", 100,                // 节名、键名、默认值
            "伤害乘数",                                 // 描述(预留 GUI 用)
            requiresRestart: false,                     // 是否需重启才可靠生效
            acceptableValues: new AcceptableValueRange<int>(0, 1000));
        _multiplier.SettingChanged += e =>
            Logger.Info($"Multiplier 热重载为 {e.Value}");
    }

    public override void OnLoad()
    {
        Logger.Info($"当前乘数 = {_multiplier.Value}");   // 已是合并后值
    }
}
```

- 声明时机:**只在 `OnPreLoad` 中 `Bind`**。同键重复 `Bind` 返回同一实例;改类型重绑会抛异常。
- 同模组的多个模块共享同一份配置(文件以 modUid 命名),节名/键名自冲突。
- 支持的类型(v1 标量):`string`、`bool`、各整型、`float`/`double`/`decimal`、`enum`。enum 在文件中写作名字符串。
- 约束:`AcceptableValueRange<T>`(越界钳制到边界)与 `AcceptableValueList<T>(...)`(列表外回退默认)。约束类型必须与绑定类型一致。

## 文件位置与格式

- 位置:`user://configs/<modUid>.toml`(Godot 环境由 loader 自动全局化;Steam 等游戏目录只读环境亦可写)。
- 首次运行:loader 把全部声明项按默认值写回文件,供用户修改。
- 手工编辑即改即生效(热重载);无需重启(声明了 `requiresRestart` 的项除外,值仍会更新,但建议提示用户重启)。

## 合并语义(D11)

| 情形 | 行为 |
| --- | --- |
| 文件缺失 | 全部按默认值,写回文件 |
| 键缺失 | 补默认值,写回文件 |
| 类型/取值不符 | 回退默认值 + 警告,写回文件 |
| 范围越界 | 钳制到最近边界 |
| 列表外值 | 回退默认值 |
| 文件里的多余键/节(孤儿) | **原样保留**,不删除 |
| 配置文件解析失败(用户改坏了) | 整模组使用默认值,**不写回**(保护用户原稿)+ 警告 |

写回为全文重建(临时文件 + 原子替换),用户手写的注释不保留(迭代空间)。

## 热重载

- 机制:文件监听 + 2s mtime 兜底扫描(FileSystemWatcher 不保证事件送达)→ 500ms 防抖 → 重载。
- 文件中**存在**的键会被更新;**删除**的键保持当前值(热重载路径不写回)。
- `SettingChanged` 回调在后台线程触发;涉及主线程亲和的 API(如 Godot 节点操作)请自行调度到主线程。

## 宿主接入(游戏侧)

- 内嵌模式(`LMLoaderEmbedded.Initialize`)零配置即启用:配置根 = `user://configs`,热重载自动开启。
- 直接构建 `ModManager` 时:`LoaderOptions.ConfigRootPath = <物理目录>`(传 null 关闭配置系统)。
- 热重载开关:`manager.Configs?.StartHotReload()`;日志窗口(可选组件 `LMLoader.UI`):`autoload.OpenLogWindow()`,默认 F12 切换。
