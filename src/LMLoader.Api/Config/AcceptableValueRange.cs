// SPDX-License-Identifier: LGPL-3.0-or-later

namespace LMLoader.Api.Config;

/// <summary>闭区间取值范围:越界值钳制到最近边界(BepInEx 行为)。T 为数值类型。</summary>
public sealed class AcceptableValueRange<T> : AcceptableValueBase where T : struct, IComparable<T>
{
	public AcceptableValueRange(T minimum, T maximum)
	{
		if (minimum.CompareTo(maximum) > 0)
		{
			throw new ArgumentException($"范围下界 {minimum} 大于上界 {maximum}");
		}

		Minimum = minimum;
		Maximum = maximum;
	}

	public T Minimum { get; }

	public T Maximum { get; }

	public override Type ValueType => typeof(T);

	public override string Describe() => $"[{Minimum}, {Maximum}]";

	public T Clamp(T value) =>
		value.CompareTo(Minimum) < 0 ? Minimum
		: value.CompareTo(Maximum) > 0 ? Maximum
		: value;

	internal override object? CoerceBoxed(object value) => value is T typed ? Clamp(typed) : null;
}
