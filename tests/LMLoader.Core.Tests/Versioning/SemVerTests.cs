using LMLoader.Core.Versioning;

namespace LMLoader.Core.Tests.Versioning;

public class SemVerTests
{
	[Theory]
	[InlineData("1.2.3")]
	[InlineData("0.0.0")]
	[InlineData("1.0.0-alpha")]
	[InlineData("1.0.0-alpha.1")]
	[InlineData("1.0.0-alpha.beta")]
	[InlineData("1.0.0-0.3.7")]
	[InlineData("1.0.0-x.7.z.92")]
	[InlineData("1.0.0-alpha+build.1")]
	[InlineData("1.0.0+20130313144700")]
	[InlineData("1.0.0-beta+exp.sha.5114f85")]
	[InlineData("10.20.30")]
	public void Parse_合法版本串可解析并round_trip(string text)
	{
		var version = SemVer.Parse(text);

		Assert.Equal(text, version.ToString());
	}

	[Fact]
	public void Parse_字段拆解正确()
	{
		var version = SemVer.Parse("2.3.7-rc.1+build.42");

		Assert.Equal(2, version.Major);
		Assert.Equal(3, version.Minor);
		Assert.Equal(7, version.Patch);
		Assert.Equal(new[] { "rc", "1" }, version.Prerelease);
		Assert.Equal(new[] { "build", "42" }, version.Build);
		Assert.True(version.IsPrerelease);
	}

	[Theory]
	[InlineData("")]
	[InlineData("1")]
	[InlineData("1.2")]
	[InlineData("v1.2.3")]
	[InlineData("01.2.3")]
	[InlineData("1.02.3")]
	[InlineData("1.2.03")]
	[InlineData("-1.2.3")]
	[InlineData("1.2.3-")]
	[InlineData("1.2.3+")]
	[InlineData("1.2.3-.alpha")]
	[InlineData("1.2.3-alpha..1")]
	[InlineData("1.2.3-01")]
	[InlineData("1.2.3-alpha_1")]
	[InlineData("1.2.3-alpha.01")]
	[InlineData(" 1.2.3+  ")]
	[InlineData("99999999999999999999.0.0")]
	[InlineData("1.2.3 中文")]
	public void TryParse_非法版本串被拒绝(string text)
	{
		Assert.False(SemVer.TryParse(text, out _));
	}

	[Fact]
	public void Parse_非法版本串抛FormatException()
	{
		Assert.Throws<FormatException>(() => SemVer.Parse("not-a-version"));
	}

	[Fact]
	public void 比较_遵循规范优先级序列()
	{
		// SemVer 规范 §11 示例序列
		string[] ordered =
		[
			"1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta",
			"1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0",
		];

		var parsed = ordered.Select(SemVer.Parse).ToArray();

		for (var i = 0; i < parsed.Length; i++)
		{
			for (var j = i + 1; j < parsed.Length; j++)
			{
				Assert.True(parsed[i] < parsed[j], $"{ordered[i]} 应小于 {ordered[j]}");
			}
		}
	}

	[Fact]
	public void 比较_数字比较而非字典序()
	{
		Assert.True(SemVer.Parse("2.0.0") < SemVer.Parse("10.0.0"));
		Assert.True(SemVer.Parse("1.0.9") < SemVer.Parse("1.0.10"));
	}

	[Fact]
	public void 比较_build元数据不影响优先级()
	{
		var plain = SemVer.Parse("1.0.0");
		var withBuild = SemVer.Parse("1.0.0+exp.sha.5114f85");

		Assert.Equal(0, plain.CompareTo(withBuild));
		Assert.True(plain == withBuild);
		Assert.Equal(plain.GetHashCode(), withBuild.GetHashCode());
	}

	[Fact]
	public void 排序_乱序列表可确定性还原()
	{
		string[] ordered =
		[
			"1.0.0-alpha", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0", "1.2.3", "1.10.0", "2.0.0",
		];
		var shuffled = new[] { "1.10.0", "1.0.0", "2.0.0", "1.0.0-beta.11", "1.2.3", "1.0.0-beta.2", "1.0.0-alpha" }
			.Select(SemVer.Parse)
			.ToArray();

		Array.Sort(shuffled);

		Assert.Equal(ordered, shuffled.Select(v => v.ToString()));
	}

	[Fact]
	public void 相等_同优先级异prerelease不等()
	{
		Assert.NotEqual(SemVer.Parse("1.0.0"), SemVer.Parse("1.0.0-rc.1"));
		Assert.NotEqual(SemVer.Parse("1.0.0-alpha"), SemVer.Parse("1.0.0-beta"));
	}
}
