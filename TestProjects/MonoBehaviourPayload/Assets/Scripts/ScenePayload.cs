using AssetRipper.EditorPayloadFixture;
using UnityEngine;

namespace AssetRipper.EditorSceneFixture
{
	public sealed class ScenePayload : PayloadBase
	{
		public int score;
		public string label;
		public UnityEngine.Object direct;
		public UnityEngine.Object[] links;
		public NestedPayload nested;
		public ScenePayload peer;
	}
}
