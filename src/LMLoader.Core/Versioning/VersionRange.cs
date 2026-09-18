// SPDX-License-Identifier: LGPL-3.0-or-later

using System.Globalization;

namespace LMLoader.Core.Versioning;

/// <summary>
/// 版本区间(阶段 5,D9 迭代):语法——
/// <list type="bullet">
/// <item><c>*</c>:任意版本(缺省等价)</item>
/// <item><c>^M.m.p</c>:同主版本兼容区 <c>[M.m.p, (M+1).0.0)</c>;主版本为 0 时按次版本收口 <c>[0.m.p, 0.(m+1).0)</c>(0.x 生态惯例)</item>
/// <item><c>~M.m.p</c>:同次版本补丁区 <c>[M.m.p, M.(m+1).0)</c>;缺次版本时 <c>~M.m</c> = <c>[M.m.0, M.(m+1).0)</c></item>
/// <item><c>&gt;=</c>、<c>&gt;</c>、<c>&lt;=</c>、<c>&lt;</c>、<c>=</c> 前缀比较符;裸版本 = 精确匹配(= 等价)</item>
/// <item>部分版本号:<c>1.2</c> 在 <c>^</c>/<c>~</c> 下按上述规则;作为精确值时等价 <c>1.2.0</c>(补零)</item>
/// <item>单个区间内不支持空格组合(交集请用多依赖声明表达,保持清单简单)</item>
/// </list>
/// 交集:不可满足(空交集)返回 false,由规划器输出冲突链。
/// </summary>
public sealed class VersionRange
{
	private readonly VersionBound _lower;
	private readonly VersionBound _upper;

	private VersionRange(VersionBound lower, VersionBound upper, string originalText)
	{
		_lower = lower;
		_upper = upper;
		OriginalText = originalText;
	}

	/// <summary>原始声明文本(错误链路展示用)。</summary>
	public string OriginalText { get; }

	/// <summary>解析失败抛 <see cref="FormatException"/>(面向清单报错链路)。</summary>
	public static VersionRange Parse(string text) =>
		TryParse(text, out var range)
			? range
			: throw new FormatException(
				$"无效的版本区间: \"{text}\"(支持 ^ ~ >= > <= < = 精确版本与 *)");

	public static bool TryParse(string? text, out VersionRange range)
	{
		range = null!;
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}

		var rest = text.Trim();
		if (rest == "*")
		{
			range = new VersionRange(
				VersionBound.Unbounded, VersionBound.Unbounded, rest);
			return true;
		}

		if (rest.Contains(' ') || rest.Contains(','))
		{
			return false; // 组合区间不支持,保持清单声明简单
		}

		string op;
		if (rest.StartsWith("^", StringComparison.Ordinal))
		{
			op = "^";
			rest = rest[1..];
		}
		else if (rest.StartsWith("~>", StringComparison.Ordinal))
		{
			op = "~";
			rest = rest[2..];
		}
		else if (rest.StartsWith("~", StringComparison.Ordinal))
		{
			op = "~";
			rest = rest[1..];
		}
		else if (rest.StartsWith(">=", StringComparison.Ordinal))
		{
			op = ">=";
			rest = rest[2..];
		}
		else if (rest.StartsWith("<=", StringComparison.Ordinal))
		{
			op = "<=";
			rest = rest[2..];
		}
		else if (rest.StartsWith(">", StringComparison.Ordinal))
		{
			op = ">";
			rest = rest[1..];
		}
		else if (rest.StartsWith("<", StringComparison.Ordinal))
		{
			op = "<";
			rest = rest[1..];
		}
		else if (rest.StartsWith("=", StringComparison.Ordinal))
		{
			op = "=";
			rest = rest[1..];
		}
		else
		{
			op = "=";
		}

		var versionText = rest.Trim();
		if (versionText.Length == 0 || versionText.EndsWith(".", StringComparison.Ordinal))
		{
			return false;
		}

