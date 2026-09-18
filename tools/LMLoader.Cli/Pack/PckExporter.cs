// SPDX-License-Identifier: LGPL-3.0-or-later

using System.Diagnostics;

namespace LMLoader.Cli.Pack;

/// <summary>进程执行结果(标准化输出)。</summary>
public sealed record ProcessResult(int ExitCode, string Output)
{
	public bool Success => ExitCode == 0;
}

/// <summary>进程执行抽象(测试注入用,不真跑 Godot)。</summary>
public interface IProcessRunner
{
	ProcessResult Run(string fileName, string arguments);
}

/// <summary>默认进程执行器。</summary>
public sealed class ProcessRunner : IProcessRunner
{
	public static readonly ProcessRunner Instance = new();

	public ProcessResult Run(string fileName, string arguments)
	{
		using var process = Process.Start(new ProcessStartInfo
		{
			FileName = fileName,
			Arguments = arguments,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true,
		}) ?? throw new InvalidOperationException($"无法启动进程: {fileName}");

		var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
		process.WaitForExit();
		return new ProcessResult(process.ExitCode, output);
	}
}

/// <summary>
/// pck 导出(6.4):包装 <c>godot --headless --path &lt;工程&gt; --export-pack &lt;预设&gt; &lt;输出&gt;</c>(D10;
/// 与 AGENTS.md 沉淀的手工命令一致)。Godot 可执行文件路径经参数/环境变量 GODOT_EXECUTABLE 提供。
/// </summary>
public sealed class PckExporter(IProcessRunner? runner = null)
{
	private readonly IProcessRunner _runner = runner ?? ProcessRunner.Instance;

	/// <summary>命令行构造(测试断言用);路径按 Windows/Git Bash 习惯加引号。</summary>
	public static string BuildArguments(string projectPath, string preset, string outputPath) =>
		$"--headless --path \"{projectPath}\" --export-pack \"{preset}\" \"{outputPath}\"";

	public ProcessResult Export(string godotExecutable, string projectPath, string preset, string outputPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(godotExecutable);
		if (!File.Exists(projectPath))
		{
			throw new FileNotFoundException($"Godot 工程文件不存在: {projectPath}", projectPath);
		}

		var fullOutput = Path.GetFullPath(outputPath);
		var outputDir = Path.GetDirectoryName(fullOutput);
		if (!string.IsNullOrEmpty(outputDir))
		{
			Directory.CreateDirectory(outputDir);
		}

		return _runner.Run(godotExecutable, BuildArguments(projectPath, preset, fullOutput));
	}
}
