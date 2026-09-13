using System.Reflection;
using LMLoader.Api;
using LMLoader.Core.Tests.TestInfrastructure;

namespace LMLoader.Core.Tests;

public class ModManagerTests : IDisposable
{
	private const string RecorderSource = """
		using System.Collections.Generic;

		public static class CallRecorder
		{
		    public static List<string> Items = new();
		}
		""";

	private const string ModuleSourceTemplate = """
		using System;
		using LMLoader.Api;

		namespace Fixture;

		public class #CLASS# : LmModule
		{
		    public static System.Collections.Generic.List<string> Calls => CallRecorder.Items;

		    public override void OnLoad() { #LOAD# }
		}
		""";

	private const string ManifestTemplate = """
		{
		  "schemaVersion": 1,
		  "uid": "#UID#",
		  "name": "#UID#",
		  "version": "1.0.0",
		  "gameId": "#GAMEID#",
		  "loaderVersion": "#LOADER#",
		  "entry": { "assembly": "#ASM#.dll", "modules": [ { "uid": "#UID#.main", "type": "Fixture.#CLASS#", "depends": #DEPS# } ]#EXTRA# }
		}
		""";

	private readonly string _root = Path.Combine(Path.GetTempPath(), $"lmloader-mm-{Guid.NewGuid():N}");

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

	/// <summary>在 mods 根下落一个完整模组目录(dll + mod.json),返回目录路径。</summary>
	private string CreateMod(
		string uid,
		string loadStatement,
		string gameId = "com.game.test",
		string loaderVersion = "1.0.0",
		(string Uid, bool Soft)[]? depends = null,
		string extraJson = "")
	{
		var dir = Path.Combine(_root, "mods", uid);
		Directory.CreateDirectory(dir);

		var recorderDll = TestCompiler.CompileToDirectory(dir, "CallRecorderLib", RecorderSource);
		var className = "M_" + uid.Replace('.', '_').Replace('-', '_');
		var source = ModuleSourceTemplate
			.Replace("#CLASS#", className)
			.Replace("#LOAD#", loadStatement);
		TestCompiler.CompileToDirectory(dir, className + "Asm", source, recorderDll, typeof(LmModule).Assembly.Location);

		var deps = depends is null || depends.Length == 0
			? "[]"
			: "[" + string.Join(", ", depends.Select(d => $$"""{ "uid": "{{d.Uid}}.main", "soft": {{(d.Soft ? "true" : "false").ToString().ToLowerInvariant()}}}""")) + "]";
		var json = ManifestTemplate
			.Replace("#UID#", uid)
			.Replace("#GAMEID#", gameId)
			.Replace("#LOADER#", loaderVersion)
			.Replace("#ASM#", className + "Asm")
			.Replace("#CLASS#", className)
			.Replace("#DEPS#", deps)
			.Replace("#EXTRA#", extraJson);
		File.WriteAllText(Path.Combine(dir, uid + ".mod.json"), json);

		return dir;
	}

	private ModManager CreateManager(Action<LoaderOptions>? configure = null)
	{
		var options = new LoaderOptions
		{
			ModsRootPath = Path.Combine(_root, "mods"),
			GameId = "com.game.test",
			ApiVersion = new Version(1, 0, 0, 0),
		};
		configure?.Invoke(options);
		return new ModManager(options);
	}

	[Trait("Category", "UsesFileSystem")]
	[Fact]
	public void 完整流程_单模组加载成功()
	{
		CreateMod("com.t.hello", @"CallRecorder.Items.Add(""hello:load"");");
		using var manager = CreateManager();

		var result = manager.LoadAll();

		Assert.Single(result.LoadedManifests);
		Assert.NotNull(result.Lifecycle);
		Assert.Equal(1, result.Lifecycle!.SucceededCount);
		Assert.Contains("成功 1", result.SummaryText);

		var instance = result.Lifecycle.Results[0].Instance!;
		var calls = (List<string>)instance.GetType().GetProperty("Calls")!.GetValue(null)!;
		Assert.Equal(["hello:load"], calls);
	}

	[Trait("Category", "UsesFileSystem")]
	[Fact]
	public void gameId不匹配_拒载并出现在汇总()
	{
		CreateMod("com.t.other", "", gameId: "com.other.game");
		using var manager = CreateManager();

		var result = manager.LoadAll();

		Assert.Empty(result.LoadedManifests);
		Assert.Empty(result.Lifecycle!.Results);
		Assert.Contains("gameId 不匹配", result.SummaryText);
	}

