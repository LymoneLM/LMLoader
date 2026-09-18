# mod.json 模组清单规范 v1.0

> 配套机器校验文件:[mod-json-schema.schema.json](mod-json-schema.schema.json)
> 运行时解析语义见下文「验证与容错」;版本声明支持区间语法,裸精确版本天然兼容。

## 文件定位约定

- 每个模组目录下,加载器按 `*mod.json` 后缀匹配清单文件(文件名任意,如 `mymod.mod.json`)。
- `resources.pck` 等资源路径均相对模组目录。
- 加载器对 mods 根目录递归扫描,以兼容 Steam Workshop 的深层订阅目录。

## 字段定义

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `schemaVersion` | int | ✔ | 清单格式主版本。当前为 `1`。加载器不识别的主版本 → **拒载并提示** |
| `uid` | string | ✔ | 模组唯一标识,反向域名结构(如 `com.author.modname`)。全局唯一 |
| `name` | string | ✔ | 模组显示名 |
| `version` | string | ✔ | 模组版本,SemVer 2.0.0(含 prerelease/build metadata) |
| `authors` | string[] | ✗ | 作者列表 |
| `description` | string | ✗ | 简介 |
| `icon` | string | ✗ | 图标路径(相对模组目录) |
| `website` | string | ✗ | 主页/仓库链接 |
| `tags` | string[] | ✗ | 展示用标签。loader 本体不消费,供管理器/分发平台过滤 |
| `gameId` | string | ✔ | **目标游戏标识**,与宿主游戏约定比对,不匹配拒载(防 A 游戏模组被 B 游戏 loader 扫入) |
| `loaderVersion` | string | ✔ | 依赖的 LMLoader API **版本区间**(见下「版本区间语法」)。裸精确版本天然兼容 |
| `entry` | object | ✔ | 程序集入口,见下 |
| `resources` | object | ✗ | 资源声明,见下 |
| `distribution` | object | ✗ | 分发平台别名,见下 |

### `entry` 入口

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `entry.assembly` | string | ✔ | 模组 DLL 文件名(相对模组目录),如 `Mod.dll` |
| `entry.modules` | Module[] | ✔ | 模块列表。**模块是加载的最小单位**;一个程序集可含多个模块 |

### `entry.modules[]` 模块

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `uid` | string | ✔ | 模块 UID,反向域名,全局唯一(如 `com.author.modname.core`)。依赖与拓扑排序以模块为单位 |
| `type` | string | ✔ | 模块类的**完整命名空间全名**(必须继承 `LMLoader.Api.LmModule`)。无命名空间的类无法被按名查找 |
| `depends` | Dependency[] | ✗ | 依赖声明。**依赖只声明在此处**,不做代码特性双声明 |

### `entry.modules[].depends[]` 依赖

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `uid` | string | ✔ | 目标模块 UID |
| `version` | string | ✗ | 目标模块的**版本区间**(裸精确 = 兼容旧清单)。缺失 = 接受任意版本 |
| `soft` | bool | ✗ | 默认 `false`。`true` 为软依赖:**仅约束顺序**(存在则保证排在其后,缺失则跳过该约束正常加载);硬依赖缺失 → 该模块放弃加载 |

### `resources` 资源

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `resources.pck` | string[] | ✗ | pck 包路径列表(相对模组目录)。**pck 早于逻辑加载**;按模块拓扑序后挂载覆盖;资源应置于 `res://mods/<uid>/` 下 |

### `distribution` 分发别名

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `distribution.thunderstore.team` | string | ✗ | Thunderstore 团队名。打包器必需,缺失时 `pack-ts` 报错 |
| `distribution.thunderstore.name` | string | ✗ | Thunderstore 包名。缺省 = UID 尾段(`.`→`_`) |

UID 是运行时唯一身份;此处仅为打包器向分发平台映射身份提供显式来源,不做 UID 自动推导。

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

```json
{
  "schemaVersion": 1,
  "uid": "com.author.modname",
  "name": "示例模组",
  "version": "1.2.3",
  "authors": ["Author"],
  "description": "演示完整字段的清单",
  "icon": "icon.png",
  "website": "https://example.com/modname",
  "tags": ["content", "npc"],
  "gameId": "com.game.identifier",
  "loaderVersion": "^1.0.0",
  "entry": {
    "assembly": "Mod.dll",
    "modules": [
      {
        "uid": "com.author.modname.core",
        "type": "Author.Modname.CoreModule",
        "depends": [
          { "uid": "com.other.mod.core", "version": "^2.0.0", "soft": false }
        ]
      },
      {
        "uid": "com.author.modname.extra",
        "type": "Author.Modname.ExtraModule",
        "depends": [
          { "uid": "com.author.modname.core", "soft": true }
        ]
      }
    ]
  },
  "resources": { "pck": ["mod.pck"] }
}
```

## 验证与容错(运行时语义)

- **未知字段**:忽略 + 警告(宽容读入,保证前向兼容)。JSON Schema 文件比运行时更严(`additionalProperties: false`),供 CLI lint 在作者侧提前报错。
- **`schemaVersion`**:主版本不识别 → 拒载并提示升级加载器。
- **`gameId`**:与宿主游戏约定值不匹配 → 拒载。
- **必填缺失 / 类型错误 / `uid` 或模块 `uid` 非法**:该模组按元数据错误处理 → 跳过加载,硬依赖它的模块级联跳过,原因进加载汇总报告。
- **版本**:模组间依赖按区间语义判定(声明区间包含目标模组版本即满足);同一模块 UID 出现多份 → 冲突,涉事模块放弃加载并记录原因。
- **循环依赖**:加载前静态检测,存在环则**整批拒绝**,输出完整依赖链用于排错。
