// SPDX-License-Identifier: LGPL-3.0-or-later

namespace LMLoader.Core.Manifest;

/// <summary>
/// 清单读取结果(D7 宽松语义:错误累积返回而非抛出,由调用方决定跳过模组并记录原因)。
/// </summary>
public sealed class ManifestReadResult
{
	public ModManifest? Manifest { get; }

	/// <summary>致命错误;非空时 <see cref="Manifest"/> 为 null,该模组应被跳过。</summary>
	public IReadOnlyList<string> Errors { get; }

	/// <summary>非致命警告(如未知字段被忽略)。</summary>
	public IReadOnlyList<string> Warnings { get; }

	public bool Success => Manifest is not null;

	private ManifestReadResult(ModManifest? manifest, IReadOnlyList<string> errors, IReadOnlyList<string> warnings)
	{
		Manifest = manifest;
		Errors = errors;
		Warnings = warnings;
	}

	public static ManifestReadResult Ok(ModManifest manifest, IReadOnlyList<string> warnings) =>
		new(manifest, Array.Empty<string>(), warnings);

	public static ManifestReadResult Fail(IReadOnlyList<string> errors, IReadOnlyList<string> warnings) =>
		new(null, errors, warnings);
}
