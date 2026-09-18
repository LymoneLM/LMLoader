// SPDX-License-Identifier: LGPL-3.0-or-later

using System.Reflection;
using LMLoader.Api;
using LMLoader.Core.Dependency;
using LMLoader.Core.Lifecycle;
using LMLoader.Core.Loading;
using LMLoader.Core.Logging;
using LMLoader.Core.Manifest;
using LMLoader.Core.Reporting;
using LMLoader.Core.Versioning;

namespace LMLoader.Core;

/// <summary>
/// 加载器门面:扫描 mods 目录 → 读取 mod.json → 过滤(gameId/loaderVersion) → 依赖规划 →
/// 生命周期执行 → 人可读汇总(模组级失败一律记录不抛出;loader 配置错误直接抛异常)。
/// </summary>
public sealed class ModManager : IDisposable
{
	private readonly LoaderOptions _options;
	private readonly ModAssemblyLoader _assemblyLoader;
	private readonly Config.ConfigManager? _configManager;

	/// <summary>日志中枢;宿主可自行增加 sink。</summary>
	public LoggerRouter LoggerRouter { get; }

	/// <summary>跨模组服务注册表。</summary>
	public ServiceRegistry Services { get; }

	/// <summary>配置协调器;未配置 ConfigRootPath 时为 null(热重载由此驱动)。</summary>
	public Config.ConfigManager? Configs => _configManager;

	public ModManager(LoaderOptions options, LoggerRouter? loggerRouter = null, ServiceRegistry? serviceRegistry = null)
	{
		ArgumentNullException.ThrowIfNull(options);

		_options = options;
		LoggerRouter = loggerRouter ?? new LoggerRouter { MinimumLevel = options.MinimumLogLevel };
		Services = serviceRegistry ?? new ServiceRegistry();

		// loader 供给 LMLoader.Api;HarmonyX 由模组自带——若进游戏依赖闭包,
		// 导出构建的默认上下文会二次解析 MonoMod,类型身份分裂,patch 必失败
		var shared = new List<Assembly> { typeof(LmModule).Assembly };
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
	}

	/// <summary>执行完整加载流程;可重复调用(如重启前重新扫描),每次独立规划。</summary>
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

		var seenUids = new HashSet<string>(StringComparer.Ordinal);
		foreach (var manifestPath in paths)
		{
			var result = ModManifestReader.ReadFile(manifestPath);
			readWarnings.AddRange(result.Warnings.Select(w => $"{manifestPath}: {w}"));

			if (!result.Success)
			{
				manifestFailures.Add((manifestPath, string.Join("; ", result.Errors)));
				loaderLogger.Warn($"清单读取失败,模组跳过: {manifestPath}");
				continue;
			}

			var manifest = result.Manifest!;

			// 附加根可能扫到主根已有模组的副本:主根优先,副本告警跳过
			if (!seenUids.Add(manifest.Uid))
			{
				manifestFailures.Add((manifestPath,
					$"模组 \"{manifest.Uid}\" 已从其他扫描根加载,本副本忽略(主根优先于附加根)"));
				continue;
			}

			// ---- 2. 宿主过滤:gameId(不匹配拒载) ----
			if (!string.IsNullOrEmpty(_options.GameId) &&
				!string.Equals(manifest.GameId, _options.GameId, StringComparison.Ordinal))
			{
				manifestFailures.Add((manifestPath, $"gameId 不匹配: 期望 {_options.GameId},实际 {manifest.GameId}"));
				continue;
			}

			// ---- 3. loaderVersion 校验(区间语义;裸精确清单天然兼容) ----
			var apiVersion = _options.ApiVersion ?? typeof(LmModule).Assembly.GetName().Version!;
			var apiSemVer = new SemVer(apiVersion.Major, apiVersion.Minor, Math.Max(apiVersion.Build, 0));
			if (!manifest.LoaderVersion.Contains(apiSemVer))
			{
				manifestFailures.Add((manifestPath,
					$"loaderVersion 不满足: 清单要求 {manifest.LoaderVersion},当前加载器 API {apiSemVer}"));
				continue;
			}

			manifests.Add(manifest);
		}

		loaderLogger.Info($"发现 {manifests.Count} 个有效模组清单(失败 {manifestFailures.Count} 个)");

		// ---- 4. 依赖规划 ----
		var plan = DependencyPlanner.Plan(manifests);

		// ---- 5. 生命周期(整批拒绝时跳过;规划后回调供宿主挂载 pck 等资源) ----
		LifecycleReport? lifecycle = null;
		if (!plan.BatchRejected)
		{
			_options.AfterPlan?.Invoke(plan);
			lifecycle = new LifecycleRunner(_assemblyLoader, LoggerRouter, _options.Strict, Services, _configManager)
				.Execute(plan);
		}

		// ---- 6. 人可读汇总 ----
		var rows = LoadReportFormatter.BuildRows(plan, lifecycle, manifestFailures);
		var summary = LoadReportFormatter.Render(rows, plan, lifecycle);

		loaderLogger.Info(Environment.NewLine + summary);

		return new LoadResult
		{
			LoadedManifests = manifests,
			ReadWarnings = readWarnings,
			Plan = plan,
			Lifecycle = lifecycle,
			SummaryText = summary,
		};
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
