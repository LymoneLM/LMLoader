// SPDX-License-Identifier: LGPL-3.0-or-later

using System.Reflection;
using LMLoader.Api;
using LMLoader.Core.Dependency;
using LMLoader.Core.Lifecycle;
using LMLoader.Core.Loading;
using LMLoader.Core.Logging;
using LMLoader.Core.Manifest;
using LMLoader.Core.Management;
using LMLoader.Core.Reporting;
using LMLoader.Core.Versioning;

namespace LMLoader.Core;

/// <summary>
/// 加载器门面:扫描 mods 目录 → 读取 mod.json → 过滤(gameId/loaderVersion) → 启停状态过滤(D19) →
/// 依赖规划 → 生命周期执行 → 人可读汇总(模组级失败一律记录不抛出;loader 配置错误直接抛异常)。
/// 管理面(D19):枚举发现视图 / 启停开关(即时持久化,重启进程生效)/ 状态变更事件 /
/// 依赖预计算;重启时机与方式由宿主决定。
/// </summary>
public sealed class ModManager : IDisposable
{
	private readonly LoaderOptions _options;
	private readonly ModAssemblyLoader _assemblyLoader;
	private readonly Config.ConfigManager? _configManager;
	private readonly ModStateStore? _stateStore;

	/// <summary>启停状态(内存态);持久化经由 <see cref="_stateStore"/>(未配置路径则重启复位)。</summary>
	private readonly ModState _state = new();

	/// <summary>上次 LoadAll 实际消费的状态快照(供 PendingRestart 判定);null = 尚未加载。</summary>
	private ModState? _appliedState;

	private LoadResult? _lastResult;

	/// <summary>日志中枢;宿主可自行增加 sink。</summary>
	public LoggerRouter LoggerRouter { get; }

	/// <summary>跨模组服务注册表。</summary>
	public ServiceRegistry Services { get; }

	/// <summary>配置协调器;未配置 ConfigRootPath 时为 null(热重载由此驱动)。</summary>
	public Config.ConfigManager? Configs => _configManager;

	/// <summary>
	/// 启停状态变更通知(D19):开关写入持久化后触发。重启时机与方式由宿主决定
	/// (进程重启;同进程重跑 LoadAll 是变相热切换,不受支持)。
	/// </summary>
	public event Action<ModStateChangeEventArgs>? ModStateChanged;

	/// <summary>
	/// 启停状态变更后、下次进程启动前为 true(状态与上次实际加载所消费的不一致);
	/// 尚未执行过 LoadAll 时为 false(变更将在首次加载直接生效)。
	/// </summary>
	public bool PendingRestart => _appliedState is not null && !_state.SameAs(_appliedState);

	public ModManager(LoaderOptions options, LoggerRouter? loggerRouter = null, ServiceRegistry? serviceRegistry = null)
	{
		ArgumentNullException.ThrowIfNull(options);

		_options = options;
		LoggerRouter = loggerRouter ?? new LoggerRouter { MinimumLevel = options.MinimumLogLevel };
		Services = serviceRegistry ?? new ServiceRegistry();

		// loader 供给 LMLoader.Api;HarmonyX 由模组自带——若进游戏依赖闭包,
		// 导出构建的默认上下文会二次解析 MonoMod,类型身份分裂,patch 必失败
		var shared = new List<Assembly> { typeof(LmMod).Assembly };
		if (options.SharedLibraries is not null)
		{
			shared.AddRange(options.SharedLibraries);
		}

		_assemblyLoader = new ModAssemblyLoader(
			LoggerRouter.GetLogger(LifecycleRunner.LoaderLogUid),
			shared,
			options.GameAssemblyResolver);

		_configManager = string.IsNullOrEmpty(options.ConfigRootPath)
			? null
			: new Config.ConfigManager(
				options.ConfigRootPath,
				logger: LoggerRouter.GetLogger(LifecycleRunner.LoaderLogUid));

		_stateStore = string.IsNullOrEmpty(options.ModStatePath)
			? null
			: new ModStateStore(options.ModStatePath, LoggerRouter.GetLogger(LifecycleRunner.LoaderLogUid));
		_state = _stateStore?.Load() ?? new ModState();
	}

