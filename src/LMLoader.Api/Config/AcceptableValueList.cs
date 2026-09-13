// SPDX-License-Identifier: LGPL-3.0-or-later

namespace LMLoader.Api.Config;

/// <summary>枚举取值列表:列表外的值不可钳制,一律回退默认值并警告(D11 宽容语义)。T 可为数值或字符串。</summary>
public sealed class AcceptableValueList<T> : AcceptableValueBase where T : notnull
{
	public AcceptableValueList(params T[] values)
	{
		if (values is null || values.Length == 0)
		{
			throw new ArgumentException("取值列表不能为空", nameof(values));
		}

		Items = Array.AsReadOnly((T[])values.Clone());
	}

	public IReadOnlyList<T> Items { get; }

	public override Type ValueType => typeof(T);

	public override string Describe() => $"{{{string.Join(", ", Items)}}}";

	public bool Contains(T value) => Items.Contains(value);

	internal override object? CoerceBoxed(object value) => value is T typed && Items.Contains(typed) ? value : null;
}
