// SPDX-License-Identifier: LGPL-3.0-or-later

using Godot;

namespace LMLoader.GodotBridge.SceneTreeEvents;

/// <summary>
/// SceneTree 信号 → C# 事件桥(模组订阅游戏级时机,loader 只做桥接;不造 tick 抽象,
/// 逐帧逻辑直接用 Godot 原生 Node)。
/// </summary>
public sealed class SceneTreeEventBridge
{
	private readonly SceneTree _tree;

	public event Action<Node>? NodeAdded;

	public event Action<Node>? NodeRemoved;

	/// <summary>当前场景切换(Godot scene_changed 信号无参数;需要新场景时读 tree.CurrentScene)。</summary>
	public event Action? SceneChanged;

	public SceneTreeEventBridge(SceneTree tree)
	{
		_tree = tree ?? throw new ArgumentNullException(nameof(tree));
		_tree.NodeAdded += OnNodeAdded;
		_tree.NodeRemoved += OnNodeRemoved;
		_tree.SceneChanged += OnSceneChanged;
	}

	/// <summary>断开全部订阅(桥接节点退出场景树时调用)。</summary>
	public void Detach()
	{
		_tree.NodeAdded -= OnNodeAdded;
		_tree.NodeRemoved -= OnNodeRemoved;
		_tree.SceneChanged -= OnSceneChanged;
	}

	private void OnNodeAdded(Node node) => NodeAdded?.Invoke(node);

	private void OnNodeRemoved(Node node) => NodeRemoved?.Invoke(node);

	private void OnSceneChanged() => SceneChanged?.Invoke();
}

/// <summary>
/// 模组侧静态门面:模组代码经 <c>LmScene.NodeAdded += ...</c> 订阅场景级事件。
/// 生命周期由 loader 引导节点负责 Attach/Detach。
/// </summary>
public static class LmScene
{
	private static SceneTreeEventBridge? _bridge;

	/// <summary>节点进入场景树(含其他模组挂载的节点)。</summary>
	public static event Action<Node>? NodeAdded;

	/// <summary>节点即将离开场景树。</summary>
	public static event Action<Node>? NodeRemoved;

	/// <summary>当前场景切换(无参数;需要新场景时经 GetTree().CurrentScene 读取)。</summary>
	public static event Action? SceneChanged;

	/// <summary>由 loader 引导节点调用;模组作者不要调用。</summary>
	public static void Attach(SceneTree tree)
	{
		Detach();
		_bridge = new SceneTreeEventBridge(tree);
		_bridge.NodeAdded += n => NodeAdded?.Invoke(n);
		_bridge.NodeRemoved += n => NodeRemoved?.Invoke(n);
		_bridge.SceneChanged += () => SceneChanged?.Invoke();
	}

	/// <summary>由 loader 引导节点调用;幂等。</summary>
	public static void Detach()
	{
		_bridge?.Detach();
		_bridge = null;
	}
}
