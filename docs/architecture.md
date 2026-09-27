# LMLoader 架构(architecture)

> 面向贡献者与深入排障者。使用面文档见 [mod 开发指南](mod-dev-guide.md) / [API 参考](api-reference.md) / [宿主接入](host-guide.md)。
> 决策编号(D#)见仓库外《设计决策》;本文是第一手事实描述,决策依据不入库。

## 分层与红线

```
LMLoader.Api        模组面:基类/上下文/日志/服务/配置 —— 零第三方依赖,模组工程唯一引用
LMLoader.Core       宿主内核:清单/规划/生命周期/ALC/配置/日志 —— 不引用 Godot,不引用 HarmonyX
LMLoader.GodotBridge Godot 专属:引导、pck 挂载、SceneTree 事件桥 —— 引用 GodotSharp
LMLoader.Embedded   内嵌引导:一行接入封装
LMLoader.UI         可选日志窗口
Distribution.*      Thunderstore 适配 / Workshop 订阅发现
LMLoader.Cli        作者工具链(lint / pack-pck / pack-ts,dotnet tool 分发)
```

红线:
- **Core 不引用 Godot/GodotSharp**;Godot 专属能力只进 GodotBridge。
- **Core/Api 不引用 HarmonyX**(P3-20:HarmonyX 进游戏依赖闭包会让导出构建的默认上下文二次解析
  MonoMod,动态代理类型身份分裂,patch 必失败)——HarmonyX 全链由**模组自带**,样例 csproj 需
  `CopyLocalLockFileAssemblies=true`。
- 依赖只声明在 mod.json(D2);以模组为节点拓扑排序;并列时 UID 字典序 tie-break(D8),无 priority 字段。
- 版本基线:Godot 4.7.2 / .NET 8 / HarmonyX 2.16.1 + MonoMod 25.x(D12)——升级前必须重跑 spike。

## 加载管线

```
扫描 mods 主根 + 附加根(*mod.json 递归;同目录多清单取字典序第一)
  → 清单读取(继承展开:文件级默认 → 模组覆盖,整项替换)
  → 宿主过滤(gameId 显式声明才比对;loaderVersion 缺省 = 当前主版本兼容区)
  → 依赖规划(模组粒度拓扑;uid 冲突跨清单先扫描者胜/同文件双拒;循环整批拒绝输出依赖链)
  → AfterPlan 宿主回调(pck 挂载时机)
  → 程序集加载 + 特性扫描(先行,按拓扑序全量;同路径去重)
  → 实例化(按 [LmMod(uid)] 特性绑定)+ PreLoad(全部)
  → 配置合并(PreLoad 声明完毕后读磁盘)
  → Load(全部)→ PostLoad(仅成功且非纯资源者)
  → 人可读汇总报告
```

### 特性绑定(D18)

- 清单 `mods[].uid` 是规划与工具链身份(不加载 dll 即知 uid 集合);代码 `[LmMod("uid")]` 是运行期
  绑定锚。双写由 `lmcli lint` 经 System.Reflection.Metadata 元数据读取交叉校验(不加载程序集)。
- 扫描范围:仅模组清单 `assemblies` 列出的 dll;过滤 public/非 abstract/非泛型定义。
- 报告三分工:声明无类 → error(模组失败);类无声明 → 孤立 warning;依赖跳过 → 正常跳过报告。
- 一个 dll 可服务多个模组(共享 ALC 全局联合绑定;程序集先行加载使绑定与拓扑序无关)。
- 无 `assemblies` 的模组 = 纯资源/依赖锚:无实例、无钩子,直接成功。

## 程序集加载(ALC,D1)

- 全部模组装进**单一共享非收集式** ALC(`LMLoader-Mods`);不提供卸载(collectible 卸载跨进程
  非确定,P0-1 扩展轮实证)。
- 解析回退顺序:loader 公共库注册表 → 游戏程序集解析器(宿主注入;GodotBridge 缺省解析入口
  程序集)→ 各模组目录(`AssemblyDependencyResolver` 按 deps.json,缺失时直探 `{name}.dll`)→
  默认上下文。
- 模组目录重名捆绑:与 loader 公共库重名 → 忽略并告警(loader 版本胜);与先前模组捆绑重名 →
  告警(先载入者胜)。
- 防御:detour 链程序集(MonoMod/Mono.Cecil/0Harmony)的解析重定向回已加载实例,消除导出构建中
  `Assembly.Load(byte[])` 动态代理落入默认上下文产生的 MonoMod 分身(P3-20 根因)。

## 失败传播(D7)

宽松优先:全部 hook 边界 try/catch;单模组失败不杀游戏、不阻塞无依赖模组;运行期失败级联跳过
硬依赖者(不动点传播,跳过不回退已完成阶段);PostLoad 仅对成功者执行且失败不级联;strict 开关
预留(任一失败即中止)。一切进加载汇总报告(模组 | 状态 | 原因),堆栈只进调试日志。

## 资源与配置

- **pck(D10)**:约定 `res://mods/<模组uid>/`;清单模组级 `pck[]` 声明,仅被加载模组挂载,按拓扑序
  后挂载覆盖(replaceFiles),运行时不做重叠检测;Godot 无 pck 卸载 API,挂载单向。
- **配置(D11)**:`user://configs/<模组uid>.toml`(Steam 环境游戏目录不可写,user:// 是硬约束);
  合并语义缺失键补默认写回/孤儿键保留/类型不符回退+警告/解析失败整模组默认不写回;热重载 =
  FileSystemWatcher + 500ms 防抖 + 2s mtime 兜底扫描(FSW 不保证送达),写回走临时文件原子替换,
  并发经合并闸门串行化。
- **日志(D6)**:自研 ILogSink(零依赖);Console/File(FileShare.ReadWrite 可 tail)/RingBuffer 三
  实现;按模组 uid 分组,loader 自身为特殊 uid `LMLoader`。

## Godot 4.7.2 平台工程事实(排障清单)

- 游戏程序装载在 Godot 自身 ALC,模组 ALC 默认上下文按名不可达 → 宿主必须按名供给
  (Embedded Initialize 自动按调用方程序集建立缺省值)。
- 启动期同步 `AddChild` 到根被拒(父节点布置中)→ 引导节点一律 CallDeferred;**延迟入树的节点
  不触发 `_Ready`** → 引导走显式 `RunBoot`(幂等)。
- 节点名禁止 `.` `:` `@` `/` `"` `%` → 挂载点统一替换 `_`。
- 导出要求项目根存在 `.sln`;工程目录内的模组源码目录必须放 `.gdignore`。
- **导出后 exe 旁 `mods/` 是手动部署副本,导出不更新它**——陈旧副本会让新模组"看似失效"
  (P4-1 实录:误诊半天);重新部署: `rm -rf build/mods && cp -r mods build/mods`。
- `.godot/mono` 构建状态残留可致 headless 启动无限挂起 → 删除重建。
- 泛型方法定义不可 patch;关闭后 `MakeGenericMethod` 具体实例化可以。
- 多线程中途 patch 安全(实测);反射按字符串查类型须完整命名空间全名。
- HarmonyX/MonoMod 版本升级前必须重跑 spike(`D:\Project\Godot\godot-test`,保留勿删)。
- NativeAOT/裁剪路线一旦落地,HarmonyX 依赖 JIT 的前提失效——长期跟踪项。
