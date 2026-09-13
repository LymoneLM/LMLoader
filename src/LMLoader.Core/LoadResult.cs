// SPDX-License-Identifier: LGPL-3.0-or-later

namespace LMLoader.Core;

/// <summary>ModManager.LoadAll 的统一产物。</summary>
public sealed class LoadResult
{
	/// <summary>通过读取与过滤、参与依赖规划的模组清单。</summary>
	public required IReadOnlyList<Manifest.ModManifest> LoadedManifests { get; init; }

	/// <summary>清单读取警告(未知字段等,前缀文件路径)。</summary>
	public required IReadOnlyList<string> ReadWarnings { get; init; }

	/// <summary>依赖规划产物(含规划期跳过与整批拒绝信息)。</summary>
	public required Dependency.LoadPlan Plan { get; init; }

	/// <summary>生命周期结果;整批拒绝(循环依赖)时为 null。</summary>
	public Lifecycle.LifecycleReport? Lifecycle { get; init; }

	/// <summary>人可读加载汇总(D7)。</summary>
	public required string SummaryText { get; init; }
}
