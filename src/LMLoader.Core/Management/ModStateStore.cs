// SPDX-License-Identifier: LGPL-3.0-or-later

using System.Text;
using LMLoader.Api;
using LMLoader.Core.Logging;

namespace LMLoader.Core.Management;

/// <summary>
/// 模组启停状态文件存取(D19):单份 TOML,路径由宿主提供(见 <see cref="LoaderOptions.ModStatePath"/>,
/// Godot 版为 <c>user://mod-state.toml</c> 的全局化路径),Core 不感知 user:// 语义。
/// 文件布局(顶层键 + [mods] 表;uid 含点号须引号):
///
///   enabled = true
///
///   [mods]
///   "com.author.mod" = false
///
/// 解析失败 → 返回全启用默认态 + 警告,**不写回**(保护用户原稿,D11 同型);写出走临时文件原子替换。
/// </summary>
public sealed class ModStateStore(string path, ILmLogger? logger = null)
{
	/// <summary>读入启停状态;文件缺失或解析失败(已告警)返回默认态。</summary>
	public ModState Load()
	{
		try
		{
			if (!File.Exists(path))
			{
				return new ModState();
			}

			var table = Tomlyn.TomlSerializer.Deserialize<Tomlyn.Model.TomlTable>(File.ReadAllText(path))
				?? throw new InvalidDataException($"状态文件顶层必须是 TOML 表: {path}");
			var state = new ModState();

			if (table.TryGetValue("enabled", out var globalEnabled) && globalEnabled is bool globalValue)
			{
				state.GlobalEnabled = globalValue;
			}
			else if (table.ContainsKey("enabled"))
			{
				logger?.Warn("mod-state: enabled 键非布尔值,按启用处理");
			}

			if (table.TryGetValue("mods", out var modsValue) && modsValue is Tomlyn.Model.TomlTable mods)
			{
				foreach (var (uid, value) in mods)
				{
					if (value is bool enabled)
					{
						state.SetMod(uid, enabled);
					}
					else
					{
						logger?.Warn($"mod-state: \"{uid}\" 的值非布尔,已忽略");
					}
				}
			}

			return state;
		}
		catch (Exception ex)
		{
			// 状态文件损坏属于玩家可造成的常态:宽容降级为全启用,不写回保护原稿
			logger?.Warn($"模组状态文件解析失败,按全部启用处理(不写回): {path}({ex.Message})");
			return new ModState();
		}
	}

	/// <summary>写出启停状态;目录不存在自动创建;临时文件 + 替换保证原子性。全文重建。</summary>
	public void Save(ModState state)
	{
		ArgumentNullException.ThrowIfNull(state);

		var builder = new StringBuilder();
		builder.Append("enabled = ").AppendLine(state.GlobalEnabled ? "true" : "false");
		if (state.Mods.Count > 0)
		{
			builder.AppendLine();
			builder.AppendLine("[mods]");
			foreach (var (uid, enabled) in state.Mods)
			{
				builder.Append('"').Append(uid).Append("\" = ").AppendLine(enabled ? "true" : "false");
			}
		}

		var fullPath = Path.GetFullPath(path);
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
			File.WriteAllText(tempPath, builder.ToString());
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
