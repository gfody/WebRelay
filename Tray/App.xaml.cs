using Hardcodet.Wpf.TaskbarNotification;
using System;
using System.Configuration;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
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
		private HwndSource messageWindow;
		private MemoryMappedFile global;

		private void Application_Startup(object sender, StartupEventArgs e)
		{
			if (e.Args.Length != 1)
			{
				Shutdown();
				return;
			}

			if (!FirstInstance(out IntPtr handle))
			{
				var filename = new FileInfo(e.Args[0]).FullName;
				var copyData = new Win32.COPYDATA() { cbData = filename.Length + 1, lpData = Marshal.StringToHGlobalAnsi(filename) };
				var ptrCopyData = Marshal.AllocCoTaskMem(Marshal.SizeOf(copyData));
				Marshal.StructureToPtr(copyData, ptrCopyData, false);
				Win32.SendMessage(handle, Win32.WM_COPYDATA, IntPtr.Zero, ptrCopyData);
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

		public bool FirstInstance(out IntPtr handle)
		{
			try
			{
				global = MemoryMappedFile.OpenExisting(nameof(WebRelay));
				using (var r = global.CreateViewAccessor())
				{
					r.Read(0, out long value);
					handle = new IntPtr(value);
					return false;
				}
			}
			catch (FileNotFoundException)
			{
				messageWindow = new HwndSource(new HwndSourceParameters());
				handle = messageWindow.Handle;
				Win32.ChangeWindowMessageFilterEx(handle, Win32.WM_COPYDATA, Win32.ChangeWindowMessageFilterExAction.Allow, IntPtr.Zero);

				global = MemoryMappedFile.CreateNew(nameof(WebRelay), 8);
				using (var w = global.CreateViewAccessor())
					w.Write(0, (long)handle);

				messageWindow.AddHook(WndProc);
				return true;
			}
		}

		private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
		{
			if (msg == Win32.WM_COPYDATA)
			{
				var data = Marshal.PtrToStructure<Win32.COPYDATA>(lParam);
				AddRelay(Marshal.PtrToStringAnsi(data.lpData));
				handled = true;
			}
			return IntPtr.Zero;
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

		private static class Win32
		{
			public static uint WM_COPYDATA = 0x004A;

			[StructLayout(LayoutKind.Sequential)]
			public struct COPYDATA
			{
				public IntPtr dwData;
				public int cbData;
				public IntPtr lpData;
			}

			public enum ChangeWindowMessageFilterExAction : uint
			{
				Reset = 0,
				Allow = 1,
				DisAllow = 2
			}

			[DllImport("user32")]
			public static extern IntPtr SendMessage(IntPtr hWnd, UInt32 Msg, IntPtr wParam, IntPtr lParam);

			[DllImport("user32")]
			public static extern bool ChangeWindowMessageFilterEx(IntPtr hWnd, uint msg, ChangeWindowMessageFilterExAction action, IntPtr changeInfo);
		}
	}
}
