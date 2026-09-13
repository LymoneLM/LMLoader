using LMLoader.Api.Config;
using LMLoader.Core.Config;
using Tomlyn.Model;

namespace LMLoader.Core.Tests.Config;

[Trait("Category", "UsesFileSystem")]
public class ConfigManagerTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), $"lmloader-cfgmgr-{Guid.NewGuid():N}");

	public ConfigManagerTests()
	{
		Directory.CreateDirectory(_root);
	}

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

	private string PathOf(string modUid) => TomlConfigStore.GetConfigFilePath(_root, modUid);

	private void Apply(ConfigManager manager, string modUid, ModConfig config) =>
		manager.ApplyAfterPreLoad([new KeyValuePair<string, ModConfig>(modUid, config)]);

	private sealed class CountingStore : TomlConfigStore
	{
		public int Writes;

		public override void Write(string filePath, TomlTable table)
		{
			Writes++;
			base.Write(filePath, table);
		}
	}

	[Fact]
	public void Register_每模组首次实例生效()
	{
		var manager = new ConfigManager(_root);
		var first = new ModConfig();
		var second = new ModConfig();

		manager.Register("com.a.b", first);
		manager.Register("com.a.b", second);

		manager.TryGetConfig("com.a.b", out var got);
		Assert.Same(first, got);
	}

	[Fact]
	public void 首次合并_文件缺失_默认值生效并写回()
	{
		var manager = new ConfigManager(_root);
		var config = new ModConfig();
		config.Bind("patch", "m", 3);
		config.Bind("", "flag", true);
		manager.Register("com.a.b", config);

		Apply(manager, "com.a.b", config);

		var disk = new TomlConfigStore().Read(PathOf("com.a.b"));
		Assert.Equal(3L, ((TomlTable)disk["patch"])["m"]);
		Assert.Equal(true, disk["flag"]);
	}

	[Fact]
	public void 首次合并_文件值生效_孤儿键保留()
	{
		var path = PathOf("com.a.b");
		File.WriteAllText(path, """
			[patch]
			m = 7

			[extra]
			stale = "keep"
			""");
		var manager = new ConfigManager(_root);
		var config = new ModConfig();
		var entry = config.Bind("patch", "m", 3);
		manager.Register("com.a.b", config);

		Apply(manager, "com.a.b", config);

		Assert.Equal(7, entry.Value);
		var disk = new TomlConfigStore().Read(path);
		var extra = Assert.IsType<TomlTable>(disk["extra"]);
		Assert.Equal("keep", extra["stale"]);
	}

	[Fact]
	public void 首次合并_类型不符回退默认并写回()
	{
		var path = PathOf("com.a.b");
		File.WriteAllText(path, "[patch]\nm = \"abc\"\n");
		var manager = new ConfigManager(_root);
		var config = new ModConfig();
		var entry = config.Bind("patch", "m", 3);
		manager.Register("com.a.b", config);

		Apply(manager, "com.a.b", config);

		Assert.Equal(3, entry.Value);
		var disk = new TomlConfigStore().Read(path);
		Assert.Equal(3L, ((TomlTable)disk["patch"])["m"]);
	}

	[Fact]
	public void 首次合并_范围越界钳制写回()
	{
		var path = PathOf("com.a.b");
		File.WriteAllText(path, "[patch]\nm = 500\n");
		var manager = new ConfigManager(_root);
		var config = new ModConfig();
		var entry = config.Bind("patch", "m", 3, acceptableValues: new AcceptableValueRange<int>(0, 100));
		manager.Register("com.a.b", config);

		Apply(manager, "com.a.b", config);

		Assert.Equal(100, entry.Value);
		var disk = new TomlConfigStore().Read(path);
		Assert.Equal(100L, ((TomlTable)disk["patch"])["m"]);
	}

	[Fact]
	public void 首次合并_解析失败_用默认值且不写回()
	{
		var path = PathOf("com.a.b");
		var corrupted = "[unterminated";
		File.WriteAllText(path, corrupted);
		var manager = new ConfigManager(_root);
		var config = new ModConfig();
		var entry = config.Bind("patch", "m", 3);
		manager.Register("com.a.b", config);

		Apply(manager, "com.a.b", config);

		Assert.Equal(3, entry.Value);
		Assert.Equal(corrupted, File.ReadAllText(path));
	}

	[Fact]
	public void 首次合并_值未变化时跳过写回()
	{
		var store = new CountingStore();
		var manager = new ConfigManager(_root, store);
		var config = new ModConfig();
		config.Bind("patch", "m", 3);
		config.Bind("patch", "n", "x");
		manager.Register("com.a.b", config);

		Apply(manager, "com.a.b", config);
		Assert.Equal(1, store.Writes);

		Apply(manager, "com.a.b", config);
		Assert.Equal(1, store.Writes);
	}

	[Fact]
	public void 首次合并_节被标量占据_覆盖为表()
	{
		var path = PathOf("com.a.b");
		File.WriteAllText(path, "patch = 5\n");
		var manager = new ConfigManager(_root);
		var config = new ModConfig();
		config.Bind("patch", "m", 3);
		manager.Register("com.a.b", config);

		Apply(manager, "com.a.b", config);

		var disk = new TomlConfigStore().Read(path);
		Assert.Equal(3L, ((TomlTable)disk["patch"])["m"]);
	}

	[Fact]
	public void 首次合并_含点节名按单层表往返()
	{
		var manager = new ConfigManager(_root);
		var config = new ModConfig();
		var entry = config.Bind("a.b", "k", 1);
		manager.Register("com.a.b", config);

		Apply(manager, "com.a.b", config);
		Assert.Equal(1, entry.Value);

		// 再次合并:文件里的节应能按 "a.b" 单层键读回,不回退默认
		Apply(manager, "com.a.b", config);
		Assert.Equal(1, entry.Value);
		var disk = new TomlConfigStore().Read(PathOf("com.a.b"));
		Assert.Equal(1L, ((TomlTable)disk["a.b"])["k"]);
	}

	[Fact]
	public void 热重载_存在键更新并触发事件()
	{
		var manager = new ConfigManager(_root);
		var config = new ModConfig();
		var entry = config.Bind("patch", "m", 3);
		manager.Register("com.a.b", config);
		Apply(manager, "com.a.b", config);

		var fired = 0;
		entry.SettingChanged += _ => fired++;
		File.WriteAllText(PathOf("com.a.b"), "[patch]\nm = 9\n");

		manager.ReloadFromFile("com.a.b", config);

		Assert.Equal(9, entry.Value);
		Assert.Equal(1, fired);
	}

	[Fact]
	public void 热重载_键被删除保持当前值且不写回()
	{
		var path = PathOf("com.a.b");
		var manager = new ConfigManager(_root);
		var config = new ModConfig();
		var entry = config.Bind("patch", "m", 3);
		manager.Register("com.a.b", config);
		File.WriteAllText(path, "[patch]\nm = 9\n");
		Apply(manager, "com.a.b", config);
		Assert.Equal(9, entry.Value);

		var rewritten = "[patch]\nother = 1\n";
		File.WriteAllText(path, rewritten);
		manager.ReloadFromFile("com.a.b", config);

		Assert.Equal(9, entry.Value);
		Assert.Equal(rewritten, File.ReadAllText(path));
	}

	[Fact]
	public void 热重载_类型不符保持当前值()
	{
		var manager = new ConfigManager(_root);
		var config = new ModConfig();
		var entry = config.Bind("patch", "m", 3);
		manager.Register("com.a.b", config);
		entry.Value = 5;

		File.WriteAllText(PathOf("com.a.b"), "[patch]\nm = \"bad\"\n");

		manager.ReloadFromFile("com.a.b", config);

		Assert.Equal(5, entry.Value);
	}

	[Fact]
	public void 热重载_解析失败维持当前值()
	{
		var manager = new ConfigManager(_root);
		var config = new ModConfig();
		var entry = config.Bind("patch", "m", 3);
		manager.Register("com.a.b", config);
		entry.Value = 5;

		File.WriteAllText(PathOf("com.a.b"), "[broken");

		manager.ReloadFromFile("com.a.b", config);

		Assert.Equal(5, entry.Value);
	}
}
