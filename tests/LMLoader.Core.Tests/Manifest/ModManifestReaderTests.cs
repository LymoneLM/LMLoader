using LMLoader.Core.Manifest;

namespace LMLoader.Core.Tests.Manifest;

public class ModManifestReaderTests
{
	private const string FullManifest = """
		{
		  "schemaVersion": 1,
		  "name": "示例合集",
		  "version": "1.2.3",
		  "authors": ["Author"],
		  "description": "演示完整字段的清单",
		  "icon": "icon.png",
		  "website": "https://example.com/modname",
		  "gameId": "com.game.identifier",
		  "loaderVersion": "1.0.0",
		  "mods": [
		    {
		      "uid": "com.author.modname.core",
		      "assemblies": ["Mod.dll"],
		      "depends": [
		        { "uid": "com.other.mod.core", "version": "2.0.0", "soft": false }
		      ]
		    },
		    {
		      "uid": "com.author.modname.extra",
		      "pck": ["pcks/mod.pck"],
		      "depends": [
		        { "uid": "com.author.modname.core", "soft": true }
		      ]
		    }
		  ]
		}
		""";

	private static ManifestReadResult Read(string json) =>
		ModManifestReader.ReadJson(json, "test.mod.json");

	[Fact]
	public void 完整清单可解析_字段完整映射()
	{
		var result = Read(FullManifest);

		Assert.True(result.Success);
		var manifest = result.Manifest!;
		Assert.Equal("com.game.identifier", manifest.GameId);
		Assert.Equal("1.0.0", manifest.LoaderVersion!.ToString());
		Assert.Equal(["Author"], manifest.Authors);
		Assert.Equal(2, manifest.Mods.Count);
		Assert.Equal("com.author.modname.core", manifest.Mods[0].Uid);
		Assert.Equal("示例合集", manifest.Mods[0].Name); // 继承文件级 name
		Assert.Equal(["Mod.dll"], manifest.Mods[0].Assemblies);
		var dependency = Assert.Single(manifest.Mods[0].Depends);
		Assert.False(dependency.Soft);
		Assert.Equal("2.0.0", dependency.Version!.Value.ToString());
		Assert.Equal("test.mod.json", manifest.SourcePath);
		Assert.True(manifest.Mods[1].PckResources.SequenceEqual(["pcks/mod.pck"]));
		Assert.Empty(manifest.Mods[1].Assemblies); // 纯资源模组:assemblies 缺省为空
	}

	[Fact]
	public void 软依赖与缺省version解析正确()
	{
		var result = Read(FullManifest);

		var soft = Assert.Single(result.Manifest!.Mods[1].Depends);
		Assert.True(soft.Soft);
		Assert.Null(soft.Version);
	}

	[Fact]
	public void 最小清单可解析_可选字段取默认()
	{
		var result = Read("""
			{
			  "schemaVersion": 1,
			  "version": "0.1.0",
			  "mods": [ { "uid": "com.a.m", "assemblies": ["M.dll"] } ]
			}
			""");

		Assert.True(result.Success);
		var manifest = result.Manifest!;
		Assert.Null(manifest.GameId); // D18:gameId 可选 = 通用模组
		Assert.Null(manifest.LoaderVersion); // 缺省 = 当前 loader 主版本兼容区(宿主侧解析)
		Assert.Empty(manifest.Authors);
		Assert.Null(manifest.Description);
		Assert.Equal("0.1.0", manifest.Mods[0].Version.ToString());
		Assert.Equal("m", manifest.Mods[0].Name); // 缺省 = uid 尾段
		Assert.Empty(manifest.Mods[0].Depends);
	}

