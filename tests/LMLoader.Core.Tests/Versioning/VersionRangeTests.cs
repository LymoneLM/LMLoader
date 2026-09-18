using LMLoader.Core.Versioning;

namespace LMLoader.Core.Tests.Versioning;

public class VersionRangeTests
{
	[Theory]
	[InlineData("^1.2.3")]
	[InlineData("~1.2.3")]
	[InlineData(">=1.2.3")]
	[InlineData(">1.2.3")]
	[InlineData("<=1.2.3")]
	[InlineData("<1.2.3")]
	[InlineData("=1.2.3")]
	[InlineData("1.2.3")]
	[InlineData("*")]
	public void 合法语法解析成功(string text)
	{
		Assert.True(VersionRange.TryParse(text, out var range));
		Assert.Equal(text, range.OriginalText);
	}

	[Theory]
	[InlineData("")]
	[InlineData(" ")]
	[InlineData("abc")]
	[InlineData("1.2.3.4")]
	[InlineData("^")]
	[InlineData("~.")]
	[InlineData(">= 1.2.3")] // 空格组合不支持(含空格一律拒绝)
	[InlineData("1.2.3 2.0.0")]
	[InlineData(">=1.0.0, <2.0.0")]
	[InlineData("^1.2.3-alpha.01")] // prerelease 前导零非法(SemVer 规则复用)
	public void 非法语法解析失败(string text)
	{
		Assert.False(VersionRange.TryParse(text, out _));
	}

	[Fact]
	public void 脱字符_标准兼容区()
	{
		var range = VersionRange.Parse("^1.2.3");

		Assert.True(range.Contains(new SemVer(1, 2, 3)));
		Assert.True(range.Contains(new SemVer(1, 9, 0)));
		Assert.False(range.Contains(new SemVer(2, 0, 0)));
		Assert.False(range.Contains(new SemVer(1, 2, 2)));
	}

	[Fact]
	public void 脱字符_零主版本按次版本收口()
	{
		var range = VersionRange.Parse("^0.2.3");

		Assert.True(range.Contains(new SemVer(0, 2, 3)));
		Assert.True(range.Contains(new SemVer(0, 2, 9)));
		Assert.False(range.Contains(new SemVer(0, 3, 0)));
		Assert.False(range.Contains(new SemVer(1, 0, 0)));
	}

	[Fact]
	public void 脱字符_零零版本按补丁收口()
	{
		var range = VersionRange.Parse("^0.0.3");

		Assert.True(range.Contains(new SemVer(0, 0, 3)));
		Assert.False(range.Contains(new SemVer(0, 0, 4)));
	}

	[Fact]
	public void 波浪符_同次版本补丁区()
	{
		var range = VersionRange.Parse("~1.2.3");

		Assert.True(range.Contains(new SemVer(1, 2, 3)));
		Assert.True(range.Contains(new SemVer(1, 2, 99)));
		Assert.False(range.Contains(new SemVer(1, 3, 0)));
	}

	[Fact]
	public void 波浪符_部分版本号_缺补丁()
	{
		var range = VersionRange.Parse("~1.2");

		Assert.True(range.Contains(new SemVer(1, 2, 0)));
		Assert.False(range.Contains(new SemVer(1, 3, 0)));
	}

	[Fact]
	public void 波浪符_单段版本_主版本浮动()
	{
		var range = VersionRange.Parse("~1");

		Assert.True(range.Contains(new SemVer(1, 9, 9)));
		Assert.False(range.Contains(new SemVer(2, 0, 0)));
	}

	[Fact]
	public void 比较符_开闭端点语义()
	{
		Assert.True(VersionRange.Parse(">=1.2.3").Contains(new SemVer(1, 2, 3)));
		Assert.False(VersionRange.Parse(">1.2.3").Contains(new SemVer(1, 2, 3)));
		Assert.True(VersionRange.Parse("<=1.2.3").Contains(new SemVer(1, 2, 3)));
		Assert.False(VersionRange.Parse("<1.2.3").Contains(new SemVer(1, 2, 3)));
		Assert.True(VersionRange.Parse(">1.0.0").Contains(new SemVer(1, 5, 0)));
	}

