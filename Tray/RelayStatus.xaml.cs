using System;
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

		public RelayStatus()
		{
			var dummy = new Item();
			Relays = new ObservableCollection<Item>() { dummy };
			Relays.CollectionChanged += (s, e) => { dummy.Show = Relays.Count == 1; };

			InitializeComponent();
		}

		public class Item : INotifyPropertyChanged
		{
			private long downloaded;
			private double progress;
			private string bps, status = "waiting for connection..";
			private TimeSpan? remaining;

			private bool show;
			public bool NoItems { get; }
			public Item() { NoItems = true; Show = false; }

			public Item(FileInfo file, string url, IRelay relay, ObservableCollection<Item> collection)
			{
				Show = true;
				URL = url;
				Filename = file.Name;
				TotalSize = file.Length;
				using (var icon = Icon.ExtractAssociatedIcon(file.FullName))
				using (var bmp = icon.ToBitmap())
					FileIcon = Imaging.CreateBitmapSourceFromHBitmap(bmp.GetHbitmap(), IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

				if (relay != null)
				{
					long lastDownloaded = 0;
					double lastLastBps = 0, lastBps = 0, inverseTotal = TotalSize > 0 ? (100.0 / TotalSize) : 1;
					relay.OnProgress += (downloaded, total) =>
					{
						double bps = downloaded - lastDownloaded;
						bps = (bps + (lastBps > 0 ? lastBps : bps) + (lastLastBps > 0 ? lastLastBps : bps)) / 3.0;
						lastLastBps = lastBps; lastBps = bps; lastDownloaded = downloaded;

						Bps = $"{((long)bps).FormatBytes()}/sec";
						Downloaded = downloaded;
						Progress = downloaded * inverseTotal;
						Remaining = bps > 0 ? TimeSpan.FromSeconds((TotalSize - downloaded) / bps) : (TimeSpan?)null;
						Status = $"{Downloaded.FormatBytes()} of {TotalSize.FormatBytes()} downloaded";
					};

					relay.OnStart += () => Status = "download started..";
					relay.OnDisconnect += () => { Status = "disconnected, waiting to resume.."; Bps = null; Remaining = null; };
				}

				Cancel = new Command(() => { collection.Remove(this); relay?.Cancel(); });
				Copy = new Command(() => Clipboard.SetDataObject(URL, true));
			}

			public ImageSource FileIcon { get; }
			public long TotalSize { get; }
			public string Filename { get; }
			public string URL { get; }

			public long Downloaded { get => downloaded; set { downloaded = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Downloaded))); } }
			public double Progress { get => progress; set { progress = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Progress))); } }
			public string Status { get => status; set { status = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status))); } }
			public TimeSpan? Remaining { get => remaining; set { remaining = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Remaining))); } }
			public string Bps { get => bps; set { bps = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Bps))); } }
			public bool Show { get => show; set { show = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Show))); } }

			public ICommand Copy { get; }
			public ICommand Cancel { get; }
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
		public override DataTemplate SelectTemplate(object item, DependencyObject container) =>
			(container as FrameworkElement)?.FindResource(((RelayStatus.Item)item).NoItems ? "NoItemsTemplate" : "RelayTemplate") as DataTemplate;
	}

	public class MockRelays
	{
		private ObservableCollection<RelayStatus.Item> relays = new ObservableCollection<RelayStatus.Item>();
		public ObservableCollection<RelayStatus.Item> Relays { get => relays; }
		public MockRelays()
		{
			var f = Directory.EnumerateFiles(".").GetEnumerator();
			if (f.MoveNext()) relays.Add(new RelayStatus.Item(new FileInfo(f.Current), "http://fy.lc/ai5jo", null, relays) { });
			if (f.MoveNext()) relays.Add(new RelayStatus.Item(new FileInfo(f.Current), "http://fy.lc/g35qt", null, relays)
			{
				Status = $"10KB of 100KB downloaded",
				Remaining = TimeSpan.FromSeconds(100),
				Downloaded = 10000,
				Bps = "100KB/sec",
			});

			relays.Add(new RelayStatus.Item() { Show = true });
		}
	}
}
