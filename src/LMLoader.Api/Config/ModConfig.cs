// SPDX-License-Identifier: LGPL-3.0-or-later

namespace LMLoader.Api.Config;

/// <summary>
/// 模组配置声明面:在 <c>OnPreLoad</c> 阶段调用 <see cref="Bind{T}"/> 声明配置项及默认值,
/// Core 随后按 D11 合并 <c>&lt;配置根&gt;/&lt;modUid&gt;.toml</c>(缺失键补默认写回、孤儿键保留、
/// 类型不符回退默认并警告)。同键重复绑定返回同一实例;实例化后即可安全读取 <c>Value</c>。
/// </summary>
public sealed class ModConfig
{
	private readonly Dictionary<(string Section, string Key), ConfigEntryBase> _byIdentity = new();
	private readonly List<ConfigEntryBase> _ordered = new();

	/// <summary>按声明顺序排列的全部配置项。</summary>
	public IReadOnlyList<ConfigEntryBase> Entries => _ordered;

	public ConfigEntry<T> Bind<T>(
		string section,
		string key,
		T defaultValue,
		string? description = null,
		bool requiresRestart = false,
		AcceptableValueBase? acceptableValues = null)
		where T : notnull
	{
		if (section is null)
		{
			throw new ArgumentNullException(nameof(section));
		}

		if (section.Length > 0 && section.AsSpan().Trim().Length == 0)
		{
			throw new ArgumentException("节名不能为纯空白(顶层裸键传空字符串)", nameof(section));
		}

		ArgumentException.ThrowIfNullOrWhiteSpace(key);
		if (!ConfigValueConverter.IsSupported(typeof(T)))
		{
			throw new NotSupportedException(
				$"配置项类型 {typeof(T)} 不受支持(v1 标量:string/bool/数值/enum)");
		}

		if (_byIdentity.TryGetValue((section, key), out var existing))
		{
			if (existing is ConfigEntry<T> typed)
			{
				return typed;
			}

			throw new InvalidOperationException(
				$"配置项 [{section}] {key} 已绑定 {existing.SettingType},不能改用 {typeof(T)} 重新绑定");
		}

		var entry = new ConfigEntry<T>(section, key, defaultValue, description, requiresRestart, acceptableValues);
		_byIdentity[(section, key)] = entry;
		_ordered.Add(entry);
		return entry;
	}

	/// <summary>Core 合并路径:按节/键定位已声明项。</summary>
	internal bool TryGetEntry(string section, string key, out ConfigEntryBase? entry) =>
		_byIdentity.TryGetValue((section, key), out entry);
}
