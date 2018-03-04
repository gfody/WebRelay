using System;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;

namespace WebRelay
{
	public static class Helpers
	{
		public static string FormatBytes(this long bytes)
		{
			int place = bytes > 0 ? Convert.ToInt32(Math.Floor(Math.Log(bytes, 1024))) : 0;
			return Math.Round(bytes / Math.Pow(1024, place), 1).ToString() + " KMGTPE"[place] + "B";
		}

		public static bool AlreadyListening(this string listenPrefix)
		{
			int port = listenPrefix.StartsWith("https") ? 443 : 80;
			var match = new Regex(@":(\d+)").Match(listenPrefix);
			if (match.Success)
				port = int.Parse(match.Groups[1].Captures[0].Value);

			return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(x => x.Port == port);
		}
	}
}
