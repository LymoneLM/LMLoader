// SPDX-License-Identifier: LGPL-3.0-or-later

namespace LMLoader.Core.Logging;

/// <summary>
/// 内存环形缓冲 sink(D6):固定容量,超限丢弃最旧;供日志窗口/崩溃回放读取最近事件。线程安全。
/// </summary>
public sealed class RingBufferLogSink : ILogSink
{
	private readonly LogEvent[] _buffer;
	private readonly object _gate = new();
	private int _head;
	private int _count;

	public RingBufferLogSink(int capacity = 512)
	{
		if (capacity <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(capacity), "环形缓冲容量必须为正");
		}

		_buffer = new LogEvent[capacity];
	}

	/// <summary>快照(最旧 → 最新);返回副本,与内部状态解耦。</summary>
	public LogEvent[] Snapshot()
	{
		lock (_gate)
		{
			var result = new LogEvent[_count];
			for (var i = 0; i < _count; i++)
			{
				result[i] = _buffer[(_head + i) % _buffer.Length];
			}

			return result;
		}
	}

	public void Emit(LogEvent @event)
	{
		lock (_gate)
		{
			_buffer[(_head + _count) % _buffer.Length] = @event;
			if (_count < _buffer.Length)
			{
				_count++;
			}
			else
			{
				_head = (_head + 1) % _buffer.Length;
			}
		}
	}
}
