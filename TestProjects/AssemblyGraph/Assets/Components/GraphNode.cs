using AssetRipper.AssemblyGraph.Model;
using UnityEngine;

namespace AssetRipper.AssemblyGraph.Components
{
	public sealed class GraphNode : GraphBase, IGraphNode
	{
		[SerializeField] private short hidden;
		public SharedPayload payload;
		public GameObject local;
		public UnityEngine.Object[] links;

		public int Evaluate(int value)
		{
			return inherited + hidden + payload.count + value;
		}
	}
}
