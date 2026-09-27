// SPDX-License-Identifier: LGPL-3.0-or-later

using System.Reflection;
using LMLoader.Api;

namespace LMLoader.Core;

/// <summary>加载器配置。无效配置(如 mods 根目录不存在)属开发者错误,ModManager 直接抛异常。</summary>
public sealed class LoaderOptions
{
	/// <summary>mods 根目录(递归扫描 <c>*mod.json</c>)。</summary>
	public required string ModsRootPath { get; init; }

	/// <summary>
	/// 附加扫描根:Steam Workshop 订阅目录等;与主根同样递归扫描 *mod.json,
	/// 同模组多根重复时以主根为准(附加根副本告警)。目录不存在时静默跳过(可选来源)。
	/// </summary>
	public IEnumerable<string>? AdditionalModsRoots { get; init; }

	/// <summary>
	/// 宿主游戏标识;非空时与清单显式声明的 <c>gameId</c> 比对,不匹配拒载整份清单。
	/// 清单缺省 gameId = 通用模组,装载于任何宿主(D18)。
	/// </summary>
	public string GameId { get; init; } = "";

	/// <summary>strict 开关:任一失败即中止;v1 默认宽松。</summary>
	public bool Strict { get; init; }

	/// <summary>
	/// loader API 版本基线(与清单 <c>loaderVersion</c> 区间比对)。
	/// 缺省取 LMLoader.Api 程序集版本。
	/// </summary>
	public Version? ApiVersion { get; init; }

	/// <summary>
	/// loader 统一供给的公共库;LMLoader.Api 自动纳入,无需重复提供。
	/// </summary>
	public IEnumerable<Assembly>? SharedLibraries { get; init; }

	/// <summary>游戏程序集解析器(Godot 将游戏程序集装在自身 ALC,需宿主环境按名返回)。</summary>
	public Func<AssemblyName, Assembly?>? GameAssemblyResolver { get; init; }

	/// <summary>
	/// 规划完成后的挂载回调:在依赖规划成功后、生命周期执行前调用(Godot 版在此挂载 pck,
	/// pck 早于逻辑)。回调抛异常视为宿主配置错误,直接上抛(宽松语义仅针对模组代码)。
	/// 整批拒绝(循环依赖)时不会调用。
	/// </summary>
	public Action<Dependency.LoadPlan>? AfterPlan { get; init; }

	/// <summary>最低日志级别。</summary>
	public Api.LmLogLevel MinimumLogLevel { get; init; } = Api.LmLogLevel.Info;

	/// <summary>
	/// 模组配置根目录:每模组一份 <c>&lt;root&gt;/&lt;模组uid&gt;.toml</c>。
	/// null/空 = 关闭配置系统。宿主负责给出物理路径(Godot 版传 <c>user://configs</c> 的全局化路径;
	/// Steam 环境游戏目录不可写,user:// 是硬约束),Core 不感知 user:// 语义。
	/// </summary>
	public string? ConfigRootPath { get; init; }

	/// <summary>
	/// 模组启停状态文件路径(D19,<c>user://mod-state.toml</c> 的全局化路径;与模组配置平级独立)。
	/// null = 状态不持久化(内存态,重启即复位;过滤逻辑不变,缺省全启用故无行为差异)。
	/// </summary>
	public string? ModStatePath { get; init; }
}
