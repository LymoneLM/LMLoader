using LMLoader.Core;
using LMLoader.Core.Tests.TestInfrastructure;

namespace LMLoader.Core.Tests.Integration;

/// <summary>
/// 端到端:真实模组 OnPreLoad 绑定配置 → ConfigManager 合并磁盘 toml →
/// OnLoad 读到生效值;缺失键写回默认;配置按模组 uid 一模组一份(D18)。
/// </summary>
public class ConfigEndToEndTests : IDisposable
{
	private const string RecorderSource = """
		using System.Collections.Generic;

		public static class CallRecorder
		{
		    public static List<string> Items = new();
		}
		""";

	private const string ManifestTemplate = """
		{
		  "schemaVersion": 1,
		  "gameId": "com.game.test",
		  "version": "1.0.0",
		  "mods": #MODS#
		}
		""";

	/// <summary>按模组 uid 生成带特性绑定的配置声明类(每个模组一份独立 dll)。</summary>
	private static string MainModSource(string uid) => $$"""
		using LMLoader.Api;
		using LMLoader.Api.Config;

		namespace Fixture;

		[LmMod("{{uid}}")]
		public class CfgMain : LmMod
		{
		    public static System.Collections.Generic.List<string> Calls => CallRecorder.Items;

		    private ConfigEntry<int> _multiplier = null!;
		    private ConfigEntry<string> _greeting = null!;

		    public override void OnPreLoad()
		    {
		        _multiplier = Config.Bind("patch", "multiplier", 3, "乘数",
		            acceptableValues: new AcceptableValueRange<int>(0, 100));
		        _greeting = Config.Bind("voice", "greeting", "hi");
		    }

		    public override void OnLoad()
		    {
		        Calls.Add("multiplier=" + _multiplier.Value);
		        Calls.Add("greeting=" + _greeting.Value);
		    }
		}
		""";

	private static string SecondModSource(string uid) => $$"""
		using LMLoader.Api;
		using LMLoader.Api.Config;

		namespace Fixture;

		[LmMod("{{uid}}")]
		public class CfgSecond : LmMod
		{
		    public static System.Collections.Generic.List<string> Calls => CallRecorder.Items;

		    private ConfigEntry<bool> _enabled = null!;

		    public override void OnPreLoad()
		    {
		        _enabled = Config.Bind("voice", "enabled", true);
		    }

		    public override void OnLoad()
		    {
		        Calls.Add("enabled=" + _enabled.Value);
		    }
		}
		""";

	private readonly string _root = Path.Combine(Path.GetTempPath(), $"lmloader-cfg-e2e-{Guid.NewGuid():N}");

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

	private string ConfigRoot => Path.Combine(_root, "configs");

	[Trait("Category", "UsesFileSystem")]
	[Fact]
	public void 端到端_文件值OnLoad生效_缺失键写回默认()
	{
		CreateMod("com.e.cfg", "CfgMainAsm", MainModSource("com.e.cfg"));

		// 预置用户配置:multiplier 已设;greeting 缺失(应补默认写回)
		Directory.CreateDirectory(ConfigRoot);
		File.WriteAllText(
			Path.Combine(ConfigRoot, "com.e.cfg.toml"),
			"[patch]\nmultiplier = 7\n");

		using var manager = new ModManager(new LoaderOptions
		{
			ModsRootPath = ModsRoot,
			GameId = "com.game.test",
			ApiVersion = new Version(1, 0, 0, 0),
			ConfigRootPath = ConfigRoot,
		});

		var result = manager.LoadAll();

		Assert.NotNull(result.Lifecycle);
		Assert.Equal(1, result.Lifecycle!.SucceededCount);
		var instance = result.Lifecycle.Results.Select(r => r.Instance).First(i => i is not null)!;
		var calls = (List<string>)instance.GetType().GetProperty("Calls")!.GetValue(null)!;
		Assert.Contains("multiplier=7", calls);
		Assert.Contains("greeting=hi", calls);

		var disk = new TomlConfigStoreProbe(ConfigRoot).ReadMod("com.e.cfg");
		var patch = Assert.IsType<Tomlyn.Model.TomlTable>(disk["patch"]);
		Assert.Equal(7L, patch["multiplier"]);
		var voice = Assert.IsType<Tomlyn.Model.TomlTable>(disk["voice"]);
		Assert.Equal("hi", voice["greeting"]);
	}

