// SPDX-License-Identifier: LGPL-3.0-or-later

using LMLoader.Core.Manifest;

namespace LMLoader.Core.Dependency;

/// <summary>通过依赖解析、待按拓扑序加载的模组。</summary>
public sealed class ModPlanItem
{
	/// <summary>模组 uid(全局唯一;规划期冲突双方均不入列)。</summary>
	public required string Uid { get; init; }

	/// <summary>所属清单(目录/SourcePath/过滤信息来源)。</summary>
	public required ModManifest Manifest { get; init; }

	/// <summary>模组定义。</summary>
	public required ModEntry Entry { get; init; }
}

/// <summary>未入列模组及原因。</summary>
public sealed class SkippedMod
{
	public required string Uid { get; init; }

	/// <summary>人可读原因;涉及依赖时点名依赖 UID,便于还原因果链。</summary>
	public required string Reason { get; init; }
}

/// <summary>
/// 依赖规划产物。循环依赖为整批拒绝(静态扫描存在环则整批拒绝,输出完整依赖链)。
/// </summary>
public sealed class LoadPlan
{
	/// <summary>按加载顺序排列的模组(并列时 UID 字典序)。</summary>
	public IReadOnlyList<ModPlanItem> Ordered { get; init; } = Array.Empty<ModPlanItem>();

	/// <summary>未入列模组及原因(含级联跳过)。</summary>
	public IReadOnlyList<SkippedMod> Skipped { get; init; } = Array.Empty<SkippedMod>();

	/// <summary>true = 存在循环依赖,本轮扫描的所有模组均不加载(重启前维持现状)。</summary>
	public bool BatchRejected { get; init; }

	/// <summary>整批拒绝时的完整依赖链描述。</summary>
	public string? BatchRejectReason { get; init; }
}
