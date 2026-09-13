// SPDX-License-Identifier: LGPL-3.0-or-later

namespace LMLoader.Api;

/// <summary>
/// 模组模块基类。模块是模组的最小加载单位,由 mod.json 的 <c>entry.modules[].type</c> 显式声明(决策 D3),
/// 加载器反射实例化(要求有无参构造函数)。依赖只声明在 mod.json,不做代码特性双声明(D2)。
/// 生命周期 v1 仅 PreLoad/Load/PostLoad;Unload 不实装,运行期开关以重启生效为可靠路径(范围决策)。
/// </summary>
public abstract class LmModule
{
	private LmModuleContext? _context;

	/// <summary>模块运行上下文;由加载器注入,重复注入视为加载器缺陷。</summary>
	public LmModuleContext Context =>
		_context ?? throw new InvalidOperationException($"模块尚未被加载器初始化: {GetType().FullName}");

	/// <summary>模块 UID(= <see cref="LmModuleContext.ModuleUid"/>)。</summary>
	public string Uid => Context.ModuleUid;

	/// <summary>per-mod logger 便捷入口。</summary>
	protected ILmLogger Logger => Context.Logger;

	/// <summary>模组配置便捷入口(D11);加载器未提供配置系统时抛错。</summary>
	protected Config.ModConfig Config =>
		Context.Config ?? throw new InvalidOperationException("当前加载器未提供配置系统,无法绑定配置项");

	/// <summary>
	/// 发布跨模组服务(D4 弱类型通道):以本模块 UID 为所有者注册。
	/// 建议在 OnPreLoad/OnLoad 发布、在 OnPostLoad 消费(PostLoad 阶段全部模块已完成注册)。
	/// </summary>
	protected void PublishService<T>(T instance) where T : class
	{
		var registry = Context.Services
			?? throw new InvalidOperationException("当前加载器未提供服务注册表,无法发布跨模组服务");
		registry.Register(Context.ModuleUid, instance);
	}

	/// <summary>取回最后注册的该类型跨模组服务(D4);不存在返回 false。</summary>
	protected bool TryGetService<T>(out T instance) where T : class
	{
		var registry = Context.Services;
		if (registry is null)
		{
			instance = default!;
			return false;
		}

		return registry.TryGet(out instance);
	}

	/// <summary>由加载器调用;模组作者不要调用。</summary>
	internal void Attach(LmModuleContext context)
	{
		if (_context is not null)
		{
			throw new InvalidOperationException($"模块 {Context.ModuleUid} 已被初始化,禁止重复注入上下文");
		}

		_context = context;
	}

	/// <summary>
	/// 依赖排序完成后按序执行;用于读取清单外信息、注册服务等加载前准备。
	/// 该阶段异常仅导致本模块跳过后续阶段(宽松失败传播,D7),不影响无依赖模块。
	/// </summary>
	public virtual void OnPreLoad() { }

	/// <summary>
	/// 模块主入口;HarmonyX/MonoMod 环境已就绪,可执行 patch(阶段 3 起)。
	/// 该阶段异常仅导致本模块跳过后续阶段(宽松失败传播,D7)。
	/// </summary>
	public virtual void OnLoad() { }

	/// <summary>
	/// 全部模块 Load 完成后统一触发,用于跨模组后置协调(如读取其他模组注册的 API)。
	/// 仅对 PreLoad/Load 均成功的模块执行(D7)。
	/// </summary>
	public virtual void OnPostLoad() { }
}
