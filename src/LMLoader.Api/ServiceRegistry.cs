// SPDX-License-Identifier: LGPL-3.0-or-later

namespace LMLoader.Api;

/// <summary>
/// 跨模组弱类型服务注册表(与强类型 Api 包直引互为补充)。
/// 键 = 注册时的精确类型(通常为接口);注册方 UID 用于按所有者取回与诊断。
/// 语义:
/// - 同类型多次注册:<see cref="TryGet{T}(out T)"/> 取<b>最后注册</b>(加载序在后的模组可覆盖前置);
/// - 同 (类型, 所有者) 重复注册:替换原条目(幂等重注册);
/// - <see cref="GetAll{T}"/> 按注册顺序返回全部实例;
/// 线程安全。
/// </summary>
public sealed class ServiceRegistry
{
	private sealed class Entry(string ownerUid, object instance, long order)
	{
		public string OwnerUid { get; } = ownerUid;

		public object Instance { get; set; } = instance;

		public long Order { get; } = order;
	}

	private readonly object _gate = new();
	private readonly Dictionary<Type, List<Entry>> _services = new();
	private long _orderCounter;

	/// <summary>注册服务实例。<paramref name="instance"/> 不得为 null。</summary>
	public void Register<T>(string ownerUid, T instance) where T : class
	{
		ArgumentException.ThrowIfNullOrEmpty(ownerUid);
		ArgumentNullException.ThrowIfNull(instance);

		lock (_gate)
		{
			if (!_services.TryGetValue(typeof(T), out var entries))
			{
				_services[typeof(T)] = entries = new List<Entry>();
			}

			var existing = entries.FindIndex(e => e.OwnerUid == ownerUid);
			if (existing >= 0)
			{
				entries[existing] = new Entry(ownerUid, instance, ++_orderCounter);
			}
			else
			{
				entries.Add(new Entry(ownerUid, instance, ++_orderCounter));
			}
		}
	}

	/// <summary>取回最后注册的该类型服务实例。</summary>
	public bool TryGet<T>(out T instance) where T : class
	{
		instance = default!;

		lock (_gate)
		{
			if (!_services.TryGetValue(typeof(T), out var entries) || entries.Count == 0)
			{
				return false;
			}

			var latest = entries.MaxBy(e => e.Order)!;
			instance = (T)latest.Instance;
			return true;
		}
	}

	/// <summary>取回指定所有者注册的该类型服务实例。</summary>
	public bool TryGet<T>(string ownerUid, out T instance) where T : class
	{
		ArgumentException.ThrowIfNullOrEmpty(ownerUid);
		instance = default!;

		lock (_gate)
		{
			if (!_services.TryGetValue(typeof(T), out var entries))
			{
				return false;
			}

			var entry = entries.Find(e => e.OwnerUid == ownerUid);
			if (entry is null)
			{
				return false;
			}

			instance = (T)entry.Instance;
			return true;
		}
	}

	/// <summary>按注册顺序返回该类型全部服务实例。</summary>
	public IReadOnlyList<T> GetAll<T>() where T : class
	{
		lock (_gate)
		{
			if (!_services.TryGetValue(typeof(T), out var entries) || entries.Count == 0)
			{
				return Array.Empty<T>();
			}

			return entries
				.OrderBy(e => e.Order)
				.Select(e => (T)e.Instance)
				.ToArray();
		}
	}
}
