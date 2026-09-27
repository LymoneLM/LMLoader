// SPDX-License-Identifier: LGPL-3.0-or-later

using LMLoader.Core.Manifest;
using LMLoader.Core.Versioning;

namespace LMLoader.Core.Dependency;

/// <summary>
/// 依赖图构建 + 模组级拓扑排序(依赖唯一来源为 mod.json 的 mods[].depends)。
/// 语义(D18):
/// - 以模组为节点(清单内多模组也互为节点),uid 跨清单全局唯一;
/// - uid 冲突:同文件内重复(reader 已整份拒绝,此处防御性兜底)双方均跳过;
///   跨文件重复 → 先扫描者胜(主根先于附加根扫描),后者跳过、前者照常;
/// - 硬依赖缺失/版本不满足 → 该模组放弃加载,并级联跳过硬依赖它的模组;
/// - 软依赖仅约束顺序:目标存在则保证排在其后,目标缺失/失败则解除约束;
/// - 循环依赖 → 整批拒绝,输出完整依赖链;
/// - 排序并列时以 UID 字典序 tie-break,结果确定可复现;
/// - 依赖版本按区间语义对目标模组版本判定。
/// </summary>
public static class DependencyPlanner
{
	public static LoadPlan Plan(IReadOnlyList<ModManifest> manifests)
	{
		var skipped = new List<SkippedMod>();
		var removalCause = new Dictionary<string, string>(StringComparer.Ordinal);

		// ---- 1. 收集节点;uid 冲突:跨文件先扫描者胜,同文件内双方均跳过 ----
		var allNodes = new List<Node>();
		var byUid = new Dictionary<string, List<Node>>(StringComparer.Ordinal);

		foreach (var manifest in manifests)
		{
			foreach (var entry in manifest.Mods)
			{
				var node = new Node(manifest, entry);
				allNodes.Add(node);

				if (!byUid.TryGetValue(entry.Uid, out var list))
				{
					byUid[entry.Uid] = list = new List<Node>();
				}

				list.Add(node);
			}
		}

		// 移除按节点身份标记(胜者与败者同 uid,不能按 uid 键过滤,否则胜者被误杀)
		var removedNodes = new HashSet<Node>();
		foreach (var group in byUid.Values.Where(l => l.Count > 1))
		{
			// manifests 已按扫描顺序排列(主根在前):跨文件冲突取首扫描者;
			// 首者与其他副本同文件(reader 已拒整份,防御兜底)→ 双方均跳过
			var winner = group[0];
			var sameFile = group.Skip(1).Any(n =>
				string.Equals(n.Manifest.SourcePath, winner.Manifest.SourcePath, StringComparison.Ordinal));

			foreach (var duplicate in group)
			{
				if (!sameFile && ReferenceEquals(duplicate, winner))
				{
					continue; // 胜者照常加载
				}

				removedNodes.Add(duplicate);
				var reason = sameFile
					? $"模组 uid \"{duplicate.Entry.Uid}\" 在清单 \"{duplicate.Manifest.SourcePath}\" 内重复定义,双方均放弃加载"
					: $"模组 uid \"{duplicate.Entry.Uid}\" 冲突:已由先扫描的清单 \"{winner.Manifest.SourcePath}\" 提供,本副本跳过(主根优先)";
				skipped.Add(new SkippedMod { Uid = duplicate.Entry.Uid, Reason = reason });
			}
		}

		var active = allNodes
			.Where(n => !removedNodes.Contains(n))
			.ToDictionary(n => n.Entry.Uid, StringComparer.Ordinal);

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

				foreach (var dependency in node.Entry.Depends.Where(d => !d.Soft))
				{
					if (!byUid.TryGetValue(dependency.Uid, out _))
					{
						Fail(active, removalCause, skipped, node,
							$"硬依赖 \"{dependency.Uid}\" 未提供(无任何清单声明该模组)");
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

					// 区间语义优先(Range 非空);裸精确声明走 Version 字段
					if (dependency.Range is { } range && !range.Contains(target.Entry.Version))
					{
						Fail(active, removalCause, skipped, node,
							$"硬依赖 \"{dependency.Uid}\" 版本不满足:声明 {range},实际 {target.Entry.Version}");
						progressed = true;
						break;
					}

					if (dependency.Range is null && dependency.Version is { } required && required != target.Entry.Version)
					{
						Fail(active, removalCause, skipped, node,
							$"硬依赖 \"{dependency.Uid}\" 版本不满足:声明 {required},实际 {target.Entry.Version}");
						progressed = true;
						break;
					}
				}
			}
		}

