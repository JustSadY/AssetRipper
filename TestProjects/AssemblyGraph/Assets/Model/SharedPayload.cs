using System;
using UnityEngine;

namespace AssetRipper.AssemblyGraph.Model
{
	[Serializable]
	public sealed class SharedPayload
	{
		public int count;
		public string label;
		public UnityEngine.Object target;
	}

	public interface IGraphNode
	{
		int Evaluate(int value);
	}

	public class GraphBase : MonoBehaviour
	{
		public int inherited;
	}
}
