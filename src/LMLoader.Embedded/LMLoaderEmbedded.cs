// SPDX-License-Identifier: LGPL-3.0-or-later

using Godot;
using LMLoader.GodotBridge.Autoload;

namespace LMLoader.Embedded;

/// <summary>
/// 内嵌版初始化入口(草稿"内嵌版:开发者一行代码接入")。
/// 在游戏入口代码(主场景 _Ready / 既有 Autoload)中调用 <see cref="Initialize"/>;
/// 引导节点入树即开始加载流程(扫描 → 规划 → 挂载 pck → 模组生命周期)。
/// </summary>
public static class LMLoaderEmbedded
{
	/// <summary>
	/// 创建并挂入引导节点,立即开始模组加载。
	/// </summary>
	/// <param name="tree">当前场景树(入口代码中通常为 <c>GetTree()</c>)。</param>
	/// <param name="configure">入树前配置(GameId/ModsRootOverride/ApiVersion),可省略。</param>
	/// <returns>引导节点;经其 Services / LastLoadResult / GetModMountPoint 访问运行期能力。</returns>
	public static LMLoaderAutoload Initialize(SceneTree tree, Action<LMLoaderAutoload>? configure = null)
	{
		ArgumentNullException.ThrowIfNull(tree);

		var loader = new LMLoaderAutoload();
		configure?.Invoke(loader);
		tree.Root.AddChild(loader); // AddChild 触发 _Ready,开始引导流程
		return loader;
	}
}
