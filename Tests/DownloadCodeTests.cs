using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace WebRelay.Tests
{
	[TestClass]
	public class DownloadCodeTests
	{
		[TestMethod]
		public void EncodeDecode()
		{
			int low = (int)Math.Pow(DownloadCode.Chars.Count, DownloadCode.Length - 1) + 1;
			int high = (int)Math.Pow(DownloadCode.Chars.Count, DownloadCode.Length);

			Parallel.ForEach(Enumerable.Range(low, high - low), i =>
			{
				var encoded = DownloadCode.Encode(i);
				Assert.AreEqual(DownloadCode.Length, encoded.Length);
				DownloadCode.TryDecode(encoded, out int decoded);
				Assert.AreEqual(i, decoded);
			});
		}

		[TestMethod]
		public void DecodeNull()
		{
			Assert.AreEqual(false, DownloadCode.TryDecode(null, out int x));
		}

		[TestMethod]
		public void DecodeBlank()
		{
			Assert.AreEqual(false, DownloadCode.TryDecode("", out int x));
		}

		[TestMethod]
		public void DecodeInvalid()
		{
			Assert.AreEqual(false, DownloadCode.TryDecode("abcde", out int x));
		}
	}
}
