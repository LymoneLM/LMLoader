using LMLoader.Core;
using LMLoader.Distribution.SteamWorkshop;

namespace LMLoader.Core.Tests.Distribution;

/// <summary>
/// 6.5 Workshop 订阅发现(D15):伪造 Steam 库布局验证 vdf 解析、ACF 孤儿过滤、
/// mod.json 发现与 ModManager 附加根接入。
/// </summary>
[Trait("Category", "UsesFileSystem")]
public class SteamWorkshopTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), $"lmcli-ws-{Guid.NewGuid():N}");

	/// <summary>steamapps/workshop/content/480/<pfid> 目录,可选放置 mod.json。</summary>
	private string ContentRoot =>
		Path.Combine(LibraryRoot, "steamapps", "workshop", "content", "480");

	private string LibraryRoot => Path.Combine(_root, "library");

	public SteamWorkshopTests() => Directory.CreateDirectory(ContentRoot);

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

	private void WriteItem(string pfid, string? modJson = """{ "schemaVersion": 1 }""")
	{
		var dir = Path.Combine(ContentRoot, pfid);
		Directory.CreateDirectory(dir);
		if (modJson is not null)
		{
			File.WriteAllText(Path.Combine(dir, "com.ws.mod.mod.json"), modJson);
		}
	}

	private void WriteLibraryVdf(params string[] libraryPaths)
	{
		var steamRoot = Path.Combine(_root, "steam");
		Directory.CreateDirectory(Path.Combine(steamRoot, "steamapps"));
		var entries = string.Join("\n", libraryPaths.Select((p, i) =>
			$"\t\"{i}\"\n\t{{\n\t\t\"path\"\t\t\"{p.Replace("\\", "\\\\")}\"\n\t}}"));
		File.WriteAllText(Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"),
			$"\"libraryfolders\"\n{{\n{entries}\n}}\n");
	}

	private void WriteAcf(string content) =>
		File.WriteAllText(Path.Combine(_root, "library", "steamapps", "workshop", "appworkshop_480.acf"), content);

	private const string AcfTemplate = """
		"WorkshopItemsInstalled"
		{
			"111111"		{ "size" "1" "timeupdated" "1" "manifest" "m1" }
			"222222"		{ "size" "2" "timeupdated" "2" "manifest" "m2" }
		}
		"WorkshopItemDetails"
		{
			"111111"		{ "manifest" "m1" }
		}
		""";

	[Fact]
	public void acf解析_提取WorkshopItemsInstalled直接子键()
	{
		var ids = AcfParser.ReadWorkshopItemsInstalled(AcfTemplate);

		Assert.NotNull(ids);
		Assert.Equal(["111111", "222222"], ids!.OrderBy(x => x));
	}

	[Theory]
	[InlineData("")]
	[InlineData("\"WorkshopItemsInstalled\"\n{ \"unterminated\"")]
	[InlineData("\"OtherSection\"\n{ \"111\" { } }")]
	public void acf解析_缺失或损坏返回null(string text)
	{
		Assert.Null(AcfParser.ReadWorkshopItemsInstalled(text));
	}

	[Fact]
	public void 扫描器_发现含modjson的条目()
	{
		WriteItem("111111");
		WriteItem("222222", modJson: null); // 无 mod.json(非模组条目,如纯资源)
		WriteItem("333333");

		var roots = WorkshopModsScanner.GetScanRoots([LibraryRoot], "480");

		Assert.Contains(roots, r => r.EndsWith("111111", StringComparison.Ordinal));
		Assert.Contains(roots, r => r.EndsWith("333333", StringComparison.Ordinal));
		Assert.Equal(2, roots.Count);
	}

	[Fact]
	public void 扫描器_acf过滤孤儿条目()
	{
		WriteItem("111111"); // 在 ACF 中(已安装)
		WriteItem("333333"); // 不在 ACF 中(孤儿:订阅已删内容残留)
		WriteAcf(AcfTemplate);

		var warnings = new List<string>();
		var roots = WorkshopModsScanner.GetScanRoots([LibraryRoot], "480", warnings.Add);

		Assert.Contains(roots, r => r.EndsWith("111111", StringComparison.Ordinal));
		Assert.DoesNotContain(roots, r => r.EndsWith("333333", StringComparison.Ordinal));
		Assert.Empty(warnings);
	}

	[Fact]
	public void 扫描器_acf缺失_不过滤()
	{
		WriteItem("111111");
		WriteItem("333333");

		var roots = WorkshopModsScanner.GetScanRoots([LibraryRoot], "480");

		Assert.Equal(2, roots.Count);
	}

	[Fact]
	public void 扫描器_无内容目录_返回空()
	{
		Assert.Empty(WorkshopModsScanner.GetScanRoots([_root], "999999"));
	}

	[Fact]
	public void 库根解析_vdf多库路径()
	{
		var other = Path.Combine(_root, "other-library");
		WriteLibraryVdf(other);

		var roots = SteamLibraryLocator.EnumerateLibraryRoots(
			new[] { Path.Combine(_root, "steam") }.ToList());

		// steam 根恒在;vdf 登记 other-library(存在目录才收录)
		Assert.Contains(roots, r => r.EndsWith("steam", StringComparison.Ordinal));
	}

	[Fact]
	public void ModManager_附加根扫描_Workshop模组可加载()
	{
		WriteItem("111111");
		var workshopDir = Path.Combine(ContentRoot, "111111");
		File.WriteAllText(Path.Combine(workshopDir, "com.ws.mod.mod.json"), """
			{
			  "schemaVersion": 1,
			  "uid": "com.ws.mod",
			  "name": "workshop mod",
			  "version": "1.0.0",
			  "gameId": "com.game.test",
			  "loaderVersion": "1.0.0",
			  "entry": { "assembly": "Mod.dll", "modules": [ { "uid": "com.ws.mod.main", "type": "A.B.M" } ] }
			}
			""");

		// 主根为空目录;模组仅在 Workshop 订阅目录
		var modsRoot = Path.Combine(_root, "mods");
		Directory.CreateDirectory(modsRoot);

		using var manager = new ModManager(new LoaderOptions
		{
			ModsRootPath = modsRoot,
			AdditionalModsRoots = [workshopDir],
			GameId = "com.game.test",
			ApiVersion = new Version(1, 0, 0),
		});

		var result = manager.LoadAll();

		Assert.Single(result.LoadedManifests);
		Assert.Equal("com.ws.mod", result.LoadedManifests[0].Uid);
	}

	[Fact]
	public void ModManager_主根优先_附加根副本跳过并告警()
	{
		var manifest = """
			{
			  "schemaVersion": 1,
			  "uid": "com.dup.mod",
			  "name": "dup",
			  "version": "1.0.0",
			  "gameId": "com.game.test",
			  "loaderVersion": "1.0.0",
			  "entry": { "assembly": "Mod.dll", "modules": [ { "uid": "com.dup.mod.main", "type": "A.B.M" } ] }
			}
			""";
		var modsRoot = Path.Combine(_root, "mods");
		Directory.CreateDirectory(modsRoot);
		File.WriteAllText(Path.Combine(modsRoot, "com.dup.mod.mod.json"), manifest);
		WriteItem("111111", modJson: manifest);

		using var manager = new ModManager(new LoaderOptions
		{
			ModsRootPath = modsRoot,
			AdditionalModsRoots = [ContentRoot],
			GameId = "com.game.test",
			ApiVersion = new Version(1, 0, 0),
		});

		var result = manager.LoadAll();

		var single = Assert.Single(result.LoadedManifests);
		Assert.EndsWith("mods", single.Directory);
		Assert.Contains("已从其他扫描根加载", result.SummaryText);
	}

	[Fact]
	public void ModManager_附加根不存在_静默跳过()
	{
		var modsRoot = Path.Combine(_root, "empty-mods");
		Directory.CreateDirectory(modsRoot);

		using var manager = new ModManager(new LoaderOptions
		{
			ModsRootPath = modsRoot,
			AdditionalModsRoots = [Path.Combine(_root, "ghost")],
			GameId = "com.game.test",
			ApiVersion = new Version(1, 0, 0),
		});

		Assert.Empty(manager.LoadAll().LoadedManifests);
	}
}
