using LMLoader.Core.Logging;
using LMLoader.Core.Management;
using LMLoader.Core.Reporting;

namespace LMLoader.Core.Tests.Management;

/// <summary>ModState/ModStateStore:缺省语义、忠实记录、容错降级与持久化往返。</summary>
[Trait("Category", "UsesFileSystem")]
public class ModStateTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), $"lmloader-state-{Guid.NewGuid():N}");

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

	private ModStateStore NewStore() => new(Path.Combine(_root, "mod-state.toml"));

	[Fact]
	public void 默认状态_全启用()
	{
		var state = new ModState();

		Assert.True(state.GlobalEnabled);
		Assert.True(state.IsEnabled("com.any.mod"));
		Assert.Empty(state.Mods);
	}

	[Fact]
	public void 全局关闭_所有模组禁用_含未声明者()
	{
		var state = new ModState { GlobalEnabled = false };

		Assert.False(state.IsEnabled("com.declared.mod"));
		Assert.False(state.IsEnabled("com.unknown.mod"));
	}

	[Fact]
	public void SetMod_忠实记录_显式true与false()
	{
		var state = new ModState();
		state.SetMod("com.a.b", false);
		state.SetMod("com.c.d", true);

		Assert.False(state.IsEnabled("com.a.b"));
		Assert.True(state.IsEnabled("com.c.d"));
		Assert.Equal(2, state.Mods.Count);
	}

	[Fact]
	public void Clone与SameAs_逐键比较()
	{
		var state = new ModState();
		state.SetMod("com.a.b", false);
		var clone = state.Clone();

		Assert.True(clone.SameAs(state));
		clone.SetMod("com.e.f", false);
		Assert.False(clone.SameAs(state));
		Assert.False(state.SameAs(clone));
	}

	[Fact]
	public void 存取往返_全局与模组键一致()
	{
		var store = NewStore();
		var state = new ModState { GlobalEnabled = false };
		state.SetMod("com.a.b", false);
		state.SetMod("com.c.d", true);
		store.Save(state);

		var loaded = store.Load();

		Assert.False(loaded.GlobalEnabled);
		Assert.False(loaded.IsEnabled("com.a.b"));
		// 全局关闭时 IsEnabled 按设计为 false;显式落盘值经字典核对
		Assert.False(loaded.IsEnabled("com.c.d"));
		Assert.True((bool)loaded.Mods["com.c.d"]);
		Assert.Equal(state.GlobalEnabled, loaded.GlobalEnabled);
		Assert.Equal(state.Mods.Count, loaded.Mods.Count);
		Assert.Equal(state.Mods, loaded.Mods);
		Assert.True(loaded.SameAs(state));
	}

	[Fact]
	public void 文件缺失_返回默认态()
	{
		var loaded = NewStore().Load();

		Assert.True(loaded.GlobalEnabled);
		Assert.Empty(loaded.Mods);
	}

	[Fact]
	public void 损坏文件_宽容降级为全启用_不抛出()
	{
		Directory.CreateDirectory(_root);
		File.WriteAllText(Path.Combine(_root, "mod-state.toml"), "enabled = !!! 这不是 TOML");

		var loaded = NewStore().Load();

		Assert.True(loaded.GlobalEnabled);
		Assert.Empty(loaded.Mods);
	}

	[Fact]
	public void 非布尔模组值_忽略该项_其余照常()
	{
		Directory.CreateDirectory(_root);
		File.WriteAllText(Path.Combine(_root, "mod-state.toml"),
			"""
			enabled = true

			[mods]
			"com.a.b" = false
			"com.c.d" = "not-a-bool"
			""");

		var loaded = NewStore().Load();

		Assert.False(loaded.IsEnabled("com.a.b"));
		Assert.True(loaded.IsEnabled("com.c.d")); // 非布尔被忽略 → 视为启用
		Assert.Single(loaded.Mods);
	}

	[Fact]
	public void 保存后无临时文件残留()
	{
		Directory.CreateDirectory(_root);
		var store = NewStore();
		store.Save(new ModState());

		Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp"));
		Assert.True(File.Exists(Path.Combine(_root, "mod-state.toml")));
	}
}

