using System;
using UnityEngine;

namespace AssetRipper.EditorPayloadFixture
{
	public class PayloadBase : MonoBehaviour
	{
		public int inherited;
		[SerializeField] private short hidden;
	}

	[Serializable]
	public class NestedPayload
	{
		public int count;
		public UnityEngine.Object target;
	}

	public sealed class EditorPayload : PayloadBase
	{
		public bool flag;
		public string label;
		public int[] values;
		public UnityEngine.Object direct;
		public UnityEngine.Object[] links;
		public NestedPayload nested;
		[NonSerialized] public int ignored;
		public static int global;
		public readonly int readOnly;
	}
}
