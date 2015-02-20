using System.Configuration;
using System.IO.MemoryMappedFiles;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Threading.Tasks;

namespace WebRelay
{
	public class WebRelayService : ServiceBase
	{
		private RelayServer server = new RelayServer();
		private TaskCompletionSource<bool> stopping = new TaskCompletionSource<bool>();
		private Task task;

		public void Start() => OnStart(null);

		protected override void OnStart(string[] args)
		{
			string listenPrefix = ConfigurationManager.AppSettings["listenPrefix"] ?? "http://*:80/";

			var security = new MemoryMappedFileSecurity();
			security.AddAccessRule(
				new AccessRule<MemoryMappedFileRights>(
					new SecurityIdentifier(WellKnownSidType.WorldSid, null), MemoryMappedFileRights.FullControl, AccessControlType.Allow));

			task = server.Listen(listenPrefix, int.Parse(ConfigurationManager.AppSettings["maxConnections"] ?? "8"), stopping);

			base.OnStart(args);
		}

		protected override void OnStop()
		{
			stopping.SetResult(true);

			base.OnStop();
		}
	}
}
