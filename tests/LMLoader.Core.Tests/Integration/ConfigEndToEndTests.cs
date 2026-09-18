using LMLoader.Core;
using LMLoader.Core.Tests.TestInfrastructure;

namespace LMLoader.Core.Tests.Integration;

/// <summary>
/// 端到端:真实模组 OnPreLoad 绑定配置 → ConfigManager 合并磁盘 toml →
/// OnLoad 读到生效值;缺失键写回默认;同模组多模块共享一份配置文件。
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
		  "uid": "#UID#",
		  "name": "#UID#",
		  "version": "1.0.0",
		  "gameId": "com.game.test",
		  "loaderVersion": "1.0.0",
		  "entry": { "assembly": "#ASM#.dll", "modules": #MODULES# }
		}
		""";

	private const string ModulesSource = """
		using LMLoader.Api;
		using LMLoader.Api.Config;

		namespace Fixture;

		public class CfgMain : LmModule
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

		public class CfgSecond : LmModule
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
		CreateMod("com.e.cfg", "CfgMainAsm",
			"""[{ "uid": "com.e.cfg.main", "type": "Fixture.CfgMain", "depends": [] }]""");

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
	public void 端到端_同模组多模块共享配置文件()
	{
		CreateMod("com.e.cfg2", "CfgBothAsm",
			"""[{ "uid": "com.e.cfg2.m1", "type": "Fixture.CfgMain", "depends": [] }, { "uid": "com.e.cfg2.m2", "type": "Fixture.CfgSecond", "depends": [] }]""");

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
		var anyInstance = result.Lifecycle.Results.Select(r => r.Instance).First(i => i is not null)!;
		var calls = (List<string>)anyInstance.GetType().GetProperty("Calls")!.GetValue(null)!;
		Assert.Contains("multiplier=3", calls);
		Assert.Contains("enabled=True", calls);

		// 一份文件同时承载两个模块的声明
		var disk = new TomlConfigStoreProbe(ConfigRoot).ReadMod("com.e.cfg2");
		var voice = Assert.IsType<Tomlyn.Model.TomlTable>(disk["voice"]);
		Assert.Equal("hi", voice["greeting"]);
		Assert.Equal(true, voice["enabled"]);
	}

	private string CreateMod(string uid, string assemblyName, string modulesJson)
	{
		var modDir = Path.Combine(ModsRoot, uid);
		Directory.CreateDirectory(modDir);
		var recorderDll = TestCompiler.CompileToDirectory(modDir, "CallRecorderLib", RecorderSource);
		TestCompiler.CompileToDirectory(
			modDir, assemblyName, ModulesSource, recorderDll, typeof(LMLoader.Api.LmModule).Assembly.Location);
		var manifestPath = Path.Combine(modDir, uid + ".mod.json");
		File.WriteAllText(manifestPath, ManifestTemplate
			.Replace("#UID#", uid)
			.Replace("#ASM#", assemblyName)
			.Replace("#MODULES#", modulesJson));
		return modDir;
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