	[Fact]
	public void 文件级字段被模组级覆盖_整项替换()
	{
		var result = Read("""
			{
			  "schemaVersion": 1,
			  "version": "1.0.0",
			  "name": "合集名",
			  "authors": ["A", "B"],
			  "tags": ["tool"],
			  "mods": [
			    { "uid": "com.a.m", "name": "定制名", "authors": ["C"], "tags": ["content", "ui"] },
			    { "uid": "com.a.n" }
			  ]
			}
			""");

		Assert.True(result.Success);
		var overridden = result.Manifest!.Mods[0];
		Assert.Equal("定制名", overridden.Name);
		Assert.Equal(["C"], overridden.Authors); // 整项替换,不与文件级合并
		Assert.Equal(["content", "ui"], overridden.Tags);
		var inherited = result.Manifest!.Mods[1];
		Assert.Equal("合集名", inherited.Name); // 未声明时继承文件级 name
		Assert.Equal(["A", "B"], inherited.Authors);
		Assert.Equal(["tool"], inherited.Tags);
	}

	[Fact]
	public void version两级皆缺_报错()
	{
		var result = Read("""
			{
			  "schemaVersion": 1,
			  "mods": [ { "uid": "com.a.m" } ]
			}
			""");

		Assert.False(result.Success);
		Assert.Contains(result.Errors, e => e.Contains("mods[0].version"));
	}

	[Fact]
	public void version模组级可单独提供()
	{
		var result = Read("""
			{
			  "schemaVersion": 1,
			  "mods": [ { "uid": "com.a.m", "version": "2.3.4" } ]
			}
			""");

		Assert.True(result.Success);
		Assert.Equal("2.3.4", result.Manifest!.Mods[0].Version.ToString());
	}

	[Fact]
	public void 缺少必填字段_错误累积收集()
	{
		var result = Read("""
			{ "schemaVersion": 1, "name": "m", "version": "bad-version!!" }
			""");

		Assert.False(result.Success);
		Assert.Contains(result.Errors, e => e.StartsWith("mods:", StringComparison.Ordinal));
		Assert.Contains(result.Errors, e => e.StartsWith("version:", StringComparison.Ordinal));
	}

