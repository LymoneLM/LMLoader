using LMLoader.Core.Manifest;
using LMLoader.Core.Versioning;
using LMLoader.Distribution.Thunderstore;

namespace LMLoader.Core.Tests.Distribution;

public class ThunderstoreRulesTests
{
	[Theory]
	[InlineData("MyMod")]
	[InlineData("my_mod")]
	[InlineData("m")]
	[InlineData("A1_B2")]
	public void 包名_合法不抛(string name) =>
		Assert.Matches(ThunderstoreRules.PackageName(), name);

	[Theory]
	[InlineData("my-mod")]
	[InlineData("my mod")]
	[InlineData("我的模组")]
	[InlineData("")]
	public void 包名_非法不匹配(string name) =>
		Assert.DoesNotMatch(ThunderstoreRules.PackageName(), name);

	[Theory]
	[InlineData("1.2.3")]
	[InlineData("0.0.1")]
	[InlineData("10.20.30")]
	public void 版本号_合法匹配(string version) =>
		Assert.Matches(ThunderstoreRules.VersionNumber(), version);

	[Theory]
	[InlineData("v1.2.3")] // 前导 v
	[InlineData("1.2")] // 缺段
	[InlineData("01.2.3")] // 前导零
	[InlineData("1.2.3-beta")] // prerelease
	[InlineData("1.2.3+build")] // build
	public void 版本号_非法不匹配(string version) =>
		Assert.DoesNotMatch(ThunderstoreRules.VersionNumber(), version);

	[Theory]
	[InlineData("Team-Name-1.2.3")]
	[InlineData("My.Team-My_Name-0.1.0")] // team 可含点
	public void 依赖引用_合法匹配(string reference) =>
		Assert.Matches(ThunderstoreRules.DependencyReference(), reference);

	[Theory]
	[InlineData("Name-1.2.3")] // 缺 team
	[InlineData("Team-Name-^1.2.3")] // 区间
	public void 依赖引用_非法不匹配(string reference) =>
		Assert.DoesNotMatch(ThunderstoreRules.DependencyReference(), reference);

	[Fact]
	public void 依赖引用_team含连字符可匹配_按平台逆向解析语义()
	{
		// 平台解析规则:版本为最后一段(恰两点),name 不含 '-',team 可含 '-'
		var match = ThunderstoreRules.DependencyReference().Match("Team-My-Mod-1.2.3");
		Assert.True(match.Success);
		Assert.Equal("Team-My", match.Groups["team"].Value);
		Assert.Equal("Mod", match.Groups["name"].Value);
	}

	[Fact]
	public void 缺省包名_UID尾段点换下划线()
	{
		Assert.Equal("modname", ThunderstoreRules.DefaultPackageName("com.author.modname"));
		Assert.Equal("my_mod", ThunderstoreRules.DefaultPackageName("com.author.my-mod"));
	}

	[Fact]
	public void 解析包名_显式声明优先_否则缺省()
	{
		var explicitName = MakeManifest("com.author.modname", distribution: new ModDistribution("Team", "Custom_Name"));
		Assert.Equal("Custom_Name", ThunderstoreRules.ResolvePackageName(explicitName));

		var implicitName = MakeManifest("com.author.modname", distribution: null);
		Assert.Equal("modname", ThunderstoreRules.ResolvePackageName(implicitName));
	}

	private static ModManifest MakeManifest(string uid, ModDistribution? distribution) => new()
	{
		SchemaVersion = 1,
		Uid = uid,
		Name = uid,
		Version = SemVer.Parse("1.0.0"),
		GameId = "com.game.test",
		LoaderVersion = VersionRange.Parse("*"),
		EntryAssembly = "Mod.dll",
		Modules = [new ModuleEntry { Uid = uid + ".main", Type = "T.M" }],
		SourcePath = $"{uid}.mod.json",
		Distribution = distribution,
	};
}

public class ThunderstoreManifestTests
{
	private const string ValidJson = """
		{
		  "name": "MyMod",
		  "version_number": "1.2.3",
		  "website_url": "https://example.com",
		  "description": "demo",
		  "dependencies": ["Team-OtherMod-2.0.0"]
		}
		""";

	[Fact]
	public void 解析合法manifest()
	{
		var manifest = ThunderstoreManifest.Parse(ValidJson);

		Assert.Equal("MyMod", manifest.Name);
		Assert.Equal("1.2.3", manifest.VersionNumber);
		var dep = Assert.Single(manifest.Dependencies);
		Assert.Equal("Team-OtherMod-2.0.0", dep);
	}

	[Fact]
	public void 往返序列化()
	{
		var manifest = ThunderstoreManifest.Parse(ValidJson);
		var roundTrip = ThunderstoreManifest.Parse(manifest.Serialize());

		Assert.Equal(manifest.Name, roundTrip.Name);
		Assert.Equal(manifest.VersionNumber, roundTrip.VersionNumber);
		Assert.Equal(manifest.Dependencies, roundTrip.Dependencies);
	}

	[Theory]
	[InlineData("bad-name")] // 包名含 '-'
	[InlineData("1.2")] // 版本缺段
	[InlineData("v1.2.3")] // 前导 v
	public void 校验_非法字段报错(string mode)
	{
		var manifest = new ThunderstoreManifest
		{
			Name = mode == "bad-name" ? "bad-name" : "MyMod",
			VersionNumber = mode is "1.2" or "v1.2.3" ? mode : "1.0.0",
		};

		Assert.NotEmpty(ThunderstoreManifest.Validate(manifest));
	}

