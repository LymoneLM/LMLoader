# mod.json 模组清单规范 v1(2026-09-27 重定义,D18)

> 配套机器校验文件:[mod-json-schema.schema.json](mod-json-schema.schema.json)
> 运行时解析语义见下文「验证与容错」;版本声明支持区间语法,裸精确版本天然兼容。

## 模型

一个 mod.json = **文件级声明块 + 一个模组列表**。模组(Mod)是加载/依赖/排序/失败传播的
最小单位,唯一身份为 `uid`(反向域名);一个清单可并列声明多个模组,共享文件级默认值。

- 文件级字段是**可继承的默认值**:模组级同名声明**整项覆盖**(数组替换,不合并)。
  `loaderVersion` 是例外——文件级唯一,不参与覆盖。
- `entry`/`modules` 概念已删除。模组代码入口由代码侧 `[LmMod("uid")]` 特性声明,
  加载器扫描模组声明的程序集、按特性 uid 与清单绑定;清单不再书写类名字符串。

## 文件定位约定

- 每个模组目录下,加载器按 `*mod.json` 后缀匹配清单文件(文件名任意,如 `mymod.mod.json`)。
- `assemblies`/`pck` 路径均相对模组目录。
- 加载器对 mods 根目录递归扫描,以兼容 Steam Workshop 的深层订阅目录。

## 文件级字段

| 字段 | 类型 | 必填 | 可被模组覆盖 | 说明 |
|---|---|---|---|---|
| `schemaVersion` | int | ✔ | — | 清单格式主版本。当前为 `1`。不识别的主版本 → **拒载并提示** |
| `mods` | Mod[] | ✔ | — | 模组声明列表,≥1 个 |
| `gameId` | string | ✗ | ✔ | **目标游戏标识(可选,D18)**。缺省 = 通用模组,装载于任何宿主;宿主设置 `GameId` 且清单声明了 → 必须匹配,不匹配拒载 |
| `loaderVersion` | string | ✗ | ✗(文件级唯一) | 依赖的 LMLoader API **版本区间**。缺省 = 当前 loader 主版本的兼容区(如 loader 1.x 时缺省 `^1.0.0`) |
| `version` | string | (✔) | ✔ | 模组版本,SemVer 2.0.0。**文件级或模组级至少一处必须存在**(依赖区间判定锚),两处皆无 → 元数据错误 |
| `name` | string | ✗ | ✔ | 模组显示名。缺省 = 模组 uid 尾段 |
| `authors` | string[] | ✗ | ✔ | 作者列表 |
| `description` | string | ✗ | ✔ | 简介 |
| `icon` | string | ✗ | ✔ | 图标路径(相对模组目录) |
| `website` | string | ✗ | ✔ | 主页/仓库链接 |
| `tags` | string[] | ✗ | ✔ | 展示用标签。loader 本体不消费,供管理器/分发平台过滤 |
| `distribution` | object | ✗ | — | 分发平台别名,见下 |

## `mods[]` 模组

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `uid` | string | ✔ | 模组唯一标识,反向域名结构(如 `com.author.modname`)。全局唯一;**必须与代码侧 `[LmMod("uid")]` 一致** |
| `assemblies` | string[] | ✗ | 该模组要加载的 dll 列表(相对模组目录,可多个)。**仅当模组被加载时才加载**;同一路径跨模组重复声明只加载一次。**缺省 = 纯资源/依赖锚模组**(无代码、无实例、无生命周期钩子) |
| `pck` | string[] | ✗ | 该模组的 pck 资源包(相对模组目录)。**pck 早于一切代码加载**;仅被加载的模组才挂载;按模组拓扑序后挂载覆盖;资源应置于 `res://mods/<uid>/` 下 |
| `depends` | Dependency[] | ✗ | 依赖声明。**依赖只声明在此处**,不做代码特性双声明 |
| 继承项 | — | — | `version`/`name`/`authors`/`description`/`icon`/`website`/`tags` 可在此覆盖文件级默认值 |

## `mods[].depends[]` 依赖

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `uid` | string | ✔ | 目标**模组** uid(可指向其他清单中的模组) |
| `version` | string | ✗ | 目标模组的**版本区间**(裸精确 = 精确匹配)。缺失 = 接受任意版本 |
| `soft` | bool | ✗ | 默认 `false`。`true` 为软依赖:**仅约束顺序**(存在则保证排在其后,缺失则跳过该约束正常加载);硬依赖缺失 → 该模组放弃加载 |

### 代码侧绑定(`LMLoader.Api`)