	[Trait("Category", "UsesFileSystem")]
	[Fact]
	public void 端到端_同清单多模组_配置按模组各自独立_D18()
	{
		var modDir = Path.Combine(ModsRoot, "com.e.pack");
		Directory.CreateDirectory(modDir);
		var recorderDll = TestCompiler.CompileToDirectory(modDir, "CallRecorderLib", RecorderSource);
		TestCompiler.CompileToDirectory(
			modDir, "CfgMainAsm", MainModSource("com.e.pack.main"), recorderDll, typeof(LMLoader.Api.LmMod).Assembly.Location);
		TestCompiler.CompileToDirectory(
			modDir, "CfgSecondAsm", SecondModSource("com.e.pack.extra"), recorderDll, typeof(LMLoader.Api.LmMod).Assembly.Location);
		var manifestPath = Path.Combine(modDir, "com.e.pack.mod.json");
		File.WriteAllText(manifestPath, ManifestTemplate
			.Replace("#MODS#", """
				[
				    { "uid": "com.e.pack.main", "assemblies": ["CfgMainAsm.dll"] },
				    { "uid": "com.e.pack.extra", "assemblies": ["CfgSecondAsm.dll"] }
				  ]
				"""));

		using var manager = new ModManager(new LoaderOptions
		{
			ModsRootPath = ModsRoot,
			GameId = "com.game.test",
			ApiVersion = new Version(1, 0, 0, 0),
			ConfigRootPath = ConfigRoot,
		});

		var result = manager.LoadAll();

		Assert.NotNull(result.Lifecycle);
		Assert.Equal(2, result.Lifecycle!.SucceededCount);

		// 一份清单两个模组:配置文件按模组 uid 各自独立
		var mainDisk = new TomlConfigStoreProbe(ConfigRoot).ReadMod("com.e.pack.main");
		var mainVoice = Assert.IsType<Tomlyn.Model.TomlTable>(mainDisk["voice"]);
		Assert.Equal("hi", mainVoice["greeting"]);
		Assert.False(mainVoice.ContainsKey("enabled"));

		var extraDisk = new TomlConfigStoreProbe(ConfigRoot).ReadMod("com.e.pack.extra");
		var extraVoice = Assert.IsType<Tomlyn.Model.TomlTable>(extraDisk["voice"]);
		Assert.Equal(true, extraVoice["enabled"]);
		Assert.False(extraVoice.ContainsKey("greeting"));
	}

	private void CreateMod(string uid, string assemblyName, string source)
	{
		var modDir = Path.Combine(ModsRoot, uid);
		Directory.CreateDirectory(modDir);
		var recorderDll = TestCompiler.CompileToDirectory(modDir, "CallRecorderLib", RecorderSource);
		TestCompiler.CompileToDirectory(
			modDir, assemblyName, source, recorderDll, typeof(LMLoader.Api.LmMod).Assembly.Location);
		var manifestPath = Path.Combine(modDir, uid + ".mod.json");
		File.WriteAllText(manifestPath, ManifestTemplate
			.Replace("#MODS#", $$"""[ { "uid": "{{uid}}", "assemblies": ["{{assemblyName}}.dll"] } ]"""));
	}

	/// <summary>探针:直接用默认存储读文件,避免依赖被测 manager 实例。</summary>
	private sealed class TomlConfigStoreProbe : LMLoader.Core.Config.TomlConfigStore
	{
		public TomlConfigStoreProbe(string root) => _root = root;

		private readonly string _root;

		public Tomlyn.Model.TomlTable ReadMod(string modUid) =>
			base.Read(System.IO.Path.Combine(_root, modUid + ".toml"));
	}
}
