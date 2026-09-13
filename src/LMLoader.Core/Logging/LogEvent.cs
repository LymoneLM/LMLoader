// SPDX-License-Identifier: LGPL-3.0-or-later

using LMLoader.Api;

namespace LMLoader.Core.Logging;

/// <summary>流向 sink 的一条日志事件。</summary>
public readonly record struct LogEvent(
	DateTimeOffset Timestamp,
	LmLogLevel Level,
	string ModUid,
	string Message,
	Exception? Exception);
