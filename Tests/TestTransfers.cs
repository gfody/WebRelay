using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;

namespace WebRelay.Tests
{
	[TestClass]
	public class TestTransfers
	{
		private static string listenPrefix = "http://*:81/";
		private static string downloadUrl = listenPrefix.Replace("*", Environment.MachineName);
		private static Uri websocketUrl = new Uri(downloadUrl.Replace("http", "ws"));

		private RelayServer server;
		private TaskCompletionSource<bool> stop;
		private Task<bool> listen;

		[TestInitialize]
		public void Setup()
		{
			server = new RelayServer();
			stop = new TaskCompletionSource<bool>();
			listen = server.Listen(listenPrefix, 1, stop);
		}

		[TestCleanup]
		public void Cleanup()
		{
			stop.SetResult(true);
			listen.Wait();
		}

		private byte[] RandomData(int size)
		{
			var buffer = new byte[size];
			var random = new Random();
			random.NextBytes(buffer);
			return buffer;
		}

		private class Unseekable : MemoryStream
		{
			public override bool CanSeek => false;
			public override long Length => throw new NotImplementedException();
			public override long Position => throw new NotImplementedException();
			public Unseekable(byte[] buffer) : base(buffer) { }
		}

		private void LocalDownload(int size, bool pipe = false)
		{
			var data = RandomData(size);
			var stream = pipe ? new MemoryStream(data) : new Unseekable(data);
			var code = server.AddRelay(new LocalRelay(stream));

			using (var client = new WebClient())
				CollectionAssert.AreEqual(data, client.DownloadData(downloadUrl + code));
		}

		private void SocketDownload(int size, bool pipe = false)
		{
			var data = RandomData(size);
			var stream = pipe ? new MemoryStream(data) : new Unseekable(data);
			var socket = new SocketRelayClient();
			var code = socket.AddRelay(websocketUrl, stream).Result;

			using (var client = new WebClient())
				CollectionAssert.AreEqual(data, client.DownloadData(downloadUrl + code));
		}

		[TestMethod] public void Local_Empty() => LocalDownload(0);
		[TestMethod] public void Local_1b() => LocalDownload(1);
		[TestMethod] public void Local_10k() => LocalDownload(10000);
		[TestMethod] public void Local_64k() => LocalDownload(65536);
		[TestMethod] public void Local_100k() => LocalDownload(100000);
		[TestMethod] public void Local_10mb() => LocalDownload(10000000);
		[TestMethod] public void Local_Pipe() => LocalDownload(10000000, true);

		[TestMethod] public void Socket_Empty() => SocketDownload(0);
		[TestMethod] public void Socket_1b() => SocketDownload(1);
		[TestMethod] public void Socket_10k() => SocketDownload(10000);
		[TestMethod] public void Socket_64k() => SocketDownload(65536);
		[TestMethod] public void Socket_100k() => SocketDownload(100000);
		[TestMethod] public void Socket_10mb() => SocketDownload(10000000);
		[TestMethod] public void Socket_Pipe() => SocketDownload(10000000, true);

	}
}
