// SPDX-License-Identifier: LGPL-3.0-or-later

using LMLoader.Core.Reporting;

namespace LMLoader.Core.Management;

/// <summary>
/// 本次加载发现的单个模组(含被禁用者;管理窗口列表数据源)。
/// 由 LoadResult.DiscoveredMods 提供:已通过读取与宿主过滤的清单 join 规划/生命周期结果。
/// </summary>
public sealed class DiscoveredMod
{
	public required string Uid { get; init; }

	/// <summary>显示名(清单继承解析后)。</summary>
	public required string Name { get; init; }

	public required string Version { get; init; }

	/// <summary>所属清单文件路径(一个清单可含多个模组,管理窗口按此分组;整包开关 = 批量写组内 uid)。</summary>
	public required string SourcePath { get; init; }

	/// <summary>启停状态开关当前值(未声明 = 启用;全局关闭时此项语义上仍为 true,实际不加载)。</summary>
	public required bool Enabled { get; init; }

	/// <summary>本次加载结果(已禁用归入 Skipped,原因见 Detail)。</summary>
	public required ModLoadStatus Status { get; init; }

	/// <summary>结果说明(禁用原因/失败原因/级联说明等)。</summary>
	public required string Detail { get; init; }
}

/// <summary>禁用某模组后将无法加载的硬依赖方(依赖预计算,D19:只预览不强制)。</summary>
public sealed record DependentImpact(string Uid, string Name, string Chain);
