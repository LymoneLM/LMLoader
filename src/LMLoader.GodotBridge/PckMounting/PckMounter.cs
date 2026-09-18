// SPDX-License-Identifier: LGPL-3.0-or-later

using Godot;
using LMLoader.Api;
using LMLoader.Core.Dependency;

namespace LMLoader.GodotBridge.PckMounting;

/// <summary>
/// pck 扫描与挂载:
/// - 挂载顺序 = 模块拓扑序(加载顺序即优先级);
/// - 后挂载覆盖( Godot 原生语义,replaceFiles: true),运行时不做重叠检测;
/// - 资源路径约定 <c>res://mods/&lt;uid&gt;/</c> 由清单与打包侧保证,CLI lint 兜底;
/// - pck 早于模块逻辑加载。
/// </summary>
public static class PckMounter
{
	/// <summary>按 LoadPlan 的拓扑序挂载各模组声明的 pck;在生命周期 PreLoad 之前调用。</summary>
	public static void MountInPlanOrder(LoadPlan plan, ILmLogger logger)
	{
		ArgumentNullException.ThrowIfNull(plan);
		ArgumentNullException.ThrowIfNull(logger);

		foreach (var item in plan.Ordered)
		{
			foreach (var pck in item.Mod.PckResources)
			{
				// 清单中为相对模组目录的路径;挂载用物理路径
				var absolute = Path.Combine(item.Mod.Directory, pck);
				if (!File.Exists(absolute))
				{
					logger.Warn($"模组 \"{item.ModUid}\" 声明的 pck 不存在,已跳过: {pck}");
					continue;
				}

				// Godot 的 LoadResourcePack 以 pck 内部记录的 res:// 路径挂载;
				// 多 pck 覆盖同一 res:// 路径时后挂载覆盖(replaceFiles: true)
				var mounted = ProjectSettings.LoadResourcePack(absolute, replaceFiles: true);
				if (mounted)
				{
					logger.Info($"已挂载模组 pck: {item.ModUid} ← {pck}");
				}
				else
				{
					// 单点失败不放大:记录后继续,不阻断加载
					logger.Error($"模组 \"{item.ModUid}\" 的 pck 挂载失败: {pck}");
				}
			}
		}
	}
}
