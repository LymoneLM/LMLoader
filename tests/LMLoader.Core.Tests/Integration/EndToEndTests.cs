using LMLoader.Api;
using LMLoader.Core.Tests.TestInfrastructure;

namespace LMLoader.Core.Tests.Integration;

/// <summary>
/// 端到端验证:真实 mod.json 文件 → ModManager 全流程
/// (扫描 → 排序 → 加载 → 钩子 → 汇总),覆盖双通道服务与确定性排序。
/// </summary>
public class EndToEndTests : IDisposable
{
	private const string RecorderSource = """
		using System.Collections.Generic;

		public static class CallRecorder
		{
		    public static List<string> Items = new();
		}
		""";

	private const string ApiSource = """
		namespace E2E;

		public interface IGreeter
		{
		    string Greet();
		}
		""";

	private const string ManifestTemplate = """
		{
		  "schemaVersion": 1,
		  "uid": "#UID#",
		  "name": "#UID#",
		  "version": "1.0.0",
		  "gameId": "com.game.test",
		  "loaderVersion": "1.0.0",
		  "entry": { "assembly": "#ASM#.dll", "modules": [ { "uid": "#UID#.main", "type": "Fixture.#CLASS#", "depends": #DEPS# } ] }
		}
		""";

	private readonly string _root = Path.Combine(Path.GetTempPath(), $"lmloader-e2e-{Guid.NewGuid():N}");

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

	private string ModsRoot => Path.Combine(_root, "mods");

	private string CreateE2EApiDll()
	{
		var apiDir = Path.Combine(_root, "api");
		Directory.CreateDirectory(apiDir);
		return TestCompiler.CompileToDirectory(apiDir, "E2EApi", ApiSource);
	}

	/// <summary>落一个模组目录:dll(带共享记录器)+ mod.json。</summary>
	private void CreateMod(
		string uid,
		string extraSource,
		string loadBody,
		string postLoadBody = "",
		bool useE2EApi = false,
		(string Uid, bool Soft)[]? depends = null,
		params string[] referencedDlls)
	{
		var dir = Path.Combine(ModsRoot, uid);
		Directory.CreateDirectory(dir);

		var className = "M_" + uid.Replace('.', '_');
		var recorderDll = TestCompiler.CompileToDirectory(dir, "CallRecorderLib", RecorderSource);
		var e2eUsing = useE2EApi ? "using E2E;" : "";
		var source = $$"""
			using System;
			using LMLoader.Api;
			{{e2eUsing}}

			namespace Fixture;

			public class {{className}} : LmModule
			{
			    public static System.Collections.Generic.List<string> Calls => CallRecorder.Items;

			    private static void Record(string entry) => CallRecorder.Items.Add(entry);

			    public override void OnLoad()
			    {
			        {{loadBody}}
			    }

			    public override void OnPostLoad()
			    {
			        {{postLoadBody}}
			    }
			}

			{{extraSource}}
			""";
		var modDll = TestCompiler.CompileToDirectory(
			dir, className + "Asm", source, new[] { recorderDll, typeof(LmModule).Assembly.Location }.Concat(referencedDlls).ToArray());

		// 模组捆绑自己的依赖(真实打包语义);同名库在共享 ALC 下先载入者胜
		foreach (var referenced in referencedDlls)
		{
			File.Copy(referenced, Path.Combine(dir, Path.GetFileName(referenced)), overwrite: true);
		}

		var deps = depends is null || depends.Length == 0
			? "[]"
			: "[" + string.Join(", ", depends.Select(d => $$"""{ "uid": "{{d.Uid}}.main", "soft": {{(d.Soft ? "true" : "false").ToString().ToLowerInvariant()}}}""")) + "]";
		var json = ManifestTemplate
			.Replace("#UID#", uid)
			.Replace("#ASM#", className + "Asm")
			.Replace("#CLASS#", className)
			.Replace("#DEPS#", deps);
		File.WriteAllText(Path.Combine(dir, uid + ".mod.json"), json);
	}

	[Trait("Category", "UsesFileSystem")]
	[Fact]
	public void 端到端_依赖链排序与跨模组服务消费()
	{
		var apiDll = CreateE2EApiDll();

		// base:发布 IGreeter 服务(弱类型通道)
		CreateMod(
			"com.e.base",
			"""
			public class Greeter : IGreeter
			{
			    public string Greet() => "hello";
			}
			""",
			loadBody: @"PublishService<IGreeter>(new Greeter()); Record(""base:load"");",
			useE2EApi: true,
			referencedDlls: new[] { apiDll });

		// mid:仅硬依赖 base
		CreateMod(
			"com.e.mid",
			"",
			loadBody: @"Record(""mid:load"");",
			depends: [("com.e.base", false)]);

		// consumer:硬依赖 mid,PostLoad 消费 base 发布的服务
		CreateMod(
			"com.e.consumer",
			"",
			loadBody: @"Record(""consumer:load"");",
			postLoadBody: @"if (TryGetService<IGreeter>(out var g)) Record(""greet:"" + g.Greet());",
			useE2EApi: true,
			depends: [("com.e.mid", false)],
			referencedDlls: new[] { apiDll });

		using var manager = new ModManager(new LoaderOptions
		{
			ModsRootPath = ModsRoot,
			GameId = "com.game.test",
			ApiVersion = new Version(1, 0, 0, 0),
		});

		var result = manager.LoadAll();

		// ---- 汇总 ----
		Assert.Equal(3, result.LoadedManifests.Count);
		Assert.NotNull(result.Lifecycle);
		Assert.Equal(3, result.Lifecycle!.SucceededCount);
		Assert.Contains("成功 3", result.SummaryText);

		// ---- 执行顺序:base → mid → consumer,随后 PostLoad 消费服务 ----
		var calls = ReadCalls(result);
		Assert.Equal(
			["base:load", "mid:load", "consumer:load", "greet:hello"],
			calls);
	}

