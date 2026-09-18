// SPDX-License-Identifier: LGPL-3.0-or-later

using LMLoader.Core.Manifest;
using LMLoader.Core.Versioning;

namespace LMLoader.Core.Dependency;

/// <summary>
/// 依赖图构建 + 模块级拓扑排序(草稿"依赖与加载顺序";依赖唯一来源为 mod.json,D2)。
/// 语义:
/// - 以模块为节点(同一 Assembly 内的模块也互为节点);
/// - 硬依赖缺失/版本不满足 → 该模块放弃加载,并级联跳过硬依赖它的模块;
/// - 软依赖仅约束顺序:目标存在则保证排在其后,目标缺失/失败则解除约束;
/// - 循环依赖 → 整批拒绝,输出完整依赖链;
/// - 排序并列时以 UID 字典序 tie-break(D8),结果确定可复现;
/// - 依赖版本 v1 仅精确匹配(区间语法阶段 5);模块依赖版本与其所属模组版本比对。
/// </summary>
public static class DependencyPlanner
{
	public static LoadPlan Plan(IReadOnlyList<ModManifest> manifests)
	{
		var skipped = new List<SkippedModule>();
		var removalCause = new Dictionary<string, string>(StringComparer.Ordinal);

		// ---- 1. 收集节点;模块 uid 冲突的双方均放弃加载 ----
		var allNodes = new List<Node>();
		var byUid = new Dictionary<string, List<Node>>(StringComparer.Ordinal);

		foreach (var mod in manifests)
		{
			foreach (var module in mod.Modules)
			{
				var node = new Node(mod, module);
				allNodes.Add(node);

				if (!byUid.TryGetValue(module.Uid, out var list))
				{
					byUid[module.Uid] = list = new List<Node>();
				}

				list.Add(node);
			}
		}

		foreach (var duplicate in byUid.Values.Where(l => l.Count > 1).SelectMany(l => l))
		{
			removalCause[duplicate.Module.Uid] = "模块 uid 冲突";
			skipped.Add(new SkippedModule
			{
				ModuleUid = duplicate.Module.Uid,
				ModUid = duplicate.Mod.Uid,
				Reason = $"模块 uid \"{duplicate.Module.Uid}\" 冲突:与其他模组声明了相同模块 uid,双方均放弃加载",
			});
		}

		var active = allNodes
			.Where(n => !removalCause.ContainsKey(n.Module.Uid))
			.ToDictionary(n => n.Module.Uid, StringComparer.Ordinal);

		// ---- 2. 硬依赖校验 + 级联消除(不动点迭代) ----
		var progressed = true;
		while (progressed)
		{
			progressed = false;

			foreach (var uid in active.Keys.OrderBy(u => u, StringComparer.Ordinal).ToArray())
			{
				if (!active.TryGetValue(uid, out var node))
				{
					continue; // 本轮迭代中已被级联跳过
				}

				foreach (var dependency in node.Module.Depends.Where(d => !d.Soft))
				{
					if (!byUid.TryGetValue(dependency.Uid, out _))
					{
						Fail(active, removalCause, skipped, node,
							$"硬依赖 \"{dependency.Uid}\" 未提供(无任何模组声明该模块)");
						progressed = true;
						break;
					}

					if (!active.TryGetValue(dependency.Uid, out var target))
					{
						var cause = removalCause.TryGetValue(dependency.Uid, out var c) ? c : "已被跳过";
						Fail(active, removalCause, skipped, node,
							$"硬依赖 \"{dependency.Uid}\" 未加载({cause}),级联跳过");
						progressed = true;
						break;
					}

					// 阶段 5:区间语义优先(Range 非空);裸精确声明走 Version 字段(旧清单兼容)
					if (dependency.Range is { } range && !range.Contains(target.Mod.Version))
					{
						Fail(active, removalCause, skipped, node,
							$"硬依赖 \"{dependency.Uid}\" 版本不满足:声明 {range},实际 {target.Mod.Version}");
						progressed = true;
						break;
					}

					if (dependency.Range is null && dependency.Version is { } required && required != target.Mod.Version)
					{
						Fail(active, removalCause, skipped, node,
							$"硬依赖 \"{dependency.Uid}\" 版本不满足:声明 {required},实际 {target.Mod.Version}");
						progressed = true;
						break;
					}
				}
			}
		}

		// ---- 3. 构图:硬依赖必已满足;软依赖仅在目标存活时约束顺序 ----
		foreach (var node in active.Values)
		{
			foreach (var dependency in node.Module.Depends)
			{
				if (active.TryGetValue(dependency.Uid, out var target))
				{
					target.Dependents.Add(node);
					node.Remaining++;
				}
			}
		}

		// ---- 4. Kahn 拓扑排序,UID 字典序 tie-break(D8) ----
		var ready = new PriorityQueue<Node, string>(Comparer<string>.Create(string.CompareOrdinal));
		foreach (var node in active.Values.Where(n => n.Remaining == 0))
		{
			ready.Enqueue(node, node.Module.Uid);
		}

		var ordered = new List<ModulePlanItem>();
		while (ready.TryDequeue(out var node, out _))
		{
			ordered.Add(new ModulePlanItem
			{
				ModuleUid = node.Module.Uid,
				ModUid = node.Mod.Uid,
				Mod = node.Mod,
				Module = node.Module,
			});

			foreach (var dependent in node.Dependents)
			{
				dependent.Remaining--;
				if (dependent.Remaining == 0)
				{
					ready.Enqueue(dependent, dependent.Module.Uid);
				}
			}
		}

		// ---- 5. 残留节点 = 存在环 → 整批拒绝并输出完整依赖链 ----
		if (ordered.Count < active.Count)
		{
			var leftover = active.Values.Where(n => n.Remaining > 0)
				.OrderBy(n => n.Module.Uid, StringComparer.Ordinal)
				.ToList();
			var chain = DescribeCycle(leftover);

			foreach (var node in active.Values)
			{
				skipped.Add(new SkippedModule
				{
					ModuleUid = node.Module.Uid,
					ModUid = node.Mod.Uid,
					Reason = $"存在循环依赖,整批拒绝加载:{chain}",
				});
			}

			return new LoadPlan
			{
				Ordered = Array.Empty<ModulePlanItem>(),
				Skipped = skipped,
				BatchRejected = true,
				BatchRejectReason = chain,
			};
		}

		return new LoadPlan { Ordered = ordered, Skipped = skipped };
	}

