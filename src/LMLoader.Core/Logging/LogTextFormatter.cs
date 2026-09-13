// SPDX-License-Identifier: LGPL-3.0-or-later

namespace LMLoader.Core.Logging;

/// <summary>文本 sink 共用行格式:[时间] [LEVEL] [modUid] 消息(异常串换行追加)。</summary>
internal static class LogTextFormatter
{
	public static string Render(LogEvent @event, bool fullDate)
	{
		var timestamp = fullDate
			? @event.Timestamp.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture)
			: @event.Timestamp.LocalDateTime.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
		var line =
			$"[{timestamp}] [{@event.Level.ToString().ToUpperInvariant()}] [{@event.ModUid}] {@event.Message}";
		return @event.Exception is null ? line : line + Environment.NewLine + @event.Exception;
	}
}
