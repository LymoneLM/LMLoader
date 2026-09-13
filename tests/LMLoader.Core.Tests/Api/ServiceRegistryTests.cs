using LMLoader.Api;

namespace LMLoader.Core.Tests.Api;

public class ServiceRegistryTests
{
	public interface IGreeter
	{
		string Greet();
	}

	private sealed class GreeterA : IGreeter
	{
		public string Greet() => "A";
	}

	private sealed class GreeterB : IGreeter
	{
		public string Greet() => "B";
	}

	[Fact]
	public void 注册后可取回()
	{
		var registry = new ServiceRegistry();

		registry.Register("com.a.mod", new GreeterA());

		Assert.True(registry.TryGet<IGreeter>(out var service));
		Assert.Equal("A", service.Greet());
	}

	[Fact]
	public void 未注册时TryGet返回false()
	{
		var registry = new ServiceRegistry();

		Assert.False(registry.TryGet<IGreeter>(out var service));
		Assert.Null(service);
	}

	[Fact]
	public void 同类型多次注册_最后注册者胜()
	{
		var registry = new ServiceRegistry();

		registry.Register("com.a.mod", new GreeterA());
		registry.Register("com.b.mod", new GreeterB());

		Assert.True(registry.TryGet<IGreeter>(out var service));
		Assert.Equal("B", service.Greet());
	}

	[Fact]
	public void 同所有者重复注册_替换原实例()
	{
		var registry = new ServiceRegistry();

		registry.Register("com.a.mod", new GreeterA());
		registry.Register("com.a.mod", new GreeterB());

		Assert.True(registry.TryGet<IGreeter>(out var latest));
		Assert.Equal("B", latest.Greet());
		Assert.Single(registry.GetAll<IGreeter>());
	}

	[Fact]
	public void 按所有者取回()
	{
		var registry = new ServiceRegistry();

		registry.Register("com.a.mod", new GreeterA());
		registry.Register("com.b.mod", new GreeterB());

		Assert.True(registry.TryGet<IGreeter>("com.a.mod", out var a));
		Assert.Equal("A", a.Greet());
		Assert.True(registry.TryGet<IGreeter>("com.b.mod", out var b));
		Assert.Equal("B", b.Greet());
		Assert.False(registry.TryGet<IGreeter>("com.ghost.mod", out _));
	}

	[Fact]
	public void GetAll_按注册顺序返回()
	{
		var registry = new ServiceRegistry();

		registry.Register("com.b.mod", new GreeterB());
		registry.Register("com.a.mod", new GreeterA());

		Assert.Equal(["B", "A"], registry.GetAll<IGreeter>().Select(g => g.Greet()));
	}

	[Fact]
	public void 具体类型与接口类型按声明类型分别成键()
	{
		var registry = new ServiceRegistry();
		var greeter = new GreeterA();

		registry.Register("com.a.mod", greeter);

		Assert.True(registry.TryGet<IGreeter>(out _));
		Assert.True(registry.TryGet<GreeterA>(out _));
		Assert.False(registry.TryGet<GreeterB>(out _));
	}

	[Fact]
	public void 非法参数被拒绝()
	{
		var registry = new ServiceRegistry();

		Assert.Throws<ArgumentNullException>(() => registry.Register<IGreeter>("com.a.mod", null!));
		Assert.Throws<ArgumentException>(() => registry.Register<IGreeter>("", new GreeterA()));
	}
}