	private static void Fail(
		Dictionary<string, Node> active,
		Dictionary<string, string> removalCause,
		List<SkippedModule> skipped,
		Node node,
		string reason)
	{
		if (!active.Remove(node.Module.Uid))
		{
			return;
		}

		removalCause[node.Module.Uid] = reason;
		skipped.Add(new SkippedModule
		{
			ModuleUid = node.Module.Uid,
			ModUid = node.Mod.Uid,
			Reason = reason,
		});
	}

	/// <summary>沿"第一个仍存活的依赖"行走,输出 A → B → … → A 形式的完整环链(尾部非环节点会被剔除)。</summary>
	private static string DescribeCycle(List<Node> leftover)
	{
		var index = leftover.ToDictionary(n => n.Module.Uid, StringComparer.Ordinal);
		var path = new List<string>();
		var positionByUid = new Dictionary<string, int>(StringComparer.Ordinal);

		// 残留节点必拥有指向残留节点的依赖边(否则入度已被减至 0),行走必然成环
		var current = leftover[0];
		while (!positionByUid.ContainsKey(current.Module.Uid))
		{
			positionByUid[current.Module.Uid] = path.Count;
			path.Add(current.Module.Uid);

			var nextUid = current.Module.Depends
				.Select(d => d.Uid)
				.First(uid => index.ContainsKey(uid));
			current = index[nextUid];
		}

		var cycle = path.Skip(positionByUid[current.Module.Uid]).ToList();
		cycle.Add(current.Module.Uid);
		return string.Join(" → ", cycle);
	}

	private sealed class Node
	{
		public Node(ModManifest mod, ModuleEntry module)
		{
			Mod = mod;
			Module = module;
		}

		public ModManifest Mod { get; }

		public ModuleEntry Module { get; }

		public List<Node> Dependents { get; } = new();

		/// <summary>Kahn 入度:尚未满足的硬+软边数。</summary>
		public int Remaining { get; set; }
	}
}
