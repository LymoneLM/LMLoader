// SPDX-License-Identifier: LGPL-3.0-or-later

namespace LMLoader.Core.Logging;

/// <summary>
/// 文件 sink(追加或截断模式);行格式同控制台但带完整日期。句柄以 ReadWrite 共享打开,
/// 外部工具(日志窗口/tail)可实时读取。IO 失败(文件被占用等)丢弃该条不放大(D7),
/// 后续事件自动重试重开。<see cref="Dispose"/> 供宿主退出前收尾。线程安全。
/// </summary>
public sealed class FileLogSink : ILogSink, IDisposable
{
	private readonly string _filePath;
	private readonly bool _append;
	private readonly object _gate = new();
	private StreamWriter? _writer;
	private bool _disposed;

	public FileLogSink(string filePath, bool append = true)
	{
		if (string.IsNullOrWhiteSpace(filePath))
		{
			throw new ArgumentException("日志文件路径不能为空", nameof(filePath));
		}

		_filePath = filePath;
		_append = append;
	}

	/// <summary>已持久化的事件数(诊断用)。</summary>
	public long EmittedCount { get; private set; }

	public void Emit(LogEvent @event)
	{
		var line = LogTextFormatter.Render(@event, fullDate: true);
		lock (_gate)
		{
			if (_disposed)
			{
				return; // 释放后:静默丢弃
			}

			try
			{
				EnsureWriter();
				_writer!.WriteLine(line);
				_writer.Flush();
				EmittedCount++;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
			{
				DropWriter();
			}
		}
	}

	public void Dispose()
	{
		lock (_gate)
		{
			_disposed = true;
			DropWriter();
		}
	}

	private void EnsureWriter()
	{
		if (_writer is not null)
		{
			return;
		}

		var directory = Path.GetDirectoryName(_filePath);
		if (!string.IsNullOrEmpty(directory))
		{
			Directory.CreateDirectory(directory);
		}

		_writer = new StreamWriter(
			new FileStream(
				_filePath,
				_append ? FileMode.Append : FileMode.Create,
				FileAccess.Write,
				FileShare.ReadWrite))
		{
			AutoFlush = true,
		};
	}

	private void DropWriter()
	{
		try
		{
			_writer?.Dispose();
		}
		catch (ObjectDisposedException)
		{
		}

		_writer = null;
	}
}
