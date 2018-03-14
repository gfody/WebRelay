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
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace WebRelay
{
	public class RelayServer : HttpTaskAsyncHandler
	{
		public bool EnableBuiltinWebclient = true;
		public bool AcceptSocketConnections = true;
		public event Action<string, long?, string, IRelay> OnSocketRelay;
		public override bool IsReusable => true;
		public override async Task ProcessRequestAsync(HttpContext context) => await ProcessRequestAsync(new HttpContextWrapper(context));

		public string AddRelay(IRelay relay)
		{
			string code;
			do code = DownloadCode.Generate(); while (!activeRelays.TryAdd(code, relay));
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

		private StaticContent staticContent = new StaticContent();
		private static ConcurrentDictionary<string, IRelay> activeRelays = new ConcurrentDictionary<string, IRelay>();
		private static ConcurrentDictionary<string, Tuple<DateTime, int>> blockedHosts = new ConcurrentDictionary<string, Tuple<DateTime, int>>();
		private static Timer blockedHostsCleanup = new Timer(x => blockedHosts.Clear(), null, 60000, 60000);
		private static Regex botAgents = new Regex("bot|slack|facebook|whatsapp|discord|telegram|skype", RegexOptions.IgnoreCase | RegexOptions.Compiled);

		private async Task HandleWebsocketRequest(WebSocketContext context)
		{
			string key = null;
			try
			{
				var timeout = new CancellationTokenSource(new TimeSpan(0, 0, 3));
				string filename = await context.WebSocket.ReceiveString(timeout.Token);
				long? filesize = null; if (long.TryParse(await context.WebSocket.ReceiveString(timeout.Token), out long size)) filesize = size;
				string mimetype = await context.WebSocket.ReceiveString(timeout.Token);

				SocketRelay relay = new SocketRelay(context.WebSocket, filename, filesize, mimetype);
				key = AddRelay(relay);
				await context.WebSocket.SendString($"code={key}", timeout.Token);

				OnSocketRelay?.Invoke(filename, filesize, key, relay);

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
					activeRelays.TryRemove(key, out IRelay x);
			}
		}

		private async Task ProcessRequestAsync(HttpContextBase context)
		{
			// block hosts with more than 10 bad requests in the last 10 seconds..
			// (item1 is last request time, item2 is count of previous requests)
			if (blockedHosts.TryGetValue(context.Request.UserHostAddress, out Tuple<DateTime, int> t) && DateTime.UtcNow.Subtract(t.Item1).TotalSeconds < 10 && t.Item2 > 10)
				return;

			if (context.IsWebSocketRequest)
			{
				if (AcceptSocketConnections)
					context.AcceptWebSocketRequest((Func<WebSocketContext, Task>)HandleWebsocketRequest);

				return;
			}

			// get download code from querystring or subdomain..
			// (http://9fakr.mydomain.com or http://mydomain.com/9fakr or http://somedomain.dom/mysite/9fakr)
			string path = context.Request.RawUrl.Substring(context.Request.ApplicationPath.Length).ToLower().Trim(new char[] { '/', '\\', ' ' });
			string code = (string.IsNullOrEmpty(path) && context.Request.Url.Host.Split('.').Length == 3) ? context.Request.Url.Host.Split('.')[0].ToLower() : path;
			bool validCode = DownloadCode.Check(code);
			bool mainpageRequest = (path == "") && !validCode;

			if (mainpageRequest && !(EnableBuiltinWebclient && AcceptSocketConnections))
				return;

			try
			{
				if (validCode)
				{
					if (activeRelays.TryGetValue(code, out IRelay relay) && !botAgents.IsMatch(context.Request.UserAgent ?? ""))
						await relay.HandleDownloadRequest(context);
					else
						context.Response.StatusCode = 410;
				}
				else if (!await staticContent.HandleRequest(context, path))
				{
					context.Response.StatusCode = 404;
					blockedHosts.AddOrUpdate(context.Request.UserHostAddress, new Tuple<DateTime, int>(DateTime.UtcNow, 1),
						(k, v) => new Tuple<DateTime, int>(DateTime.UtcNow, DateTime.UtcNow.Subtract(v.Item1).TotalSeconds < 10 ? v.Item2 + 1 : 1));
				}
			}
			finally
			{
				context.Response.OutputStream.Dispose();
			}
		}

		private class StaticContent
		{
			private DateTime buildDate;
			private Dictionary<(string, bool), (string, byte[])> content; // (name, gzip) -> (contentType, content)

			public StaticContent()
			{
				var asm = Assembly.GetExecutingAssembly();
				var name = asm.GetName();
				buildDate = new DateTime(2000, 1, 1).AddDays(name.Version.Build).AddSeconds(name.Version.Revision * 2).ToUniversalTime();

				// inline images and js..
				string prefix = $"{nameof(WebRelay)}.webclient.";
				string main = Encoding.UTF8.GetString(asm.GetManifestResourceStream($"{prefix}main.html").ToArray());

				foreach (var r in asm.GetManifestResourceNames())
				{
					if (r.EndsWith(".js"))
						main = main.Replace($" src=\"{r.Replace(prefix, "")}\">",
							$">\r\n{Encoding.UTF8.GetString(asm.GetManifestResourceStream(r).ToArray())}\r\n");

					else if (r.StartsWith($"{prefix}images."))
						main = main.Replace(r.Replace($"{prefix}images.", ""),
							$"data:{Path.GetExtension(r).GuessMimeType()};base64,{Convert.ToBase64String(asm.GetManifestResourceStream(r).ToArray())}");
				}

				var mainBytes = Encoding.UTF8.GetBytes(main);
				var favicon = asm.GetManifestResourceStream($"{prefix}favicon.ico").ToArray();
				var robots = Encoding.UTF8.GetBytes("user-agent: *\r\nAllow: /$\r\nDisallow: /");
				content = new Dictionary<(string, bool), (string, byte[])>()
				{
					{ ("", false), ("text/html; charset=utf-8", mainBytes) },
					{ ("", true), ("text/html; charset=utf-8", mainBytes.GZip()) },
					{ ("favicon.ico", false), ("image/x-icon", favicon) },
					{ ("favicon.ico", true), ("image/x-icon", favicon.GZip()) },
					{ ("robots.txt", false), ("text/plain; charset=utf-8", robots) },
					{ ("robots.txt", true), ("text/plain; charset=utf-8", robots.GZip()) },
				};
			}

			public async Task<bool> HandleRequest(HttpContextBase context, string path)
			{
				context.Response.AddHeader("Server", "");
				if (DateTime.TryParse(context.Request.Headers["If-Modified-Since"], out DateTime cache_date) && cache_date.ToUniversalTime().Equals(buildDate))
				{
					context.Response.StatusCode = 304;
					return true;
				}
				else
				{
					bool gzip = context.Request.Headers["Accept-Encoding"]?.Contains("gzip") ?? false;
					if (content.TryGetValue((path, gzip), out ValueTuple<string, byte[]> body))
					{
						if (gzip) context.Response.AddHeader("Content-Encoding", "gzip");
						context.Response.AddHeader("Content-Length", body.Item2.Length.ToString());
						context.Response.AddHeader("Last-Modified", buildDate.ToString("R"));
						context.Response.ContentType = body.Item1;
						await context.Response.OutputStream.WriteAsync(body.Item2, 0, body.Item2.Length);
						return true;
					}
					else
						return false;
				}
			}
		}
	}

	public static partial class Extensions
	{
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

		public static byte[] ToArray(this Stream stream)
		{
			using (var m = new MemoryStream((int)stream.Length))
			{
				stream.CopyTo(m);
				return m.ToArray();
			}
		}

		public static byte[] GZip(this byte[] bytes)
		{
			using (var ms = new MemoryStream())
			using (var gz = new GZipStream(ms, CompressionLevel.Optimal))
			{
				gz.Write(bytes, 0, bytes.Length);
				gz.Close();
				return ms.ToArray();
			}
		}
	}
}
