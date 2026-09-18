using System.IO.Compression;
using LMLoader.Cli.Lint;
using LMLoader.Cli.Pack;
using LMLoader.Core.Manifest;
using LMLoader.Distribution.Thunderstore;

namespace LMLoader.Core.Tests.Cli;

/// <summary>ModLinter 严格校验:运行时"宽容忽略"的行为在这里必须是 error。</summary>
[Trait("Category", "UsesFileSystem")]
public class ModLinterTests : IDisposable
{
	private readonly string _dir = Path.Combine(Path.GetTempPath(), $"lmcli-lint-{Guid.NewGuid():N}");

	public void Dispose()
	{
		try
		{
			Directory.Delete(_dir, recursive: true);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
		}
	}

	private const string GoodManifest = """
		{
		  "schemaVersion": 1,
		  "uid": "com.author.modname",
		  "name": "示例",
		  "version": "1.0.0",
		  "description": "demo",
		  "tags": ["content"],
		  "gameId": "com.game.test",
		  "loaderVersion": "^1.0.0",
		  "entry": { "assembly": "Mod.dll", "modules": [ { "uid": "com.author.modname.main", "type": "A.B.M" } ] }
		}
		""";

	private void WriteMod(string manifest, string? extraFile = null, string? extraContent = null)
	{
		Directory.CreateDirectory(_dir);
		File.WriteAllText(Path.Combine(_dir, "com.author.modname.mod.json"), manifest);
		File.WriteAllText(Path.Combine(_dir, "Mod.dll"), "fake"); // 入口程序集占位
		if (extraFile is not null)
		{
			File.WriteAllText(Path.Combine(_dir, extraFile), extraContent ?? "");
		}
	}

	[Fact]
	public void 干净模组_通过且仅质量提示()
	{
		WriteMod(GoodManifest);
		var result = ModLinter.Lint(_dir);

		Assert.True(result.Success);
		Assert.DoesNotContain(result.Issues, i => i.IsError);
	}

	[Fact]
	public void 无清单_失败()
	{
		Directory.CreateDirectory(_dir);

		Assert.False(ModLinter.Lint(_dir).Success);
	}

	[Fact]
	public void 多份清单_失败()
	{
		WriteMod(GoodManifest);
		File.WriteAllText(Path.Combine(_dir, "another.mod.json"), GoodManifest);

		var result = ModLinter.Lint(_dir);

		Assert.False(result.Success);
		Assert.Contains(result.Issues, i => i.IsError && i.Message.Contains("2 份清单"));
	}

	[Fact]
	public void 未知字段_严格报错()
	{
		WriteMod(GoodManifest.Replace("\"tags\": [\"content\"],", "\"tags\": [\"content\"], \"future\": 1,"));

		var result = ModLinter.Lint(_dir);

		Assert.False(result.Success);
		Assert.Contains(result.Issues, i => i.IsError && i.Message.Contains("未知字段: future"));
	}

	[Fact]
	public void 模块依赖未知字段_严格报错()
	{
		WriteMod(GoodManifest.Replace(
			"\"type\": \"A.B.M\" } ]",
			"\"type\": \"A.B.M\", \"priority\": 5 } ]"));

		var result = ModLinter.Lint(_dir);

		Assert.False(result.Success);
		Assert.Contains(result.Issues, i => i.IsError && i.Message.Contains("未知字段: entry.modules[].priority"));
	}

	[Fact]
	public void 嵌套依赖的合法字段不误报()
	{
		// 回归:depends[] 嵌在 modules[] 内,字段表曾被错用模块集(uid/type/depends)
		WriteMod(GoodManifest.Replace(
			"\"type\": \"A.B.M\" } ]",
			"\"type\": \"A.B.M\", \"depends\": [ { \"uid\": \"com.other.core\", \"version\": \"^1.0.0\", \"soft\": false } ] } ]"));

		var result = ModLinter.Lint(_dir);

		Assert.DoesNotContain(result.Issues, i => i.Message.Contains("depends"));
	}

