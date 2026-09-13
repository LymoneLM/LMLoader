// SPDX-License-Identifier: LGPL-3.0-or-later

using System.Collections.Concurrent;
using HarmonyLib;

namespace LMLoader.Core.Patching;

/// <summary>
/// per-mod Harmony 实例管理(阶段 3.2):harmony id = 模组 uid,模块间互不串扰;
/// 整体还原走静态 <c>Harmony.UnpatchID(modUid)</c>(2.16 起实例 UnpatchAll 已废弃,CS0619)。
/// 动态卸载不在 v1 范围(范围决策)。
/// </summary>
public sealed class PatchManager
{
	private readonly ConcurrentDictionary<string, Harmony> _patchers = new(StringComparer.Ordinal);

	/// <summary>取模组的 Harmony 实例(同模组复用同一实例,id = 模组 uid)。</summary>
	public Harmony GetPatcher(string modUid)
	{
		if (string.IsNullOrEmpty(modUid))
		{
			throw new ArgumentException("modUid 不能为空", nameof(modUid));
		}

		return _patchers.GetOrAdd(modUid, uid => new Harmony(uid));
	}

	/// <summary>当前已创建 patcher 的模组 uid 集合(诊断用)。</summary>
	public IReadOnlyCollection<string> PatchedModUids => _patchers.Keys.ToArray();
}
