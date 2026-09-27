// SPDX-License-Identifier: LGPL-3.0-or-later

namespace LMLoader.Api;

/// <summary>
/// 模组基类。模组是加载的最小单位,由代码侧 <see cref="LmModAttribute"/> 声明身份、
/// mod.json 的 <c>mods[]</c> 声明依赖与资源;加载器扫描模组声明的程序集,
/// 按特性 uid 绑定并反射实例化(要求公共无参构造)。依赖只声明在 mod.json,不做代码双声明。
/// 生命周期仅 PreLoad/Load/PostLoad;Unload 不实装,运行期开关以重启生效为可靠路径(范围决策)。
/// </summary>
public abstract class LmMod
{
	private LmModContext? _context;

	/// <summary>模组运行上下文;由加载器注入,重复注入视为加载器缺陷。</summary>
	public LmModContext Context =>
		_context ?? throw new InvalidOperationException($"模组尚未被加载器初始化: {GetType().FullName}");

	/// <summary>模组 UID(= <see cref="LmModContext.Uid"/>,与 <see cref="LmModAttribute"/> 声明一致)。</summary>
	public string Uid => Context.Uid;

	/// <summary>per-mod logger 便捷入口。</summary>
	protected ILmLogger Logger => Context.Logger;

	/// <summary>模组配置便捷入口;加载器未提供配置系统时抛错。</summary>
	protected Config.ModConfig Config =>
		Context.Config ?? throw new InvalidOperationException("当前加载器未提供配置系统,无法绑定配置项");

	/// <summary>
	/// 发布跨模组服务(弱类型通道):以本模组 UID 为所有者注册。
	/// 建议在 OnPreLoad/OnLoad 发布、在 OnPostLoad 消费(PostLoad 阶段全部模组已完成注册)。
	/// </summary>
	protected void PublishService<T>(T instance) where T : class
	{
		var registry = Context.Services
			?? throw new InvalidOperationException("当前加载器未提供服务注册表,无法发布跨模组服务");
		registry.Register(Context.Uid, instance);
	}

	/// <summary>取回最后注册的该类型跨模组服务;不存在返回 false。</summary>
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
	internal void Attach(LmModContext context)
	{
		if (_context is not null)
		{
			throw new InvalidOperationException($"模组 {Context.Uid} 已被初始化,禁止重复注入上下文");
		}

		_context = context;
	}

	/// <summary>
	/// 依赖排序完成后按序执行;用于读取清单外信息、注册服务等加载前准备。
	/// 该阶段异常仅导致本模组跳过后续阶段(宽松失败传播),不影响无依赖模组。
	/// </summary>
	public virtual void OnPreLoad() { }

	/// <summary>
	/// 模组主入口;HarmonyX/MonoMod 环境已就绪,可执行 patch。
	/// 该阶段异常仅导致本模组跳过后续阶段(宽松失败传播)。
	/// </summary>
	public virtual void OnLoad() { }

	/// <summary>
	/// 全部模组 Load 完成后统一触发,用于跨模组后置协调(如读取其他模组注册的 API)。
	/// 仅对 PreLoad/Load 均成功的模组执行。
	/// </summary>
	public virtual void OnPostLoad() { }
}
