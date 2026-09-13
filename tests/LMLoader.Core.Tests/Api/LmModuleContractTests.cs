using LMLoader.Api;

namespace LMLoader.Core.Tests.Api;

public class LmModuleContractTests
{
	private sealed class NullLogger : ILmLogger
	{
		public List<(LmLogLevel Level, string Message)> Calls { get; } = new();

		public void Log(LmLogLevel level, string message, Exception? exception = null) =>
			Calls.Add((level, message));
	}

	private sealed class EmptyModule : LmModule { }

	private static LmModuleContext NewContext(ILmLogger? logger = null) =>
		new("com.test.mod.main", "com.test.mod", @"C:\mods\test", logger ?? new NullLogger());

	[Fact]
	public void Context_未注入时访问抛出()
	{
		var module = new EmptyModule();
		Assert.Throws<InvalidOperationException>(() => _ = module.Context);
	}

	[Fact]
	public void Attach_注入后可读Uid与Logger_重复注入抛出()
	{
		var module = new EmptyModule();
		var context = NewContext();
		module.Attach(context);

		Assert.Equal("com.test.mod.main", module.Uid);
		Assert.Same(context, module.Context);
		Assert.Throws<InvalidOperationException>(() => module.Attach(NewContext()));
	}

	[Fact]
	public void 默认生命周期钩子为空操作可直接调用()
	{
		var module = new EmptyModule();
		module.Attach(NewContext());

		module.OnPreLoad();
		module.OnLoad();
		module.OnPostLoad();
	}

	[Fact]
	public void ILmLogger_默认接口方法路由到Log()
	{
		ILmLogger logger = new NullLogger();

		logger.Trace("t");
		logger.Debug("d");
		logger.Info("i");
		logger.Warn("w");
		logger.Error("e");

		Assert.Equal(
			[LmLogLevel.Trace, LmLogLevel.Debug, LmLogLevel.Info, LmLogLevel.Warning, LmLogLevel.Error],
			((NullLogger)logger).Calls.Select(c => c.Level));
	}
}
