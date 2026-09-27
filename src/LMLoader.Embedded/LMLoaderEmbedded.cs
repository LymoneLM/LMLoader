// SPDX-License-Identifier: LGPL-3.0-or-later

using System.Runtime.CompilerServices;
using System.Reflection;
using Godot;
using LMLoader.GodotBridge.Autoload;

namespace LMLoader.Embedded;

/// <summary>
/// 内嵌版初始化入口:开发者一行代码接入。
/// 在游戏入口代码(主场景 _Ready / 既有 Autoload)中调用 <see cref="Initialize"/>;
/// 引导节点经延迟添加入树,入树即开始加载流程(扫描 → 规划 → 挂载 pck → 模组生命周期),
/// 完成时机经 <see cref="LMLoaderAutoload.BootCompleted"/> 事件通知。
/// </summary>
public static class LMLoaderEmbedded
{
	/// <summary>
	/// 创建引导节点并<b>延迟</b>挂入场景树根(Godot 惯例:父节点布置子节点期间禁止同步 AddChild)。
	/// 加载为异步完成:订阅 <c>loader.BootCompleted</c> 后经 <c>loader.LastLoadResult</c> 取结果。
	/// </summary>
	/// <param name="tree">当前场景树(入口代码中通常为 <c>GetTree()</c>)。</param>
	/// <param name="configure">入树前配置(GameId/ModsRootOverride/ApiVersion),可省略。</param>
	/// <returns>引导节点;经其 Services / LastLoadResult / GetModMountPoint 访问运行期能力。</returns>
	/// <remarks>
	/// 游戏程序集缺省解析:取<b>本方法调用方程序集</b>(典型即游戏程序集本体;Godot 进程内
	/// GetEntryAssembly 与 AppDomain 枚举均不可达游戏程序集——它装在 Godot 自身 ALC),
	/// 未在 configure 中显式覆盖 <c>GameAssemblyResolver</c> 时自动生效。
	/// </remarks>
	[MethodImpl(MethodImplOptions.NoInlining)] // 保证 GetCallingAssembly 语义稳定
	public static LMLoaderAutoload Initialize(SceneTree tree, Action<LMLoaderAutoload>? configure = null)
	{
		ArgumentNullException.ThrowIfNull(tree);

		var gameAssembly = Assembly.GetCallingAssembly();
		var loader = new LMLoaderAutoload();
		configure?.Invoke(loader);
		loader.GameAssemblyResolver ??= n =>
			string.Equals(n.Name, gameAssembly.GetName().Name, StringComparison.Ordinal) ? gameAssembly : null;

		// 始终延迟入树(父节点布置子节点期间 Godot 拒绝同步 AddChild,典型于主场景 _Ready 期间),
		// 桥项目无源生成器,托管引导经 Callable 包装延迟执行;完成时机统一经 BootCompleted 通知。
		tree.Root.CallDeferred(Node.MethodName.AddChild, loader);
		Callable.From(loader.RunBoot).CallDeferred();
		return loader;
	}
}
