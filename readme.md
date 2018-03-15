## Summary
This is a utility for sharing files or streams via HTTP. It can host locally/directly or relay through a remote host. You can try out the web client at [http://fy.lc](http://fy.lc). The server can be hosted in IIS as well and is very easy to publish to Azure as a single-file Web App.

## Installation
Download the [release package](https://github.com/gfody/WebRelay/releases/download/v1.0/WebRelay.zip) and unpack it somewhere (preferably in your PATH if you'll be using the CLI). Run WebrelayTray.exe once as administrator with no arguments to install the registry key for the context menu (`[HKEY_CLASSES_ROOT\*\shell\WebRelay]`). For relaying you're good to go. For using the built-in server you may need to add acls for the ports you'll be using (`netsh http add urlacl url=http://*:80/` this isn't necessary if you run as administrator) and take care of any necessary firewall and/or router config.

## Usage
### Webrelay
This is the CLI util. It can be installed as a service and used as a remote host as well. All of the parameters can be specified in the config, arguments will take precedence. The service always uses the values in the config.

```powershell
Webrelay inputFile [[--listenPrefix|-l] <String>] [[--remoteHost|-r] <String>] [[--filename|-f] <String>]
    [[--contentType|-c] <String>] [--inline|-i] [[--maxConnections|-m] <Int>] [--install] [--uninstall]
    [[--username] <String>] [[--password] <String>] [--help] [--version]
```

Parameter | Description
----------|------------
inputFile | File to host, by default it will read from stdin
listenPrefix | Hostname and port on which to listen (in the [UrlPrefix format](https://msdn.microsoft.com/en-us/library/windows/desktop/aa364698(v=vs.85).aspx) e.g.: "http://*:80/")
remoteHost | Remote instance to relay through instead of listening on this machine (e.g.: "ws://fy.lc")
filename | Value to be used in content-disposition header, defaults to input filename unless `--inline` is specified
inline | Use content-disposition: inline (no download prompt)
contentType | Value to be used in content-type header, if left blank this will be inferred from the filename and defaults to "text/plain" if filename is blank
maxConnections | Max concurrent connections
install | Install service, the service is started automatically after it's installed
uninstall | Uninstall service, if it's running it will be stopped
username | Username for the service install (default is LocalSystem)
password | Password for the service install if necessary
help | Displays the parameter list and some examples
version | Displays version information

### Demo:
![Commandline](screenshots/app.gif)
------

### WebrelayTray
GUI for quickly copying the download link for any file from the right-click menu in explorer. It shows the download status in the tray and raises balloon notifications when downloads complete.

![Trayapp](screenshots/tray.gif)
------

### Webclient
The server has a very lightweight webclient built-in that you can enable or disable in the config. This is what it looks like:

![Webclient](screenshots/web.gif)
