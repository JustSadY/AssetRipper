// Source-owned corpus. Compiled by the harness with explicit language, references and options.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AssetRipper.ManagedFixture
{
	public class Sample
	{
		public int Value;
		public int SerializedField;
		public int Current { get; private set; }
		public event Action<int> Changed;

		public int Add(int amount)
		{
			Value += amount;
			Current = Value * 2;
			Changed?.Invoke(Current);
			return Value;
		}

		public static int[] StateAndEvents(int start, int amount)
		{
			Sample sample = new Sample { Value = start };
			int calls = 0;
			int observed = 0;
			Action<int> handler = value => { calls++; observed = value; };
			sample.Changed += handler;
			int first = sample.Add(amount);
			sample.Changed -= handler;
			sample.Add(1);
			return new[] { first, sample.Value, sample.Current, calls, observed };
		}

		public static int Clamp(int value) { return value < 0 ? 0 : value; }

		public static int Sum(int count)
		{
			int result = 0;
			for (int i = 0; i < count; i++) result += i;
			return result;
		}

		public static IEnumerable<int> Sequence(int count)
		{
			if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
			for (int i = 0; i < count; i++) yield return i * i;
		}

		public static async Task<int> Asynchronous(int value, bool fail)
		{
			await Task.Yield();
			if (fail) throw new ArgumentOutOfRangeException(nameof(value));
			return value * 3;
		}

		public static T Choose<T>(bool first, T left, T right) where T : class
		{
			return first ? left : right;
		}

		public interface IAdjustment<T>
		{
			T Adjust(T value);
		}

		public sealed class Adjustment : IAdjustment<int>
		{
			int IAdjustment<int>.Adjust(int value) { return value + 7; }
		}

		public static int InterfaceDispatch(int value)
		{
			IAdjustment<int> adjustment = new Adjustment();
			return adjustment.Adjust(value);
		}

		public static int RefOut(int input, ref int value, out int doubled)
		{
			value += input;
			doubled = value * 2;
			return value - 1;
		}

		public static int[] ExceptionFlow(int dividend, int divisor)
		{
			int result = 0;
			int caught = 0;
			int finalized = 0;
			try { result = dividend / divisor; }
			catch (DivideByZeroException) { caught = 1; }
			finally { finalized++; }
			return new[] { result, caught, finalized };
		}

		public static int Ölç(int değer) { return checked(değer + 1); }
	}
}
