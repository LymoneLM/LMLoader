using LMLoader.Core.Manifest;

namespace LMLoader.Core.Tests.Manifest;

public class ModManifestReaderTests
{
	private const string FullManifest = """
		{
		  "schemaVersion": 1,
		  "uid": "com.author.modname",
		  "name": "示例模组",
		  "version": "1.2.3",
		  "authors": ["Author"],
		  "description": "演示完整字段的清单",
		  "icon": "icon.png",
		  "website": "https://example.com/modname",
		  "gameId": "com.game.identifier",
		  "loaderVersion": "1.0.0",
		  "entry": {
		    "assembly": "Mod.dll",
		    "modules": [
		      {
		        "uid": "com.author.modname.core",
		        "type": "Author.Modname.CoreModule",
		        "depends": [
		          { "uid": "com.other.mod.core", "version": "2.0.0", "soft": false }
		        ]
		      },
		      {
		        "uid": "com.author.modname.extra",
		        "type": "Author.Modname.ExtraModule",
		        "depends": [
		          { "uid": "com.author.modname.core", "soft": true }
		        ]
		      }
		    ]
		  },
		  "resources": { "pck": ["pcks/mod.pck"] }
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
		Assert.Equal("com.author.modname", manifest.Uid);
		Assert.Equal("示例模组", manifest.Name);
		Assert.Equal("1.2.3", manifest.Version.ToString());
		Assert.Equal(["Author"], manifest.Authors);
		Assert.Equal("com.game.identifier", manifest.GameId);
		Assert.Equal("1.0.0", manifest.LoaderVersion.ToString());
		Assert.Equal("Mod.dll", manifest.EntryAssembly);
		Assert.Equal(2, manifest.Modules.Count);
		Assert.Equal("com.author.modname.core", manifest.Modules[0].Uid);
		Assert.Equal("Author.Modname.CoreModule", manifest.Modules[0].Type);
		var dependency = Assert.Single(manifest.Modules[0].Depends);
		Assert.False(dependency.Soft);
		Assert.Equal("2.0.0", dependency.Version!.Value.ToString());
		Assert.Equal("test.mod.json", manifest.SourcePath);
		Assert.True(manifest.PckResources.SequenceEqual(["pcks/mod.pck"]));
	}

	[Fact]
	public void 软依赖与缺省version解析正确()
	{
		var result = Read(FullManifest);

		var soft = Assert.Single(result.Manifest!.Modules[1].Depends);
		Assert.True(soft.Soft);
		Assert.Null(soft.Version);
	}

	[Fact]
	public void 最小清单可解析_可选字段取默认()
	{
		var result = Read("""
			{
			  "schemaVersion": 1,
			  "uid": "com.a.m",
			  "name": "m",
			  "version": "0.1.0",
			  "gameId": "com.game.id",
			  "loaderVersion": "1.0.0",
			  "entry": { "assembly": "M.dll", "modules": [ { "uid": "com.a.m.main", "type": "A.M.Main" } ] }
			}
			""");

		Assert.True(result.Success);
		var manifest = result.Manifest!;
		Assert.Empty(manifest.Authors);
		Assert.Empty(manifest.PckResources);
		Assert.Empty(manifest.Modules[0].Depends);
		Assert.Null(manifest.Description);
	}

	[Fact]
	public void 缺少必填字段_错误累积收集()
	{
		var result = Read("""
			{ "schemaVersion": 1, "name": "m", "version": "bad-version!!" }
			""");

		Assert.False(result.Success);
		Assert.Contains(result.Errors, e => e.StartsWith("uid:", StringComparison.Ordinal));
		Assert.Contains(result.Errors, e => e.StartsWith("version:", StringComparison.Ordinal));
		Assert.Contains(result.Errors, e => e.StartsWith("gameId:", StringComparison.Ordinal));
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
			  "uid": "com.a.m",
			  "name": "m",
			  "version": "0.1.0",
			  "gameId": "g",
			  "loaderVersion": "1.0.0",
			  "futureTopField": { "x": 1 },
			  "entry": { "assembly": "M.dll", "modules": [ { "uid": "com.a.m.main", "type": "A.M.Main", "futureModField": 7 } ] }
			}
			""");

		Assert.True(result.Success);
		Assert.Contains(result.Warnings, w => w == "futureTopField: 未知字段,已忽略(前向兼容)");
		Assert.Contains(result.Warnings, w => w.Contains("entry.modules[0].futureModField"));
	}

	[Fact]
	public void 宽容JSON_注释与尾逗号可用()
	{
		var result = Read("""
			{
			  // 手写清单常见注释
			  "schemaVersion": 1,
			  "uid": "com.a.m",
			  "name": "m",
			  "version": "0.1.0",
			  "gameId": "g",
			  "loaderVersion": "1.0.0",
			  "entry": { "assembly": "M.dll", "modules": [ { "uid": "com.a.m.main", "type": "A.M.Main", } ], }
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
			  "uid": "com.a.m",
			  "name": "first",
			  "name": "second",
			  "version": "0.1.0",
			  "gameId": "g",
			  "loaderVersion": "1.0.0",
			  "entry": { "assembly": "M.dll", "modules": [ { "uid": "com.a.m.main", "type": "A.M.Main" } ] }
			}
			""");

		Assert.True(result.Success);
		Assert.Equal("second", result.Manifest!.Name);
		Assert.Contains(result.Warnings, w => w.Contains("name: 字段重复定义"));
	}

	[Theory]
	[InlineData("modname")]
	[InlineData("Com Author.mod")]
	[InlineData(".author.mod")]
	[InlineData("com..author.mod")]
	public void uid或模块uid非法_报错(string badUid)
	{
		var result = Read($$"""
			{
			  "schemaVersion": 1,
			  "uid": "{{badUid}}",
			  "name": "m",
			  "version": "0.1.0",
			  "gameId": "g",
			  "loaderVersion": "1.0.0",
			  "entry": { "assembly": "M.dll", "modules": [ { "uid": "com.a.m.main", "type": "A.M.Main" } ] }
			}
			""");

		Assert.False(result.Success);
		Assert.Contains(result.Errors, e => e.StartsWith("uid:", StringComparison.Ordinal));
	}

	[Fact]
	public void loaderVersion区间语法_报错并提示阶段5()
	{
		var result = Read(FullManifest.Replace("\"loaderVersion\": \"1.0.0\"", "\"loaderVersion\": \">=1.0.0 <2.0.0\""));

		Assert.False(result.Success);
		Assert.Contains(result.Errors, e => e.Contains("loaderVersion") && e.Contains("阶段 5"));
	}

	[Fact]
	public void 模块type无命名空间_报错()
	{
		var result = Read("""
			{
			  "schemaVersion": 1,
			  "uid": "com.a.m",
			  "name": "m",
			  "version": "0.1.0",
			  "gameId": "g",
			  "loaderVersion": "1.0.0",
			  "entry": { "assembly": "M.dll", "modules": [ { "uid": "com.a.m.main", "type": "Main" } ] }
			}
			""");

		Assert.False(result.Success);
		Assert.Contains(result.Errors, e => e.Contains("完整命名空间全名"));
	}

	[Fact]
	public void 模块uid清单内重复_报错()
	{
		var result = Read("""
			{
			  "schemaVersion": 1,
			  "uid": "com.a.m",
			  "name": "m",
			  "version": "0.1.0",
			  "gameId": "g",
			  "loaderVersion": "1.0.0",
			  "entry": {
			    "assembly": "M.dll",
			    "modules": [
			      { "uid": "com.a.m.main", "type": "A.M.Main" },
			      { "uid": "com.a.m.main", "type": "A.M.Other" }
			    ]
			  }
			}
			""");

		Assert.False(result.Success);
		Assert.Contains(result.Errors, e => e.Contains("重复定义"));
	}

	[Fact]
	public void entryAssembly含路径分隔符_报错()
	{
		var result = Read("""
			{
			  "schemaVersion": 1,
			  "uid": "com.a.m",
			  "name": "m",
			  "version": "0.1.0",
			  "gameId": "g",
			  "loaderVersion": "1.0.0",
			  "entry": { "assembly": "libs/M.dll", "modules": [ { "uid": "com.a.m.main", "type": "A.M.Main" } ] }
			}
			""");

		Assert.False(result.Success);
		Assert.Contains(result.Errors, e => e.Contains("entry.assembly"));
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
		Assert.Empty(result.Manifest!.PckResources);
		Assert.Contains(result.Warnings, w => w.Contains("resources.pck"));
	}

	[Fact]
	public void 依赖soft非布尔_报错()
	{
		var result = Read("""
			{
			  "schemaVersion": 1,
			  "uid": "com.a.m",
			  "name": "m",
			  "version": "0.1.0",
			  "gameId": "g",
			  "loaderVersion": "1.0.0",
			  "entry": {
			    "assembly": "M.dll",
			    "modules": [ { "uid": "com.a.m.main", "type": "A.M.Main", "depends": [ { "uid": "com.b.m", "soft": "yes" } ] } ]
			  }
			}
			""");

		Assert.False(result.Success);
		Assert.Contains(result.Errors, e => e.Contains(".soft"));
	}

	[Fact]
	public void modules为空数组_报错()
	{
		var result = Read("""
			{
			  "schemaVersion": 1,
			  "uid": "com.a.m",
			  "name": "m",
			  "version": "0.1.0",
			  "gameId": "g",
			  "loaderVersion": "1.0.0",
			  "entry": { "assembly": "M.dll", "modules": [] }
			}
			""");

		Assert.False(result.Success);
		Assert.Contains(result.Errors, e => e.Contains("至少需要一个模块"));
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
