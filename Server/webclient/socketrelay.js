var SocketRelay = {
	Client: function (host) {
		this.uploading = false;
		this.oncode = function (code) { };
		this.onstart = function () { };
		this.onfinish = function (status) { };
		this.ondisconnect = function () { };
		this.onprogress = function (eta, bps, progress) { };

		var chunkSize = 65536, chunk, lastChunk, uploaded = 0, lastUploaded = 0, size,
			timer, reader = new FileReader(), socket, wshost = host, self = this;

		this.cancel = function () {
			self.uploading = false;
			if (timer !== undefined) {
				clearInterval(timer);
				timer = undefined;
			}
			if (socket !== undefined && socket.readyState === 1) {
				socket.onclose = null;
				socket.close();
				self.onfinish("canceled");
			}
		};

		this.setFile = function (file, onerror) {
			if (onerror) {
				reader.onerror = onerror;
				reader.onloadend = function () {
					if (reader.error)
						onerror(reader.error);
					else
						self.setFile(file);
				};

				try {
					reader.readAsArrayBuffer(file.slice(0, 1));
				}
				catch (err) {
					onerror(err);
				}

				return;
			}

			self.cancel();
			socket = new WebSocket(wshost);
			socket.binaryType = "arraybuffer";

			timer = setInterval(function () {
				if (self.uploading) {
					var bps = uploaded - lastUploaded;
					self.onprogress((file.size - uploaded) / bps, bps, (uploaded / file.size) * 100);
					lastUploaded = uploaded;
				}
			}, 1000);

			socket.onopen = function () {
				socket.send(file.name);
				socket.send(file.size);
				socket.send(file.type);
			};

			reader.onloadend = function (e) {
				socket.send(e.target.result);
				uploaded += e.total;
			};

			socket.onmessage = function (e) {
				switch (e.data.slice(0, 4)) {
					case "code":
						self.oncode(e.data.substr(5));
						break;
					case "disc":
						self.uploading = false;
						self.ondisconnect();
						break;
					case "comp":
						socket.close(1000, "completed successfully");
						break;
					case "rang":
						var s = e.data.substr(7).split("-");
						uploaded = parseInt(s[0]);
						size = uploaded + parseInt(s[1]);
						chunk = Math.floor(uploaded / chunkSize) + 1;
						lastChunk = Math.floor(size / chunkSize) + 1;
						reader.readAsArrayBuffer(file.slice(uploaded, uploaded + (size > chunkSize ? chunkSize - (uploaded % chunkSize) : size)));
						self.onstart();
						self.uploading = true;
						break;
					default:
						reader.readAsArrayBuffer(file.slice(chunk++ * chunkSize, chunk * chunkSize - (chunk === lastChunk ? chunkSize - (size % chunkSize) : 0)));
						break;
				}
			};

			socket.onclose = function (e) {
				socket.onmessage = null;
				if (timer !== undefined) {
					clearInterval(timer);
					timer = undefined;
				}
				self.uploading = false;
				self.onfinish(e.reason);
			};
		};
	}
};