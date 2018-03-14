using CommandLine;
using CommandLine.Text;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Configuration;
using System.Configuration.Install;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;

namespace WebRelay
{
	static class Program
	{
		private static readonly string HostName = ConfigurationManager.AppSettings["hostname"] ?? Environment.MachineName;

		private class Options
		{
			[Value(0, MetaName = "inputFile", HelpText = "Input filename (leave blank to read from stdin, if streaming this parameter can specify filename)")]
			public string InputFile { get; set; }

			[Option('l', "listenPrefix", HelpText = "Hostname and port on which to listen (e.g.: http://*:80/)")]
			public string ListenPrefix { get; set; } = ConfigurationManager.AppSettings["listenPrefix"] ?? "http://*:80/";

			[Option('r', "remoteHost", HelpText = "Remote instance to relay through instead of listening on this machine (e.g.: ws://fy.lc)")]
			public string RemoteHost { get; set; } = ConfigurationManager.AppSettings["remoteHost"];

			[Option('f', "filename", HelpText = "Value to be used in content-disposition header, defaults to input filename unless --inline is specified")]
			public string Filename { get; set; }

			[Option('i', "inline", HelpText = "Use inline content-disposition (no download prompt)")]
			public bool Inline { get; set; }

			[Option('c', "contentType", HelpText = "Value to be used in content-type header, defaults to \"text/plain\" if filename is blank")]
			public string ContentType { get; set; }

			[Option('m', "maxConnections", Default = 8, HelpText = "Max concurrent connections")]
			public int? MaxConnections { get; set; } = int.Parse(ConfigurationManager.AppSettings["maxConnections"] ?? "8");

			[Option("install", HelpText = "Install service")]
			public bool Install { get; set; }

			[Option("uninstall", HelpText = "Uninstall service")]
			public bool Uninstall { get; set; }

			[Option("username", Default = "LocalSystem", HelpText = "Username for service")]
			public string Username { get; set; }

			[Option("password", HelpText = "Password for service user if necessary")]
			public string Password { get; set; }

			[Option("service", Hidden = true)]
			public bool Service { get; set; } // secret argument to start in service mode

			[Option("stayopen", Hidden = true)]
			public bool Stayopen { get; set; } // secret argument to hang open with "press any key to exit"

			[Usage(ApplicationAlias = nameof(WebRelay))]
			public static IEnumerable<Example> Examples
			{
				get
				{
					yield return new Example("Host a file",
						new Options { InputFile = "input.dat", ListenPrefix = null, MaxConnections = null });

					yield return new Example("Host a stream (pipe to webrelay)",
						new UnParserSettings() { PreferShortName = true }, new Options { InputFile = "[filename]", ContentType = "text/plain", ListenPrefix = null, MaxConnections = null });

					yield return new Example("Relay to a remote host",
						new UnParserSettings() { PreferShortName = true }, new Options { InputFile = "input.dat", ListenPrefix = null, MaxConnections = null, RemoteHost = "ws://fy.lc" });

					yield return new Example("Install as service",
						new Options { Install = true, ListenPrefix = null, MaxConnections = null, Username = "[DOMAIN\\User]", Password = "[password]" });
				}
			}

			public string FinalFilename => Inline ? "" : (Filename ?? InputFile);
			public string FinalContentType => string.IsNullOrEmpty(ContentType) ? Filename?.GuessMimeType() ?? InputFile?.GuessMimeType() ?? "text/plain" : ContentType;
		}