	[Fact]
	public void 精确与裸版本_补零等价()
	{
		Assert.True(VersionRange.Parse("1.2.3").Contains(new SemVer(1, 2, 3)));
		Assert.False(VersionRange.Parse("1.2.3").Contains(new SemVer(1, 2, 4)));

		// 部分号 = 补零精确
		Assert.True(VersionRange.Parse("1.2").Contains(new SemVer(1, 2, 0)));
		Assert.False(VersionRange.Parse("1.2").Contains(new SemVer(1, 2, 1)));

		Assert.True(VersionRange.Parse("=2.0.0").Contains(new SemVer(2, 0, 0)));
	}

	[Fact]
	public void 星号_任意版本()
	{
		var range = VersionRange.Parse("*");

		Assert.True(range.Contains(new SemVer(0, 0, 1)));
		Assert.True(range.Contains(new SemVer(99, 0, 0)));
	}

	[Fact]
	public void prerelease_比较参与区间判断()
	{
		var range = VersionRange.Parse("^1.2.3");

		Assert.True(range.Contains(new SemVer(1, 3, 0, ["beta", "1"])));
		Assert.False(range.Contains(new SemVer(1, 2, 0, ["alpha"])));
	}

	[Fact]
	public void 交集_重叠区间取公共部分()
	{
		var a = VersionRange.Parse(">=1.2.0");
		var b = VersionRange.Parse("<2.0.0");

		Assert.True(a.TryIntersect(b, out var both));

		Assert.True(both.Contains(new SemVer(1, 5, 0)));
		Assert.True(both.Contains(new SemVer(1, 2, 0)));
		Assert.False(both.Contains(new SemVer(2, 0, 0)));
		Assert.False(both.Contains(new SemVer(1, 1, 9)));
	}

	[Fact]
	public void 交集_无重叠返回false()
	{
		var a = VersionRange.Parse("<1.0.0");
		var b = VersionRange.Parse(">=2.0.0");

		Assert.False(a.TryIntersect(b, out _));
	}

	[Fact]
	public void 交集_端点相接开闭语义()
	{
		// [1.0.0, 2.0.0) 与 [2.0.0, 3.0.0):端点相遇但一开一闭 → 空
		var a = VersionRange.Parse(">=1.0.0");
		var upperExclusive = VersionRange.Parse("<2.0.0");
		var b = VersionRange.Parse(">=2.0.0");

		Assert.True(a.TryIntersect(upperExclusive, out var leftHalf));
		Assert.True(leftHalf.Contains(new SemVer(1, 5, 0)));
		Assert.False(leftHalf.Contains(new SemVer(2, 0, 0)));

		Assert.False(upperExclusive.TryIntersect(b, out _));
	}

	[Fact]
	public void 交集_同点闭端点可相交_开端为空()
	{
		var exact = VersionRange.Parse("1.5.0");
		var closedAt = VersionRange.Parse("<=1.5.0");
		var openAt = VersionRange.Parse(">1.5.0");

		Assert.True(exact.TryIntersect(closedAt, out var point));
		Assert.True(point.Contains(new SemVer(1, 5, 0)));

		Assert.False(exact.TryIntersect(openAt, out _));
	}

	[Fact]
	public void 交集_多区间链式求交()
	{
		var a = VersionRange.Parse(">=1.0.0");
		var b = VersionRange.Parse("<1.5.0");
		var c = VersionRange.Parse("^1.2.0");

		Assert.True(a.TryIntersect(b, out var ab));
		Assert.True(ab.TryIntersect(c, out var abc));

		Assert.True(abc.Contains(new SemVer(1, 2, 0)));
		Assert.True(abc.Contains(new SemVer(1, 4, 9)));
		Assert.False(abc.Contains(new SemVer(1, 5, 0)));
	}

	[Fact]
	public void 解析失败_抛FormatException()
	{
		Assert.Throws<FormatException>(() => VersionRange.Parse("nope"));
	}
}
