using System.Reflection;
using LMLoader.Api;
using LMLoader.Core.Loading;
using LMLoader.Core.Tests.TestInfrastructure;

namespace LMLoader.Core.Tests.Loading;

public class ModAssemblyLoaderTests : IDisposable
{
	private sealed class RecordingLogger : ILmLogger
	{
		public List<string> Warnings { get; } = new();

		public void Log(LmLogLevel level, string message, Exception? exception = null)
		{
			if (level == LmLogLevel.Warning)
			{
				Warnings.Add(message);
			}
		}
	}

	private readonly string _root = Path.Combine(Path.GetTempPath(), $"lmloader-alc-{Guid.NewGuid():N}");

	private string Dir(string name)
	{
		var path = Path.Combine(_root, name);
		Directory.CreateDirectory(path);
		return path;
	}

	private string ApiDll => typeof(LmModule).Assembly.Location;

	[Trait("Category", "UsesFileSystem")]
	[Fact]
	public void 模组主程序集可加载并反射调用()
	{
		var dir = Dir("basic");
		var dll = TestCompiler.CompileToDirectory(dir, "BasicMod", """
			public class Hello
			{
			    public string Ping() => "pong";
			}
			""");

		using var loader = new ModAssemblyLoader(new RecordingLogger());
		var assembly = loader.LoadModAssembly("com.t.basic", dll);

		var hello = Activator.CreateInstance(assembly.GetType("Hello")!)!;
		Assert.Equal("pong", hello.GetType().GetMethod("Ping")!.Invoke(hello, null));
	}

	[Trait("Category", "UsesFileSystem")]
	[Fact]
	public void 模组目录自带依赖经目录直探解析()
	{
		var dir = Dir("bundled");
		var libDll = TestCompiler.CompileToDirectory(dir, "PrivateLib", """
			public class LibUtil
			{
			    public static int Value() => 7;
			}
			""");
		var modDll = TestCompiler.CompileToDirectory(dir, "ConsumerMod", """
			public class Consumer
			{
			    public int Get() => LibUtil.Value();
			}
			""", libDll);

		using var loader = new ModAssemblyLoader(new RecordingLogger());
		var assembly = loader.LoadModAssembly("com.t.consumer", modDll);

		var consumer = Activator.CreateInstance(assembly.GetType("Consumer")!)!;
		Assert.Equal(7, consumer.GetType().GetMethod("Get")!.Invoke(consumer, null));
	}

	[Trait("Category", "UsesFileSystem")]
	[Fact]
	public void loader公共库优先于模组捆绑副本并告警()
	{
		var libSource = """
			public class S
			{
			    public static string Where() => "{{ORIGIN}}";
			}
			""";
		var loaderCopyDir = Dir("shared-lib");
		var loaderCopyDll = TestCompiler.CompileToDirectory(
			loaderCopyDir, "SharedLibT3", libSource.Replace("{{ORIGIN}}", "loader"));
		var modDir = Dir("shared-mod");
		var bundledDll = TestCompiler.CompileToDirectory(
			modDir, "SharedLibT3", libSource.Replace("{{ORIGIN}}", "bundled"));
		var modDll = TestCompiler.CompileToDirectory(modDir, "SharedUserMod", """
			public class UsesS
			{
			    public string Call() => S.Where();
			}
			""", bundledDll);

		var logger = new RecordingLogger();
		using var loader = new ModAssemblyLoader(
			logger,
			sharedLibraries: [Assembly.LoadFrom(loaderCopyDll)]);
		var assembly = loader.LoadModAssembly("com.t.user", modDll);

		var user = Activator.CreateInstance(assembly.GetType("UsesS")!)!;
		Assert.Equal("loader", user.GetType().GetMethod("Call")!.Invoke(user, null));
		Assert.Contains(logger.Warnings, w => w.Contains("SharedLibT3") && w.Contains("com.t.user"));
	}

