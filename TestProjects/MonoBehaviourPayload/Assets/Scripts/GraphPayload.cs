using System;
using System.Collections.Generic;
using UnityEngine;

namespace AssetRipper.EditorGraphFixture
{
	[Serializable]
	public abstract class GraphNode
	{
		public string label;
		[SerializeReference] public GraphNode next;
		public UnityEngine.Object target;
	}

	[Serializable] public sealed class NumberNode : GraphNode { public int number; }
	[Serializable] public sealed class TextNode : GraphNode { public string text; }
	[Serializable] public sealed class InlineGraph { [SerializeReference] public GraphNode child; }
	public abstract class GraphHost : MonoBehaviour { [SerializeReference] public GraphNode root; }

	public sealed class GraphPayload : GraphHost
	{
		[SerializeReference] public GraphNode alias;
		[SerializeReference] public GraphNode missing;
		[SerializeReference] public List<GraphNode> items;
		public InlineGraph inline;
	}
}