/// <summary>ModManager 管理面(D19):状态过滤、发现视图、启停 API、事件与依赖预计算。</summary>
[Trait("Category", "UsesFileSystem")]
public class ModManagerManagementTests : IDisposable
{
	private const string RecorderSource = """
		using System.Collections.Generic;

		public static class CallRecorder
		{
		    public static List<string> Items = new();
		}
		""";

	private readonly string _root = Path.Combine(Path.GetTempPath(), $"lmloader-mgmt-{Guid.NewGuid():N}");
	private int _modCounter;

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

	private string StatePath => Path.Combine(_root, "mod-state.toml");

	/// <summary>落一个可真实加载的模组目录,返回 uid。</summary>
	private string CreateMod(string uid, params (string TargetUid, bool Soft)[] depends)
	{
		_modCounter++;
		var dir = Path.Combine(ModsRoot, uid);
		Directory.CreateDirectory(dir);
		var recorderDll = TestInfrastructure.TestCompiler.CompileToDirectory(dir, "CallRecorderLib", RecorderSource);
		var className = "M_" + _modCounter;
		var dependsCode = string.Join("", depends.Select(d =>
			$"TryGetService<object>(out _);")); // 占位:依赖语义由清单驱动,源码无需真实引用
		var source = $$"""
			using LMLoader.Api;

			namespace Fixture;

			[LmMod("{{uid}}")]
			public class {{className}} : LmMod
			{
			    public override void OnLoad() { {{dependsCode}} }
			}
			""";
		TestInfrastructure.TestCompiler.CompileToDirectory(
			dir, className + "Asm", source, recorderDll, typeof(LMLoader.Api.LmMod).Assembly.Location);

		var deps = depends.Length == 0
			? ""
			: ", \"depends\": [" + string.Join(", ", depends.Select(d =>
				$$"""{ "uid": "{{d.TargetUid}}", "soft": {{(d.Soft ? "true" : "false").ToString().ToLowerInvariant()}}}""")) + "]";
		File.WriteAllText(Path.Combine(dir, uid + ".mod.json"),
			$$"""{ "schemaVersion": 1, "gameId": "com.game.test", "version": "1.0.0", "mods": [ { "uid": "{{uid}}", "assemblies": ["{{className}}Asm.dll"]{{deps}} } ] }""");
		return uid;
	}

	private ModManager CreateManager(Action<LoaderOptions>? configure = null)
	{
		var options = new LoaderOptions
		{
			ModsRootPath = ModsRoot,
			GameId = "com.game.test",
			ApiVersion = new Version(1, 0, 0, 0),
			ModStatePath = StatePath,
		};
		configure?.Invoke(options);
		return new ModManager(options);
	}

	[Fact]
	public void 预置禁用状态_模组跳过_发现视图在列()
	{
		var x = CreateMod("com.t.x");
		var y = CreateMod("com.t.y");
		var store = new ModStateStore(StatePath);
		var state = new ModState();
		state.SetMod(x, false);
		store.Save(state);

		using var manager = CreateManager();
		var result = manager.LoadAll();

		Assert.Single(result.Plan.Ordered, i => i.Uid == y);
		Assert.Contains(result.Plan.Skipped, s => s.Uid == x && s.Reason.Contains("已禁用"));
		Assert.Contains("已禁用", result.SummaryText);

		var discoveredX = Assert.Single(result.DiscoveredMods, m => m.Uid == x);
		Assert.False(discoveredX.Enabled);
		Assert.Equal(ModLoadStatus.Skipped, discoveredX.Status);
		Assert.Equal("1.0.0", discoveredX.Version);
		Assert.EndsWith(x + ".mod.json", discoveredX.SourcePath);

		var discoveredY = Assert.Single(result.DiscoveredMods, m => m.Uid == y);
		Assert.True(discoveredY.Enabled);
		Assert.Equal(ModLoadStatus.Loaded, discoveredY.Status);
	}

	[Fact]
	public void 硬依赖禁用模组_级联原因链指明已禁用()
	{
		var x = CreateMod("com.t.x");
		var y = CreateMod("com.t.y", (x, false));
		var store = new ModStateStore(StatePath);
		var state = new ModState();
		state.SetMod(x, false);
		store.Save(state);

		using var manager = CreateManager();
		var result = manager.LoadAll();

		Assert.Empty(result.Plan.Ordered);
		var ySkip = Assert.Single(result.Plan.Skipped, s => s.Uid == y);
		Assert.Contains("已禁用", ySkip.Reason);
		Assert.Contains(x, ySkip.Reason);
	}

