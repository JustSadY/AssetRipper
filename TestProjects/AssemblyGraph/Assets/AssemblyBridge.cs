using AssetRipper.AssemblyGraph.Components;
using AssetRipper.AssemblyGraph.Model;
using UnityEngine;

namespace AssetRipper.AssemblyGraph
{
	public sealed class AssemblyBridge : MonoBehaviour
	{
		public GraphNode node;
		public SharedPayload shared;
		public UnityEngine.Object[] links;
		public int stamp;

		public int Evaluate(int value)
		{
			return node.Evaluate(value) + shared.count + stamp;
		}
	}
}
