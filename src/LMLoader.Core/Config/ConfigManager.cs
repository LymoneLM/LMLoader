// SPDX-License-Identifier: LGPL-3.0-or-later

using System.Globalization;
using LMLoader.Api;
using LMLoader.Api.Config;
using Tomlyn;
using Tomlyn.Model;

namespace LMLoader.Core.Config;

/// <summary>
/// 模组配置协调器(D11):按 modUid 汇集声明面(同模组多模块共享一份),
/// PreLoad 后读入 <c>&lt;root&gt;/&lt;modUid&gt;.toml</c> 合并——缺失键补默认并写回、孤儿键保留、
/// 类型/取值不符回退默认并警告;配置文件解析失败时整模组跳过(用默认值且不写回,保护用户原稿)。
/// </summary>
public sealed class ConfigManager
{
	private readonly string _rootPath;
	private readonly TomlConfigStore _store;
	private readonly ILmLogger _logger;
	private readonly Dictionary<string, ModConfig> _configsByMod = new(StringComparer.Ordinal);

	public ConfigManager(string rootPath, TomlConfigStore? store = null, ILmLogger? logger = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
		_rootPath = rootPath;
		_store = store ?? new TomlConfigStore();
		_logger = logger ?? NullLogger.Instance;
	}

	/// <summary>已注册配置的模组数(诊断用)。</summary>
	public int RegisteredCount => _configsByMod.Count;

	/// <summary>按 modUid 取已注册配置;未注册(模组失败/未声明)返回 false。</summary>
	public bool TryGetConfig(string modUid, out ModConfig? config) =>
		_configsByMod.TryGetValue(modUid, out config);

	/// <summary>Runner 在模块实例化时注册(每模组首次注册生效;D11 文件以 modUid 命名)。</summary>
	internal void Register(string modUid, ModConfig config)
	{
		if (_configsByMod.TryAdd(modUid, config))
		{
			return;
		}

		// 同模组后续模块共享已注册实例,保证单文件单一事实来源
		var shared = _configsByMod[modUid];
		if (!ReferenceEquals(shared, config))
		{
			// Runner 侧按模组去重,正常不会走到;防御性提示
			_logger.Warn($"模组 \"{modUid}\" 出现多份配置实例,以首次注册为准");
		}
	}

	/// <summary>首次合并(D11):PreLoad 之后、OnLoad 之前逐模组调用;写回文件。</summary>
	internal void ApplyAfterPreLoad(IReadOnlyCollection<KeyValuePair<string, ModConfig>> modules)
	{
		foreach (var pair in modules)
		{
			MergeAndWrite(pair.Key, pair.Value);
		}
	}

	/// <summary>热重载核心(4.4 由防抖监听驱动):文件中存在的键才更新;键被删除保持当前值;不写回。</summary>
	internal void ReloadFromFile(string modUid, ModConfig config)
	{
		var path = TomlConfigStore.GetConfigFilePath(_rootPath, modUid);
		TomlTable disk;
		try
		{
			disk = _store.Read(path);
		}
		catch (Exception ex)
		{
			_logger.Warn($"模组 \"{modUid}\" 配置热重载解析失败,维持当前值: {ex.Message}");
			return;
		}

		foreach (var entry in config.Entries)
		{
			var raw = TryGetRaw(disk, entry.Section, entry.Key);
			if (raw is null)
			{
				continue;
			}

			if (entry.TrySetFromRaw(raw) == ConfigSetOutcome.TypeMismatch)
			{
				_logger.Warn($"模组 \"{modUid}\" 配置 [{entry.Section}] {entry.Key} 热重载类型或取值不符,保持当前值");
			}
		}
	}