	[Fact]
	public void 入口程序集缺失_报错()
	{
		Directory.CreateDirectory(_dir);
		File.WriteAllText(Path.Combine(_dir, "com.author.modname.mod.json"), GoodManifest);

		var result = ModLinter.Lint(_dir);

		Assert.False(result.Success);
		Assert.Contains(result.Issues, i => i.IsError && i.Message.Contains("entry.assembly 文件不存在"));
	}

	[Fact]
	public void 声明的pck缺失_报错_存在则过()
	{
		WriteMod(GoodManifest.Replace(
			"\"entry\":",
			"\"resources\": { \"pck\": [\"pack.pck\"] }, \"entry\":"));

		Assert.False(ModLinter.Lint(_dir).Success);

		File.WriteAllText(Path.Combine(_dir, "pack.pck"), "fake-pck");
		var result = ModLinter.Lint(_dir);
		Assert.True(result.Success);
	}

	[Fact]
	public void 运行时宽容警告升级为error_如非法pck路径被忽略()
	{
		// resources.pck 含绝对路径:运行时忽略该项继续,lint 必须挡下
		WriteMod(GoodManifest.Replace(
			"\"entry\":",
			"\"resources\": { \"pck\": [\"C:/abs.pck\"] }, \"entry\":"));

		var result = ModLinter.Lint(_dir);

		Assert.False(result.Success);
		Assert.Contains(result.Issues, i => i.IsError && i.Message.Contains("忽略"));
	}

	[Fact]
	public void 图标非png_警告不失败_缺失则报错()
	{
		WriteMod(GoodManifest.Replace("\"description\": \"demo\",", "\"description\": \"demo\", \"icon\": \"icon.svg\","));
		File.WriteAllText(Path.Combine(_dir, "icon.svg"), "fake");

		var result = ModLinter.Lint(_dir);
		Assert.True(result.Success);
		Assert.Contains(result.Issues, i => !i.IsError && i.Message.Contains("非 png"));

		File.WriteAllText(Path.Combine(_dir, "com.author.modname.mod.json"),
			GoodManifest.Replace("\"description\": \"demo\",", "\"description\": \"demo\", \"icon\": \"missing.png\","));
		var result2 = ModLinter.Lint(_dir);
		Assert.False(result2.Success);
	}

	[Fact]
	public void thunderstore别名_非法包名报错_合法通过()
	{
		WriteMod(GoodManifest.Replace(
			"\"entry\":",
			"\"distribution\": { \"thunderstore\": { \"team\": \"Author\", \"name\": \"bad-name\" } }, \"entry\":"));
		Assert.False(ModLinter.Lint(_dir).Success);

		WriteMod(GoodManifest.Replace(
			"\"entry\":",
			"\"distribution\": { \"thunderstore\": { \"team\": \"Author\" } }, \"entry\":"));
		Assert.True(ModLinter.Lint(_dir).Success);
	}
}

