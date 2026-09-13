// SPDX-License-Identifier: LGPL-3.0-or-later

namespace LMLoader.Core.Logging;

/// <summary>控制台 sink;格式:[HH:mm:ss.fff] [LEVEL] [modUid] 消息(+异常串)。</summary>
public sealed class ConsoleLogSink : ILogSink
{
	private readonly TextWriter _writer;

	public ConsoleLogSink()
		: this(Console.Out)
	{
	}

	/// <summary>注入 writer 供测试或重定向。</summary>
	public ConsoleLogSink(TextWriter writer)
	{
		_writer = writer ?? throw new ArgumentNullException(nameof(writer));
	}

	public void Emit(LogEvent @event)
	{
		lock (_writer)
		{
			_writer.WriteLine(LogTextFormatter.Render(@event, fullDate: false));
		}
	}
}
