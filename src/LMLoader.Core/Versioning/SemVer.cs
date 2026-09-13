// SPDX-License-Identifier: LGPL-3.0-or-later

using System.Globalization;

namespace LMLoader.Core.Versioning;

/// <summary>
/// SemVer 2.0.0 版本值(草稿"内置以SemVer为基准的版本管理体系")。
/// 严格解析:禁止前导零、空标识符与非法字符。
/// 排序与相等遵循规范优先级规则:<see cref="Build"/> 不参与(规范:build metadata 不影响 precedence)。
/// </summary>
public readonly struct SemVer : IComparable<SemVer>, IEquatable<SemVer>
{
	private static readonly string[] EmptyIdentifiers = Array.Empty<string>();

	public int Major { get; }
	public int Minor { get; }
	public int Patch { get; }

	/// <summary>prerelease 标识符(点分段);空 = 正式版。</summary>
	public IReadOnlyList<string> Prerelease { get; }

	/// <summary>build 元数据标识符(点分段);空 = 无。仅用于显示,不参与比较。</summary>
	public IReadOnlyList<string> Build { get; }

	/// <summary>是否为 prerelease(1.0.0-rc.1 之类;优先级低于同版本号正式版)。</summary>
	public bool IsPrerelease => Prerelease.Count > 0;

	public SemVer(int major, int minor, int patch, IReadOnlyList<string>? prerelease = null, IReadOnlyList<string>? build = null)
	{
		if (major < 0 || minor < 0 || patch < 0)
		{
			throw new ArgumentOutOfRangeException(nameof(major), "SemVer 核心版本号必须为非负整数");
		}

		Major = major;
		Minor = minor;
		Patch = patch;
		Prerelease = ValidateIdentifiers(prerelease, allowNumericLeadingZero: false, nameof(prerelease));
		Build = ValidateIdentifiers(build, allowNumericLeadingZero: true, nameof(build));
	}

	/// <summary>解析失败抛 <see cref="FormatException"/>(面向清单报错链路)。</summary>
	public static SemVer Parse(string text)
	{
		if (!TryParse(text, out var version))
		{
			throw new FormatException($"无效的 SemVer 版本串: \"{text}\"(要求 MAJOR.MINOR.PATCH[-prerelease][+build])");
		}

		return version;
	}

	public static bool TryParse(string? text, out SemVer version)
	{
		version = default;

		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}

		var rest = text.Trim();

		// build 元数据:+ 之后(仅第一处;标识符本身不含 '+')
		string buildPart = "";
		var plus = rest.IndexOf('+');
		if (plus >= 0)
		{
			buildPart = rest[(plus + 1)..];
			rest = rest[..plus];
			if (buildPart.Length == 0)
			{
				return false;
			}
		}

		// prerelease:核心版本号(纯数字)后的第一个 '-' 即分隔符
		string prereleasePart = "";
		var dash = rest.IndexOf('-');
		if (dash >= 0)
		{
			prereleasePart = rest[(dash + 1)..];
			rest = rest[..dash];
			if (prereleasePart.Length == 0)
			{
				return false;
			}
		}

		var core = rest.Split('.');
		if (core.Length != 3)
		{
			return false;
		}

		if (!TryParseCoreNumber(core[0], out var major) ||
			!TryParseCoreNumber(core[1], out var minor) ||
			!TryParseCoreNumber(core[2], out var patch))
		{
			return false;
		}

		if (!TrySplitIdentifiers(prereleasePart, out var prerelease) ||
			!TrySplitIdentifiers(buildPart, out var build))
		{
			return false;
		}

		if (!ValidatePrereleaseIdentifiers(prerelease))
		{
			return false;
		}

		version = new SemVer(major, minor, patch, prerelease, build);
		return true;
	}

	public bool Equals(SemVer other) => CompareTo(other) == 0;

	public override bool Equals(object? obj) => obj is SemVer other && Equals(other);

	// Build 不参与比较,哈希亦须排除以与 Equals 一致
	public override int GetHashCode() =>
		HashCode.Combine(Major, Minor, Patch, string.Join(".", Prerelease));

	public int CompareTo(SemVer other)
	{
		var core = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
		if (core != 0)
		{
			return core;
		}

		// 正式版 > prerelease;双方均无 prerelease 时此处即相等(返回 0)
		if (Prerelease.Count != other.Prerelease.Count && (Prerelease.Count == 0 || other.Prerelease.Count == 0))
		{
			return Prerelease.Count == 0 ? 1 : -1;
		}

		var shared = Math.Min(Prerelease.Count, other.Prerelease.Count);
		for (var i = 0; i < shared; i++)
		{
			var cmp = CompareIdentifier(Prerelease[i], other.Prerelease[i]);
			if (cmp != 0)
			{
				return cmp;
			}
		}

		// 前缀全等时,较短一方的优先级更低
		return Prerelease.Count.CompareTo(other.Prerelease.Count);
	}

	public override string ToString()
	{
		var text = $"{Major.ToString(CultureInfo.InvariantCulture)}.{Minor.ToString(CultureInfo.InvariantCulture)}.{Patch.ToString(CultureInfo.InvariantCulture)}";

		if (Prerelease.Count > 0)
		{
			text += "-" + string.Join(".", Prerelease);
		}

		if (Build.Count > 0)
		{
			text += "+" + string.Join(".", Build);
		}

		return text;
	}

	public static bool operator ==(SemVer left, SemVer right) => left.Equals(right);

	public static bool operator !=(SemVer left, SemVer right) => !left.Equals(right);

	public static bool operator <(SemVer left, SemVer right) => left.CompareTo(right) < 0;

	public static bool operator <=(SemVer left, SemVer right) => left.CompareTo(right) <= 0;

	public static bool operator >(SemVer left, SemVer right) => left.CompareTo(right) > 0;

	public static bool operator >=(SemVer left, SemVer right) => left.CompareTo(right) >= 0;

	private static bool TryParseCoreNumber(string segment, out int value)
	{
		value = 0;

		// 全数字且无前导零(int.TryParse 会放过 "007",此处显式拒绝)
		if (segment.Length == 0 || (segment.Length > 1 && segment[0] == '0') || !segment.All(char.IsAsciiDigit))
		{
			return false;
		}

		return int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out value);
	}

	private static bool TrySplitIdentifiers(string part, out string[] identifiers)
	{
		if (part.Length == 0)
		{
			identifiers = EmptyIdentifiers;
			return true;
		}

		identifiers = part.Split('.');
		return identifiers.All(id => id.Length > 0 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));
	}

	private static bool ValidatePrereleaseIdentifiers(string[] identifiers) =>
		identifiers.All(id => !IsNumeric(id) || id.Length == 1 || id[0] != '0');

	private static bool IsNumeric(string identifier) => identifier.All(char.IsAsciiDigit);

	private static int CompareIdentifier(string left, string right)
	{
		var leftNumeric = IsNumeric(left);
		var rightNumeric = IsNumeric(right);

		// 规范:数字标识符 < 字母数字标识符
		if (leftNumeric != rightNumeric)
		{
			return leftNumeric ? -1 : 1;
		}

		if (leftNumeric)
		{
			// 无前导零:先比位数再比字典序,避免超长数字溢出
			var byLength = left.Length.CompareTo(right.Length);
			return byLength != 0 ? byLength : string.CompareOrdinal(left, right);
		}

		return string.CompareOrdinal(left, right);
	}

	private static string[] ValidateIdentifiers(IReadOnlyList<string>? identifiers, bool allowNumericLeadingZero, string paramName)
	{
		if (identifiers is null || identifiers.Count == 0)
		{
			return EmptyIdentifiers;
		}

		var copy = new string[identifiers.Count];
		for (var i = 0; i < identifiers.Count; i++)
		{
			var id = identifiers[i];
			if (id.Length == 0 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
			{
				throw new ArgumentException($"非法版本标识符: \"{id}\"", paramName);
			}

			if (!allowNumericLeadingZero && IsNumeric(id) && id.Length > 1 && id[0] == '0')
			{
				throw new ArgumentException($"prerelease 数字标识符禁止前导零: \"{id}\"", paramName);
			}

			copy[i] = id;
		}

		return copy;
	}
}