	[Trait("Category", "UsesFileSystem")]
	[Fact]
	public void 模组间同名库先载入者胜并告警涉事双方()
	{
		var dupSource = """
			public class Dup
			{
			    public static string Where() => "{{ORIGIN}}";
			}
			""";
		var dirA = Dir("mod-a");
		var dupADll = TestCompiler.CompileToDirectory(dirA, "DupLibT4", dupSource.Replace("{{ORIGIN}}", "fromA"));
		var modADll = TestCompiler.CompileToDirectory(dirA, "ModA", """
			public class ModA
			{
			    public string Run() => Dup.Where();
			}
			""", dupADll);

		var dirB = Dir("mod-b");
		var dupBDll = TestCompiler.CompileToDirectory(dirB, "DupLibT4", dupSource.Replace("{{ORIGIN}}", "fromB"));
		var modBDll = TestCompiler.CompileToDirectory(dirB, "ModB", """
			public class ModB
			{
			    public string Run() => Dup.Where();
			}
			""", dupBDll);

		var logger = new RecordingLogger();
		using var loader = new ModAssemblyLoader(logger);
		var assemblyA = loader.LoadModAssembly("com.t.a", modADll);
		var assemblyB = loader.LoadModAssembly("com.t.b", modBDll);

		var a = Activator.CreateInstance(assemblyA.GetType("ModA")!)!;
		Assert.Equal("fromA", a.GetType().GetMethod("Run")!.Invoke(a, null));

		var b = Activator.CreateInstance(assemblyB.GetType("ModB")!)!;
		Assert.Equal("fromA", b.GetType().GetMethod("Run")!.Invoke(b, null)); // B 拿到 A 的副本

		Assert.Contains(logger.Warnings, w => w.Contains("先载入者胜") && w.Contains("com.t.a") && w.Contains("com.t.b"));
	}

	[Trait("Category", "UsesFileSystem")]
	[Fact]
	public void 游戏程序集经注入解析器回退()
	{
		var gameDir = Dir("game");
		var gameDll = TestCompiler.CompileToDirectory(gameDir, "GameLibT5", """
			public class GameHelper
			{
			    public static string Name() => "game";
			}
			""");
		var modDir = Dir("game-mod");
		var modDll = TestCompiler.CompileToDirectory(modDir, "GameUserMod", """
			public class GameUser
			{
			    public string Call() => GameHelper.Name();
			}
			""", gameDll);

		// 模拟 P0-1 实证环境:游戏程序集位于宿主自身 ALC,loader 须按名显式返回
		var gameAssembly = Assembly.LoadFrom(gameDll);
		using var loader = new ModAssemblyLoader(
			new RecordingLogger(),
			gameAssemblyResolver: name => name.Name == "GameLibT5" ? gameAssembly : null);
		var assembly = loader.LoadModAssembly("com.t.gameuser", modDll);

		var user = Activator.CreateInstance(assembly.GetType("GameUser")!)!;
		Assert.Equal("game", user.GetType().GetMethod("Call")!.Invoke(user, null));
	}

	[Trait("Category", "UsesFileSystem")]
	[Fact]
	public void 继承LmModule的模组类型可加载与实例化()
	{
		var modDir = Dir("lmmod");
		var modDll = TestCompiler.CompileToDirectory(modDir, "RealShapeMod", """
			using System.Collections.Generic;
			using LMLoader.Api;

			namespace Fixture;

			public sealed class ShapeModule : LmModule
			{
			    public List<string> Log { get; } = new();

			    public override void OnLoad() => Log.Add("load:" + Uid);
			}
			""", ApiDll);

		using var loader = new ModAssemblyLoader(new RecordingLogger(), sharedLibraries: [typeof(LmModule).Assembly]);
		var assembly = loader.LoadModAssembly("com.t.shape", modDll);

		var module = (LmModule)Activator.CreateInstance(assembly.GetType("Fixture.ShapeModule")!)!;
		module.Attach(new LmModuleContext("com.t.shape.main", "com.t.shape", modDir, new RecordingLogger()));
		module.OnLoad();

		var log = (List<string>)module.GetType().GetProperty("Log")!.GetValue(module)!;
		Assert.Equal("load:com.t.shape.main", log[0]);
	}

	public void Dispose() => TryDelete(_root);

	private static void TryDelete(string path)
	{
		// 非收集式 ALC 加载后 dll 被进程锁定,目录删除失败属预期,残留交由系统临时目录清理
		try
		{
			Directory.Delete(path, recursive: true);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
		}
	}
}
