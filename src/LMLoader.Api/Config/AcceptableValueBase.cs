// SPDX-License-Identifier: LGPL-3.0-or-later

namespace LMLoader.Api.Config;

/// <summary>
/// 配置项取值约束基类(D11 元数据,为 GUI 配置面板预留):范围或枚举列表。
/// 文件读入路径上,范围越界做钳制、列表外值回退默认;代码直接赋值 <see cref="ConfigEntry{T}.Value"/>
/// 时范围同样钳制,列表外值视为编程错误直接抛出。
/// </summary>
public abstract class AcceptableValueBase
{
	/// <summary>约束适用的绑定类型(必须与 <c>Bind&lt;T&gt;</c> 的 T 一致)。</summary>
	public abstract Type ValueType { get; }

	/// <summary>人可读约束描述(警告与 GUI 用)。</summary>
	public abstract string Describe();

	/// <summary>
	/// 把装箱值收敛进合法域:范围约束钳制到边界,列表约束对非法值返回 null(调用方按 D11 回退默认)。
	/// 值类型与 <see cref="ValueType"/> 不符返回 null。
	/// </summary>
	internal abstract object? CoerceBoxed(object value);
}