	[Fact]
	public void schemaVersion主版本不识别_拒载()
	{
		var result = Read(FullManifest.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2"));

		Assert.False(result.Success);
		Assert.Contains(result.Errors, e => e.Contains("主版本 2 不受支持"));
	}

	[Fact]
	public void schemaVersion为字符串_报错()
	{
		var result = Read(FullManifest.Replace("\"schemaVersion\": 1", "\"schemaVersion\": \"1\""));

		Assert.False(result.Success);
		Assert.Contains(result.Errors, e => e.StartsWith("schemaVersion:", StringComparison.Ordinal));
	}

	[Fact]
	public void JSON语法错误_致命失败()
	{
		var result = Read("{ schemaVersion: ");

		Assert.False(result.Success);
		Assert.Contains(result.Errors, e => e.Contains("JSON 解析失败"));
	}

	[Fact]
	public void 宽容读入_未知字段忽略并警告()
	{
		var result = Read("""
			{
			  "schemaVersion": 1,
			  "version": "0.1.0",
			  "futureTopField": { "x": 1 },
			  "mods": [ { "uid": "com.a.m", "assemblies": ["M.dll"], "futureModField": 7 } ]
			}
			""");

		Assert.True(result.Success);
		Assert.Contains(result.Warnings, w => w == "futureTopField: 未知字段,已忽略(前向兼容)");
		Assert.Contains(result.Warnings, w => w.Contains("mods[0].futureModField"));
	}

	[Fact]
	public void 宽容JSON_注释与尾逗号可用()
	{
		var result = Read("""
			{
			  // 手写清单常见注释
			  "schemaVersion": 1,
			  "version": "0.1.0",
			  "mods": [ { "uid": "com.a.m", "assemblies": ["M.dll"], } ],
			}
			""");

		Assert.True(result.Success);
	}

	[Fact]
	public void 重复JSON键_警告并取最后值()
	{
		var result = Read("""
			{
			  "schemaVersion": 1,
			  "name": "first",
			  "name": "second",
			  "version": "0.1.0",
			  "mods": [ { "uid": "com.a.m" } ]
			}
			""");

		Assert.True(result.Success);
		Assert.Equal("second", result.Manifest!.Mods[0].Name); // 文件级继承展开
		Assert.Contains(result.Warnings, w => w.Contains("name: 字段重复定义"));
	}

	[Theory]
	[InlineData("modname")]
	[InlineData("Com Author.mod")]
	[InlineData(".author.mod")]
	[InlineData("com..author.mod")]
	public void 模组uid非法_报错(string badUid)
	{
		var result = Read($$"""
			{
			  "schemaVersion": 1,
			  "version": "0.1.0",
			  "mods": [ { "uid": "{{badUid}}" } ]
			}
			""");

		Assert.False(result.Success);
		Assert.Contains(result.Errors, e => e.Contains("不是合法的反向域名结构"));
	}

	[Fact]
	public void loaderVersion非法区间_报错并提示区间语法()
	{
		var result = Read(FullManifest.Replace("\"loaderVersion\": \"1.0.0\"", "\"loaderVersion\": \">=1.0.0 <2.0.0\""));

		Assert.False(result.Success);
		Assert.Contains(result.Errors, e => e.Contains("loaderVersion") && e.Contains("区间语法"));
	}

	[Fact]
	public void 模组uid清单内重复_报错()
	{
		var result = Read("""
			{
			  "schemaVersion": 1,
			  "version": "0.1.0",
			  "mods": [
			    { "uid": "com.a.m", "assemblies": ["M.dll"] },
			    { "uid": "com.a.m", "assemblies": ["M.dll"] }
			  ]
			}
			""");

		Assert.False(result.Success);
		Assert.Contains(result.Errors, e => e.Contains("重复定义"));
	}

	[Theory]
	[InlineData("libs/M.dll")]
	[InlineData("M")]
	[InlineData("M.zip")]
	public void assemblies非法文件名_报错(string badAssembly)
	{
		var result = Read($$"""
			{
			  "schemaVersion": 1,
			  "version": "0.1.0",
			  "mods": [ { "uid": "com.a.m", "assemblies": ["{{badAssembly}}"] } ]
			}
			""");

		Assert.False(result.Success);
		Assert.Contains(result.Errors, e => e.Contains(".assemblies["));
	}

	[Theory]
	[InlineData("/abs/mod.pck")]
	[InlineData("res://mods/x.pck")]
	[InlineData("../escape.pck")]
	[InlineData("C:\\x.pck")]
	[InlineData("")]
	public void pck非法路径_警告并忽略该项(string badPath)
	{
		var json = FullManifest.Replace("\"pcks/mod.pck\"", $"\"{badPath.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"");
		var result = Read(json);

		Assert.True(result.Success);
		Assert.Empty(result.Manifest!.Mods[1].PckResources);
		Assert.Contains(result.Warnings, w => w.Contains("mods[1].pck"));
	}

	[Fact]
	public void 依赖soft非布尔_报错()
	{
		var result = Read("""
			{
			  "schemaVersion": 1,
			  "version": "0.1.0",
			  "mods": [ { "uid": "com.a.m", "depends": [ { "uid": "com.b.m", "soft": "yes" } ] } ]
			}
			""");

		Assert.False(result.Success);
		Assert.Contains(result.Errors, e => e.Contains(".soft"));
	}

	[Fact]
	public void mods为空数组_报错()
	{
		var result = Read("""
			{ "schemaVersion": 1, "version": "0.1.0", "mods": [] }
			""");

		Assert.False(result.Success);
		Assert.Contains(result.Errors, e => e.Contains("至少需要一个模组"));
	}

	[Fact]
	public void 从文件读取_目录字段正确派生()
	{
		var tempDir = Path.Combine(Path.GetTempPath(), $"lmloader-test-{Guid.NewGuid():N}");
		Directory.CreateDirectory(tempDir);
		try
		{
			var manifestPath = Path.Combine(tempDir, "test.mod.json");
			File.WriteAllText(manifestPath, FullManifest);

			var result = ModManifestReader.ReadFile(manifestPath);

			Assert.True(result.Success);
			Assert.Equal(tempDir, result.Manifest!.Directory);
		}
		finally
		{
			Directory.Delete(tempDir, recursive: true);
		}
	}
}