	private void MergeAndWrite(string modUid, ModConfig config)
	{
		var path = TomlConfigStore.GetConfigFilePath(_rootPath, modUid);
		TomlTable disk;
		string? originalText;
		try
		{
			originalText = File.Exists(path) ? File.ReadAllText(path) : null;
			disk = _store.Read(path);
		}
		catch (Exception ex)
		{
			_logger.Warn($"模组 \"{modUid}\" 配置文件解析失败,使用默认值且不写回(保护用户原稿): {ex.Message}");
			foreach (var entry in config.Entries)
			{
				entry.ResetToDefault();
			}

			return;
		}

		foreach (var entry in config.Entries)
		{
			var raw = TryGetRaw(disk, entry.Section, entry.Key);
			if (raw is null)
			{
				entry.ResetToDefault();
				continue;
			}

			if (entry.TrySetFromRaw(raw) == ConfigSetOutcome.TypeMismatch)
			{
				entry.ResetToDefault();
				_logger.Warn(
					$"模组 \"{modUid}\" 配置 [{entry.Section}] {entry.Key} 类型或取值不符" +
					$"(期望 {entry.SettingType.Name}{(entry.AcceptableValues is null ? "" : $" {entry.AcceptableValues.Describe()}")})," +
					"已回退默认值");
			}
		}

		OverlayEntries(disk, config, modUid);
		try
		{
			var text = TomlSerializer.Serialize(disk);
			if (text == originalText)
			{
				return;
			}

			_store.Write(path, disk);
		}
		catch (Exception ex)
		{
			// 写回失败不放大(D7):值已在内存生效,文件下次合并时再落盘
			_logger.Warn($"模组 \"{modUid}\" 配置写回失败: {ex.Message}");
		}
	}

	/// <summary>声明值叠加进读入表(孤儿键原样保留);节被用户写成非表值时覆盖为表并警告。</summary>
	private void OverlayEntries(TomlTable table, ModConfig config, string modUid)
	{
		foreach (var entry in config.Entries)
		{
			object value;
			try
			{
				value = ToTomlValue(entry);
			}
			catch (Exception ex) when (ex is OverflowException or InvalidCastException or FormatException)
			{
				_logger.Warn($"模组 \"{modUid}\" 配置 [{entry.Section}] {entry.Key} 值无法序列化,保留文件原值: {ex.Message}");
				continue;
			}

			if (entry.Section.Length == 0)
			{
				table[entry.Key] = value;
				continue;
			}

			if (!table.TryGetValue(entry.Section, out var sectionValue) || sectionValue is not TomlTable sectionTable)
			{
				if (sectionValue is not null)
				{
					_logger.Warn($"模组 \"{modUid}\" 配置节 [{entry.Section}] 被声明为标量,已按表覆盖");
				}

				sectionTable = new TomlTable();
				table[entry.Section] = sectionTable;
			}

			sectionTable[entry.Key] = value;
		}
	}

	private static object? TryGetRaw(TomlTable table, string section, string key)
	{
		if (section.Length == 0)
		{
			return table.TryGetValue(key, out var value) ? value : null;
		}

		if (!table.TryGetValue(section, out var sectionValue) || sectionValue is not TomlTable sectionTable)
		{
			return null;
		}

		return sectionTable.TryGetValue(key, out var keyValue) ? keyValue : null;
	}

	/// <summary>绑定值 → TOML 标量(enum 写名字符串;浮点统一 double;整型统一 long)。</summary>
	private static object ToTomlValue(ConfigEntryBase entry)
	{
		var value = entry.BoxedValue ?? throw new InvalidOperationException("配置项当前值为空");
		if (value is string or bool or long or double)
		{
			return value;
		}

		if (entry.SettingType.IsEnum)
		{
			return value.ToString()!;
		}

		if (value is float or double or decimal)
		{
			return Convert.ToDouble(value, CultureInfo.InvariantCulture);
		}

		return Convert.ToInt64(value, CultureInfo.InvariantCulture);
	}

	private sealed class NullLogger : ILmLogger
	{
		public static readonly NullLogger Instance = new();

		public void Log(LmLogLevel level, string message, Exception? exception = null) { }
	}
}
