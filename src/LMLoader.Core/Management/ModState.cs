// SPDX-License-Identifier: LGPL-3.0-or-later

namespace LMLoader.Core.Management;

/// <summary>
/// 模组启停状态(D19):全局总开关 + 模组 uid 级开关。缺省 = 全部启用;
/// 仅显式声明的条目落盘,模组更新不影响玩家选择,孤儿键保留不清理。
/// 过滤发生在启动期(规划之前),变更经 <c>ModManager</c> 持久化后于**下次进程启动**生效
/// ——同进程重跑 LoadAll 是变相热切换,不属于本状态的生效路径。
/// </summary>
public sealed class ModState
{
	private readonly Dictionary<string, bool> _mods = new(StringComparer.Ordinal);

	/// <summary>全局总开关;false = 纯净模式(所有模组不加载,排障用)。缺省 true。</summary>
	public bool GlobalEnabled { get; set; } = true;

	/// <summary>模组 uid → 启用状态(显式声明项;未声明的 uid 视为启用)。</summary>
	public IReadOnlyDictionary<string, bool> Mods => _mods;

	/// <summary>设置单个模组启用状态(忠实记录,含显式 true)。</summary>
	public void SetMod(string uid, bool enabled)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(uid);
		_mods[uid] = enabled;
	}

	/// <summary>当前生效判定:全局启用 且 该模组未被显式禁用。</summary>
	public bool IsEnabled(string uid) =>
		GlobalEnabled && (!_mods.TryGetValue(uid, out var enabled) || enabled);

	/// <summary>与另一份状态逐键比较(供 PendingRestart 判定)。</summary>
	public bool SameAs(ModState other)
	{
		ArgumentNullException.ThrowIfNull(other);
		return GlobalEnabled == other.GlobalEnabled &&
			_mods.Count == other._mods.Count &&
			_mods.All(kv => other._mods.TryGetValue(kv.Key, out var value) && value == kv.Value);
	}

	/// <summary>深拷贝(LoadAll 时留存"本次实际消费"的快照)。</summary>
	public ModState Clone()
	{
		var copy = new ModState { GlobalEnabled = GlobalEnabled };
		foreach (var (uid, enabled) in _mods)
		{
			copy._mods[uid] = enabled;
		}

		return copy;
	}
}

/// <summary>模组启停状态变更通知(D19):开关写入持久化后触发;重启时机由游戏决定。</summary>
public sealed class ModStateChangeEventArgs
{
	/// <summary>变更后的全局总开关值。</summary>
	public required bool GlobalEnabled { get; init; }

	/// <summary>变更后的显式禁用模组 uid 全集。</summary>
	public required IReadOnlyList<string> DisabledUids { get; init; }

	/// <summary>人可读变更摘要(弹窗/日志用);已附"重启后生效"提示。</summary>
	public required string Summary { get; init; }
}
