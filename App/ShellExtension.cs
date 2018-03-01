using SharpShell.Attributes;
using SharpShell.SharpContextMenu;
using System;
using System.Configuration;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WebRelay
{
	[ComVisible(true)]
	[COMServerAssociation(AssociationType.AllFiles)]
	public class WebRelayExtension : SharpContextMenu
	{
		private RelayServer server;
		private TaskCompletionSource<bool> stop;
		private Task<bool> listen;
		private bool remote, faulted;
		private string remoteHost, listenPrefix, urlBase, hostName;

		public WebRelayExtension() => Init();
		~WebRelayExtension() => stop?.SetResult(false);

		private void Init()
		{
			hostName = ConfigurationManager.AppSettings["hostname"] ?? Environment.MachineName;
			remoteHost = ConfigurationManager.AppSettings["remoteHost"];
			listenPrefix = ConfigurationManager.AppSettings["listenPrefix"] ?? "http://*:80/";
			urlBase = listenPrefix.Replace("*", hostName).Replace("+", hostName).Replace(":80", "");
			try
			{
				if (!string.IsNullOrEmpty(remoteHost))
				{
					remote = true;
					urlBase = remoteHost.Replace("ws", "http");
				}
				else if (listenPrefix.AlreadyListening())
				{
					remote = true;
					remoteHost = urlBase.Replace("http", "ws");
				}
				else
				{
					remote = false;
					server = new RelayServer();
					stop = new TaskCompletionSource<bool>();
					listen = server.Listen(listenPrefix, int.Parse(ConfigurationManager.AppSettings["maxConnections"] ?? "8"), stop);
				}

				faulted = false;
			}
			catch (Exception e)
			{
				EventLog.WriteEntry(nameof(WebRelayExtension), e.ToString(), EventLogEntryType.Error);

				faulted = true;
			}
		}

		protected override bool CanShowMenu() => SelectedItemPaths.Count() == 1;

		protected override ContextMenuStrip CreateMenu()
		{
			if (faulted)
			{
				ConfigurationManager.RefreshSection("appSettings");
				Init();
			}

			var menu = new ContextMenuStrip();
			ToolStripMenuItem item;

			if (faulted)
				item = new ToolStripMenuItem($"{nameof(WebRelayExtension)} failed (see eventlog)") { Enabled = false };
			else
				item = new ToolStripMenuItem($"Copy download link", null, OnClick);

			menu.Items.Add(item);
			return menu;
		}

		private void OnClick(object sender, EventArgs e)
		{
			IRelay relay;
			var file = new FileInfo(SelectedItemPaths.First());
			var stream = file.OpenRead();
			string code;
			if (remote)
			{
				relay = new SocketRelayClient(new Uri(remoteHost), stream, out code, file.Name);
			}
			else
			{
				relay = new LocalRelay(stream, file.Name);
				code = server.AddRelay(relay);
			}

			var tray = new NotifyIcon() { Icon = ProgressIcon(0), Visible = true };
			SetNotifyIconText(tray, $"{file.Name}\r\nwaiting for connection..");
			tray.ContextMenu = new ContextMenu(new MenuItem[]
			{
				new MenuItem("Cancel", (o, a) => relay.Cancel()),
				new MenuItem("Copy download link", (o, a) => Clipboard.SetDataObject(urlBase + code, true, 2, 100))
			});

			long lastLastBps = 0, lastBps = 0, lastDownloaded = 0;
			double inverseTotal = 1.0 / file.Length;
			relay.OnProgress += (downloaded, total) =>
			{
				var bps = downloaded - lastDownloaded;
				bps = (bps + (lastBps > 0 ? lastBps : bps) + (lastLastBps > 0 ? lastLastBps : bps)) / 3;
				lastLastBps = lastBps; lastBps = bps; lastDownloaded = downloaded;
				double percentage = downloaded * inverseTotal;
				SetNotifyIconText(tray, string.Format("{0}\r\ndownloading at {1}/sec\r\ntime remaining {2}", file.Name, bps.FormatBytes(),
					bps > 0 ? new TimeSpan(0, 0, 0, (int)((total.Value - downloaded) / bps)).ToString() : "--:--:--"));
				tray.Icon = ProgressIcon(percentage);
			};

			relay.OnComplete += () =>
			{
				stream.Close();
				tray.ShowBalloonTip(3000, "Download complete", file.Name, ToolTipIcon.Info);
				tray.Dispose();
			};

			relay.OnDisconnect += () =>
			{
				tray.ShowBalloonTip(3000, "Download disconnected", file.Name, ToolTipIcon.Warning);
				SetNotifyIconText(tray, $"{file.Name}\r\nwaiting to resume..");
			};

			relay.OnCancel += () =>
			{
				stream.Close();
				tray.ShowBalloonTip(3000, "Download canceled", file.Name, ToolTipIcon.Warning);
				tray.Dispose();
			};

			Clipboard.SetDataObject(urlBase + code, true, 2, 100);
		}

		public static void SetNotifyIconText(NotifyIcon tray, string text)
		{
			if (text.Length > 128) text = text.Substring(0, 128);

			// workaround the false 64 char limit..
			Type t = typeof(NotifyIcon);
			BindingFlags hidden = BindingFlags.NonPublic | BindingFlags.Instance;
			t.GetField("text", hidden).SetValue(tray, text);
			if ((bool)t.GetField("added", hidden).GetValue(tray))
				t.GetMethod("UpdateIcon", hidden).Invoke(tray, new object[] { true });
		}

		private static Icon ProgressIcon(double percentage)
		{
			using (var img = new Bitmap(SystemInformation.SmallIconSize.Width, SystemInformation.SmallIconSize.Height))
			using (var g = Graphics.FromImage(img))
			{
				g.SmoothingMode = SmoothingMode.HighQuality;
				g.FillEllipse(new Pen(Color.FromArgb(64, Color.White)).Brush, 1, 1, img.Width - 2, img.Height - 2);
				g.FillPie(new Pen(Color.White).Brush, 1, 1, img.Width - 2, img.Height - 2, 0, (int)(percentage * 360.0));

				//HACK: there's a better way to do this..
				using (var ms = new MemoryStream())
				{
					var icoHeader = new byte[] { 0, 0, 1, 0, 1, 0, (byte)img.Width, (byte)img.Height, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 22, 0, 0, 0 };
					ms.Write(icoHeader, 0, icoHeader.Length);
					img.Save(ms, ImageFormat.Png);
					var size = (int)ms.Position - icoHeader.Length;
					ms.Position = icoHeader.Length - 8;
					ms.Write(BitConverter.GetBytes(size), 0, 4);
					ms.Position = 0;

					return new Icon(ms);
				}
			}
		}
	}
}