	[Trait("Category", "UsesFileSystem")]
	[Fact]
	public void 清单损坏_失败行_其余模组正常加载()
	{
		var goodDir = CreateMod("com.t.good", @"CallRecorder.Items.Add(""good:load"");");
		var badDir = Path.Combine(_root, "mods", "com.t.corrupt");
		Directory.CreateDirectory(badDir);
		File.WriteAllText(Path.Combine(badDir, "corrupt.mod.json"), "{ schemaVersion: ");

		using var manager = CreateManager();

		var result = manager.LoadAll();

		Assert.Single(result.LoadedManifests);
		Assert.NotNull(result.Lifecycle);
		Assert.Equal(1, result.Lifecycle.SucceededCount);
		Assert.Contains("JSON 解析失败", result.SummaryText);
		Assert.Contains("com.t.good.main", result.SummaryText);
	}

	[Trait("Category", "UsesFileSystem")]
	[Fact]
	public void loaderVersion不满足_拒载()
	{
		CreateMod("com.t.picky", "", loaderVersion: "9.9.9");
		using var manager = CreateManager();

		var result = manager.LoadAll();

		Assert.Empty(result.LoadedManifests);
		Assert.Contains("loaderVersion 不满足", result.SummaryText);
	}

	[Trait("Category", "UsesFileSystem")]
	[Fact]
	public void 未知字段警告汇聚到读取警告()
	{
		CreateMod("com.t.future", "", extraJson: ", \"futureField\": 1");
		using var manager = CreateManager();

		var result = manager.LoadAll();

		Assert.Single(result.LoadedManifests);
		Assert.Contains(result.ReadWarnings, w => w.Contains("未知字段"));
	}

	[Trait("Category", "UsesFileSystem")]
	[Fact]
	public void 循环依赖_整批拒绝_Lifecycle为null()
	{
		CreateMod("com.t.a", "", depends: [("com.t.b", false)]);
		CreateMod("com.t.b", "", depends: [("com.t.a", false)]);
		using var manager = CreateManager();

		var result = manager.LoadAll();

		Assert.Null(result.Lifecycle);
		Assert.True(result.Plan.BatchRejected);
		Assert.Contains("整批拒绝", result.SummaryText);
		Assert.Contains("com.t.a.main", result.Plan.BatchRejectReason);
	}

	[Trait("Category", "UsesFileSystem")]
	[Fact]
	public void 模组目录不存在_抛配置异常()
	{
		var options = new LoaderOptions { ModsRootPath = Path.Combine(_root, "no-such-dir") };
		using var badManager = new ModManager(options);

		Assert.Throws<DirectoryNotFoundException>(() => badManager.LoadAll());
	}

	[Trait("Category", "UsesFileSystem")]
	[Fact]
	public void 深层订阅目录中的清单可被发现()
	{
		// 模拟 Steam Workshop 深层结构 workshop/<appid>/<itemid>/
		var deepDir = Path.Combine(_root, "mods", "workshop", "123456", "654321");
		Directory.CreateDirectory(deepDir);
		var recorderDll = TestCompiler.CompileToDirectory(deepDir, "CallRecorderLib", RecorderSource);
		var source = ModuleSourceTemplate.Replace("#CLASS#", "DeepMod").Replace("#LOAD#", "");
		TestCompiler.CompileToDirectory(deepDir, "DeepAsm", source, recorderDll, typeof(LmModule).Assembly.Location);
		var json = ManifestTemplate
			.Replace("#UID#", "com.t.deep")
			.Replace("#GAMEID#", "com.game.test")
			.Replace("#LOADER#", "1.0.0")
			.Replace("#ASM#", "DeepAsm")
			.Replace("#CLASS#", "DeepMod")
			.Replace("#DEPS#", "[]")
			.Replace("#EXTRA#", "");
		File.WriteAllText(Path.Combine(deepDir, "deep.mod.json"), json);

		using var manager = CreateManager();

		var result = manager.LoadAll();

		Assert.Single(result.LoadedManifests);
		Assert.Equal("com.t.deep", result.LoadedManifests[0].Uid);
	}
}