	[Fact]
	public void 校验_描述超长报错()
	{
		var manifest = new ThunderstoreManifest
		{
			Name = "MyMod",
			VersionNumber = "1.0.0",
			Description = new string('x', 257),
		};

		Assert.NotEmpty(ThunderstoreManifest.Validate(manifest));
	}

	[Fact]
	public void 解析非法manifest_抛FormatException()
	{
		Assert.Throws<FormatException>(() => ThunderstoreManifest.Parse(
			"""{ "name": "bad-name", "version_number": "1.0.0", "dependencies": [] }"""));
	}
}

public class ThunderstoreAdapterTests
{
	private static ModManifest MakeMod(
		string uid,
		string version = "1.0.0",
		ModDistribution? distribution = null,
		string? website = null,
		string? description = null,
		(string ModuleUid, string? Range, string? Exact, bool Soft)[]? depends = null)
	{
		var modules = new[]
		{
			new ModuleEntry
			{
				Uid = uid + ".main",
				Type = "T.M",
				Depends = (depends ?? []).Select(d => new ModuleDependency(
					d.ModuleUid,
					d.Exact is null ? null : SemVer.Parse(d.Exact),
					d.Soft)
				{
					Range = d.Range is null ? null : VersionRange.Parse(d.Range),
				}).ToArray(),
			},
		};
		return new ModManifest
		{
			SchemaVersion = 1,
			Uid = uid,
			Name = uid,
			Version = SemVer.Parse(version),
			GameId = "com.game.test",
			LoaderVersion = VersionRange.Parse("*"),
			EntryAssembly = "Mod.dll",
			Modules = modules,
			SourcePath = $"{uid}.mod.json",
			Distribution = distribution,
			Website = website,
			Description = description,
		};
	}

	[Fact]
	public void 转换_基本字段与缺省包名()
	{
		var manifest = MakeMod("com.author.modname", "1.2.3",
			new ModDistribution("Author"), website: "https://example.com", description: "demo");

		var ts = ThunderstoreAdapter.ToThunderstore(manifest, "Author", new Dictionary<string, ModManifest>());

		Assert.Equal("modname", ts.Name);
		Assert.Equal("1.2.3", ts.VersionNumber);
		Assert.Equal("https://example.com", ts.WebsiteUrl);
		Assert.Equal("demo", ts.Description);
		Assert.Empty(ts.Dependencies);
	}

	[Fact]
	public void 转换_显式包名优先()
	{
		var manifest = MakeMod("com.author.modname", distribution: new ModDistribution("Author", "Custom_Name"));

		var ts = ThunderstoreAdapter.ToThunderstore(manifest, "Author", new Dictionary<string, ModManifest>());

		Assert.Equal("Custom_Name", ts.Name);
	}

	[Fact]
	public void 转换_prerelease版本报错()
	{
		var manifest = MakeMod("com.author.modname", "1.0.0-beta.1");

		Assert.Throws<NotSupportedException>(
			() => ThunderstoreAdapter.ToThunderstore(manifest, "Author", new Dictionary<string, ModManifest>()));
	}

	[Fact]
	public void 转换_依赖经别名映射为精确引用()
	{
		// 依赖模组 com.other.lib v2.1.0,别名 TeamB/lib;依赖声明区间 ^2.0.0
		var dependency = MakeMod("com.other.lib", "2.1.0", new ModDistribution("TeamB"));
		var manifest = MakeMod("com.author.modname", depends:
		[
			("com.other.lib.main", "^2.0.0", null, false),
		]);

		var ts = ThunderstoreAdapter.ToThunderstore(
			manifest, "Author", new Dictionary<string, ModManifest> { ["com.other.lib"] = dependency });

		var dep = Assert.Single(ts.Dependencies);
		Assert.Equal("TeamB-lib-2.0.0", dep); // 区间 ^2.0.0 → 闭下界 2.0.0(D16)
	}

	[Fact]
	public void 转换_同模组多模块依赖只生成一条()
	{
		var dependency = MakeMod("com.other.lib", "1.0.0", new ModDistribution("TeamB"));
		var manifest = MakeMod("com.author.modname", depends:
		[
			("com.other.lib.main", null, null, false),
			("com.other.lib.extra", null, null, true),
		]);

		var ts = ThunderstoreAdapter.ToThunderstore(
			manifest, "Author", new Dictionary<string, ModManifest> { ["com.other.lib"] = dependency });

		var dep = Assert.Single(ts.Dependencies);
		Assert.Equal("TeamB-lib-1.0.0", dep);
	}

	[Fact]
	public void 转换_依赖无别名_警告并跳过()
	{
		var dependency = MakeMod("com.other.lib", "1.0.0"); // 无 distribution
		var manifest = MakeMod("com.author.modname", depends:
		[
			("com.other.lib.main", null, null, false),
		]);
		var warnings = new List<string>();

		var ts = ThunderstoreAdapter.ToThunderstore(
			manifest, "Author", new Dictionary<string, ModManifest> { ["com.other.lib"] = dependency },
			warnings.Add);

		Assert.Empty(ts.Dependencies);
		Assert.Single(warnings);
	}

	[Fact]
	public void 转换_依赖缺失清单_警告并跳过()
	{
		var manifest = MakeMod("com.author.modname", depends:
		[
			("com.ghost.mod.main", null, null, false),
		]);
		var warnings = new List<string>();

		var ts = ThunderstoreAdapter.ToThunderstore(manifest, "Author", new Dictionary<string, ModManifest>(), warnings.Add);

		Assert.Empty(ts.Dependencies);
		Assert.Single(warnings);
	}
}
