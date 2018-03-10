using Hardcodet.Wpf.TaskbarNotification;
using System;
using System.Configuration;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
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
		private bool remote, stayOpen;
		private Icon appIcon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);

		private void Application_Startup(object sender, StartupEventArgs e)
		{
			try
			{
				stayOpen = bool.Parse(ConfigurationManager.AppSettings["stayOpen"] ?? "true");
				if (e.Args.Length == 0 && !stayOpen)
				{
					Shutdown();
					return;
				}

				if (OneInstance.First())
					OneInstance.OnMessage += AddRelay;

				else if (e.Args.Length > 0)
				{
					OneInstance.SendMessage(new FileInfo(e.Args[0]).FullName);
					Shutdown();
					return;
				}

				hostName = ConfigurationManager.AppSettings["hostname"] ?? Environment.MachineName;
				remoteHost = ConfigurationManager.AppSettings["remoteHost"];
				listenPrefix = ConfigurationManager.AppSettings["listenPrefix"] ?? "http://*:80/";
				urlBase = listenPrefix.Replace("*", hostName).Replace("+", hostName).Replace(":80", "");
				var enableWebclient = bool.Parse(ConfigurationManager.AppSettings["enableWebClient"] ?? "true");
				var maxConnections = int.Parse(ConfigurationManager.AppSettings["maxConnections"] ?? "8");

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
					server = new RelayServer() { EnableBuiltinWebclient = enableWebclient };
					stop = new TaskCompletionSource<bool>();
					listen = server.Listen(listenPrefix, maxConnections, stop);
					if (listen.IsFaulted) throw listen.Exception.InnerException;
					server.OnSocketRelay += (file, size, code, relay) => AddRelayStatus(file, size, urlBase + code, relay);
				}

				urlBase += urlBase.EndsWith("/") ? "" : "/";
				urlBase = Regex.Replace(urlBase, "localhost", hostName, RegexOptions.IgnoreCase);

				var idleStatus = $"{(remote ? "Relaying to" : "Listening on")} {urlBase}";
				if (!remote) idleStatus += $"\r\nWebclient is {(enableWebclient ? "enabled" : "disabled")}\r\nMax connections {maxConnections}";

				relayStatus = new RelayStatus(idleStatus);
				notifyIcon = (TaskbarIcon)FindResource("NotifyIcon");
				notifyIcon.TrayToolTip = relayStatus;
				notifyIcon.DataContext = relayStatus;

				if (e.Args.Length > 0)
					AddRelay(e.Args[0]);
				else
					notifyIcon.Icon = appIcon;

			}
			catch (System.Net.HttpListenerException ex) when ((uint)ex.HResult == 0x80004005) // access denied
			{
				MessageBox.Show($"Listen requires admin or an explicit urlacl for your listen prefix.. E.g.: netsh http add urlacl url={listenPrefix} user=everyone", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
				Shutdown();
			}
			catch (Exception ex)
			{
				MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
				Shutdown();
			}
		}

		private async void AddRelay(string filename)
		{
			IRelay relay;
			var file = new FileInfo(filename);
			var stream = file.OpenRead();
			string code;
			if (remote)
			{
				relay = new SocketRelayClient();
				code = await (relay as SocketRelayClient).AddRelay(new Uri(remoteHost), stream, file.Name);
			}
			else
			{
				relay = new LocalRelay(stream, file.Name);
				code = server.AddRelay(relay);
			}

			relay.OnComplete += () => stream.Close();
			relay.OnCancel += () => stream.Close();
			AddRelayStatus(file.FullName, file.Length, urlBase + code, relay);
			Clipboard.SetDataObject(urlBase + code, true);
		}

		private void AddRelayStatus(string filename, long? filesize, string url, IRelay relay)
		{
			var status = new RelayStatus.Item(filename, filesize, url, relay, relayStatus.Relays);
			relay.OnStart += () => Current.Dispatcher.BeginInvoke((Action)ShowRelays);
			relay.OnDisconnect += () => Current.Dispatcher.BeginInvoke((Action)ShowRelays);

			void UpdateProgressIcon()
			{
				double totalbytes = relayStatus.Relays.Sum(x => x.TotalSize ?? 0);
				double downloaded = relayStatus.Relays.Sum(x => x.TotalSize.HasValue ? x.Downloaded : 0);
				notifyIcon.Icon = ProgressIcon(totalbytes > 0 ? downloaded / totalbytes : 0);
			}

			void RemoveAndShutdown()
			{
				relayStatus.Relays.Remove(status);
				if (relayStatus.Relays.Count == 1)
				{
					if (stayOpen)
						notifyIcon.Icon = appIcon;
					else
						Shutdown();
				}
			}

			relay.OnProgress += (downloaded, total) => UpdateProgressIcon();
			relay.OnCancel += () => Current.Dispatcher.BeginInvoke((Action)(() => RemoveAndShutdown()));
			relay.OnComplete += () => Current.Dispatcher.BeginInvoke((Action)(() =>
			{
				notifyIcon.ShowBalloonTip(filename, "Download complete", BalloonIcon.Info);
				RemoveAndShutdown();
			}));

			relayStatus.Relays.Add(status);
			UpdateProgressIcon();
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
				Opacity = 0.0,
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

			var ani = new DoubleAnimationUsingKeyFrames() { KeyFrames = new DoubleKeyFrameCollection()
			{
				new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0))),
				new LinearDoubleKeyFrame(0.85, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.25))),
				new LinearDoubleKeyFrame(0.85, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.5))),
				new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(3)))
			}};

			ani.Completed += (o, a) =>
			{
				popup.Close();
				notifyIcon.TrayToolTip = relayStatus;
				notifyIcon.DataContext = relayStatus;
			};
			popup.BeginAnimation(UIElement.OpacityProperty, ani);
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
