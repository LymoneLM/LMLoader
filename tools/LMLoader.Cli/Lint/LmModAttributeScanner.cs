// SPDX-License-Identifier: LGPL-3.0-or-later

using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace LMLoader.Cli.Lint;

/// <summary>
/// dll 元数据中的 <c>[LmMod("uid")]</c> 特性读取(经 System.Reflection.Metadata,
/// 不加载程序集、不触发类型解析,缺依赖的模组 dll 也可扫描)。
/// 供 lint 将清单 mods[].uid 与代码侧特性 uid 交叉校验(D18 双写一致性)。
/// </summary>
public static class LmModAttributeScanner
{
	/// <summary>读取 dll 内全部 LmMod 特性 uid(按出现顺序,含重复)。文件损坏/非托管 PE 抛异常。</summary>
	public static IReadOnlyList<string> ScanUids(string dllPath)
	{
		using var stream = File.OpenRead(dllPath);
		using var pe = new PEReader(stream);
		if (!pe.HasMetadata)
		{
			throw new BadImageFormatException($"不是有效的托管程序集: {dllPath}");
		}

		var metadata = pe.GetMetadataReader();
		var result = new List<string>();

		foreach (var handle in metadata.CustomAttributes)
		{
			var attribute = metadata.GetCustomAttribute(handle);
			if (attribute.Constructor.Kind != HandleKind.MemberReference ||
				!IsLmModAttributeCtor(metadata, (MemberReferenceHandle)attribute.Constructor))
			{
				continue;
			}

			var value = metadata.GetBlobReader(attribute.Value);
			// 命名参数前的 prolog 固定为 0x01 0x00
			if (value.Length < 2 || value.ReadByte() != 0x01 || value.ReadByte() != 0x00)
			{
				continue;
			}

			// 构造参数为单个字符串(SerString:压缩长度 + UTF8)
			var length = value.ReadCompressedInteger();
			if (length < 0 || length > value.RemainingBytes)
			{
				continue;
			}

			result.Add(value.ReadUTF8(length));
		}

		return result;
	}

	private static bool IsLmModAttributeCtor(MetadataReader metadata, MemberReferenceHandle ctorHandle)
	{
		var ctor = metadata.GetMemberReference(ctorHandle);
		if (ctor.Parent.Kind != HandleKind.TypeReference)
		{
			return false;
		}

		var type = metadata.GetTypeReference((TypeReferenceHandle)ctor.Parent);
		return metadata.GetString(type.Name) == "LmModAttribute" &&
			metadata.GetString(type.Namespace) == "LMLoader.Api";
	}
}