	[Trait("Category", "UsesFileSystem")]
	[Fact]
	public void 端到端_强类型直引通道跨模组静态调用()
	{
		var apiDll = CreateE2EApiDll();

		var baseDir = Path.Combine(ModsRoot, "com.e.direct");
		Directory.CreateDirectory(baseDir);
		var recorderDll = TestCompiler.CompileToDirectory(baseDir, "CallRecorderLib", RecorderSource);
		var baseSource = """
			using LMLoader.Api;

			namespace Fixture;

			public class M_com_e_direct : LmModule
			{
			    public static System.Collections.Generic.List<string> Calls => CallRecorder.Items;

			    public override void OnLoad() { }
			}

			public static class BaseUtil
			{
			    public static string Info() => "info-from-base";
			}
			""";
		var baseModDll = TestCompiler.CompileToDirectory(
			baseDir, "BaseDirectAsm", baseSource, recorderDll, typeof(LmModule).Assembly.Location);
		File.WriteAllText(Path.Combine(baseDir, "com.e.direct.mod.json"), ManifestTemplate
			.Replace("#UID#", "com.e.direct")
			.Replace("#ASM#", "BaseDirectAsm")
			.Replace("#CLASS#", "M_com_e_direct")
			.Replace("#DEPS#", "[]"));

		// consumer:编译期直引 base 模组 dll(强类型通道;运行期经共享 ALC 解析)
		var consumerDir = Path.Combine(ModsRoot, "com.e.reader");
		Directory.CreateDirectory(consumerDir);
		var consumerRecorderDll = TestCompiler.CompileToDirectory(consumerDir, "CallRecorderLib", RecorderSource);
		var consumerSource = """
			using LMLoader.Api;

			namespace Fixture;

			public class M_com_e_reader : LmModule
			{
			    public static System.Collections.Generic.List<string> Calls => CallRecorder.Items;

			    private static void Record(string entry) => CallRecorder.Items.Add(entry);

			    public override void OnLoad() => Record("direct:" + BaseUtil.Info());
			}
			""";
		TestCompiler.CompileToDirectory(
			consumerDir, "ReaderAsm", consumerSource,
			consumerRecorderDll, typeof(LmModule).Assembly.Location, baseModDll);
		File.WriteAllText(Path.Combine(consumerDir, "com.e.reader.mod.json"), ManifestTemplate
			.Replace("#UID#", "com.e.reader")
			.Replace("#ASM#", "ReaderAsm")
			.Replace("#CLASS#", "M_com_e_reader")
			.Replace("#DEPS#", """[{ "uid": "com.e.direct.main" }]"""));

		using var manager = new ModManager(new LoaderOptions
		{
			ModsRootPath = ModsRoot,
			GameId = "com.game.test",
			ApiVersion = new Version(1, 0, 0, 0),
		});

		var result = manager.LoadAll();

		Assert.Equal(2, result.Lifecycle!.SucceededCount);
		Assert.Contains("direct:info-from-base", ReadCalls(result));
	}

	[Trait("Category", "UsesFileSystem")]
	[Fact]
	public void 端到端_两次独立运行加载顺序一致_确定性排序()
	{
		CreateMod("com.e.alpha", "", loadBody: @"Record(""alpha:load"");");
		CreateMod("com.e.beta", "", loadBody: @"Record(""beta:load"");", depends: [("com.e.alpha", false)]);

		List<string> RunOnce()
		{
			using var manager = new ModManager(new LoaderOptions
			{
				ModsRootPath = ModsRoot,
				GameId = "com.game.test",
				ApiVersion = new Version(1, 0, 0, 0),
			});
			var result = manager.LoadAll();
			Assert.Equal(2, result.Lifecycle!.SucceededCount);
			return ReadCalls(result);
		}

		var first = RunOnce();
		var second = RunOnce();

		Assert.Equal(["alpha:load", "beta:load"], first);
		Assert.Equal(first, second);
	}

	private static List<string> ReadCalls(LMLoader.Core.LoadResult result)
	{
		var anyInstance = result.Lifecycle!.Results.Select(r => r.Instance).FirstOrDefault(i => i is not null);
		Assert.NotNull(anyInstance);
		return (List<string>)anyInstance.GetType().GetProperty("Calls")!.GetValue(null)!;
	}
}
