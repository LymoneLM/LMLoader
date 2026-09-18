// SPDX-License-Identifier: LGPL-3.0-or-later

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace LMLoader.Core.Tests.TestInfrastructure;

/// <summary>
/// 测试夹具编译器:运行时把 C# 源码编译为 dll(Roslyn),
/// 供 ALC 加载与生命周期集成测试构造"真实模组程序集"。
/// </summary>
public static class TestCompiler
{
	public static string CompileToDirectory(
		string outputDirectory,
		string assemblyName,
		string source,
		params string[] referencedDlls)
	{
		Directory.CreateDirectory(outputDirectory);

		var syntaxTree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));

		// 以当前进程已加载的非动态程序集为基准引用集(含 System.Runtime facade 等),
		// 使夹具可用任意框架类型;重复路径去重。
		// 注意:非收集式共享 ALC 加载过的模组程序集会留在 GetAssemblies() 里,其临时目录
		// 在测试结束后可能已删除(Linux 可删除已打开文件;Windows 因文件锁残留目录,
		// 恰好掩盖过此问题)——必须过滤死路径。
		var references = AppDomain.CurrentDomain.GetAssemblies()
			.Where(a => !a.IsDynamic && a.Location.Length > 0)
			.Select(a => a.Location)
			.Concat(referencedDlls)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.Where(File.Exists)
			.Select(d => MetadataReference.CreateFromFile(d))
			.ToList();

		var compilation = CSharpCompilation.Create(
			assemblyName,
			new[] { syntaxTree },
			references,
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

		var path = Path.Combine(outputDirectory, assemblyName + ".dll");
		using (var stream = File.Create(path))
		{
			var result = compilation.Emit(stream);
			if (!result.Success)
			{
				var errors = string.Join("; ", result.Diagnostics
					.Where(d => d.Severity == DiagnosticSeverity.Error)
					.Select(d => d.GetMessage()));
				throw new InvalidOperationException($"测试夹具 \"{assemblyName}\" 编译失败: {errors}");
			}
		}

		return path;
	}
}
