using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

public class DownloadCode
{
	public static readonly List<char> Chars = new List<char>("23456789ahijkqruwxy"); // no 0O, l1, BCDEGPTVZ, MN, SF
	public static readonly int Length = 5;

	private static Regex regex = new Regex(string.Format("^[{0}]{{{1}}}$", new String(Chars.ToArray()), Length), RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

	public static string Generate() => Encode(new Random().Next((int)Math.Pow(Chars.Count, Length - 1) + 1, (int)Math.Pow(Chars.Count, Length)));
	public static bool Check(string encoded) => TryDecode(encoded, out int x);

	public static string Encode(int value)
	{
		char[] encoded = new char[(int)Math.Ceiling(Math.Log(value, Chars.Count))];

		for (int i = 0; i < encoded.Length; i++)
		{
			encoded[i] = Chars[value % Chars.Count];
			value /= Chars.Count;
		}

		return new string(encoded);
	}

	public static bool TryDecode(string encoded, out int value)
	{
		value = 0;

		if (string.IsNullOrEmpty(encoded) || !regex.IsMatch(encoded))
			return false;

		for(int i=0; i < encoded.Length; i++)
			value += Chars.BinarySearch(encoded[i]) * (int)Math.Pow(Chars.Count, i);

		return true;
	}
}
