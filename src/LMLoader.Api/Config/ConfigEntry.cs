// SPDX-License-Identifier: LGPL-3.0-or-later

namespace LMLoader.Api.Config;

/// <summary>
/// 类型化配置项。值变更(含文件合并与热重载写入)触发 <see cref="SettingChanged"/>,
/// 回调在触发线程上执行(热重载来自防抖定时器线程,涉及主线程亲和的 API 由订阅方自行调度)。
/// </summary>
public sealed class ConfigEntry<T> : ConfigEntryBase where T : notnull
{
	private T _value;

	internal ConfigEntry(
		string section,
		string key,
		T defaultValue,
		string? description,
		bool requiresRestart,
		AcceptableValueBase? acceptableValues)
		: base(section, key, description, requiresRestart, acceptableValues)
	{
		if (acceptableValues is not null && acceptableValues.ValueType != typeof(T))
		{
			throw new ArgumentException(
				$"约束类型 {acceptableValues.ValueType} 与绑定类型 {typeof(T)} 不一致", nameof(acceptableValues));
		}

		if (acceptableValues is not null)
		{
			// 范围钳制默认值;默认值落在列表外属于声明错误,构造即失败
			var coerced = acceptableValues.CoerceBoxed(defaultValue)
				?? throw new ArgumentException(
					$"默认值 \"{defaultValue}\" 不在允许域 {acceptableValues.Describe()} 内", nameof(defaultValue));
			_value = (T)coerced;
		}
		else
		{
			_value = defaultValue;
		}

		DefaultValue = defaultValue;
	}

	/// <summary>声明时的默认值。</summary>
	public T DefaultValue { get; }

	public override Type SettingType => typeof(T);

	public override object? BoxedDefaultValue => DefaultValue;

	public override object? BoxedValue => _value;

	public event Action<ConfigEntry<T>>? SettingChanged;

	public T Value
	{
		get => _value;
		set
		{
			if (AcceptableValues is not null)
			{
				value = (T)(AcceptableValues.CoerceBoxed(value)
					?? throw new ArgumentException(
						$"值 \"{value}\" 不在允许域 {AcceptableValues.Describe()} 内", nameof(value)));
			}

			if (EqualityComparer<T>.Default.Equals(_value, value))
			{
				return;
			}

			_value = value;
			SettingChanged?.Invoke(this);
		}
	}

	internal override ConfigSetOutcome TrySetFromRaw(object? raw)
	{
		if (raw is null
			|| !ConfigValueConverter.TryConvert(raw, typeof(T), out var converted)
			|| converted is null)
		{
			return ConfigSetOutcome.TypeMismatch;
		}

		var typed = (T)converted;
		if (AcceptableValues is not null)
		{
			var coerced = AcceptableValues.CoerceBoxed(typed);
			if (coerced is null)
			{
				return ConfigSetOutcome.TypeMismatch;
			}

			typed = (T)coerced;
		}

		Value = typed;
		return ConfigSetOutcome.Applied;
	}

	internal override void ResetToDefault() => Value = DefaultValue;
}