		// 部分版本号(1.2 / 1)补零到 M.m.p 再走严格 SemVer;^/~ 的部分号语义由 op 分支处理
		var segmentCount = versionText.Split('.').Length;
		var normalized = segmentCount switch
		{
			1 => versionText + ".0.0",
			2 => versionText + ".0",
			_ => versionText,
		};
		if (!SemVer.TryParse(normalized, out var version))
		{
			return false;
		}
		range = op switch
		{
			"^" => Caret(version, segmentCount),
			"~" => Tilde(version, segmentCount),
			">=" => new VersionRange(
				VersionBound.Inclusive(version), VersionBound.Unbounded, text.Trim()),
			">" => new VersionRange(
				VersionBound.Exclusive(version), VersionBound.Unbounded, text.Trim()),
			"<=" => new VersionRange(
				VersionBound.Unbounded, VersionBound.Inclusive(version), text.Trim()),
			"<" => new VersionRange(
				VersionBound.Unbounded, VersionBound.Exclusive(version), text.Trim()),
			_ => new VersionRange(
				VersionBound.Inclusive(version), VersionBound.Inclusive(version), text.Trim()),
		};
		return true;
	}

	private static VersionRange Caret(SemVer version, int segmentCount)
	{
		// ^0.0.x 特例:仅补丁位浮动 [x 版本, 0.0.(x+1))
		if (version.Major == 0 && version.Minor == 0 && segmentCount >= 3)
		{
			return new VersionRange(
				VersionBound.Inclusive(version),
				VersionBound.Exclusive(new SemVer(0, 0, version.Patch + 1)),
				"^" + version);
		}

		// ^0.m.p / ^0.m → [0.m.p, 0.(m+1).0);^M.m.p(M≥1)→ [M.m.p, (M+1).0.0)
		var upper = version.Major == 0
			? new SemVer(0, version.Minor + 1, 0)
			: new SemVer(version.Major + 1, 0, 0);
		return new VersionRange(
			VersionBound.Inclusive(version), VersionBound.Exclusive(upper), "^" + version);
	}

	private static VersionRange Tilde(SemVer version, int segmentCount)
	{
		// ~1.2(部分号)与 ~1.2.3 同为 [1.2.0, 1.3.0);~1 → [1.0.0, 2.0.0)
		var upper = segmentCount == 1
			? new SemVer(version.Major + 1, 0, 0)
			: new SemVer(version.Major, version.Minor + 1, 0);
		return new VersionRange(
			VersionBound.Inclusive(version), VersionBound.Exclusive(upper), "~" + version);
	}

	/// <summary>取区间下界:<paramref name="inclusive"/> 为 false 表示开下界(&gt;v);无界返回 false。</summary>
	public bool TryGetLowerBound(out SemVer bound, out bool inclusive)
	{
		if (_lower.IsUnbounded)
		{
			bound = default;
			inclusive = false;
			return false;
		}

		bound = _lower.Version;
		inclusive = _lower.IsInclusive;
		return true;
	}

	/// <summary>版本是否落在区间内。</summary>
	public bool Contains(SemVer version)
	{		if (!_lower.IsUnbounded)
		{
			var lowerCompare = version.CompareTo(_lower.Version);
			if (_lower.IsInclusive ? lowerCompare < 0 : lowerCompare <= 0)
			{
				return false;
			}
		}

		if (!_upper.IsUnbounded)
		{
			var upperCompare = version.CompareTo(_upper.Version);
			if (_upper.IsInclusive ? upperCompare > 0 : upperCompare >= 0)
			{
				return false;
			}
		}

		return true;
	}

	/// <summary>
	/// 求交集;无可满足版本返回 false(规划器输出冲突链)。
	/// 精确区间(单点)彼此求交同样适用:同为一点时相等即相交。
	/// </summary>
	public bool TryIntersect(VersionRange other, out VersionRange intersection)
	{
		var lower = Max(_lower, other._lower);
		var upper = Min(_upper, other._upper);

		// lower > upper → 空交集;lower == upper 且任一端为开 → 空
		var comparison = CompareBounds(lower, upper);
		if (comparison > 0 || (comparison == 0 && (!lower.IsInclusive || !upper.IsInclusive)))
		{
			intersection = null!;
			return false;
		}

		intersection = new VersionRange(lower, upper, $"{OriginalText} ∩ {other.OriginalText}");
		return true;
	}

	private static VersionBound Max(VersionBound a, VersionBound b)
	{
		if (a.IsUnbounded)
		{
			return b;
		}

		if (b.IsUnbounded)
		{
			return a;
		}

		var comparison = a.Version.CompareTo(b.Version);
		if (comparison != 0)
		{
			return comparison > 0 ? a : b;
		}

		// 同点:开端更严格
		return a.IsInclusive ? b : a;
	}

	private static VersionBound Min(VersionBound a, VersionBound b)
	{
		if (a.IsUnbounded)
		{
			return b;
		}

		if (b.IsUnbounded)
		{
			return a;
		}

		var comparison = a.Version.CompareTo(b.Version);
		if (comparison != 0)
		{
			return comparison < 0 ? a : b;
		}

		// 同点:开端更严格
		return a.IsInclusive ? b : a;
	}

	private static int CompareBounds(VersionBound a, VersionBound b)
	{
		if (a.IsUnbounded || b.IsUnbounded)
		{
			return a.IsUnbounded && b.IsUnbounded ? 0 : a.IsUnbounded ? -1 : 1;
		}

		return a.Version.CompareTo(b.Version);
	}

	public override string ToString() => OriginalText;

	private readonly record struct VersionBound(SemVer Version, bool IsInclusive, bool IsUnbounded)
	{
		public static VersionBound Unbounded { get; } = new(default, false, true);

		public static VersionBound Inclusive(SemVer version) => new(version, true, false);

		public static VersionBound Exclusive(SemVer version) => new(version, false, false);
	}
}