/// <summary>PckExporter:命令构造与包装逻辑(fake runner,不真跑 Godot)。</summary>
[Trait("Category", "UsesFileSystem")]
public class PckExporterTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), $"lmcli-pck-{Guid.NewGuid():N}");

	public PckExporterTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		try
		{
			Directory.Delete(_root, recursive: true);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
		}
	}

	private sealed class RecordingRunner : IProcessRunner
	{
		public string? FileName { get; private set; }
		public string? Arguments { get; private set; }
		public int NextExitCode { get; set; }

		public ProcessResult Run(string fileName, string arguments)
		{
			FileName = fileName;
			Arguments = arguments;
			return new ProcessResult(NextExitCode, "output");
		}
	}

	[Fact]
	public void 参数构造_与AGENTS沉淀命令一致()
	{
		var arguments = PckExporter.BuildArguments("proj", "PCK", "out/pack.pck");

		Assert.Equal("--headless --path \"proj\" --export-pack \"PCK\" \"out/pack.pck\"", arguments);
	}

	[Fact]
	public void 导出_走注入runner_输出目录自动创建()
	{
		var runner = new RecordingRunner();
		var exporter = new PckExporter(runner);
		var projectDir = Path.Combine(_root, "project");
		Directory.CreateDirectory(projectDir);
		File.WriteAllText(Path.Combine(projectDir, "project.godot"), "");
		var output = Path.Combine(_root, "deep", "dir", "pack.pck");

		var result = exporter.Export("godot.exe", projectDir, "PCK", output);

		Assert.True(result.Success);
		Assert.Equal("godot.exe", runner.FileName);
		Assert.Contains($"--path \"{projectDir}\"", runner.Arguments);
		Assert.Contains($"--export-pack \"PCK\" \"{output}\"", runner.Arguments);
		Assert.True(Directory.Exists(Path.Combine(_root, "deep", "dir")));
	}

	[Fact]
	public void 工程目录缺失或无project文件_报错()
	{
		var exporter = new PckExporter(new RecordingRunner());

		// 目录不存在
		Assert.Throws<DirectoryNotFoundException>(
			() => exporter.Export("godot.exe", Path.Combine(_root, "missing"), "PCK", "out.pck"));

		// 目录存在但无 project.godot(如误传了工程文件路径或空目录)
		var empty = Path.Combine(_root, "empty");
		Directory.CreateDirectory(empty);
		Assert.Throws<DirectoryNotFoundException>(
			() => exporter.Export("godot.exe", empty, "PCK", "out.pck"));
	}

	[Fact]
	public void 非零退出码_透传失败()
	{
		var runner = new RecordingRunner { NextExitCode = 1 };
		var projectDir = Path.Combine(_root, "project");
		Directory.CreateDirectory(projectDir);
		File.WriteAllText(Path.Combine(projectDir, "project.godot"), "");

		Assert.False(new PckExporter(runner).Export("godot.exe", projectDir, "PCK", "out.pck").Success);
	}
}

/// <summary>ThunderstorePacker:zip 布局(manifest.json 根置 + mod.json 一并打入 + icon.png 必须)。</summary>
[Trait("Category", "UsesFileSystem")]
public class ThunderstorePackerTests : IDisposable
{
	private readonly string _dir = Path.Combine(Path.GetTempPath(), $"lmcli-ts-{Guid.NewGuid():N}");

	public ThunderstorePackerTests() => Directory.CreateDirectory(_dir);

	public void Dispose()
	{
		try
		{
			Directory.Delete(_dir, recursive: true);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
		}
	}

	private const string Manifest = """
		{
		  "schemaVersion": 1,
		  "uid": "com.author.modname",
		  "name": "示例",
		  "version": "1.2.3",
		  "description": "demo 模组",
		  "gameId": "com.game.test",
		  "loaderVersion": "^1.0.0",
		  "entry": { "assembly": "Mod.dll", "modules": [ { "uid": "com.author.modname.main", "type": "A.B.M" } ] }
		}
		""";

	private void WriteMod() 
	{
		File.WriteAllText(Path.Combine(_dir, "com.author.modname.mod.json"), Manifest);
		File.WriteAllText(Path.Combine(_dir, "Mod.dll"), "fake");
		File.WriteAllText(Path.Combine(_dir, "icon.png"), "fake-png");
	}

	[Fact]
	public void 打包_zip根含manifest_modjson与全部文件()
	{
		WriteMod();
		var zip = Path.Combine(_dir, "out", "package.zip");

		var result = ThunderstorePacker.Pack(_dir, "Author", zip);

		Assert.Equal("modname", result.ManifestName);
		Assert.Equal("1.2.3", result.VersionNumber);
		using var archive = ZipFile.OpenRead(zip);
		var names = archive.Entries.Select(e => e.FullName).ToHashSet();
		Assert.Contains("manifest.json", names);
		Assert.Contains("com.author.modname.mod.json", names);
		Assert.Contains("Mod.dll", names);
		Assert.Contains("icon.png", names);

		var manifestEntry = archive.GetEntry("manifest.json")!;
		using var reader = new StreamReader(manifestEntry.Open());
		var json = reader.ReadToEnd();
		Assert.Contains("\"name\": \"modname\"", json);
		Assert.Contains("\"version_number\": \"1.2.3\"", json);
	}

