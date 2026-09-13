using LMLoader.Api;
using LMLoader.Core.Logging;

namespace LMLoader.Core.Tests.Logging;

public class LoggerRouterTests
{
	private sealed class RecordingSink : ILogSink
	{
		public List<LogEvent> Events { get; } = new();

		public void Emit(LogEvent @event) => Events.Add(@event);
	}

	[Fact]
	public void 事件路由到全部sink_字段完整()
	{
		var sinkA = new RecordingSink();
		var sinkB = new RecordingSink();
		var router = new LoggerRouter(sinkA, sinkB);

		router.GetLogger("com.a.mod").Info("hello");

		Assert.Single(sinkA.Events);
		Assert.Single(sinkB.Events);
		var @event = sinkA.Events[0];
		Assert.Equal(LmLogLevel.Info, @event.Level);
		Assert.Equal("com.a.mod", @event.ModUid);
		Assert.Equal("hello", @event.Message);
		Assert.Null(@event.Exception);
		Assert.True(@event.Timestamp <= DateTimeOffset.Now);
	}

	[Fact]
	public void 低于最低级别的事件被丢弃()
	{
		var sink = new RecordingSink();
		var router = new LoggerRouter(sink) { MinimumLevel = LmLogLevel.Warning };

		router.GetLogger("com.a.mod").Debug("debug 不可见");
		router.GetLogger("com.a.mod").Info("info 不可见");
		router.GetLogger("com.a.mod").Warn("warn 可见");
		router.GetLogger("com.a.mod").Error("error 可见");

		Assert.Equal(
			[LmLogLevel.Warning, LmLogLevel.Error],
			sink.Events.Select(e => e.Level));
	}

	[Fact]
	public void 异常随事件传递()
	{
		var sink = new RecordingSink();
		var router = new LoggerRouter(sink);
		var exception = new InvalidOperationException("boom");

		router.GetLogger("com.a.mod").Error("failed", exception);

		Assert.Same(exception, Assert.Single(sink.Events).Exception);
	}

	[Fact]
	public void 同uid复用同一logger实例()
	{
		var router = new LoggerRouter();

		var loggerA = router.GetLogger("com.a.mod");
		var loggerB = router.GetLogger("com.a.mod");

		Assert.Same(loggerA, loggerB);
	}

	[Fact]
	public void 控制台sink格式包含级别模块与时间()
	{
		var buffer = new StringWriter();
		var sink = new ConsoleLogSink(buffer);
		// 用本地偏移构造,断言本地时间渲染
		var localOffset = TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 14));
		var timestamp = new DateTimeOffset(2026, 9, 14, 12, 30, 45, 123, localOffset);

		sink.Emit(new LogEvent(timestamp, LmLogLevel.Warning, "com.a.mod", "something odd", null));

		var line = buffer.ToString().TrimEnd();
		Assert.StartsWith("[12:30:45.123] [WARNING] [com.a.mod] something odd", line);
	}

	[Fact]
	public void 控制台sink_异常输出追加其后()
	{
		var buffer = new StringWriter();
		var sink = new ConsoleLogSink(buffer);

		sink.Emit(new LogEvent(
			new DateTimeOffset(2026, 9, 14, 12, 30, 45, 123, TimeSpan.Zero),
			LmLogLevel.Error, "com.a.mod", "crashed", new InvalidOperationException("boom")));

		var text = buffer.ToString();
		Assert.Contains("[com.a.mod] crashed", text);
		Assert.Contains("InvalidOperationException: boom", text);
	}
}
