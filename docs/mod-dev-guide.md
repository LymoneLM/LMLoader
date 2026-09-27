# LMLoader mod 开发指南(v1.1 内嵌版)

> 面向 mod 作者。配套:[API 参考](api-reference.md) · [mod.json 规范](mod-json-schema.md) · [配置详解](mod-config.md)。
> 本指南把实测踩过的"坑"沉淀为惯例,遇到问题优先回查。

## 最小模组

```
MyMod/
├─ com.author.mymod.mod.json     # 清单(唯一入口声明)
├─ MyMod.dll                     # 清单 mods[].assemblies
└─ (可选)pack.pck / 依赖 dll / icon.png
```

```csharp
using LMLoader.Api;

namespace Author.MyMod;

[LmMod("com.author.mymod")]     // 特性 uid = 清单 mods[].uid(D18 特性绑定)
public class MyModule : LmMod
{
    public override void OnLoad()
    {
        Logger.Info("hello");
    }
}
```

```json
{
  "schemaVersion": 1,
  "version": "1.0.0",
  "gameId": "com.game.identifier",
  "mods": [ { "uid": "com.author.mymod", "assemblies": ["MyMod.dll"] } ]
}
```

- 清单与代码各写一次 uid(双写):清单侧供规划与工具链,代码侧 `[LmMod]` 供运行期绑定,
  `lmcli lint` 交叉校验两者一致。类名重构不影响绑定,uid 是稳定身份。
- 工程引用 `LMLoader.Api`(仅此一个 loader 包),`CopyLocalLockFileAssemblies=true`
  (把自带依赖拷到输出目录;netstandard 资产默认不拷贝,必须显式开)。
- 一个 mod.json 可并列声明多个模组(见 [mod.json 规范](mod-json-schema.md));
  一个 dll 也可服务多个模组——每个模组在自己的 `assemblies` 里列出它。

## 生命周期与失败语义

| 阶段 | 用途 | 异常后果 |
|---|---|---|
| `OnPreLoad` | 读清单外信息、**声明配置**、发布服务 | 本模组跳过,硬依赖它的模组级联跳过 |
| `OnLoad` | 主入口:patch、注册、读配置 | 同上 |
| `OnPostLoad` | 跨模组后置协调(消费其他模组服务) | 仅记录,不影响他人;不会级联 |