	[Fact]
	public void 缺icon_打包失败()
	{
		File.WriteAllText(Path.Combine(_dir, "com.author.modname.mod.json"), Manifest);
		File.WriteAllText(Path.Combine(_dir, "Mod.dll"), "fake");

		Assert.Throws<FileNotFoundException>(
			() => ThunderstorePacker.Pack(_dir, "Author", Path.Combine(_dir, "out.zip")));
	}

	[Fact]
	public void icon改名进zip_不修改源目录()
	{
		// 无 icon.png,但 icon 字段指向 logo.png:zip 内改名,源目录保持原样
		File.WriteAllText(Path.Combine(_dir, "com.author.modname.mod.json"), Manifest.Replace(
			"\"description\": \"demo 模组\",", "\"description\": \"demo 模组\", \"icon\": \"logo.png\","));
		File.WriteAllText(Path.Combine(_dir, "Mod.dll"), "fake");
		File.WriteAllText(Path.Combine(_dir, "logo.png"), "fake-png");
		var zip = Path.Combine(_dir, "out.zip");

		ThunderstorePacker.Pack(_dir, "Author", zip);

		Assert.False(File.Exists(Path.Combine(_dir, "icon.png"))); // 源目录未被污染
		using var archive = ZipFile.OpenRead(zip);
		Assert.NotNull(archive.GetEntry("icon.png"));
	}

	[Fact]
	public void 无readme且含描述_生成readme进zip()
	{
		WriteMod();
		var zip = Path.Combine(_dir, "out.zip");

		ThunderstorePacker.Pack(_dir, "Author", zip);

		using var archive = ZipFile.OpenRead(zip);
		var readme = archive.GetEntry("README.md")!;
		using var reader = new StreamReader(readme.Open());
		Assert.Contains("demo 模组", reader.ReadToEnd());
	}

	[Fact]
	public void 目录内已有manifest_json不与生成的冲突()
	{
		WriteMod();
		File.WriteAllText(Path.Combine(_dir, "manifest.json"), "stale");
		var zip = Path.Combine(_dir, "out.zip");

		var result = ThunderstorePacker.Pack(_dir, "Author", zip);

		using var archive = ZipFile.OpenRead(zip);
		var manifestEntry = archive.GetEntry("manifest.json")!;
		using var reader = new StreamReader(manifestEntry.Open());
		Assert.Contains("version_number", reader.ReadToEnd()); // 生成版覆盖 stale
	}

	[Fact]
	public void 依赖别名经deps清单映射()
	{
		WriteMod();
		File.WriteAllText(Path.Combine(_dir, "com.author.modname.mod.json"), Manifest.Replace(
			"\"type\": \"A.B.M\" }",
			"\"type\": \"A.B.M\", \"depends\": [ { \"uid\": \"com.other.lib.main\" } ] }"));
		var dependencyDir = Path.Combine(_dir, "deps", "com.other.lib");
		Directory.CreateDirectory(dependencyDir);
		File.WriteAllText(Path.Combine(dependencyDir, "com.other.lib.mod.json"), """
			{ "schemaVersion": 1, "uid": "com.other.lib", "name": "lib", "version": "2.1.0",
			  "gameId": "com.game.test", "loaderVersion": "*",
			  "distribution": { "thunderstore": { "team": "TeamB" } },
			  "entry": { "assembly": "Lib.dll", "modules": [ { "uid": "com.other.lib.main", "type": "L.M" } ] } }
			""");
		var zip = Path.Combine(_dir, "out.zip");

		var warnings = new List<string>();
		ThunderstorePacker.Pack(_dir, "Author", zip, warn: warnings.Add);
		Assert.Single(warnings); // 未提供 deps 清单 → 依赖条目省略

		var dependencyManifest = Directory.EnumerateFiles(dependencyDir, "*mod.json")
			.Select(ModManifestReader.ReadFile).First(r => r.Success).Manifest!;
		ThunderstorePacker.Pack(_dir, "Author", zip,
			new Dictionary<string, ModManifest> { ["com.other.lib"] = dependencyManifest });
		using var archive = ZipFile.OpenRead(zip);
		var manifestEntry = archive.GetEntry("manifest.json")!;
		using var reader = new StreamReader(manifestEntry.Open());
		Assert.Contains("TeamB-lib-2.1.0", reader.ReadToEnd());
	}
}
