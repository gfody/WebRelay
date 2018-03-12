using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;

public static class OneInstance
{
	private static MemoryMappedFile mmf;
	private static HwndSource messageWindow;
	private static IntPtr messageHandle;

	public static event Action<string> OnMessage;

	public static bool First(string globalName = null)
	{
		globalName = globalName ?? Assembly.GetExecutingAssembly().GetName().Name;

		try
		{
			mmf = MemoryMappedFile.CreateNew(globalName, 8);
			messageWindow = new HwndSource(new HwndSourceParameters());
			ChangeWindowMessageFilterEx(messageWindow.Handle, WM_COPYDATA, 1, IntPtr.Zero);
			using (var w = mmf.CreateViewAccessor())
				w.Write(0, (long)messageWindow.Handle);

			messageWindow.AddHook(WndProc);

			return true;
		}
		catch (IOException e) when ((uint)e.HResult == 0x800700B7) // already exists
		{
			mmf = MemoryMappedFile.OpenExisting(globalName);
			using (var r = mmf.CreateViewAccessor())
			{
				r.Read(0, out long value);
				messageHandle = new IntPtr(value);

				return false;
			}
		}
	}

	public static void SendMessage(string message)
	{
		var bytes = Encoding.UTF8.GetBytes(message);
		var copyData = new COPYDATA() { cbData = bytes.Length, lpData = Marshal.AllocHGlobal(bytes.Length) };
		Marshal.Copy(bytes, 0, copyData.lpData, bytes.Length);
		SendMessage(messageHandle, WM_COPYDATA, IntPtr.Zero, ref copyData);
		Marshal.FreeHGlobal(copyData.lpData);
	}

	public static void Dispose()
	{
		mmf.Dispose();
	}

	private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
	{
		if (msg == WM_COPYDATA)
		{
			var data = Marshal.PtrToStructure<COPYDATA>(lParam);
			var bytes = new byte[data.cbData];
			Marshal.Copy(data.lpData, bytes, 0, bytes.Length);
			OnMessage?.Invoke(Encoding.UTF8.GetString(bytes));
			handled = true;
		}

		return IntPtr.Zero;
	}


	private const uint WM_COPYDATA = 0x004A;

	[StructLayout(LayoutKind.Sequential)]
	private struct COPYDATA
	{
		public IntPtr dwData;
		public int cbData;
		public IntPtr lpData;
	}

	[DllImport("user32")]
	private static extern IntPtr SendMessage(IntPtr hWnd, UInt32 Msg, IntPtr wParam, ref COPYDATA lParam);
	[DllImport("user32")]
	private static extern bool ChangeWindowMessageFilterEx(IntPtr hWnd, uint msg, uint action, IntPtr changeInfo);
}