```csharp
[LmMod("com.author.modname")]        // 特性 uid = 清单 mods[].uid
public class MyModule : LmMod { }    // 公共、非 abstract、非泛型定义
```

- 加载器只扫描**被加载模组 `assemblies` 列出的 dll**;目录内其他 dll 不扫描。
- 绑定报告:清单声明了 uid 但扫不到类 → **error**(该模组失败);扫到带特性的类但 uid 未声明 →
  **warning**;模组因依赖被跳过 → 正常跳过报告,不产生绑定警告。
- 继承 `LmMod` 未标注特性、标注了 `[LmMod]` 但未继承/abstract/非 public/泛型定义 → 告警并忽略。
- 一个 dll 可服务多个模组(共享 ALC 全局联合绑定)。

### `distribution` 分发别名

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `distribution.thunderstore.team` | string | ✗ | Thunderstore 团队名。打包器必需,缺失时 `pack-ts` 报错 |
| `distribution.thunderstore.name` | string | ✗ | Thunderstore 包名。缺省 = **首个模组** uid 尾段(`.`→`_`) |

uid 是运行时唯一身份;此处仅为打包器向分发平台映射身份提供显式来源,不做 UID 自动推导。

## 版本区间语法

| 声明 | 含义 |
|---|---|
| `*` 或缺省 | 任意版本 |
| `1.2.3` / `=1.2.3` | 精确版本(部分号 `1.2` 补零为 `1.2.0`) |
| `^1.2.3` | 兼容区 `[1.2.3, 2.0.0)`;`0.x` 按次版本收口(`^0.2.3` = `[0.2.3, 0.3.0)`;`^0.0.3` = `[0.0.3, 0.0.4)`) |
| `~1.2.3` | 补丁区 `[1.2.3, 1.3.0)`;`~1.2` 同;`~1` = `[1.0.0, 2.0.0)` |
| `>=v` `>v` `<=v` `<v` | 开/闭端点比较 |

- 单个声明内不支持空格/逗号组合(交集场景用多个依赖声明表达,保持清单简单)。
- prerelease 与 build metadata 遵循 SemVer 2.0.0 比较规则(build 不参与优先级)。

## 示例

最小模组(单模组,展示全部默认值):

```json
{
  "schemaVersion": 1,
  "version": "1.0.0",
  "gameId": "com.game.identifier",
  "mods": [
    { "uid": "com.author.mymod", "assemblies": ["MyMod.dll"] }
  ]
}
```

多模组合集(依赖与资源各自声明,继承文件级元数据):

```json
{
  "schemaVersion": 1,
  "gameId": "com.game.identifier",
  "loaderVersion": "^1.0.0",
  "name": "作者的工具合集",
  "version": "1.2.0",
  "authors": ["Author"],
  "tags": ["content", "npc"],
  "mods": [
    {
      "uid": "com.author.pack.core",
      "assemblies": ["Pack.dll"]
    },
    {
      "uid": "com.author.pack.tools",
      "assemblies": ["Pack.dll"],
      "pck": ["tools.pck"],
      "depends": [
        { "uid": "com.author.pack.core", "soft": true },
        { "uid": "com.other.lib", "version": "^2.0.0" }
      ]
    },
    {
      "uid": "com.author.pack.assets",
      "pck": ["assets.pck"]
    }
  ]
}
```

## 验证与容错(运行时语义)

- **未知字段**:忽略 + 警告(宽容读入,保证前向兼容)。JSON Schema 文件比运行时更严
  (`additionalProperties: false`),供 CLI lint 在作者侧提前报错。
- **`schemaVersion`**:主版本不识别 → 拒载并提示升级加载器。
- **`gameId`**:可选;宿主设置 `GameId` 且清单声明时比对,不匹配**整份清单拒载**。
- **`loaderVersion`**:缺省取当前 loader 主版本兼容区;声明区间不含当前 API 版本 → 整份清单拒载。
- **必填缺失 / 类型错误 / uid 非法 / version 两级皆缺**:该模组按元数据错误处理 → 跳过加载,
  硬依赖它的模组级联跳过,原因进加载汇总报告。
- **绑定**(加载期):清单声明了 `assemblies` 但按特性找不到类 → 该模组失败(Instantiation 阶段);
  扫到未声明的特性类 → 告警。
- **版本**:模组间依赖按区间语义判定(声明区间包含目标模组版本即满足);同一模组 uid 出现多份 →
  同文件内重复双方都拒,跨文件重复先扫描者(主根优先)胜、后者跳过并记录原因。
- **循环依赖**:加载前静态检测,存在环则**整批拒绝**,输出完整依赖链用于排错。
