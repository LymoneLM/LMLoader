// SPDX-License-Identifier: LGPL-3.0-or-later

using LMLoader.Cli.Lint;
using LMLoader.Cli.Pack;
using LMLoader.Core.Manifest;

return Args.ParseAndRun(args);

internal static class Args
{
	private const string Usage = """
		LMLoader.Cli — mod 作者侧工具(6.4)

		用法:
		  lmcli lint      --dir <mod目录>
		  lmcli pack-pck  --godot <godot可执行> --project <godot工程目录> --preset <导出预设名> --output <pck路径>
		  lmcli pack-ts   --dir <mod目录> --team <Thunderstore团队> [--output <zip路径>] [--deps-root <mods根目录>]

		说明:
		  pack-pck 包装 `godot --headless --export-pack`(D10);pack-ts 生成 Thunderstore 包
		  (根含 manifest.json + icon.png + 模组文件;mod.json 一并打入,解压即原生格式,D16)。
		  依赖映射:pack-ts 经 --deps-root 扫描依赖模组的 mod.json 提供别名;缺省时依赖条目省略(警告)。
		""";

	public static int ParseAndRun(string[] args)
	{
		if (args.Length == 0 || args[0] is "--help" or "-h")
		{
			Console.Out.Write(Usage);
			return args.Length == 0 ? 2 : 0;
		}

		try
		{
			return args[0] switch
			{
				"lint" => RunLint(ParseOptions(args)),
				"pack-pck" => RunPackPck(ParseOptions(args)),
				"pack-ts" => RunPackTs(ParseOptions(args)),
				_ => Unknown(args[0]),
			};
		}
		catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or DirectoryNotFoundException
			or FormatException or NotSupportedException or InvalidOperationException)
		{
			Console.Error.WriteLine($"错误: {ex.Message}");
			return 1;
		}
	}

	private static int Unknown(string command)
	{
		Console.Error.WriteLine($"未知命令: {command}");
		Console.Out.Write(Usage);
		return 2;
	}

	private static Dictionary<string, string> ParseOptions(string[] args)
	{
		var options = new Dictionary<string, string>(StringComparer.Ordinal);
		for (var i = 1; i + 1 < args.Length; i += 2)
		{
			var key = args[i];
			if (!key.StartsWith("--", StringComparison.Ordinal))
			{
				throw new ArgumentException($"无法解析参数: {key}(期望 --key value 形式)");
			}

			options[key[2..]] = args[i + 1];
		}

		return options;
	}

	private static string Require(Dictionary<string, string> options, string name)
	{
		if (!options.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
		{
			throw new ArgumentException($"缺少必填参数 --{name}");
		}

		return value;
	}

	private static int RunLint(Dictionary<string, string> options)
	{
		var result = ModLinter.Lint(Require(options, "dir"));
		foreach (var issue in result.Issues)
		{
			Console.Out.WriteLine($"{(issue.IsError ? "[error]  " : "[warning] ")}{issue.Message}");
		}

		Console.Out.WriteLine(result.Success ? "lint 通过" : $"lint 失败({result.Issues.Count(i => i.IsError)} 个错误)");
		return result.Success ? 0 : 1;
	}

	private static int RunPackPck(Dictionary<string, string> options)
	{
		var output = Require(options, "output");
		var godot = Environment.GetEnvironmentVariable("GODOT_EXECUTABLE") is { Length: > 0 } env
			? env
			: Require(options, "godot");
		var result = new PckExporter().Export(
			godot, Require(options, "project"), Require(options, "preset"), output);

		Console.Out.Write(result.Output);
		Console.Out.WriteLine(result.Success
			? $"pck 导出完成: {output}"
			: $"pck 导出失败(退出码 {result.ExitCode})");
		return result.Success ? 0 : 1;
	}

	private static int RunPackTs(Dictionary<string, string> options)
	{
		var dir = Require(options, "dir");
		var zip = options.TryGetValue("output", out var output) && output.Length > 0
			? output
			: Path.Combine(dir, Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar)) + "-thunderstore.zip");

		// 依赖别名来源:--deps-root 下递归读全部 mod.json
		Dictionary<string, ModManifest>? dependencies = null;
		if (options.TryGetValue("deps-root", out var depsRoot) && Directory.Exists(depsRoot))
		{
			dependencies = Directory.EnumerateFiles(depsRoot, "*mod.json", SearchOption.AllDirectories)
				.Select(ModManifestReader.ReadFile)
				.Where(r => r.Success)
				.Select(r => r.Manifest!)
				.GroupBy(m => m.Uid)
				.ToDictionary(g => g.Key, g => g.First());
		}

		var result = ThunderstorePacker.Pack(dir, Require(options, "team"), zip, dependencies, Console.Out.WriteLine);
		Console.Out.WriteLine($"Thunderstore 包已生成: {result.ZipPath}({result.ManifestName} v{result.VersionNumber})");
		return 0;
	}
}