		private static void Main(string[] args)
		{
			Parser.Default.ParseArguments<Options>(args).WithParsed(options =>
			{
				try
				{
					if (options.Service)
					{
						var service = new WebRelayService();
						if (Environment.UserInteractive)
						{
							service.Start(options.ListenPrefix);
							Console.WriteLine("Press any key to stop");
							Console.ReadKey();
							service.Stop();
						}
						else
							ServiceBase.Run(new ServiceBase[] { service });
					}
					else if (options.Install)
						InstallService(options);

					else if (options.Uninstall)
						RemoveService();

					else if (Console.IsInputRedirected)
						using (var stream = Console.OpenStandardInput())
							HandleRelay(stream, options);

					else if (!string.IsNullOrEmpty(options.InputFile))
						using (var stream = File.OpenRead(options.InputFile))
							HandleRelay(stream, options);

					else
						Console.WriteLine("try --help for examples");
				}
				catch (System.Net.HttpListenerException e) when ((uint)e.HResult == 0x80004005) // access denied
				{
					Console.WriteLine("Listen requires admin or an explicit urlacl for your listen prefix..");
					Console.WriteLine($"E.g.: netsh http add urlacl url={options.ListenPrefix} user=everyone");
				}
				catch (Exception e)
				{
					Console.WriteLine(e.Message);
				}
				finally
				{
					if (options.Stayopen)
					{
						Console.WriteLine("Press any key to exit");
						Console.ReadKey();
					}
				}
			});
		}

		private static void HandleRelay(Stream stream, Options options)
		{
			IRelay relay; string code, urlBase = options.ListenPrefix.Replace("*", HostName).Replace("+", HostName).Replace(":80", "");
			var done = new TaskCompletionSource<bool>();
			Task<bool> listenTask = null;

			if (!string.IsNullOrEmpty(options.RemoteHost))
			{
				relay = new SocketRelayClient();
				code = (relay as SocketRelayClient).AddRelay(new Uri(options.RemoteHost), stream, options.FinalFilename, options.FinalContentType).Result;
				urlBase = options.RemoteHost.Replace("ws", "http");
			}
			else if (options.ListenPrefix.AlreadyListening())
			{
				// if something is listening on the requested port, assume it's our service and try to connect to it..
				try
				{
					relay = new SocketRelayClient();
					code = (relay as SocketRelayClient).AddRelay(new Uri(urlBase.Replace("http", "ws")), stream, options.FinalFilename, options.FinalContentType).Result;
				}
				catch (Exception e)
				{
					throw new Exception($"Another process is already listening on {options.ListenPrefix}\r\n" +
						"Try a different port or install the service to avoid this error.", e);
				}
			}
			else
			{
				relay = new LocalRelay(stream, options.FinalFilename, options.FinalContentType);
				var server = new RelayServer() { AcceptSocketConnections = false };
				code = server.AddRelay(relay);
				listenTask = server.Listen(options.ListenPrefix, options.MaxConnections.Value, done);
				if (listenTask.IsFaulted) throw listenTask.Exception.InnerException;
			}

			int lastLen = Console.BufferWidth - 1;
			void PrintStatus(string status)
			{
				string line = string.Format("Status: {0}", status);
				int lineLen = line.Length;
				if (lineLen < lastLen)
					line = line.PadRight(lastLen);

				lastLen = lineLen;
				Console.CursorLeft = 0;
				Console.Write(line);
			}

			relay.OnStart += () => PrintStatus("download starting..");
			relay.OnComplete += () => { PrintStatus("download complete"); done.TrySetResult(true); };
			relay.OnDisconnect += () => PrintStatus("disconnected, waiting for resume..");
			relay.OnCancel += () => { PrintStatus("canceled"); done.TrySetResult(false); };

			long lastDownloaded = 0;
			double lastLastBps = 0, lastBps = 0, inverseTotal = (stream.CanSeek && stream.Length > 0) ? 100.0 / stream.Length : 1.0;
			void ProgressUpdate(long downloaded, long? total)
			{
				double bps = downloaded - lastDownloaded;
				bps = (bps + (lastBps > 0 ? lastBps : bps) + (lastLastBps > 0 ? lastLastBps : bps)) / 3.0;
				lastLastBps = lastBps; lastBps = bps; lastDownloaded = downloaded;

				if (total.HasValue)
				{
					var remaining = bps > 0 ? TimeSpan.FromSeconds((total.Value - downloaded) / bps).ToString("hh\\:mm\\:ss") : "--:--:--";
					PrintStatus($"{downloaded.FormatBytes()} ({downloaded * inverseTotal:0}%) downloaded, time remaining {remaining} (at {((long)bps).FormatBytes()}/sec)");
				}
				else
					PrintStatus($"{downloaded.FormatBytes()} downloaded (at {((long)bps).FormatBytes()}/sec)");
			}

			relay.OnProgress += (transferred, total) => ProgressUpdate(transferred, total);

			Console.WriteLine($"Download link: {urlBase.TrimEnd('/')}/{code}");
			Console.WriteLine($"Press {(Console.IsInputRedirected ? "CTRL+C" : "any key")} to cancel");
			Console.WriteLine();
			PrintStatus("waiting for connection..");

			if (Console.IsInputRedirected)
			{
				Console.CancelKeyPress += (e, args) => { done.TrySetResult(false); relay.Cancel(); };
				done.Task.Wait();
			}
			else
			{
				while (!done.Task.IsCompleted)
				{
					Thread.Sleep(100);
					if (Console.KeyAvailable)
					{
						done.TrySetResult(false);
						relay.Cancel();
					}
				}
			}

			if (listenTask != null && listenTask.IsFaulted)
			{
				PrintStatus("error");
				Console.WriteLine();
				throw listenTask.Exception.InnerException;
			}

			if (done.Task.Result)
				PrintStatus("completed successfully");
			else
				PrintStatus("canceled");
		}

