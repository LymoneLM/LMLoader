// SPDX-License-Identifier: LGPL-3.0-or-later

using System.Reflection;
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
/// LMLoader 引导节点:内嵌接入的 Godot 侧入口。
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

	/// <summary>宿主游戏标识(gameId 过滤);入树前设置。</summary>
	public string GameId { get; set; } = "";

	/// <summary>mods 根目录覆盖;缺省 <c>res://mods</c> 全局化路径。入树前设置。</summary>
	public string ModsRootOverride { get; set; } = "";

	/// <summary>loaderVersion 基线覆盖;缺省取 LMLoader.Api 程序集版本。入树前设置。</summary>
	public Version? ApiVersion { get; set; }

	/// <summary>
	/// 游戏程序集解析器(游戏程序集在宿主自身 ALC;模组要 patch 游戏方法时必需)。
	/// 嵌入模式典型实现:<c>n => n.Name == "我的游戏程序集名" ? typeof(入口类型).Assembly : null</c>。入树前设置。
	/// </summary>
	public Func<AssemblyName, Assembly?>? GameAssemblyResolver { get; set; }

	/// <summary>最近一次加载结果;未加载(无 mods 目录)时为 null。</summary>
	public LoadResult? LastLoadResult { get; private set; }

	/// <summary>跨模组服务注册表。</summary>
	public ServiceRegistry? Services => _modManager?.Services;

	/// <summary>内存环形日志缓冲;日志窗口数据源,宿主可另接 UI。</summary>
	public RingBufferLogSink? LogBuffer => _logBuffer;

	/// <summary>配置协调器;未引导或未启用配置时为 null(热重载已随引导开启)。</summary>
	public LMLoader.Core.Config.ConfigManager? Configs => _modManager?.Configs;

	private Node? _modsMountRoot;
	private bool _booted;
	private RingBufferLogSink? _logBuffer;
	private LMLoader.UI.LmLogWindow? _logWindow;
	private string? _summaryForWindow;

	/// <summary>
	/// 引导流程结束(成功、部分失败或无 mods 目录跳过);经 <see cref="LastLoadResult"/> 取结果
	/// (无 mods 目录时为 null)。
	/// </summary>
	public event Action? BootCompleted;

	public override void _Ready() => RunBoot();

	/// <summary>
	/// 执行引导(幂等)。同步入树时由 <see cref="LMLoader.Embedded.LMLoaderEmbedded.Initialize"/> 立即调用;
	/// 延迟入树时经 CallDeferred 调用(实测 Godot 4.7.2 中,启动期延迟入树的节点不触发 _Ready,
	/// 故 _Ready 仅作项目 Autoload 注册路径的兜底入口)。
	/// </summary>
	public void RunBoot()
	{
		if (_booted)
		{
			return;
		}

		_booted = true;
		Boot();
	}

	private void Boot()
	{
		Instance = this;
		Name = "LMLoader"; // 固定挂载点树根名:/root/LMLoader/Mods/<modUid>

		// 三个 sink:Godot 控制台 + 内存环形缓冲(日志窗口) + 文件(user://logs);级别 Debug 便于模组排障
		var router = new LoggerRouter { MinimumLevel = LmLogLevel.Debug };
		router.AddSink(new GodotLogSink());
		_logBuffer = new RingBufferLogSink();
		router.AddSink(_logBuffer);
		router.AddSink(new FileLogSink(
			ProjectSettings.GlobalizePath("user://logs/LMLoader.log"), append: true));
		var logger = router.GetLogger(LifecycleRunner.LoaderLogUid);

		LmScene.Attach(GetTree());

		var modsRoot = ModsRootOverride.Length > 0
			? ModsRootOverride
			: ProjectSettings.GlobalizePath("res://mods");

		if (!Directory.Exists(modsRoot))
		{
			// 游戏未携带 mods 目录属正常形态,不视为错误
			logger.Info($"mods 目录不存在({modsRoot}),跳过模组加载");
			BootCompleted?.Invoke();
			return;
		}

		_modManager = new ModManager(new LoaderOptions
		{
			ModsRootPath = modsRoot,
			GameId = GameId,
			ApiVersion = ApiVersion,
			GameAssemblyResolver = GameAssemblyResolver,
			AfterPlan = plan => PckMounter.MountInPlanOrder(plan, logger),
			ConfigRootPath = ProjectSettings.GlobalizePath("user://configs"), // Steam 环境游戏目录不可写,配置必须落 user://
		}, router);

		LastLoadResult = _modManager.LoadAll();
		_summaryForWindow = LastLoadResult.SummaryText;
		_modManager.Configs?.StartHotReload(); // 4.4:配置改文件即生效
		logger.Debug($"引导完成: 配置根={(_modManager.Configs is null ? "(未启用)" : "已启用")},热重载=已启动");
		BuildMountPoints(LastLoadResult);
		BootCompleted?.Invoke();
	}

	public override void _UnhandledKeyInput(InputEvent @event)
	{
		// F12 切换日志窗口(可选组件,4.6)
		if (@event is InputEventKey { Pressed: true, Keycode: Key.F12 })
		{
			OpenLogWindow();
		}
	}

	/// <summary>打开或切换日志窗口(分级查看 + 加载汇总报告);数据源为本 loader 的环形缓冲。</summary>
	public void OpenLogWindow()
	{
		if (_logWindow is not null)
		{
			_logWindow.Visible = !_logWindow.Visible;
			return;
		}

		if (_logBuffer is null)
		{
			return; // 未引导(未入树),无数据源
		}

		_logWindow = new LMLoader.UI.LmLogWindow(_logBuffer);
		AddChild(_logWindow);
		if (_summaryForWindow is not null)
		{
			_logWindow.ShowLoadSummary(_summaryForWindow);
		}
	}

	public override void _ExitTree()
	{
		LmScene.Detach();
		_modManager?.Dispose();
		if (ReferenceEquals(Instance, this))
		{
			Instance = null;
		}
	}

	/// <summary>取模组私有挂载点;模组加载成功后可用,否则 null。</summary>
	public Node? GetModMountPoint(string modUid) =>
		_modsMountRoot?.GetNodeOrNull(SanitizeNodeName(modUid));

	/// <summary>Godot 节点名禁止 . : @ / " % 等字符,统一替换为下划线。</summary>
	private static string SanitizeNodeName(string name) =>
		new string(name.Select(c => c is '.' or ':' or '@' or '/' or '"' or '%' ? '_' : c).ToArray());

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
			_modsMountRoot.AddChild(new Node { Name = SanitizeNodeName(modUid) });
		}
	}
}
