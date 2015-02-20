using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace WebRelay.Tests
{
	[TestClass]
	public class DownloadProgressTests
	{
		[TestMethod]
		public void Sequential()
		{
			var dp = new DownloadProgress();
			dp.Download(0, 10);
			dp.Download(10, 10);
			dp.Download(20, 10);
			Assert.AreEqual(30, dp.Downloaded);
		}

		[TestMethod]
		public void Shuffled()
		{
			var dp = new DownloadProgress();
			dp.Download(0, 10);
			dp.Download(20, 10);
			dp.Download(10, 10);
			Assert.AreEqual(30, dp.Downloaded);
		}

		[TestMethod]
		public void Overlapped()
		{
			var dp = new DownloadProgress();
			dp.Download(0, 10);
			dp.Download(20, 10);
			dp.Download(0, 30);
			Assert.AreEqual(30, dp.Downloaded);
		}

		[TestMethod]
		public void GapsAndOverlaps()
		{
			var dp = new DownloadProgress();
			dp.Download(0, 10);
			dp.Download(20, 10);
			dp.Download(40, 10);
			dp.Download(60, 10);
			dp.Download(0, 70);
			Assert.AreEqual(70, dp.Downloaded);
		}

		[TestMethod]
		public void Combo()
		{
			var dp = new DownloadProgress();
			var r = new Random(1);
			for (int i = 0; i < 1000; i++)
				dp.Download(r.Next(i) * 100, r.Next(200));
			Assert.AreEqual(48583, dp.Downloaded);
			dp.Download(0, dp.HighWaterMark);
			Assert.AreEqual(dp.HighWaterMark, dp.Downloaded);
		}

		[TestMethod]
		public void Reset()
		{
			var dp = new DownloadProgress();
			dp.Download(0, 10);
			dp.Download(10, 10);
			dp.Reset();
			Assert.AreEqual(0, dp.Downloaded);
		}

		[TestMethod]
		public void DownloadedBetween()
		{
			var dp = new DownloadProgress();
			dp.Download(0, 10);
			dp.Download(20, 10);
			dp.Download(40, 10);
			dp.Download(60, 10);
			Assert.AreEqual(dp.Downloaded, dp.DownloadedBetween(0, 70));
			Assert.AreEqual(20, dp.DownloadedBetween(0, 30));
			Assert.AreEqual(10, dp.DownloadedBetween(5, 20));
			dp.Download(0, 70);
			Assert.AreEqual(30, dp.DownloadedBetween(0, 30));
		}

		[TestMethod]
		public void HighWaterMark()
		{
			var dp = new DownloadProgress();
			Assert.AreEqual(0, dp.HighWaterMark);
			dp.Download(20, 10);
			Assert.AreEqual(30, dp.HighWaterMark);
			dp.Download(20, 15);
			Assert.AreEqual(35, dp.HighWaterMark);
			dp.Reset();
			Assert.AreEqual(0, dp.HighWaterMark);
		}

		[TestMethod]
		public void ComboConcurrent()
		{
			var inputs = new List<Tuple<int, int>>();
			var r = new Random(1);
			for (int i = 0; i < 1000; i++)
				inputs.Add(new Tuple<int, int>(r.Next(i) * 100, r.Next(200)));

			var dp = new DownloadProgress();
			Parallel.ForEach(inputs, t => dp.Download(t.Item1, t.Item2));
			Assert.AreEqual(48583, dp.Downloaded);
			dp.Download(0, dp.HighWaterMark);
			Assert.AreEqual(dp.HighWaterMark, dp.Downloaded);
		}
	}
}