- 失败不杀游戏、不阻塞无依赖模组;原因进加载汇总表(游戏日志/user://logs/LMLoader.log)。
- 模组间依赖建议用服务消费(弱类型)或直接引用 mod 的 Api dll(强类型)——**依赖声明本身只写在 mod.json**,不要代码里再判断一次存在性,两处声明会漂移。
- **纯资源模组**:清单模组不声明 `assemblies` 即合法(只挂 pck 或作依赖锚),无实例、无钩子。

## 依赖声明惯例

```json
"depends": [
  { "uid": "com.other.mod", "version": "^2.0.0" },     // 硬依赖:缺失/版本不符 → 放弃加载
  { "uid": "com.other.lib", "soft": true }             // 软依赖:仅约束顺序(存在则保证排在其后)
]
```

- 依赖 uid 是对方的**模组 uid**(mods[].uid),跨清单引用,不再需要知道对方内部结构。
- 区间语法:`^` 兼容区(0.x 按次版本收口)、`~` 补丁区、`>=` `<` 等;裸精确天然兼容。单声明内不支持空格组合。
- 排序确定性:拓扑并列按 uid 字典序;没有也不需要 priority 字段。
- 循环依赖整批拒绝(输出完整链);版本不符时依赖方跳过、被依赖方正常加载。
- 软依赖上声明 version 目前仅记录不校验(顺序优先语义);别依赖它做版本门禁。

## 特性绑定规则(D18)

- 加载器**只扫描模组清单 `assemblies` 列出的 dll**,目录内其他 dll 不扫描;
  扫描目标:public、非 abstract、非泛型定义的 `LmMod` 子类。
- 绑定报告三分工:清单声明了 uid 但扫不到类 → **error**(该模组失败);
  扫到 `[LmMod]` 但 uid 未在清单声明 → **warning**(孤立类);
  模组因依赖被跳过 → 正常跳过报告,不产生绑定警告。
- 继承 `LmMod` 忘标特性、把特性标到非 LmMod 类/抽象类上 → 告警忽略(lint 提前抓)。

## HarmonyX patch 注意事项(核心红线)

**HarmonyX 全链由模组自带,loader 不供给、不引用:**

```xml
<!-- MyMod.csproj -->
<ItemGroup>
  <PackageReference Include="HarmonyX" Version="2.16.1" />
  <PackageReference Include="MonoMod.RuntimeDetour" Version="25.3.6" />
  <PackageReference Include="LMLoader.Api" Version="1.1.0" />
</ItemGroup>
<PropertyGroup>
  <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
</PropertyGroup>
```

- per-mod Harmony 实例约定 id = 模组 uid:`var patcher = new Harmony(Uid);`
- **泛型方法定义不可 patch**(`MethodInfo` 是 definition 时 MonoMod 直接失败);
  关闭后 `MakeGenericMethod(typeof(...))` 得到的**具体实例化**可以。
- **多线程中途 patch 安全**(P0-1 实测):后台线程高频调用目标方法时打入 detour,
  patch 立即生效、无崩溃——但请自行保证 patch 目标与线程的竞态语义。
- 反射按字符串查类型必须用**完整命名空间全名**——无命名空间的类无法被按名查找。
- 不要把 HarmonyX 放进游戏依赖闭包(游戏 data 目录):导出构建的默认 ALC 会按 deps.json
  再解析出第二份 MonoMod,动态代理类型身份分裂,patch 必失败。
  随模组分发的副本由 loader 忽略重名库并告警。
- patch 游戏自身方法:宿主游戏配置了 `GameAssemblyResolver` 时可直接 `typeof(游戏类型)` 引用
  (工程引用游戏程序集并设 `Private=false`,运行期解析走宿主 ALC)。
- **模组目录卫生**:模组目录只应出现本体 dll 与自带依赖链;GodotSharp/loader 内部库的
  副本会被 loader 忽略并告警——csproj 中把 loader 引用设 `Private=false`,
  构建后清理传递拷贝(样例工程 `CleanModOutput` 目标可照抄)。

## pck 资源约定

- 资源放进**独立的 Godot 工程**导出 pck,目录结构对应 `res://mods/<modUid>/`;
  导出:`godot --headless --path <模组pck工程> --export-pack "PCK" "../..../MyMod/pack.pck"`
  (或用 CLI:`lmcli pack-pck --godot ... --project <pck工程目录> --preset PCK --output pack.pck`)。
- 模组工程源码目录放 `.gdignore`,否则游戏导出失败。
- pck 声明在模组级 `pck[]`:**早于一切代码加载**;仅被加载的模组才挂载;
  多模组 pck 按拓扑序后挂载覆盖。
- 模组资源树:游戏目录旁 `mods/<uid>/` 为物理副本(手动部署/Workshop 均如此);
  **游戏导出后 exe 旁的 mods 不会自动更新**,重新部署,否则"看似失效"。

## 配置语义

- **只在 `OnPreLoad` 里 `Bind`**(合并已完成的声明会抛错);`OnLoad` 起读到合并后值。
- 文件 `user://configs/<模组uid>.toml`,一模组一份;首启写回全部默认值;用户手改即改即生效(热重载)。
- 合并规则:缺失键补默认写回;孤儿键保留;类型/取值不符回退默认+警告;解析失败整模组默认且不写回。
- `SettingChanged` 在后台线程触发——动 Godot 节点请 `CallDeferred`。
- 详见 [mod 配置](mod-config.md)。

## SceneTree 集成

```csharp
public override void OnLoad()
{
    LmScene.NodeAdded += n => { /* 全局节点监听 */ };
    var mount = /* 经 Context/宿主 */;   // /root/LMLoader/Mods/<你的 modUid sanitized>
}
```

- 节点名禁止 `.` `:` `@` `/` `"` `%` 等字符(挂载点已统一替换 `_`)。
- 启动期同步 `AddChild` 到根会被 Godot 拒绝(父节点布置中)——用 `CallDeferred`;
  延迟入树的节点**不触发 `_Ready`**(Godot 4.7 实测行为)。

## 分发

- 原生形态:整个模组目录(含 mod.json)放进游戏 `mods/` 或 Workshop 条目。
- Thunderstore:`lmcli pack-ts --dir MyMod --team 你的团队 --deps-root <mods根>` 生成平台包
  (包内 mod.json 让它解压后仍是原生格式;包名缺省 = 首个模组 uid 尾段)。发布前 `lmcli lint`。
- Steam Workshop:订阅后内容落在 `<Steam库>/steamapps/workshop/content/<appid>/<条目id>/`,
  loader 自动发现其中的 mod.json;条目内必须含 mod.json。
- 版本与兼容:清单 `loaderVersion` 写当前 API 的区间(如 `^1.1.0`;缺省 = 当前主版本兼容区);升级 API 前查变更记录。
