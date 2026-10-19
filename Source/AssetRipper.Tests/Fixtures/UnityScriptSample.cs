// Source-owned compile fixture. Native Unity methods are never executed by the .NET test runner.
using System.Collections.Generic;
using System.Runtime.Versioning;
using UnityEngine;

[assembly: TargetFramework(".NETStandard,Version=v2.1")]

namespace AssetRipper.UnityFixture
{
	public class UnityScriptSample : MonoBehaviour
	{
		[SerializeField] private int score = 7;
		[SerializeField] private Vector3 offset = new Vector3(1, 2, 3);

		public int Score => score;
		public Vector3 Offset => offset;

		public static List<int> Build(int value) => new() { value };

		public int Evaluate(object value)
		{
			if (value is int number and > 0)
				return number switch { < 10 => number + score, _ => score };
			return -1;
		}

		public void Move(Transform target, float amount)
		{
			if (target != null) target.position += offset * amount;
		}
	}
}
