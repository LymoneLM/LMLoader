// SPDX-License-Identifier: LGPL-3.0-or-later

using Tomlyn;
using Tomlyn.Model;

namespace LMLoader.Core.Config;

/// <summary>
/// TOML 配置文件存取:每个模组一份 <c>&lt;配置根目录&gt;/&lt;modUid&gt;.toml</c>。
/// 根目录由宿主提供(见 <see cref="LoaderOptions.ConfigRootPath"/>),Core 不感知 user:// 语义。
/// 文件缺失视为空配置(首次运行常态);解析错误上抛,由上层宽容处置。
/// </summary>
public class TomlConfigStore
{
	/// <summary>模组配置文件路径约定;uid 经清单校验为反向域名,此处仅防御路径分隔符。</summary>
	public static string GetConfigFilePath(string rootPath, string modUid)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
		ArgumentException.ThrowIfNullOrWhiteSpace(modUid);
		if (modUid.Contains('/') || modUid.Contains('\\'))
		{
			throw new ArgumentException($"modUid \"{modUid}\" 含路径分隔符,不能用作配置文件名", nameof(modUid));
		}

		return Path.Combine(rootPath, modUid + ".toml");
	}

	/// <summary>读入配置表;文件不存在返回空表。</summary>
	/// <exception cref="Exception">文件存在但不是合法 TOML(Tomlyn 2.x 解析异常类型不固定,交由上层宽容处置)。</exception>
	public virtual TomlTable Read(string filePath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
		if (!File.Exists(filePath))
		{
			return new TomlTable();
		}

		return TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(filePath))
			?? throw new InvalidDataException($"配置文件顶层必须是 TOML 表: {filePath}");
	}

	/// <summary>
	/// 写出配置表;目录不存在自动创建。临时文件 + 替换保证原子性(热重载监听/游戏读档
	/// 不会读到半截文件)。注意:重建全文,用户手写注释不保留(迭代空间)。
	/// </summary>
	public virtual void Write(string filePath, TomlTable table)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
		ArgumentNullException.ThrowIfNull(table);

		var fullPath = Path.GetFullPath(filePath);
		var directory = Path.GetDirectoryName(fullPath);
		if (!string.IsNullOrEmpty(directory))
		{
			Directory.CreateDirectory(directory);
		}

		var tempPath = Path.Combine(
			Path.GetDirectoryName(fullPath) ?? ".",
			$".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
		try
		{
			File.WriteAllText(tempPath, TomlSerializer.Serialize(table));
			File.Move(tempPath, fullPath, overwrite: true);
		}
		finally
		{
			if (File.Exists(tempPath))
			{
				File.Delete(tempPath);
			}
		}
	}
}
