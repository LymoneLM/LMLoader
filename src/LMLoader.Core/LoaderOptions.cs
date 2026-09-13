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
	/// 宿主游戏标识;非空时与模组 <c>gameId</c> 比对,不匹配拒载(D9)。
	/// </summary>
	public string GameId { get; init; } = "";

	/// <summary>strict 开关(D7 预留):任一失败即中止;v1 默认宽松。</summary>
	public bool Strict { get; init; }

	/// <summary>
	/// loader API 版本基线(与清单 <c>loaderVersion</c> 精确比对,v1;区间语法阶段 5)。
	/// 缺省取 LMLoader.Api 程序集版本。
	/// </summary>
	public Version? ApiVersion { get; init; }

	/// <summary>
	/// loader 统一供给的公共库(D1);LMLoader.Api 自动纳入,无需重复提供。
	/// </summary>
	public IEnumerable<Assembly>? SharedLibraries { get; init; }

	/// <summary>游戏程序集解析器(P0-1:Godot 将游戏程序集装在自身 ALC,需宿主环境按名返回)。</summary>
	public Func<AssemblyName, Assembly?>? GameAssemblyResolver { get; init; }

	/// <summary>
	/// 规划完成后的挂载回调:在依赖规划成功后、生命周期执行前调用(Godot 版在此挂载 pck,
	/// 草稿:pck 早于逻辑)。回调抛异常视为宿主配置错误,直接上抛(D7 宽松语义仅针对模组代码)。
	/// 整批拒绝(循环依赖)时不会调用。
	/// </summary>
	public Action<Dependency.LoadPlan>? AfterPlan { get; init; }

	/// <summary>最低日志级别。</summary>
	public Api.LmLogLevel MinimumLogLevel { get; init; } = Api.LmLogLevel.Info;

	/// <summary>
	/// 模组配置根目录(D11):每模组一份 <c>&lt;root&gt;/&lt;modUid&gt;.toml</c>。
	/// null/空 = 关闭配置系统。宿主负责给出物理路径(Godot 版传 <c>user://configs</c> 的全局化路径;
	/// Steam 环境游戏目录不可写,user:// 是硬约束),Core 不感知 user:// 语义。
	/// </summary>
	public string? ConfigRootPath { get; init; }
}
