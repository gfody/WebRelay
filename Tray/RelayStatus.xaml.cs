using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WebRelay
{
	public partial class RelayStatus : UserControl
	{
		public ObservableCollection<Item> Relays { get; }

		public RelayStatus(string idleStatus = null)
		{
			var dummy = new Item() { Status = idleStatus };
			Relays = new ObservableCollection<Item>() { dummy };
			dummy.Cancel = new Command(() => App.Current.Shutdown());
			Relays.CollectionChanged += (s, e) => { dummy.Show = Relays.Count == 1; };

			InitializeComponent();
		}

		public class Item : INotifyPropertyChanged
		{
			private long downloaded;
			private double progress;
			private string bps, status = "waiting for connection..";
			private TimeSpan? remaining;

			private bool show = true;
			private bool isIndeterminate;
			public bool NoItems { get; }
			public Item() { NoItems = true; }

			public Item(string filename, long? size, string url, IRelay relay, ObservableCollection<Item> collection)
			{
				URL = url;
				Filename = Path.GetFileName(filename);
				TotalSize = size;

				if (File.Exists(filename))
				{
					using (var icon = Icon.ExtractAssociatedIcon(filename))
					using (var bmp = icon.ToBitmap())
						FileIcon = Imaging.CreateBitmapSourceFromHBitmap(bmp.GetHbitmap(), IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
				}

				if (relay != null)
				{
					long lastDownloaded = 0;
					double lastLastBps = 0, lastBps = 0, inverseTotal = (TotalSize ?? 0) > 0 ? (100.0 / TotalSize.Value) : 1;
					relay.OnProgress += (downloaded, total) =>
					{
						double bps = downloaded - lastDownloaded;
						bps = (bps + (lastBps > 0 ? lastBps : bps) + (lastLastBps > 0 ? lastLastBps : bps)) / 3.0;
						lastLastBps = lastBps; lastBps = bps; lastDownloaded = downloaded;

						Bps = $"{((long)bps).FormatBytes()}/sec";
						Downloaded = downloaded;
						Progress = downloaded * inverseTotal;
						if (TotalSize.HasValue)
						{
							Remaining = bps > 0 ? TimeSpan.FromSeconds((TotalSize.Value - downloaded) / bps) : (TimeSpan?)null;
							Status = $"{Downloaded.FormatBytes()} of {TotalSize.Value.FormatBytes()} downloaded";
						}
						else
							Status = $"{Downloaded.FormatBytes()} downloaded";
					};

					relay.OnStart += () => { Status = "download started.."; IsIndeterminate = !TotalSize.HasValue; };
					relay.OnDisconnect += () => { Status = "disconnected, waiting to resume.."; Bps = null; Remaining = null; IsIndeterminate = false; };
				}

				Cancel = new Command(() => { collection.Remove(this); relay?.Cancel(); });
				Copy = new Command(() => Clipboard.SetDataObject(URL, true));
			}

			public ImageSource FileIcon { get; }
			public long? TotalSize { get; }
			public string Filename { get; }
			public string URL { get; }
			public bool IsIndeterminate { get => isIndeterminate; set { isIndeterminate = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsIndeterminate))); } }
			public long Downloaded { get => downloaded; set { downloaded = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Downloaded))); } }
			public double Progress { get => progress; set { progress = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Progress))); } }
			public string Status { get => status; set { status = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status))); } }
			public TimeSpan? Remaining { get => remaining; set { remaining = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Remaining))); } }
			public string Bps { get => bps; set { bps = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Bps))); } }
			public bool Show { get => show; set { show = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Show))); } }

			public ICommand Copy { get; }
			public ICommand Cancel { get; set; }
			public event PropertyChangedEventHandler PropertyChanged;
		}

		public class Command : ICommand
		{
			private Action action;
			public Command(Action action) => this.action = action;
			public void Execute(object parameter) => action();
			public bool CanExecute(object parameter) => true;
			public event EventHandler CanExecuteChanged = delegate { };
		}
	}

	public class RelayTemplateSelector : DataTemplateSelector
	{
		public override DataTemplate SelectTemplate(object item, DependencyObject container) => (container as FrameworkElement)?.FindResource(((RelayStatus.Item)item).NoItems ? "NoItemsTemplate" : "RelayTemplate") as DataTemplate;
	}

	public class MockRelays
	{
		public List<RelayStatus.Item> Relays
		{
			get => new List<RelayStatus.Item>()
			{
				new RelayStatus.Item("some file.dat", 12345, "http://fy.lc/s92jk", null, null),
				new RelayStatus.Item(null, null, "http://localhost:81/s8j2k", null, null) { IsIndeterminate = true },
				new RelayStatus.Item() { Status = "Listening on http://*:80/\r\nWebclient is enabled" },
			};
		}
	}
}
