// SPDX-License-Identifier: LGPL-3.0-or-later

using Godot;
using LMLoader.Api;
using LMLoader.Core;
using LMLoader.Core.Lifecycle;
using LMLoader.Core.Logging;
using LMLoader.GodotBridge.Logging;
using LMLoader.GodotBridge.PckMounting;
using LMLoader.GodotBridge.SceneTreeEvents;

namespace LMLoader.GodotBridge.Autoload;

/// <summary>
/// LMLoader 引导节点(决策 D5):内嵌接入的 Godot 侧入口。
/// 入树(_Ready)即执行:扫描 mods → 依赖规划 → 挂载 pck(AfterPlan 回调,pck 早于逻辑)→
/// 生命周期 → 建立 <c>LMLoader/Mods/&lt;modUid&gt;</c> 挂载点树(仅加载成功的模组)。
/// 接入方式:游戏侧在自己代码中 <c>new LMLoaderAutoload() { GameId = "..." }</c> 后
/// <c>GetTree().Root.AddChild(loader)</c>(见 LMLoader.Embedded),或注册为项目 Autoload。
/// 配置属性须在入树前设置。
/// </summary>
public partial class LMLoaderAutoload : Node
{
	public static LMLoaderAutoload? Instance { get; private set; }

	private ModManager? _modManager;

	/// <summary>宿主游戏标识(gameId 过滤,D9);入树前设置。</summary>
	public string GameId { get; set; } = "";

	/// <summary>mods 根目录覆盖;缺省 <c>res://mods</c> 全局化路径。入树前设置。</summary>
	public string ModsRootOverride { get; set; } = "";

	/// <summary>loaderVersion 基线覆盖;缺省取 LMLoader.Api 程序集版本。入树前设置。</summary>
	public Version? ApiVersion { get; set; }

	/// <summary>最近一次加载结果;未加载(无 mods 目录)时为 null。</summary>
	public LoadResult? LastLoadResult { get; private set; }

	/// <summary>跨模组服务注册表(D4)。</summary>
	public ServiceRegistry? Services => _modManager?.Services;

	private Node? _modsMountRoot;

	public override void _Ready()
	{
		Instance = this;
		Name = "LMLoader"; // 固定挂载点树根名(D5):/root/LMLoader/Mods/<modUid>

		var router = new LoggerRouter { MinimumLevel = LmLogLevel.Info };
		router.AddSink(new GodotLogSink());
		var logger = router.GetLogger(LifecycleRunner.LoaderLogUid);

		LmScene.Attach(GetTree());

		var modsRoot = ModsRootOverride.Length > 0
			? ModsRootOverride
			: ProjectSettings.GlobalizePath("res://mods");

		if (!Directory.Exists(modsRoot))
		{
			// 游戏未携带 mods 目录属正常形态,不视为错误
			logger.Info($"mods 目录不存在({modsRoot}),跳过模组加载");
			return;
		}

		_modManager = new ModManager(new LoaderOptions
		{
			ModsRootPath = modsRoot,
			GameId = GameId,
			ApiVersion = ApiVersion,
			AfterPlan = plan => PckMounter.MountInPlanOrder(plan, logger),
		}, router);

		LastLoadResult = _modManager.LoadAll();
		BuildMountPoints(LastLoadResult);
	}

	public override void _ExitTree()
	{
		LmScene.Detach();
		if (ReferenceEquals(Instance, this))
		{
			Instance = null;
		}
	}

	/// <summary>取模组私有挂载点(D5);模组加载成功后可用,否则 null。</summary>
	public Node? GetModMountPoint(string modUid) => _modsMountRoot?.GetNodeOrNull(modUid);

	private void BuildMountPoints(LoadResult result)
	{
		_modsMountRoot = new Node { Name = "Mods" };
		AddChild(_modsMountRoot);

		if (result.Lifecycle is null)
		{
			return;
		}

		foreach (var modUid in result.Lifecycle.Results
			.Where(r => r.Succeeded)
			.Select(r => r.ModUid)
			.Distinct(StringComparer.Ordinal))
		{
			_modsMountRoot.AddChild(new Node { Name = modUid });
		}
	}
}
