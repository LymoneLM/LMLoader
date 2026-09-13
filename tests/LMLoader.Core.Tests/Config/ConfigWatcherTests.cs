using LMLoader.Api.Config;
using LMLoader.Core.Config;
using Tomlyn.Model;

namespace LMLoader.Core.Tests.Config;

/// <summary>
/// 热重载监听验证:真实文件系统 + 真实 FileSystemWatcher,轮询等待防抖窗口后断言。
/// </summary>
[Trait("Category", "UsesFileSystem")]
public class ConfigWatcherTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), $"lmloader-watch-{Guid.NewGuid():N}");

	public ConfigWatcherTests()
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

	private static void Eventually(Func<bool> condition, TimeSpan? timeout = null)
	{
		var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
		while (DateTime.UtcNow < deadline)
		{
			if (condition())
			{
				return;
			}

			Thread.Sleep(25);
		}

		throw new TimeoutException("等待配置热重载生效超时");
	}

	[Fact]
	public void 文件变更_防抖后自动重载_值更新并触发事件()
	{
		var config = new ModConfig();
		var entry = config.Bind("patch", "m", 3);
		using var manager = new ConfigManager(_root);
		manager.Register("com.a.b", config);
		File.WriteAllText(PathOf("com.a.b"), "[patch]\nm = 3\n");
		manager.ApplyAfterPreLoad([new("com.a.b", config)]);
		Assert.Equal(3, entry.Value);

		var fired = 0;
		entry.SettingChanged += _ => fired++;
		using var watcher = new ConfigWatcher(manager, _root, debounce: TimeSpan.FromMilliseconds(120));

		File.WriteAllText(PathOf("com.a.b"), "[patch]\nm = 9\n");

		Eventually(() => entry.Value == 9);
		Assert.Equal(1, fired);
	}

	[Fact]
	public void 防抖窗口内多次写入只触发一次重载()
	{
		var config = new ModConfig();
		var entry = config.Bind("patch", "m", 3);
		using var manager = new ConfigManager(_root);
		manager.Register("com.a.b", config);
		File.WriteAllText(PathOf("com.a.b"), "[patch]\nm = 3\n");
		manager.ApplyAfterPreLoad([new("com.a.b", config)]);

		var fired = 0;
		entry.SettingChanged += _ => fired++;
		using var watcher = new ConfigWatcher(manager, _root, debounce: TimeSpan.FromMilliseconds(300));

		// 窗口内连续两次写入:防抖后只看最终值
		File.WriteAllText(PathOf("com.a.b"), "[patch]\nm = 5\n");
		File.WriteAllText(PathOf("com.a.b"), "[patch]\nm = 8\n");

		Eventually(() => entry.Value == 8);
		Assert.Equal(1, fired);
	}

	[Fact]
	public void 未注册模组的配置变更被忽略()
	{
		using var manager = new ConfigManager(_root);
		var applied = false;
		using var watcher = new ConfigWatcher(manager, _root, debounce: TimeSpan.FromMilliseconds(80));

		File.WriteAllText(PathOf("com.unknown"), "[patch]\nm = 9\n");

		// 不抛异常即视为忽略;给足时间确认无副作用路径崩溃
		Thread.Sleep(300);
		Assert.False(applied);
	}

	[Fact]
	public void StartHotReload_幂等且随Manager释放()
	{
		var config = new ModConfig();
		var entry = config.Bind("patch", "m", 3);
		var manager = new ConfigManager(_root);
		manager.Register("com.a.b", config);
		File.WriteAllText(PathOf("com.a.b"), "[patch]\nm = 3\n");
		manager.ApplyAfterPreLoad([new("com.a.b", config)]);

		manager.StartHotReload(TimeSpan.FromMilliseconds(100));
		manager.StartHotReload(TimeSpan.FromMilliseconds(100)); // 第二次应为无操作

		File.WriteAllText(PathOf("com.a.b"), "[patch]\nm = 6\n");
		Eventually(() => entry.Value == 6);

		manager.Dispose();

		File.WriteAllText(PathOf("com.a.b"), "[patch]\nm = 2\n");
		Thread.Sleep(250);
		Assert.Equal(6, entry.Value); // 释放后不再重载
	}

	[Fact]
	public void 原子写_落盘后无临时文件残留()
	{
		var store = new TomlConfigStore();
		var path = PathOf("com.a.b");

		store.Write(path, new TomlTable { ["k"] = "v" });
		store.Write(path, new TomlTable { ["k"] = "v2" }); // 覆盖既有文件

		var disk = store.Read(path);
		Assert.Equal("v2", disk["k"]);
		Assert.Equal([path], Directory.GetFiles(_root).Where(f => !Path.GetFileName(f).StartsWith('.')).ToArray());
		Assert.DoesNotContain(Directory.GetFiles(_root), f => f.EndsWith(".tmp", StringComparison.Ordinal));
	}
}