	/// <summary>执行完整加载流程;可重复调用(每次独立规划)。注意:已加载的模组程序集不会因此释放,
	/// 进程内二次加载不是"应用启停变更"的手段(D19)——变更以重启进程生效。</summary>
	public LoadResult LoadAll()
	{
		if (!Directory.Exists(_options.ModsRootPath))
		{
			throw new DirectoryNotFoundException($"mods 根目录不存在: {_options.ModsRootPath}");
		}

		var loaderLogger = LoggerRouter.GetLogger(LifecycleRunner.LoaderLogUid);
		var manifestFailures = new List<(string SourcePath, string Reason)>();
		var readWarnings = new List<string>();
		var manifests = new List<ModManifest>();

		// ---- 1. 扫描与读取(主根 + 附加根:Workshop 订阅目录等,6.5) ----
		var scan = ScanManifestPaths(_options.ModsRootPath);
		readWarnings.AddRange(scan.Warnings);
		var paths = new List<string>(scan.Paths);

		foreach (var additionalRoot in _options.AdditionalModsRoots ?? [])
		{
			if (!Directory.Exists(additionalRoot))
			{
				continue; // 可选来源:缺失即跳过(Workshop 未装/路径失效属正常)
			}

			var additional = ScanManifestPaths(additionalRoot);
			readWarnings.AddRange(additional.Warnings);
			paths.AddRange(additional.Paths.Except(paths, StringComparer.OrdinalIgnoreCase));
		}

		foreach (var manifestPath in paths)
		{
			var result = ModManifestReader.ReadFile(manifestPath);
			readWarnings.AddRange(result.Warnings.Select(w => $"{manifestPath}: {w}"));

			if (!result.Success)
			{
				manifestFailures.Add((manifestPath, string.Join("; ", result.Errors)));
				loaderLogger.Warn($"清单读取失败,整份清单的模组跳过: {manifestPath}");
				continue;
			}

			var manifest = result.Manifest!;

			// ---- 2. 宿主过滤:gameId(清单缺省 = 通用模组;宿主未设 GameId 不比对,D18) ----
			if (!string.IsNullOrEmpty(_options.GameId) &&
				manifest.GameId is { Length: > 0 } gameId &&
				!string.Equals(gameId, _options.GameId, StringComparison.Ordinal))
			{
				manifestFailures.Add((manifestPath, $"gameId 不匹配: 期望 {_options.GameId},实际 {gameId}"));
				continue;
			}

			// ---- 3. loaderVersion 校验(区间语义;缺省 = 当前 API 主版本兼容区,D18) ----
			var apiVersion = _options.ApiVersion ?? typeof(LmMod).Assembly.GetName().Version!;
			var apiSemVer = new SemVer(apiVersion.Major, apiVersion.Minor, Math.Max(apiVersion.Build, 0));
			var effectiveLoaderVersion = manifest.LoaderVersion ??
				VersionRange.Parse(FormattableString.Invariant($"^{apiSemVer.Major}.0.0"));
			if (!effectiveLoaderVersion.Contains(apiSemVer))
			{
				manifestFailures.Add((manifestPath,
					$"loaderVersion 不满足: 清单要求 {effectiveLoaderVersion},当前加载器 API {apiSemVer}"));
				continue;
			}

			manifests.Add(manifest);
		}

		loaderLogger.Info($"发现 {manifests.Count} 份有效模组清单(失败 {manifestFailures.Count} 份)");

		// ---- 4. 启停状态过滤 + 依赖规划(D19:禁用模组不进依赖图,级联原因链指明) ----
		var disabledReasons = BuildDisabledReasons(manifests);
		var plan = DependencyPlanner.Plan(manifests, disabledReasons);
		_appliedState = _state.Clone();

		// ---- 5. 生命周期(整批拒绝时跳过;规划后回调供宿主挂载 pck 等资源) ----
		LifecycleReport? lifecycle = null;
		if (!plan.BatchRejected)
		{
			_options.AfterPlan?.Invoke(plan);
			lifecycle = new LifecycleRunner(_assemblyLoader, LoggerRouter, _options.Strict, Services, _configManager)
				.Execute(plan);
		}

		foreach (var warning in lifecycle?.BindingWarnings ?? [])
		{
			readWarnings.Add(warning);
		}

		// ---- 6. 人可读汇总 ----
		var rows = LoadReportFormatter.BuildRows(plan, lifecycle, manifestFailures);
		var summary = LoadReportFormatter.Render(rows, plan, lifecycle);

		loaderLogger.Info(Environment.NewLine + summary);

		_lastResult = new LoadResult
		{
			LoadedManifests = manifests,
			ReadWarnings = readWarnings,
			Plan = plan,
			Lifecycle = lifecycle,
			DiscoveredMods = BuildDiscoveredMods(manifests, plan, lifecycle, disabledReasons),
			SummaryText = summary,
		};
		return _lastResult;
	}

