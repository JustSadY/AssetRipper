using System;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.Scripting;

namespace AssetRipper.Il2CppFixture
{
	[Preserve] public static class PureMethods
	{
		[Preserve, MethodImpl(MethodImplOptions.NoInlining)] public static int Affine(int x) { return unchecked(x * 7 - 11); }
		[Preserve, MethodImpl(MethodImplOptions.NoInlining)] public static int Branch(int x) { return x < 0 ? -x + 3 : x == 0 ? 17 : x % 2 == 0 ? x / 2 : x * 3 + 1; }
		[Preserve, MethodImpl(MethodImplOptions.NoInlining)] public static int Sum(int n) { int value = 0; for (int i = 1; i <= n; i++) value += i; return value; }
		// Use an explicit managed throw: release IL2CPP can emit unchecked native division.
		[Preserve, MethodImpl(MethodImplOptions.NoInlining)] public static int Divide(int a, int b) { try { if (b == 0) throw new DivideByZeroException(); return a / b; } catch (DivideByZeroException) { return -999; } }
		[Preserve, MethodImpl(MethodImplOptions.NoInlining)] public static T Echo<T>(T value) { return value; }
	}
	[Preserve] public interface ICounter { int Step(int delta); }
	[Preserve] public abstract class AbstractCounter { [Preserve] public abstract int Step(int delta); }
	[Preserve] public sealed class Counter : AbstractCounter, ICounter
	{
		public int value;
		[Preserve, MethodImpl(MethodImplOptions.NoInlining)] public override int Step(int delta) { value += delta; if (value < 0) value = 0; return value; }
	}
	[Serializable] public sealed class Observation { public string Id; public string Method; public int A; public int B; public int Value; }
	[Serializable] public sealed class RuntimeReport
	{
		public int SchemaVersion = 1; public string UnityVersion; public string Backend; public Observation[] Cases;
	}
	public static class ProbeCases
	{
		public static Observation[] Observe()
		{
			var cases = new System.Collections.Generic.List<Observation>();
			foreach (int x in new[] { -9, 0, 2, 100, int.MaxValue }) cases.Add(new Observation { Id = "Affine/" + x.ToString(CultureInfo.InvariantCulture), Method = "Affine", A = x, Value = PureMethods.Affine(x) });
			foreach (int x in new[] { -4, 0, 8, 5 }) cases.Add(new Observation { Id = "Branch/" + x, Method = "Branch", A = x, Value = PureMethods.Branch(x) });
			foreach (int x in new[] { -2, 0, 6, 12 }) cases.Add(new Observation { Id = "Sum/" + x, Method = "Sum", A = x, Value = PureMethods.Sum(x) });
			foreach (int[] pair in new[] { new[] { 17, 3 }, new[] { -17, 3 }, new[] { 7, 0 } }) cases.Add(new Observation { Id = "Divide/" + pair[0] + "/" + pair[1], Method = "Divide", A = pair[0], B = pair[1], Value = PureMethods.Divide(pair[0], pair[1]) });
			Counter counter = new Counter { value = 5 };
			foreach (int delta in new[] { 3, -8, 14 }) { int before = counter.value; cases.Add(new Observation { Id = "Counter/" + delta, Method = "Step", A = before, B = delta, Value = counter.Step(delta) }); }
			cases.Add(new Observation { Id = "Echo/int", Method = "Echo", A = 42, Value = PureMethods.Echo(42) });
			return cases.ToArray();
		}
	}
	public static class PlayerProbe
	{
		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
		private static void Run()
		{
			string output = Environment.GetEnvironmentVariable("ASSETRIPPER_METHOD_FIXTURE_REPORT");
			// An Android player receives no environment; it reports through logcat in numbered chunks instead.
			bool android = Application.platform == RuntimePlatform.Android;
			if (string.IsNullOrEmpty(output) && !android) return;
			try
			{
#if ENABLE_IL2CPP
				const string backend = "IL2CPP";
#else
				const string backend = "Mono";
#endif
				RuntimeReport report = new RuntimeReport { UnityVersion = Application.unityVersion, Backend = backend, Cases = ProbeCases.Observe() };
				if (android)
				{
					Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
					string json = JsonUtility.ToJson(report, false);
					const int chunk = 600;
					int count = (json.Length + chunk - 1) / chunk;
					for (int i = 0; i < count; i++)
						Debug.Log("ASSETRIPPER_METHOD_FIXTURE_REPORT " + i + "/" + count + " " + json.Substring(i * chunk, Math.Min(chunk, json.Length - i * chunk)));
				}
				else
				{
					File.WriteAllText(output, JsonUtility.ToJson(report, true));
				}
				Application.Quit(0);
			}
			catch (Exception exception) { Debug.LogException(exception); Application.Quit(1); }
		}
	}
}
