using System;
using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Threading.Tasks;
using System.Web;
using System.Web.WebSockets;

namespace WebRelay
{
	public class HttpListenerContextWrapper : HttpContextBase
	{
		private HttpListenerContext context;
		private HttpListenerRequestWrapper request;
		private HttpListenerResponseWrapper response;

		public HttpListenerContextWrapper(HttpListenerContext context)
		{
			this.context = context;
			request = new HttpListenerRequestWrapper(context.Request);
			response = new HttpListenerResponseWrapper(context.Response);
		}

		public override async void AcceptWebSocketRequest(Func<AspNetWebSocketContext, Task> callback)
		{
			await ((Func<WebSocketContext, Task>)callback)(await context.AcceptWebSocketAsync(null));
		}
		public override bool IsWebSocketRequest { get { return context.Request.IsWebSocketRequest; } }
		public override HttpResponseBase Response { get { return response; } }
		public override HttpRequestBase Request { get { return request; } }

		private class HttpListenerRequestWrapper : HttpRequestBase
		{
			private HttpListenerRequest request;

			public HttpListenerRequestWrapper(HttpListenerRequest request)
			{
				this.request = request;
			}

			public override string ApplicationPath { get { return string.Empty; } }
			public override NameValueCollection Headers { get { return request.Headers; } }
			public override string HttpMethod { get { return request.HttpMethod; } }
			public override bool IsLocal { get { return request.IsLocal; } }
			public override string RawUrl { get { return request.RawUrl; } }
			public override Uri Url { get { return request.Url; } }
			public override string UserAgent { get { return request.UserAgent; } }
			public override string UserHostAddress { get { return request.UserHostAddress; } }
		}

		private class HttpListenerResponseWrapper : HttpResponseBase
		{
			private HttpListenerResponse response;

			public HttpListenerResponseWrapper(HttpListenerResponse response)
			{
				this.response = response;
			}

			public override void AddHeader(string name, string value)
			{
				if (name == "Content-Length")
					response.ContentLength64 = long.Parse(value);
				else
					response.AddHeader(name, value);
			}
			public override bool BufferOutput
			{
				get
				{
					return false;
				}
				set
				{
				}
			}
			public override void ClearHeaders()
			{
				response.Headers.Clear();
			}
			public override void Close()
			{
				response.Close();
			}
			public override string ContentType
			{
				get
				{
					return response.ContentType;
				}
				set
				{
					response.ContentType = value;
				}
			}
			public override NameValueCollection Headers
			{
				get { return response.Headers; }
			}
			public override bool IsClientConnected
			{
				get { return true; }
			}
			public override Stream OutputStream
			{
				get { return response.OutputStream; }
			}
			public override int StatusCode
			{
				get
				{
					return response.StatusCode;
				}
				set
				{
					response.StatusCode = value;
				}
			}
		}
	}
}