		// ---- 3. 构图:硬依赖必已满足;软依赖仅在目标存活时约束顺序 ----
		foreach (var node in active.Values)
		{
			foreach (var dependency in node.Entry.Depends)
			{
				if (active.TryGetValue(dependency.Uid, out var target))
				{
					target.Dependents.Add(node);
					node.Remaining++;
				}
			}
		}

		// ---- 4. Kahn 拓扑排序,UID 字典序 tie-break ----
		var ready = new PriorityQueue<Node, string>(Comparer<string>.Create(string.CompareOrdinal));
		foreach (var node in active.Values.Where(n => n.Remaining == 0))
		{
			ready.Enqueue(node, node.Entry.Uid);
		}

		var ordered = new List<ModPlanItem>();
		while (ready.TryDequeue(out var node, out _))
		{
			ordered.Add(new ModPlanItem
			{
				Uid = node.Entry.Uid,
				Manifest = node.Manifest,
				Entry = node.Entry,
			});

			foreach (var dependent in node.Dependents)
			{
				dependent.Remaining--;
				if (dependent.Remaining == 0)
				{
					ready.Enqueue(dependent, dependent.Entry.Uid);
				}
			}
		}

		// ---- 5. 残留节点 = 存在环 → 整批拒绝并输出完整依赖链 ----
		if (ordered.Count < active.Count)
		{
			var leftover = active.Values.Where(n => n.Remaining > 0)
				.OrderBy(n => n.Entry.Uid, StringComparer.Ordinal)
				.ToList();
			var chain = DescribeCycle(leftover);

			foreach (var node in active.Values)
			{
				skipped.Add(new SkippedMod
				{
					Uid = node.Entry.Uid,
					Reason = $"存在循环依赖,整批拒绝加载:{chain}",
				});
			}

			return new LoadPlan
			{
				Ordered = Array.Empty<ModPlanItem>(),
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
		List<SkippedMod> skipped,
		Node node,
		string reason)
	{
		if (!active.Remove(node.Entry.Uid))
		{
			return;
		}

		removalCause[node.Entry.Uid] = reason;
		skipped.Add(new SkippedMod { Uid = node.Entry.Uid, Reason = reason });
	}

	/// <summary>沿"第一个仍存活的依赖"行走,输出 A → B → … → A 形式的完整环链(尾部非环节点会被剔除)。</summary>
	private static string DescribeCycle(List<Node> leftover)
	{
		var index = leftover.ToDictionary(n => n.Entry.Uid, StringComparer.Ordinal);
		var path = new List<string>();
		var positionByUid = new Dictionary<string, int>(StringComparer.Ordinal);

		// 残留节点必拥有指向残留节点的依赖边(否则入度已被减至 0),行走必然成环
		var current = leftover[0];
		while (!positionByUid.ContainsKey(current.Entry.Uid))
		{
			positionByUid[current.Entry.Uid] = path.Count;
			path.Add(current.Entry.Uid);

			var nextUid = current.Entry.Depends
				.Select(d => d.Uid)
				.First(uid => index.ContainsKey(uid));
			current = index[nextUid];
		}

		var cycle = path.Skip(positionByUid[current.Entry.Uid]).ToList();
		cycle.Add(current.Entry.Uid);
		return string.Join(" → ", cycle);
	}

	private sealed class Node
	{
		public Node(ModManifest manifest, ModEntry entry)
		{
			Manifest = manifest;
			Entry = entry;
		}

		public ModManifest Manifest { get; }

		public ModEntry Entry { get; }

		public List<Node> Dependents { get; } = new();

		/// <summary>Kahn 入度:尚未满足的硬+软边数。</summary>
		public int Remaining { get; set; }
	}
}