	/// <summary>最近一次 LoadAll 的发现视图(含被禁用模组);尚未加载返回空。</summary>
	public IReadOnlyList<DiscoveredMod> GetDiscoveredMods() =>
		_lastResult?.DiscoveredMods ?? Array.Empty<DiscoveredMod>();

	/// <summary>当前全局总开关值(D19,管理 UI 数据源;变更经 SetAllEnabled)。</summary>
	public bool GlobalEnabled => _state.GlobalEnabled;

	/// <summary>
	/// 设置单个模组启停(即时持久化 + 触发 <see cref="ModStateChanged"/>)。
	/// 已是目标状态时为无操作;尚未加载过时允许预设任意 uid(首次加载即生效)。
	/// </summary>
	public void SetModEnabled(string uid, bool enabled)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(uid);

		if (_lastResult is not null && _lastResult.DiscoveredMods.All(m => m.Uid != uid))
		{
			throw new KeyNotFoundException($"未发现模组 \"{uid}\",无法设置启停状态");
		}

		if (_state.IsEnabled(uid) == enabled)
		{
			return; // 无操作(未声明条目 = 启用,须与显式声明同等判定)
		}

		_state.SetMod(uid, enabled);
		PersistAndNotify($"模组 \"{uid}\" 已{(enabled ? "启用" : "禁用")}");
	}

	/// <summary>设置全局总开关(D19 纯净模式):false = 所有模组不加载(排障用);即时持久化 + 触发事件。</summary>
	public void SetAllEnabled(bool enabled)
	{
		if (_state.GlobalEnabled == enabled)
		{
			return;
		}

		_state.GlobalEnabled = enabled;
		PersistAndNotify(enabled ? "模组已整体启用" : "模组已整体禁用(纯净模式)");
	}

	/// <summary>
	/// 依赖预计算(D19):禁用 <paramref name="uid"/> 后,因硬依赖缺失而无法加载的传递依赖方
	/// (仅统计当前启用者;已被禁用的依赖方不在影响面内)。数据来自最近一次 LoadAll;未加载返回空。
	/// </summary>
	public IReadOnlyList<DependentImpact> PreviewDisable(string uid)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(uid);
		if (_lastResult is null || !_state.IsEnabled(uid))
		{
			return Array.Empty<DependentImpact>();
		}

		var entryByName = new Dictionary<string, (ModEntry Entry, ModManifest Manifest)>(StringComparer.Ordinal);
		var reverseHardDeps = new Dictionary<string, List<string>>(StringComparer.Ordinal);
		foreach (var manifest in _lastResult.LoadedManifests)
		{
			foreach (var entry in manifest.Mods)
			{
				entryByName.TryAdd(entry.Uid, (entry, manifest));
				foreach (var dependency in entry.Depends.Where(d => !d.Soft))
				{
					if (!reverseHardDeps.TryGetValue(dependency.Uid, out var list))
					{
						reverseHardDeps[dependency.Uid] = list = new List<string>();
					}

					list.Add(entry.Uid);
				}
			}
		}

		// BFS 沿"硬依赖方"边传播;已禁用者不在影响面(其本就无法加载),也不继续穿行
		var impacts = new List<DependentImpact>();
		var visited = new HashSet<string>(StringComparer.Ordinal) { uid };
		var queue = new Queue<(string Uid, string Chain)>();
		queue.Enqueue((uid, uid));
		while (queue.Count > 0)
		{
			var (current, chain) = queue.Dequeue();
			if (!reverseHardDeps.TryGetValue(current, out var dependents))
			{
				continue;
			}

			foreach (var dependent in dependents.OrderBy(d => d, StringComparer.Ordinal))
			{
				if (!visited.Add(dependent) || !_state.IsEnabled(dependent))
				{
					continue;
				}

				var impactChain = $"{chain} ← {dependent}";
				var (entry, _) = entryByName[dependent];
				impacts.Add(new DependentImpact(dependent, entry.Name, impactChain));
				queue.Enqueue((dependent, impactChain));
			}
		}

		return impacts;
	}

	/// <summary>构建禁用原因表:全局关闭 → 全部模组;否则仅显式禁用者。孤儿键(未发现的 uid)自然落空。</summary>
	private Dictionary<string, string> BuildDisabledReasons(List<ModManifest> manifests)
	{
		var reasons = new Dictionary<string, string>(StringComparer.Ordinal);
		if (!_state.GlobalEnabled)
		{
			foreach (var manifest in manifests)
			{
				foreach (var entry in manifest.Mods)
				{
					reasons[entry.Uid] = "全局开关已关闭,模组整体禁用(重启生效)";
				}
			}

			return reasons;
		}

		foreach (var manifest in manifests)
		{
			foreach (var entry in manifest.Mods)
			{
				if (_state.Mods.TryGetValue(entry.Uid, out var enabled) && !enabled)
				{
					reasons[entry.Uid] = "已禁用(重启生效)";
				}
			}
		}

		return reasons;
	}

	private static IReadOnlyList<DiscoveredMod> BuildDiscoveredMods(
		List<ModManifest> manifests,
		LoadPlan plan,
		LifecycleReport? lifecycle,
		Dictionary<string, string> disabledReasons)
	{
		var lifecycleByUid = lifecycle?.Results.ToDictionary(r => r.Uid, StringComparer.Ordinal);
		var skippedByUid = plan.Skipped
			.GroupBy(s => s.Uid, StringComparer.Ordinal)
			.ToDictionary(g => g.Key, g => g.First().Reason, StringComparer.Ordinal);

		var result = new List<DiscoveredMod>();
		foreach (var manifest in manifests)
		{
			foreach (var entry in manifest.Mods)
			{
				var (status, detail) = lifecycleByUid is not null && lifecycleByUid.TryGetValue(entry.Uid, out var run)
					? run.FailedStage is not null
						? (ModLoadStatus.Failed, $"{run.FailedStage}失败: {run.Exception?.Message}")
						: run.Skipped ? (ModLoadStatus.Skipped, run.Note ?? "已跳过")
						: run.Succeeded ? (ModLoadStatus.Loaded, run.Note ?? "")
						: (ModLoadStatus.NotRun, run.Note ?? "未执行")
					: skippedByUid.TryGetValue(entry.Uid, out var skipReason)
						? (ModLoadStatus.Skipped, skipReason)
						: (ModLoadStatus.NotRun, "未参与本次规划");

				result.Add(new DiscoveredMod
				{
					Uid = entry.Uid,
					Name = entry.Name,
					Version = entry.Version.ToString(),
					SourcePath = manifest.SourcePath,
					Enabled = !disabledReasons.ContainsKey(entry.Uid),
					Status = status,
					Detail = detail,
				});
			}
		}

		return result;
	}

	private void PersistAndNotify(string summary)
	{
		_stateStore?.Save(_state);
		ModStateChanged?.Invoke(new ModStateChangeEventArgs
		{
			GlobalEnabled = _state.GlobalEnabled,
			DisabledUids = _state.Mods.Where(kv => !kv.Value).Select(kv => kv.Key).ToArray(),
			Summary = summary + "(重启后生效)",
		});
	}

	/// <summary>递归扫描 <c>*mod.json</c>;同目录多份清单只取字典序第一份并告警。</summary>
	private static (List<string> Paths, List<string> Warnings) ScanManifestPaths(string root)
	{
		var byDirectory = Directory.EnumerateFiles(root, "*mod.json", SearchOption.AllDirectories)
			.OrderBy(p => p, StringComparer.Ordinal)
			.GroupBy(Path.GetDirectoryName, StringComparer.Ordinal);

		var paths = new List<string>();
		var warnings = new List<string>();

		foreach (var group in byDirectory)
		{
			var candidates = group.ToList();
			paths.Add(candidates[0]);

			if (candidates.Count > 1)
			{
				warnings.Add(
					$"目录 \"{group.Key}\" 存在多份清单,已采用 {Path.GetFileName(candidates[0])}," +
					$"忽略 {candidates.Count - 1} 份(模组打包错误,CLI lint 将兜底校验)");
			}
		}

		return (paths, warnings);
	}

	public void Dispose()
	{
		_configManager?.Dispose();
		_assemblyLoader.Dispose();
	}
}