		#region service

		public class WebRelayService : ServiceBase
		{
			private RelayServer server;
			private TaskCompletionSource<bool> stopping;
			private Task task;

			public void Start(string listenPrefix = null) => OnStart(new string[] { listenPrefix });

			protected override void OnStart(string[] args)
			{
				server = new RelayServer() { EnableBuiltinWebclient = bool.Parse(ConfigurationManager.AppSettings["enableWebClient"] ?? "true") };
				stopping = new TaskCompletionSource<bool>();
				task = server.Listen(
					args.Length > 0 ? args[0] : ConfigurationManager.AppSettings["listenPrefix"] ?? "http://*:80/",
					int.Parse(ConfigurationManager.AppSettings["maxConnections"] ?? "8"),
					stopping);

				base.OnStart(args);
			}

			protected override void OnStop()
			{
				stopping.SetResult(true);

				base.OnStop();
			}
		}


		private static void InstallService(Options o)
		{
			var path = Assembly.GetEntryAssembly().Location;

			if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
			{
				o.Stayopen = true;
				Process.Start(new ProcessStartInfo(path, Parser.Default.FormatCommandLine(o)) { Verb = "runas" });
				o.Stayopen = false;
				return;
			}

			var spi = new ServiceProcessInstaller() { Context = new InstallContext(null, new string[] { $"/assemblypath=\"{path}\" --service" }) };

			if (string.IsNullOrEmpty(o.Username))
				spi.Account = ServiceAccount.LocalSystem;
			else if (Enum.TryParse(o.Username, out ServiceAccount account))
				spi.Account = account;
			else
			{
				spi.Account = ServiceAccount.User;
				spi.Username = o.Username;
				spi.Password = o.Password;
			}

			spi.Installers.Add(new ServiceInstaller()
			{
				ServiceName = nameof(WebRelay),
				DisplayName = nameof(WebRelay),
				Description = "Thin HTTP server used by WebRelay console app and shell extension.",
				StartType = ServiceStartMode.Automatic
			});

			spi.Install(new ListDictionary());

			new ServiceController(nameof(WebRelay)).Start();
		}

		private static void RemoveService()
		{
			if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
			{
				Process.Start(new ProcessStartInfo(Assembly.GetEntryAssembly().Location, Parser.Default.FormatCommandLine(
					new Options() { Uninstall = true, Stayopen = true }))
				{ Verb = "runas" });
				return;
			}

			var sc = new ServiceController(nameof(WebRelay));
			if (sc.Status == ServiceControllerStatus.Running) sc.Stop();
			new ServiceInstaller() { Context = new InstallContext(), ServiceName = nameof(WebRelay) }.Uninstall(null);
		}

		#endregion
	}
}
