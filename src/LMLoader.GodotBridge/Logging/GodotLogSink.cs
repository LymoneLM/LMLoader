// SPDX-License-Identifier: LGPL-3.0-or-later

using Godot;
using LMLoader.Api;
using LMLoader.Core.Logging;

namespace LMLoader.GodotBridge.Logging;

/// <summary>日志 sink:路由到 Godot 控制台(GD.Print / PushWarning / PushError),编辑器输出可见。</summary>
public sealed class GodotLogSink : ILogSink
{
	public void Emit(LogEvent @event)
	{
		var line = $"[LMLoader][{@event.ModUid}] {@event.Message}";
		if (@event.Exception is not null)
		{
			line += System.Environment.NewLine + @event.Exception;
		}

		switch (@event.Level)
		{
			case LmLogLevel.Error:
				GD.PushError(line);
				break;
			case LmLogLevel.Warning:
				GD.PushWarning(line);
				break;
			default:
				GD.Print(line);
				break;
		}
	}
}
