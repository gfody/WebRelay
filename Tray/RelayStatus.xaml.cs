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
		public ObservableCollection<Item> Relays { get; } = new ObservableCollection<Item>();

		public RelayStatus()
		{
			InitializeComponent();
		}

		public class Item : INotifyPropertyChanged
		{
			public Item(string filename, string url, IRelay relay, ObservableCollection<Item> collection)
			{
				URL = url;
				Filename = Path.GetFileName(filename);
				using (var icon = Icon.ExtractAssociatedIcon(filename))
				using (var bmp = icon.ToBitmap())
					FileIcon = Imaging.CreateBitmapSourceFromHBitmap(bmp.GetHbitmap(), IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

				Cancel = new Command(() => { collection.Remove(this); relay?.Cancel(); });
				Copy = new Command(() => Clipboard.SetDataObject(URL, true));
			}

			public ImageSource FileIcon { get; private set; }

			public event PropertyChangedEventHandler PropertyChanged;
			public string Filename { get; set; }
			private double progress;
			public double Progress { get => progress; set { progress = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Progress))); } }
			public string URL { get; set; }
			private string status = "waiting for connection..";
			public string Status { get => status; set { status = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status))); } }

			public ICommand Copy { get; set; }
			public ICommand Cancel { get; set; }
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

	public class MockRelays
	{
		private ObservableCollection<RelayStatus.Item> relays = new ObservableCollection<RelayStatus.Item>();
		public ObservableCollection<RelayStatus.Item> Relays { get => relays; }
		public MockRelays()
		{
			var f = Directory.EnumerateFiles(".").GetEnumerator();
			if (f.MoveNext()) relays.Add(new RelayStatus.Item(f.Current, "http://fy.lc/ai5jo", null, relays));
			if (f.MoveNext()) relays.Add(new RelayStatus.Item(f.Current, "http://fy.lc/g35qt", null, relays) { Progress = 24, Status = "23MB (24%) downloaded, time remaining 00:02:34 (at 400kb/sec)" });
			if (f.MoveNext()) relays.Add(new RelayStatus.Item(f.Current, "http://fy.lc/s88ui", null, relays) { Progress = 50, Status = "500MB (50%) downloaded, time remaining 00:15:34 (at 3.8MB/sec)" });
		}
	}
}
