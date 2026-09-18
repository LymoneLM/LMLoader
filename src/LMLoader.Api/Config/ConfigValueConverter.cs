// SPDX-License-Identifier: LGPL-3.0-or-later

using System.Globalization;

namespace LMLoader.Api.Config;

/// <summary>
/// 配置标量类型转换(文件值 → 绑定类型)。Api 不依赖 Tomlyn:Core 把 TOML 原始值解包成
/// 普通 CLR 标量(string/bool/数值)后交给这里。转换失败一律返回 false,由调用方按 D11 回退默认。
/// </summary>
internal static class ConfigValueConverter
{
	private static readonly HashSet<Type> SupportedTypes = new()
	{
		typeof(string), typeof(bool),
		typeof(sbyte), typeof(byte), typeof(short), typeof(ushort),
		typeof(int), typeof(uint), typeof(long), typeof(ulong),
		typeof(float), typeof(double), typeof(decimal),
	};

	public static bool IsSupported(Type type) => SupportedTypes.Contains(type) || type.IsEnum;

	public static bool TryConvert(object raw, Type targetType, out object? converted)
	{
		converted = null;
		try
		{
			if (targetType.IsEnum)
			{
				// 注意:Enum.TryParse 对数字字符串("99")即使未定义也返回 true,必须补 IsDefined
				if (raw is string name && Enum.TryParse(targetType, name, ignoreCase: true, out var parsed)
					&& Enum.IsDefined(targetType, parsed))
				{
					converted = parsed;
					return true;
				}

				var underlying = Convert.ChangeType(raw, Enum.GetUnderlyingType(targetType), CultureInfo.InvariantCulture);
				if (!Enum.IsDefined(targetType, underlying))
				{
					return false;
				}

				converted = Enum.ToObject(targetType, underlying);
				return true;
			}

			converted = Convert.ChangeType(raw, targetType, CultureInfo.InvariantCulture);
			return converted is not null;
		}
		catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
		{
			return false;
		}
	}
}
