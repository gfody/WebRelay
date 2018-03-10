using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace WebRelay
{
	public interface IRelay
	{
		event Action OnStart, OnCancel, OnDisconnect, OnComplete;
		event Action<long, long?> OnProgress;
		string UserHostAddress { get; set; }
		Task HandleDownloadRequest(HttpContextBase context);
		void Cancel();
	}

	public class LocalRelay : IRelay
	{
		public event Action OnStart, OnCancel, OnDisconnect, OnComplete;
		public event Action<long, long?> OnProgress;
		public string UserHostAddress { get; set; }

		private DownloadProgress progress = new DownloadProgress();
		private int lastProgress = Environment.TickCount;
		private int pendingRequests = 0;
		private bool adaptiveStreaming = false;
		private bool cancel = false;

		private string filename, mimetype;
		private Stream stream;

		public LocalRelay(Stream stream, string filename = null, string mimetype = null)
		{
			this.stream = stream;
			this.filename = filename;
			this.mimetype = mimetype ?? filename?.GuessMimeType();
		}

		public void Cancel()
		{
			cancel = true;
			OnCancel?.Invoke();
		}

		public async Task HandleDownloadRequest(HttpContextBase context)
		{
			(long offset, long count) = context.WriteCommonHeaders(filename, stream.CanSeek ? stream.Length : (long?)null, mimetype);

			if (context.Request.HttpMethod == "HEAD")
				return;

			int thisRequest = Interlocked.Increment(ref pendingRequests);
			if (thisRequest == 1 && OnStart != null) OnStart();

			var buffer = new byte[65536];
			bool isAdaptiveStream = stream.CanSeek && context.IsAdaptiveStream();
			if (isAdaptiveStream && offset == 0 && count == 2)
				adaptiveStreaming = true;

			if (offset == 0)
				progress.Reset();

			bool disconnected = false;
			try
			{
				int bytesRead = 0;
				long downloaded = 0, curPosition, endPosition = buffer.Length;
				if (stream.CanSeek)
				{
					endPosition = Math.Min(offset + count, stream.Length);
					if (stream.Position != offset)
						stream.Position = offset;
				}

				do
				{
					curPosition = stream.CanSeek ? stream.Position : 0;
					bytesRead = await stream.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, endPosition - curPosition));
					await context.Response.OutputStream.WriteAsync(buffer, 0, bytesRead);
					downloaded += bytesRead;
					progress.Download(stream.CanSeek ? curPosition : (downloaded - bytesRead), bytesRead);
					if (OnProgress != null && Environment.TickCount - lastProgress > 1000)
					{
						lastProgress = Environment.TickCount;
						OnProgress(progress.Downloaded, stream.CanSeek ? stream.Length : (long?)null);
					}

				} while (bytesRead > 0 && !cancel);

				context.Response.OutputStream.Close();
			}
			// Response.OutputStream.Write throws if downloader disconnected..
			catch (HttpListenerException e) when ((uint)e.HResult == 0x80004005)
			{
				disconnected = true;
			}
			catch (HttpException e) when ((uint)e.HResult == 0x800704CD)
			{
				disconnected = true;
			}

			if (Interlocked.Decrement(ref pendingRequests) == 0)
			{
				await Task.Delay(10);

				if (OnComplete != null && (!stream.CanSeek || (progress.Downloaded >= stream.Length || (adaptiveStreaming && progress.IsAdaptiveStreamDone(stream.Length, buffer.Length)))))
				{
					if (isAdaptiveStream)
					{
						// mobile safari will sometimes download an entire video, throw it away, and then download it again. wait a moment for the subsequent request to hit before we hangup..
						await Task.Delay(1000);
						if (pendingRequests == 0)
							OnComplete();
					}
					else if (disconnected)
						OnDisconnect?.Invoke();
					else
						OnComplete();
				}
				else if (cancel && OnCancel != null)
					OnCancel();

				else if (disconnected && OnDisconnect != null)
					OnDisconnect();
			}
		}
	}

	public class SocketRelay : IRelay
	{
		public event Action OnStart, OnCancel, OnDisconnect, OnComplete;
		public event Action<long, long?> OnProgress;
		public string UserHostAddress { get; set; }

		private DownloadProgress progress = new DownloadProgress();
		private int lastProgress = Environment.TickCount;
		private bool adaptiveStreaming = false;
		private bool isAdaptiveStream = false;

		private ConcurrentQueue<TaskCompletionSource<bool>> requests = new ConcurrentQueue<TaskCompletionSource<bool>>();

		private enum DownloadResult { Partial, Canceled, Completed, Interrupted }
		private TaskCompletionSource<DownloadResult> completed = new TaskCompletionSource<DownloadResult>();
		private TaskCompletionSource<TaskCompletionSource<bool>> connected = new TaskCompletionSource<TaskCompletionSource<bool>>();
		private CancellationTokenSource cancel = new CancellationTokenSource();

		private Task<WebSocketReceiveResult> pendingReceive;
		private WebSocket socket;
		private byte[] buffer = new byte[65536];
		private bool socketClosed = false;
		private string closeStatus, filename, mimetype;
		private long? filesize;

		public SocketRelay(WebSocket socket, string filename = null, long? filesize = null, string mimetype = null)
		{
			this.socket = socket;
			this.filename = filename;
			this.filesize = filesize;
			this.mimetype = mimetype ?? filename?.GuessMimeType();

			pendingReceive = socket.ReceiveAsync(new ArraySegment<byte>(buffer, 0, buffer.Length), CancellationToken.None);
		}

		public void Cancel()
		{
			cancel.Cancel();
			OnCancel?.Invoke();
		}

		public async Task HandleUpload()
		{
			while (!cancel.IsCancellationRequested)
			{
				// wait for downloader to connect or uploader to cancel..
				var t = await Task.WhenAny(connected.Task, pendingReceive, Task.Delay(Timeout.Infinite, cancel.Token));

				// uploader stopped or canceled..
				if (t is Task<WebSocketReceiveResult>)
				{
					socketClosed = true;
					if (filesize.HasValue || progress.Downloaded == 0) OnCancel?.Invoke();
					closeStatus = ((Task<WebSocketReceiveResult>)t).Result.CloseStatusDescription;
					await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, closeStatus, cancel.Token);
					return;
				}
				// downloader connected..
				else
				{
					var request = ((Task<TaskCompletionSource<bool>>)t).Result;
					var result = await completed.Task;
					completed = new TaskCompletionSource<DownloadResult>();
					connected = new TaskCompletionSource<TaskCompletionSource<bool>>();


					// skip this result if there's a request pending..
					if (request == requests.Peek())
					{
						// mobile safari will sometimes download an entire video, throw it away, and then download it again. wait a moment for the subsequent request to hit before we hangup..
						if (isAdaptiveStream && (result == DownloadResult.Completed || result == DownloadResult.Interrupted))
						{
							await Task.Delay(1000);
							if (request != requests.Peek())
							{
								request.TrySetResult(true);
								continue;
							}
						}

						switch (result)
						{
							// tell client we're done and wait for the close, if we just hangup the message doesn't always make it..
							case DownloadResult.Completed:
								OnComplete?.Invoke();
								await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("completed")), WebSocketMessageType.Text, true, cancel.Token);
								await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, (await pendingReceive).CloseStatusDescription, cancel.Token);
								return;

							// if download is interrupted inform uploader but keep listening incase downloader recovers..
							case DownloadResult.Interrupted:
								OnDisconnect?.Invoke();
								await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("disconnected")), WebSocketMessageType.Text, true, cancel.Token);
								break;

							case DownloadResult.Canceled:
								OnCancel?.Invoke();
								await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, closeStatus, cancel.Token);
								return;
						}
					}
					request.TrySetResult(true);
				}
			}

			if (socket.State != WebSocketState.Closed)
				await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, closeStatus, cancel.Token);
		}

		public async Task HandleDownloadRequest(HttpContextBase context)
		{
			(long offset, long count) = context.WriteCommonHeaders(filename, filesize, mimetype);

			if (context.Request.HttpMethod == "HEAD")
				return;

			// serialize overlapping requests..
			TaskCompletionSource<bool> lastRequest = null;
			var thisRequest = new TaskCompletionSource<bool>();
			lock (requests)
			{
				requests.TryDequeue(out lastRequest);
				requests.Enqueue(thisRequest);
			}
			if (lastRequest != null) await lastRequest.Task;

			// signal upload to start..
			connected.SetResult(thisRequest);
			if (OnStart != null && lastRequest == null) OnStart();
			
			isAdaptiveStream = filesize.HasValue && context.IsAdaptiveStream();
			if (isAdaptiveStream && offset == 0 && count == 2)
				adaptiveStreaming = true;

			if (offset == 0)
				progress.Reset();

			try
			{
				// include size and offset for range requests in first signal, subsequent signals can be blank and the client will send the next chunk..
				await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes($"range: {offset}-{count}")), WebSocketMessageType.Text, true, cancel.Token);
				var blank = new ArraySegment<byte>(new byte[] { });
				do
				{
					byte[] msg = await ReceiveBytes();
					await context.Response.OutputStream.WriteAsync(msg, 0, msg.Length);
					progress.Download(offset, msg.Length);
					offset += msg.Length;
					count -= msg.Length;

					if (OnProgress != null && Environment.TickCount - lastProgress > 1000)
					{
						lastProgress = Environment.TickCount;
						OnProgress(progress.Downloaded, filesize);
					}

					if (!socketClosed && (!filesize.HasValue || count > 0))
						await socket.SendAsync(blank, WebSocketMessageType.Text, true, cancel.Token);
				}
				while (!socketClosed && (!filesize.HasValue || count > 0));

				context.Response.OutputStream.Close();

				if (!filesize.HasValue && socketClosed)
					completed.SetResult(DownloadResult.Completed);
				else if (filesize.HasValue && (progress.Downloaded >= filesize.Value || (adaptiveStreaming && progress.IsAdaptiveStreamDone(filesize.Value, buffer.Length))))
					completed.SetResult(DownloadResult.Completed);
				else if (filesize.HasValue && socketClosed)
					completed.SetResult(DownloadResult.Canceled);
				else
					completed.SetResult(DownloadResult.Partial);
			}
			catch (TaskCanceledException)
			{
				completed.SetResult(DownloadResult.Canceled);
			}
			// cannot close stream until all bytes are written..
			catch (InvalidOperationException e) when ((uint)e.HResult == 0x80131509)
			{
				completed.SetResult(DownloadResult.Canceled);
			}
			// Response.OutputStream.Write throws if downloader disconnected..
			catch (HttpListenerException e) when ((uint)e.HResult == 0x80004005)
			{
				completed.SetResult(DownloadResult.Interrupted);
			}
			catch (HttpException e) when ((uint)e.HResult == 0x800704CD)
			{
				completed.SetResult(DownloadResult.Interrupted);
			}
		}

		private async Task<byte[]> ReceiveBytes()
		{
			WebSocketReceiveResult result = await pendingReceive;
			int received = result.Count;

			while (!result.EndOfMessage)
			{
				result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, received, buffer.Length - received), cancel.Token);
				received += result.Count;
			}

			if (result.MessageType == WebSocketMessageType.Close)
			{
				socketClosed = true;
				closeStatus = result.CloseStatusDescription;
			}
			else
				pendingReceive = socket.ReceiveAsync(new ArraySegment<byte>(buffer, 0, buffer.Length), cancel.Token);

			return new ArraySegment<byte>(buffer, 0, received).ToArray();
		}
	}

	public class SocketRelayClient : IRelay
	{
		public event Action OnStart, OnCancel, OnDisconnect, OnComplete;
		public event Action<long, long?> OnProgress;
		public Task HandleDownloadRequest(HttpContextBase context) => throw new NotImplementedException();
		public string UserHostAddress { get; set; }

		private ClientWebSocket socket = new ClientWebSocket();
		private CancellationTokenSource cancel = new CancellationTokenSource();
		private Task listenTask;
		private Stream stream;
		private long bytesUploaded;

		public async Task<string> AddRelay(Uri server, Stream stream, string filename = null, string mimetype = null)
		{
			this.stream = stream;
			var timeout = new CancellationTokenSource(new TimeSpan(0, 0, 3));

			await socket.ConnectAsync(server, timeout.Token);
			await socket.SendString(filename ?? "", timeout.Token);
			await socket.SendString(stream.CanSeek ? stream.Length.ToString() : "", timeout.Token);
			await socket.SendString(mimetype ?? "", timeout.Token);
			var code = (await socket.ReceiveString(timeout.Token)).Replace("code=", "");
			listenTask = Task.Factory.StartNew(Run, cancel.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);

			return code;
		}

		public void Cancel()
		{
			cancel.Cancel();
			socket.CloseOutputAsync(WebSocketCloseStatus.Empty, null, CancellationToken.None);
			listenTask.Wait();
			OnCancel?.Invoke();
		}

		private async void Run()
		{
			byte[] buffer = new byte[65536];
			int lastProgress = 0;

			try
			{
				while (socket.State == WebSocketState.Open)
				{
					var msg = await socket.ReceiveString(cancel.Token);
					int len = buffer.Length;
					switch (msg?.Substring(0, 4))
					{
						case "comp":
							await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cancel.Token);
							OnComplete?.Invoke();
							break;

						case "disc":
							OnDisconnect?.Invoke();
							break;

						case "rang":
							var range = msg.Substring(7).Split('-');
							bytesUploaded = long.Parse(range[0]);
							long size = long.Parse(range[1]);
							len = (int)(size > buffer.Length ? (buffer.Length - (bytesUploaded % buffer.Length)) : size);
							if (stream.CanSeek && stream.Position != bytesUploaded)
								stream.Seek(bytesUploaded, SeekOrigin.Begin);
							OnStart?.Invoke();
							goto default;

						default:
							var read = await stream.ReadAsync(buffer, 0, len);
							if (!stream.CanSeek && read == 0)
							{
								await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cancel.Token);
								OnComplete?.Invoke();
								return;
							}
							await socket.SendAsync(new ArraySegment<byte>(buffer, 0, read), WebSocketMessageType.Binary, true, cancel.Token);
							bytesUploaded += read;
							if (OnProgress != null && Environment.TickCount - lastProgress > 1000)
							{
								lastProgress = Environment.TickCount;
								OnProgress(bytesUploaded, stream.CanSeek ? stream.Length : (long?)null);
							}
							break;
					}
				}
			}
			catch (OperationCanceledException)
			{
				if (socket.State == WebSocketState.Open)
					await socket.CloseAsync(WebSocketCloseStatus.Empty, null, CancellationToken.None);
			}
			catch (WebSocketException)
			{
				if (stream.CanSeek && bytesUploaded < stream.Length)
					OnCancel?.Invoke();
				else if (!stream.CanSeek)
					OnDisconnect?.Invoke();
			}

			if (!cancel.IsCancellationRequested && stream.CanSeek && bytesUploaded < stream.Length)
				OnCancel?.Invoke();
		}
	}

	public static partial class Extensions
	{
		public static (long, long) WriteCommonHeaders(this HttpContextBase context, string filename, long? filesize, string mimetype)
		{
			context.Response.BufferOutput = false;
			context.Response.AddHeader("Server", ""); // gratuitous header

			if (!string.IsNullOrEmpty(mimetype))
				context.Response.ContentType = mimetype;

			context.Response.AddHeader("Content-Disposition",
				string.IsNullOrEmpty(filename) ? "inline" : $"attachment; filename=\"{filename}\"");

			context.Response.AddHeader("Accept-Ranges", filesize.HasValue ? "bytes" : "none");

			// support range requests when the filesize is known..
			long offset = 0, count = 65536;
			if (filesize.HasValue)
			{
				count = filesize.Value;
				if (!string.IsNullOrEmpty(context.Request.Headers["Range"]))
				{
					string[] range = context.Request.Headers["Range"].Substring(6).Split('-');
					offset = long.Parse(range[0]);
					count = (string.IsNullOrEmpty(range[1]) ? count : long.Parse(range[1]) + 1) - offset;
					context.Response.AddHeader("Content-Range", $"bytes {offset}-{offset + count - 1}/{filesize.Value}");

					if (offset + count > filesize.Value)
						context.Response.StatusCode = 416;
					else
						context.Response.StatusCode = 206;
				}
				context.Response.AddHeader("Content-Length", count.ToString());
			}

			return (offset, count);
		}

		public static string GuessMimeType(this string filename)
		{
			string ext = Path.GetExtension(filename);
			switch (ext.ToLower())
			{
				case ".svg":
					return "image/svg+xml";

				case ".mp4":
				case ".m4v":
				case ".mkv":
					return "video/mp4";

				default:
					return MimeMapping.GetMimeMapping(ext);
			}
		}

		public static T Peek<T> (this ConcurrentQueue<T> queue)
		{
			queue.TryPeek(out T item);
			return item;
		}

		private static Regex ios = new Regex("iPad|iPhone|iPod", RegexOptions.Compiled);
		private static Regex media = new Regex("^video|audio", RegexOptions.Compiled | RegexOptions.IgnoreCase);

		public static bool IsAdaptiveStream(this HttpContextBase context) =>
			ios.IsMatch(context.Request.UserAgent ?? "") && media.IsMatch(context.Response.ContentType ?? "");

		// in adaptive streaming mode the player may have skipped bytes that it didn't need or the user may have seeked to the end, there's no perfect way to detect that they've actually finished downloading but checking that they've downloaded something from the last 3 chunks seems to work well enough..
		public static bool IsAdaptiveStreamDone(this DownloadProgress progress, long filesize, int chunksize) =>
			   progress.DownloadedBetween(Math.Max(0, filesize - (chunksize * 3)), chunksize) > 0
			&& progress.DownloadedBetween(Math.Max(0, filesize - (chunksize * 2)), chunksize) > 0
			&& progress.DownloadedBetween(Math.Max(0, filesize - (chunksize * 1)), chunksize) > 0;
	}
}
