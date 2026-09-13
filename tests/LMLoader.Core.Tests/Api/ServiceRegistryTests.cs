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

		registry.Register<IGreeter>("com.a.mod", new GreeterA());

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

		registry.Register<IGreeter>("com.a.mod", new GreeterA());
		registry.Register<IGreeter>("com.b.mod", new GreeterB());

		Assert.True(registry.TryGet<IGreeter>(out var service));
		Assert.Equal("B", service.Greet());
	}

	[Fact]
	public void 同所有者重复注册_替换原实例()
	{
		var registry = new ServiceRegistry();

		registry.Register<IGreeter>("com.a.mod", new GreeterA());
		registry.Register<IGreeter>("com.a.mod", new GreeterB());

		Assert.True(registry.TryGet<IGreeter>(out var latest));
		Assert.Equal("B", latest.Greet());
		Assert.Single(registry.GetAll<IGreeter>());
	}

	[Fact]
	public void 按所有者取回()
	{
		var registry = new ServiceRegistry();

		registry.Register<IGreeter>("com.a.mod", new GreeterA());
		registry.Register<IGreeter>("com.b.mod", new GreeterB());

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

		registry.Register<IGreeter>("com.b.mod", new GreeterB());
		registry.Register<IGreeter>("com.a.mod", new GreeterA());

		Assert.Equal(["B", "A"], registry.GetAll<IGreeter>().Select(g => g.Greet()));
	}

	[Fact]
	public void 键为调用处声明的泛型类型()
	{
		var registry = new ServiceRegistry();

		IGreeter asInterface = new GreeterA();
		registry.Register("com.a.mod", asInterface); // 泛型推断:T = IGreeter

		Assert.True(registry.TryGet<IGreeter>(out _));
		Assert.False(registry.TryGet<GreeterA>(out _)); // 未按具体类型注册过

		registry.Register("com.b.mod", new GreeterB()); // 泛型推断:T = GreeterB

		Assert.True(registry.TryGet<GreeterB>(out _));
	}

	[Fact]
	public void 非法参数被拒绝()
	{
		var registry = new ServiceRegistry();

		Assert.Throws<ArgumentNullException>(() => registry.Register<IGreeter>("com.a.mod", null!));
		Assert.Throws<ArgumentException>(() => registry.Register<IGreeter>("", new GreeterA()));
	}
}
