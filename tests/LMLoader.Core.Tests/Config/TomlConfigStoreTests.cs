using LMLoader.Core.Config;
using Tomlyn.Model;

namespace LMLoader.Core.Tests.Config;

[Trait("Category", "UsesFileSystem")]
public class TomlConfigStoreTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), $"lmloader-cfg-{Guid.NewGuid():N}");

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

	[Fact]
	public void 路径约定_根目录加modUid加toml()
	{
		var path = TomlConfigStore.GetConfigFilePath(_root, "com.author.mod");

		Assert.Equal(Path.Combine(_root, "com.author.mod.toml"), path);
	}

	[Theory]
	[InlineData("com/evil")]
	[InlineData(@"com\evil")]
	public void modUid含路径分隔符被拒绝(string uid)
	{
		Assert.Throws<ArgumentException>(() => TomlConfigStore.GetConfigFilePath(_root, uid));
	}

	[Fact]
	public void 文件缺失返回空表_首次运行常态()
	{
		var store = new TomlConfigStore();
		var path = TomlConfigStore.GetConfigFilePath(_root, "com.author.mod");

		var table = store.Read(path);

		Assert.Empty(table);
	}

	[Fact]
	public void 写出读回_基础类型roundtrip()
	{
		var store = new TomlConfigStore();
		var path = TomlConfigStore.GetConfigFilePath(_root, "com.author.mod");
		var tags = new TomlArray();
		tags.Add("a");
		tags.Add("b");
		var table = new TomlTable
		{
			["name"] = "sample",
			["count"] = 42L,
			["enabled"] = true,
			["ratio"] = 0.5,
			["tags"] = tags,
		};

		store.Write(path, table);
		var read = store.Read(path);

		Assert.Equal("sample", read["name"]);
		Assert.Equal(42L, read["count"]);
		Assert.Equal(true, read["enabled"]);
		Assert.Equal(0.5, read["ratio"]);
		var readTags = Assert.IsType<TomlArray>(read["tags"]);
		Assert.Equal(["a", "b"], readTags.ToArray());
	}

	[Fact]
	public void 写出读回_嵌套表roundtrip()
	{
		var store = new TomlConfigStore();
		var path = TomlConfigStore.GetConfigFilePath(_root, "com.author.mod");
		var table = new TomlTable
		{
			["patch"] = new TomlTable { ["multiplier"] = 100L },
		};

		store.Write(path, table);
		var read = store.Read(path);

		var section = Assert.IsType<TomlTable>(read["patch"]);
		Assert.Equal(100L, section["multiplier"]);
	}

	[Fact]
	public void 写出自动创建缺失目录()
	{
		var store = new TomlConfigStore();
		var path = TomlConfigStore.GetConfigFilePath(Path.Combine(_root, "configs"), "com.author.mod");

		store.Write(path, new TomlTable { ["k"] = "v" });

		Assert.True(File.Exists(path));
	}

	[Fact]
	public void 非法toml读入抛异常_交由上层宽容处置()
	{
		var store = new TomlConfigStore();
		var path = TomlConfigStore.GetConfigFilePath(_root, "com.author.mod");
		Directory.CreateDirectory(_root);
		File.WriteAllText(path, "[unterminated");

		Assert.ThrowsAny<Exception>(() => store.Read(path));
	}
}
