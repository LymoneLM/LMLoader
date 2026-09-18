// SPDX-License-Identifier: LGPL-3.0-or-later

namespace LMLoader.Core.Logging;

/// <summary>
/// 日志 sink SPI(多 sink——控制台/文件/内存环形缓冲;实现须自行保证线程安全)。
/// </summary>
public interface ILogSink
{
	void Emit(LogEvent @event);
}
