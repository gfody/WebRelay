using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace WebRelay
{
	public class RelayServer : HttpTaskAsyncHandler
	{
		public bool EnableBuiltinWebclient = true;
		public override bool IsReusable => true;
		public override async Task ProcessRequestAsync(HttpContext context) => await ProcessRequestAsync(new HttpContextWrapper(context));

		public string AddRelay(IRelay relay)
		{
			string code;
			do code = DownloadCode.Generate(); while (!ActiveRelays.TryAdd(code, relay));
			return code;
		}

		public async Task<bool> Listen(string prefix, int maxConcurrentRequests, TaskCompletionSource<bool> stop)
		{
			HttpListener listener = new HttpListener();
			listener.Prefixes.Add(prefix);
			listener.Start();
			try
			{
				var requests = new HashSet<Task>() { stop.Task };
				for (int i = 0; i < maxConcurrentRequests; i++)
					requests.Add(listener.GetContextAsync());

				while (!stop.Task.IsCompleted)
				{
					Task t = await Task.WhenAny(requests);
					requests.Remove(t);

					if (t.IsFaulted)
						throw t.Exception;

					if (!stop.Task.IsCompleted && t is Task<HttpListenerContext>)
					{
						requests.Add(ProcessRequestAsync(new HttpListenerContextWrapper((t as Task<HttpListenerContext>).Result)));
						requests.Add(listener.GetContextAsync());
					}
				}

				return stop.Task.Result;
			}
			finally
			{
				listener.Stop();
			}
		}

		private static ConcurrentDictionary<string, IRelay> ActiveRelays = new ConcurrentDictionary<string, IRelay>();
		private static DateTime BuildDateFromAssemblyInfo = new DateTime(2000, 1, 1).AddDays(Assembly.GetExecutingAssembly().GetName().Version.Build).AddSeconds(Assembly.GetExecutingAssembly().GetName().Version.Revision * 2).ToUniversalTime();
		private static byte[] Mainpage = Encoding.UTF8.GetBytes(Resources.main).GZip();
		private static ConcurrentDictionary<string, Tuple<DateTime, int>> BlockedHosts = new ConcurrentDictionary<string, Tuple<DateTime, int>>();
		private static Timer BlockedHostsCleanup = new Timer(x => { BlockedHosts.Clear(); }, null, 60000, 60000);

		private async Task ServeMainpage(HttpContextBase context, string path)
		{
			context.Response.Headers.Remove("Server");

			if (string.IsNullOrEmpty(path))
			{
				if (DateTime.TryParse(context.Request.Headers["If-Modified-Since"], out DateTime cache_date) && cache_date.ToUniversalTime().Equals(BuildDateFromAssemblyInfo))
					context.Response.StatusCode = 304;
				else
				{
					context.Response.AddHeader("Content-Encoding", "gzip");
					context.Response.AddHeader("Content-Length", Mainpage.Length.ToString());
					context.Response.AddHeader("Last-Modified", BuildDateFromAssemblyInfo.ToString("R"));
					context.Response.ContentType = "text/html; charset=utf-8";
					await context.Response.OutputStream.WriteAsync(Mainpage, 0, Mainpage.Length);
				}
			}
			else
			{
				context.Response.AddHeader("Location", context.Request.ApplicationPath);
				context.Response.StatusCode = 301;
			}

			context.Response.OutputStream.Close();
		}

		private async Task HandleWebsocketRequest(WebSocketContext context)
		{
			string key = null;
			try
			{
				var timeout = new CancellationTokenSource(new TimeSpan(0, 0, 1));
				string filename = await context.WebSocket.ReceiveString(timeout.Token);
				long? filesize = null; if (long.TryParse(await context.WebSocket.ReceiveString(timeout.Token), out long size)) filesize = size;
				string mimetype = await context.WebSocket.ReceiveString(timeout.Token);

				SocketRelay relay = new SocketRelay(context.WebSocket, filename, filesize, mimetype);
				key = AddRelay(relay);

				await context.WebSocket.SendString($"code={key}", timeout.Token);

				await relay.HandleUpload();
			}
			catch (TaskCanceledException)
			{
				await context.WebSocket.CloseOutputAsync(WebSocketCloseStatus.Empty, null, CancellationToken.None);
			}
			// client disconnects without closing
			catch (WebSocketException) { }
			catch (Exception e) when (e.InnerException is WebSocketException) { }
			finally
			{
				if (key != null)
					ActiveRelays.TryRemove(key, out IRelay x);
			}
		}

		private async Task ProcessRequestAsync(HttpContextBase context)
		{
			// block hosts with more than 10 bad requests in the last 10 seconds..
			// (item1 is last request time, item2 is count of previous requests)
            if (BlockedHosts.TryGetValue(context.Request.UserHostAddress, out Tuple<DateTime, int> t) && DateTime.UtcNow.Subtract(t.Item1).TotalSeconds < 10 && t.Item2 > 10)
				return;

			if (context.IsWebSocketRequest)
			{
				if (EnableBuiltinWebclient)
					context.AcceptWebSocketRequest((Func<WebSocketContext, Task>)HandleWebsocketRequest);
				return;
			}

			// get download code from querystring or subdomain..
			// (http://9fakr.mydomain.com or http://mydomain.com/9fakr or http://somedomain.dom/mysite/9fakr)
			string path = context.Request.RawUrl.Substring(context.Request.ApplicationPath.Length).ToLower().Trim(new char[] { '/', '\\', ' ' });
			string code = (string.IsNullOrEmpty(path) && context.Request.Url.Host.Split('.').Length == 3) ? context.Request.Url.Host.Split('.')[0].ToLower() : path;

			// get the corresponding relay or log bad request..
			// block subsequent requests for the same code that don't come from the original host..
			if (DownloadCode.Check(code) && ActiveRelays.TryGetValue(code, out IRelay relay) && (string.IsNullOrEmpty(relay.UserHostAddress) || relay.UserHostAddress == context.Request.UserHostAddress))
			{
				if (context.Request.HttpMethod != "HEAD") // don't let an initial HEAD from a facebook or slack spoil the link
					relay.UserHostAddress = context.Request.UserHostAddress;

				await relay.HandleDownloadRequest(context);
			}
			else
			{
				// serve up the main page for bad requests..
				if (EnableBuiltinWebclient)
					await ServeMainpage(context, path);

				BlockedHosts.AddOrUpdate(context.Request.UserHostAddress, new Tuple<DateTime, int>(DateTime.UtcNow, 1),
					(k, v) => new Tuple<DateTime, int>(DateTime.UtcNow, DateTime.UtcNow.Subtract(v.Item1).TotalSeconds < 10 ? v.Item2 + 1 : 1));
			}
		}
	}

	public static partial class Extensions
	{
		public static byte[] GZip(this byte[] value)
		{
			using (var ms = new MemoryStream())
			using (var gz = new GZipStream(ms, CompressionLevel.Optimal))
			{
				gz.Write(value, 0, value.Length);
				gz.Close();
				return ms.ToArray();
			}
		}

		public static async Task SendString(this WebSocket socket, string text, CancellationToken cancel) =>
			await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, cancel);

		public static async Task<string> ReceiveString(this WebSocket socket, CancellationToken cancel)
		{
			WebSocketReceiveResult result;
			var buffer = new byte[1024];
			int received = 0;
			do
			{
				result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, received, buffer.Length - received), cancel);
				received += result.Count;
			}
			while (!result.EndOfMessage);

			return received == 0 ? null : Encoding.UTF8.GetString(new ArraySegment<byte>(buffer, 0, received).ToArray());
		}
	}
}
