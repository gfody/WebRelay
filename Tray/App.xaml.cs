using Hardcodet.Wpf.TaskbarNotification;
using System;
using System.Configuration;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Brush = System.Windows.Media.Brush;
using Color = System.Drawing.Color;
using Pen = System.Drawing.Pen;

namespace WebRelay
{
	public partial class App : Application
	{
		private RelayStatus relayStatus;
		private TaskbarIcon notifyIcon;
		private RelayServer server;
		private TaskCompletionSource<bool> stop;
		private Task<bool> listen;
		private string hostName, remoteHost, listenPrefix, urlBase;
		private bool remote;

		private void Application_Startup(object sender, StartupEventArgs e)
		{
			if (e.Args.Length != 1)
			{
				Shutdown();
				return;
			}

			if (OneInstance.First())
				OneInstance.OnMessage += AddRelay;
			else
			{
				OneInstance.SendMessage(new FileInfo(e.Args[0]).FullName);
				Shutdown();
				return;
			}

			hostName = ConfigurationManager.AppSettings["hostname"] ?? Environment.MachineName;
			remoteHost = ConfigurationManager.AppSettings["remoteHost"];
			listenPrefix = ConfigurationManager.AppSettings["listenPrefix"] ?? "http://*:80/";
			urlBase = listenPrefix.Replace("*", hostName).Replace("+", hostName).Replace(":80", "");

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

			relayStatus = new RelayStatus();
			notifyIcon = (TaskbarIcon)FindResource("NotifyIcon");
			notifyIcon.TrayToolTip = relayStatus;
			notifyIcon.DataContext = relayStatus;
			notifyIcon.Icon = ProgressIcon(0);

			AddRelay(e.Args[0]);
		}

		private void AddRelay(string filename)
		{
			IRelay relay;
			var file = new FileInfo(filename);
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

			var status = new RelayStatus.Item(file.FullName, urlBase + code, relay, relayStatus.Relays);

			long lastLastBps = 0, lastBps = 0, lastDownloaded = 0;
			double inverseTotal = 1.0 / file.Length;
			relay.OnProgress += (downloaded, total) =>
			{
				var bps = downloaded - lastDownloaded;
				bps = (bps + (lastBps > 0 ? lastBps : bps) + (lastLastBps > 0 ? lastLastBps : bps)) / 3;
				lastLastBps = lastBps; lastBps = bps; lastDownloaded = downloaded;
				double percentage = downloaded * inverseTotal;
				status.Progress = percentage * 100;
				status.Status = string.Format("{0} ({1:0}%) downloaded, time remaining {2} (at {3}/sec)", downloaded.FormatBytes(), status.Progress,
						bps > 0 ? new TimeSpan(0, 0, 0, (int)((total.Value - downloaded) / bps)).ToString() : "--:--:--", bps.FormatBytes());

				notifyIcon.Icon = ProgressIcon(0.01 * relayStatus.Relays.Average(x => x.Progress));
			};

			relay.OnComplete += () =>
			{
				stream.Close();
				notifyIcon.ShowBalloonTip(file.Name, "Download complete", BalloonIcon.Info);
				Current.Dispatcher.BeginInvoke((Action)delegate
				{
					relayStatus.Relays.Remove(status);
					if (relayStatus.Relays.Count == 0)
						Shutdown();
				});
			};

			relay.OnStart += () =>
			{
				status.Status = "download starting..";
				ShowRelays();
			};

			relay.OnDisconnect += () =>
			{
				status.Status = "disconnected, waiting to resume..";
				ShowRelays();
			};

			relay.OnCancel += () =>
			{
				stream.Close();
				if (relayStatus.Relays.Count == 0)
					Shutdown();
			};

			Clipboard.SetDataObject(urlBase + code, true);
			relayStatus.Relays.Add(status);
			ShowRelays();
		}

		private void ShowRelays()
		{
			notifyIcon.TrayToolTip = null;
			notifyIcon.DataContext = null;
			var bc = new BrushConverter();
			var desktop = SystemParameters.WorkArea;
			var popup = new Window()
			{
				Content = relayStatus,
				SizeToContent = SizeToContent.WidthAndHeight,
				ShowInTaskbar = false,
				WindowStyle = WindowStyle.None,
				ResizeMode = ResizeMode.NoResize,
				AllowsTransparency = true,
				Opacity = 0.85,
				BorderThickness = new Thickness(1),
				BorderBrush = (Brush)bc.ConvertFromString("#252525"),
				Background = (Brush)bc.ConvertFromString("#202020"),
				WindowStartupLocation = WindowStartupLocation.Manual,
				Topmost = true,
				ShowActivated = false,
			};
			popup.Show();
			popup.Left = desktop.Right - relayStatus.ActualWidth - 2;
			popup.Top = desktop.Bottom - relayStatus.ActualHeight - 2;
			DoubleAnimation da = new DoubleAnimation() { From = 0.85, To = 0, BeginTime = TimeSpan.FromSeconds(2.5), Duration = new Duration(TimeSpan.FromSeconds(0.5)) };
			da.Completed += (o, a) =>
			{
				popup.Close();
				notifyIcon.TrayToolTip = relayStatus;
				notifyIcon.DataContext = relayStatus;
			};
			popup.BeginAnimation(UIElement.OpacityProperty, da);
		}

		private static Icon ProgressIcon(double percentage)
		{
			using (var img = new Bitmap((int)SystemParameters.SmallIconWidth, (int)SystemParameters.SmallIconHeight))
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