	[Fact]
	public void 全局开关关闭_纯净模式_全部禁用()
	{
		CreateMod("com.t.a");
		CreateMod("com.t.b");
		var store = new ModStateStore(StatePath);
		store.Save(new ModState { GlobalEnabled = false });

		using var manager = CreateManager();
		var result = manager.LoadAll();

		Assert.Empty(result.Plan.Ordered);
		Assert.Equal(2, result.Plan.Skipped.Count);
		Assert.All(result.Plan.Skipped, s => Assert.Contains("全局开关", s.Reason));
	}

	[Fact]
	public void SetModEnabled_即时持久化_事件与PendingRestart()
	{
		CreateMod("com.t.a");
		using var manager = CreateManager();
		var first = manager.LoadAll();
		Assert.False(manager.PendingRestart);

		var events = new List<ModStateChangeEventArgs>();
		manager.ModStateChanged += events.Add;

		manager.SetModEnabled("com.t.a", false);

		Assert.True(manager.PendingRestart);
		var change = Assert.Single(events);
		Assert.Contains("已禁用", change.Summary);
		Assert.Contains("重启后生效", change.Summary);
		Assert.Equal(new[] { "com.t.a" }, change.DisabledUids);

		// 状态文件已落盘
		var store = new ModStateStore(StatePath);
		Assert.False(store.Load().IsEnabled("com.t.a"));

		// 重新加载后:变更生效,PendingRestart 复位
		var second = manager.LoadAll();
		Assert.Empty(second.Plan.Ordered);
		Assert.False(manager.PendingRestart);
		Assert.Contains(first.DiscoveredMods, m => m.Uid == "com.t.a" && m.Enabled);
	}

	[Fact]
	public void SetModEnabled_同值无操作_未发现uid抛出()
	{
		CreateMod("com.t.a");
		using var manager = CreateManager();
		manager.LoadAll();

		var events = 0;
		manager.ModStateChanged += _ => events++;
		manager.SetModEnabled("com.t.a", true); // 本就启用 → 无操作

		Assert.Equal(0, events);
		Assert.Throws<KeyNotFoundException>(() => manager.SetModEnabled("com.t.ghost", false));
	}

	[Fact]
	public void SetAllEnabled_纯净模式往返()
	{
		CreateMod("com.t.a");
		using var manager = CreateManager();
		manager.LoadAll();

		manager.SetAllEnabled(false);
		Assert.True(manager.PendingRestart);

		var disabled = manager.LoadAll();
		Assert.Empty(disabled.Plan.Ordered);
		Assert.False(manager.PendingRestart);

		manager.SetAllEnabled(true);
		var reEnabled = manager.LoadAll();
		Assert.Single(reEnabled.Plan.Ordered);
	}

	[Fact]
	public void PreviewDisable_传递依赖方与路径_D19()
	{
		var a = CreateMod("com.t.a");
		var b = CreateMod("com.t.b", (a, false));
		var c = CreateMod("com.t.c", (b, false));
		CreateMod("com.t.free"); // 无依赖,不在影响面
		using var manager = CreateManager();
		manager.LoadAll();

		var impacts = manager.PreviewDisable(a);

		Assert.Equal(new[] { "com.t.b", "com.t.c" }, impacts.Select(i => i.Uid).ToArray());
		Assert.Equal("b", impacts[0].Name); // name 缺省 = uid 尾段
		Assert.EndsWith("← com.t.c", impacts[1].Chain);
	}

	[Fact]
	public void PreviewDisable_已禁用依赖方不在影响面()
	{
		var a = CreateMod("com.t.a");
		var b = CreateMod("com.t.b", (a, false));
		using var manager = CreateManager();
		manager.LoadAll();

		manager.SetModEnabled(b, false); // b 已禁用:禁用 a 不新增影响面
		var impacts = manager.PreviewDisable(a);

		Assert.Empty(impacts);
	}

	[Fact]
	public void 孤儿键_未安装的uid_不影响加载()
	{
		CreateMod("com.t.a");
		var store = new ModStateStore(StatePath);
		var state = new ModState();
		state.SetMod("com.t.ghost", false);
		store.Save(state);

		using var manager = CreateManager();
		var result = manager.LoadAll();

		Assert.Single(result.Plan.Ordered, i => i.Uid == "com.t.a");
	}
}
