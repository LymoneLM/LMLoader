// SPDX-License-Identifier: LGPL-3.0-or-later

using LMLoader.Api.Config;

namespace LMLoader.Api;

/// <summary>
/// 模块运行上下文(只读视图),由加载器在实例化后、任何生命周期钩子前注入(D3/D5)。
/// </summary>
public sealed class LmModuleContext
{
	public LmModuleContext(
		string moduleUid,
		string modUid,
		string modDirectory,
		ILmLogger logger,
		ServiceRegistry? services = null,
		ModConfig? config = null)
	{
		ModuleUid = moduleUid ?? throw new ArgumentNullException(nameof(moduleUid));
		ModUid = modUid ?? throw new ArgumentNullException(nameof(modUid));
		ModDirectory = modDirectory ?? throw new ArgumentNullException(nameof(modDirectory));
		Logger = logger ?? throw new ArgumentNullException(nameof(logger));
		Services = services;
		Config = config;
	}

	/// <summary>模块唯一标识(mod.json 中该模块的 uid)。</summary>
	public string ModuleUid { get; }

	/// <summary>所属模组的 uid。</summary>
	public string ModUid { get; }

	/// <summary>模组目录物理路径(dll 与自带依赖所在;res:// 资源经由 pck 挂载,不在此目录语义内)。</summary>
	public string ModDirectory { get; }

	/// <summary>per-mod logger(输出按模组划分,D6)。</summary>
	public ILmLogger Logger { get; }

	/// <summary>跨模组服务注册表(D4);加载器未提供时为 null(模块侧 Publish 将抛错)。</summary>
	public ServiceRegistry? Services { get; }

	/// <summary>
	/// 模组配置声明面(D11);加载器未提供配置系统时为 null(模块侧 Bind 将抛错)。
	/// 在 OnPreLoad 中 <see cref="ModConfig.Bind{T}"/>,OnLoad 起读到的即为合并后值。
	/// </summary>
	public ModConfig? Config { get; }

}
