// SPDX-License-Identifier: LGPL-3.0-or-later

using LMLoader.Core.Manifest;

namespace LMLoader.Core.Dependency;

/// <summary>通过依赖解析、待按拓扑序加载的模块。</summary>
public sealed class ModulePlanItem
{
	public required string ModuleUid { get; init; }

	public required string ModUid { get; init; }

	/// <summary>所属模组清单(实例化与程序集加载需要)。</summary>
	public required ModManifest Mod { get; init; }

	/// <summary>模块定义。</summary>
	public required ModuleEntry Module { get; init; }
}

/// <summary>未入列模块及原因(D7:一切可诊断)。</summary>
public sealed class SkippedModule
{
	public required string ModuleUid { get; init; }

	public required string ModUid { get; init; }

	/// <summary>人可读原因;涉及依赖时点名依赖 UID,便于还原因果链。</summary>
	public required string Reason { get; init; }
}

/// <summary>
/// 依赖规划产物。循环依赖为整批拒绝(草稿:静态扫描存在环则整批拒绝,输出完整依赖链)。
/// </summary>
public sealed class LoadPlan
{
	/// <summary>按加载顺序排列的模块(D8:并列时 UID 字典序)。</summary>
	public IReadOnlyList<ModulePlanItem> Ordered { get; init; } = Array.Empty<ModulePlanItem>();

	/// <summary>未入列模块及原因(含级联跳过)。</summary>
	public IReadOnlyList<SkippedModule> Skipped { get; init; } = Array.Empty<SkippedModule>();

	/// <summary>true = 存在循环依赖,本轮扫描的所有模块均不加载(重启前维持现状)。</summary>
	public bool BatchRejected { get; init; }

	/// <summary>整批拒绝时的完整依赖链描述。</summary>
	public string? BatchRejectReason { get; init; }
}
