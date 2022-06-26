﻿using FFmpeg.AutoGen;
using Serilog;
using SharpAdbClient;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace ScrcpyNet
{
    public class Scrcpy
    {
        public string DeviceName { get; private set; } = "";
        public int Width { get; internal set; }
        public int Height { get; internal set; }
        public long Bitrate { get; set; } = 8000000;
        public string ScrcpyServerFile { get; set; } = "ScrcpyNet/scrcpy-server.jar";

        public bool Connected { get; private set; }
        public VideoStreamDecoder VideoStreamDecoder { get; }

        private Thread? videoThread;
        private Thread? controlThread;
        private Thread? bufferThread;
        private TcpClient? videoClient;
        private TcpClient? controlClient;
        private TcpListener? listener;
        private CancellationTokenSource? cts;

        private readonly AdbClient adb;
        private readonly DeviceData device;
        private readonly Channel<IControlMessage> controlChannel = Channel.CreateUnbounded<IControlMessage>();
        private readonly Channel<AVPacket> bufferChannel = Channel.CreateUnbounded<AVPacket>();
        private static readonly ArrayPool<byte> pool = ArrayPool<byte>.Shared;
        private static readonly ILogger log = Log.ForContext<VideoStreamDecoder>();
        private int port = 27183;

        public Scrcpy(DeviceData device, VideoStreamDecoder? videoStreamDecoder = null)
        {
            adb = new AdbClient();
            this.device = device;
            VideoStreamDecoder = videoStreamDecoder ?? new VideoStreamDecoder();
            VideoStreamDecoder.Scrcpy = this;
        }

        //public void SetDecoder(VideoStreamDecoder videoStreamDecoder)
        //{
        //    this.videoStreamDecoder = videoStreamDecoder;
        //    this.videoStreamDecoder.Scrcpy = this;
        //}

        public void Start(long timeoutMs = 5000)
        {
            if (Connected)
                throw new Exception("Already connected.");

            UpdatePort();
            MobileServerSetup();

            listener = new TcpListener(IPAddress.Loopback, this.port);
            listener.Start();

            MobileServerStart();

            int waitTimeMs = 0;
            while (!listener.Pending())
            {
                Thread.Sleep(10);
                waitTimeMs += 10;

                if (waitTimeMs > timeoutMs)
                    throw new Exception("Timeout while waiting for server to connect.");
            }

            videoClient = listener.AcceptTcpClient();
            log.Information("Video socket connected.");

            if (!listener.Pending())
                throw new Exception("Server is not sending a second connection request. Is 'control' disabled?");

            controlClient = listener.AcceptTcpClient();
            log.Information("Control socket connected.");

            ReadDeviceInfo();

            cts = new CancellationTokenSource();

            bufferThread = new Thread(BufferMain);
            bufferThread.Start();
            videoThread = new Thread(VideoMain) { Name = "ScrcpyNet Video" };
            controlThread = new Thread(ControllerMain) { Name = "ScrcpyNet Controller" };

            videoThread.Start();
            controlThread.Start();

            Connected = true;

            // ADB forward/reverse is not needed anymore.
            MobileServerCleanup();
        }

        private void UpdatePort()
        {
            //check port ,more deivce can connect
            for (int i = this.port; i <= 65535; i++)
            {
                if (!PortInUse(i))
                {
                    this.port = i;
                    return;
                }
            }
            //This is a nonsense
            throw new Exception("No port can use");
        }

        private bool PortInUse(int port)
        {
            var ipPorperties = IPGlobalProperties.GetIPGlobalProperties();
            var ipEndPoints = ipPorperties.GetActiveTcpListeners();
            var first = ipEndPoints.FirstOrDefault(i => i.Port == port);
            if (first != null)
                return true;

            ipEndPoints = ipPorperties.GetActiveUdpListeners();
            first = ipEndPoints.FirstOrDefault(i => i.Port == port);
            return first != null;

        }

        private async void BufferMain()
        {
            if (this.cts == null) return;
            await foreach (var item in this.bufferChannel.Reader.ReadAllAsync())
            {
                this.VideoStreamDecoder?.DecodePacket(item);
            }
        }

        public void Stop()
        {
            if (!Connected)
                throw new Exception("Not connected.");

            cts?.Cancel();

            videoThread?.Join();
            controlThread?.Join();
            listener?.Stop();
        }

        public void SendControlCommand(IControlMessage msg)
        {
            if (controlClient == null)
                log.Warning("SendControlCommand() called, but controlClient is null.");
            else
                controlChannel.Writer.TryWrite(msg);
        }

        public event Action<Size> OnLoadSizeEvent;
        private void ReadDeviceInfo()
        {
            if (videoClient == null)
                throw new Exception("Can't read device info when videoClient is null.");

            var infoStream = videoClient.GetStream();
            infoStream.ReadTimeout = 2000;

            // Read 68-byte header.
            var deviceInfoBuf = pool.Rent(68);
            int bytesRead = infoStream.Read(deviceInfoBuf, 0, 68);

            if (bytesRead != 68)
                throw new Exception($"Expected to read exactly 68 bytes, but got {bytesRead} bytes.");

            // Decode device name from header.
            var deviceInfoSpan = deviceInfoBuf.AsSpan();
            DeviceName = Encoding.UTF8.GetString(deviceInfoSpan[..64]).TrimEnd(new[] { '\0' });
            log.Information("Device name: " + DeviceName);

            Width = BinaryPrimitives.ReadInt16BigEndian(deviceInfoSpan[64..]);
            Height = BinaryPrimitives.ReadInt16BigEndian(deviceInfoSpan[66..]);
            log.Information($"Initial texture: {Width}x{Height}");
            this.OnLoadSizeEvent?.Invoke(new Size(Width, Height));
            pool.Return(deviceInfoBuf);
        }

        private async void VideoMain()
        {
            // Both of these should never happen.
            if (videoClient == null || VideoStreamDecoder == null) throw new Exception("videoClient is null.");
            if (cts == null) throw new Exception("cts is null.");

            var videoStream = videoClient.GetStream();

            int bytesRead;
            var metaBuf = pool.Rent(12);

            Stopwatch sw = new();

            while (!cts.Token.IsCancellationRequested)
            {
                // Read metadata (each packet starts with some metadata)
                try
                {
                    bytesRead = await videoStream.ReadAsync(metaBuf, 0, 12, this.cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException ex)
                {
                    // Ignore timeout errors.
                    if (ex.InnerException is SocketException x && x.SocketErrorCode == SocketError.TimedOut)
                        continue;
                    throw ex;
                }

                if (bytesRead != 12)
                    throw new Exception($"Expected to read exactly 12 bytes, but got {bytesRead} bytes.");

                sw.Restart();

                // Decode metadata

                var presentationTimeUs = BinaryPrimitives.ReadInt64BigEndian(metaBuf);
                var packetSize = BinaryPrimitives.ReadInt32BigEndian(metaBuf[8..]);

                // Read the whole frame, this might require more than one .Read() call.
                var packetBuf = pool.Rent(packetSize);
                var pos = 0;
                var bytesToRead = packetSize;

                while (bytesToRead != 0 && !cts.Token.IsCancellationRequested)
                {
                    bytesRead = await videoStream.ReadAsync(packetBuf, pos, bytesToRead, this.cts.Token);

                    if (bytesRead == 0)
                        throw new Exception("Unable to read any bytes.");

                    pos += bytesRead;
                    bytesToRead -= bytesRead;
                }

                if (!cts.Token.IsCancellationRequested)
                {
                    //Log.Verbose($"Presentation Time: {presentationTimeUs}us, PacketSize: {packetSize} bytes");
                    var packets = VideoStreamDecoder.Decode(packetBuf, presentationTimeUs);

                    if (packets.Count != 0)
                    {
                        foreach (var info in packets)
                        {
                            this.bufferChannel.Writer.TryWrite(info);
                        }
                    }

                    //var info = new BufferInfo()
                    //{
                    //    Buffer = new byte[packetSize],
                    //    Pts = presentationTimeUs
                    //};
                    //Array.Copy(packetBuf, info.Buffer, packetSize);
                    //this.bufferChannel.Writer.TryWrite(info);

                    //this.bufferChannel.Writer.TryWrite(packet);
                    log.Verbose("Received and decoded a packet in {@ElapsedMilliseconds} ms", sw.ElapsedMilliseconds);
                }

                sw.Stop();

                pool.Return(packetBuf);
                await Task.Delay(1);
            }
        }

        private async void ControllerMain()
        {
            // Both of these should never happen.
            if (controlClient == null) throw new Exception("controlClient is null.");
            if (cts == null) throw new Exception("cts is null.");

            var stream = controlClient.GetStream();

            try
            {
                await foreach (var cmd in controlChannel.Reader.ReadAllAsync(cts.Token))
                {
                    ControllerSend(stream, cmd);
                }
            }
            catch (OperationCanceledException) { }
        }

        // This needs to be in a separate method, because we can't use a Span<byte> inside an async function.
        private void ControllerSend(NetworkStream stream, IControlMessage cmd)
        {
            log.Debug("Sending control message: {@ControlMessage}", cmd.Type);
            var bytes = cmd.ToBytes();
            stream.Write(bytes);
        }

        private void MobileServerSetup()
        {
            MobileServerCleanup();

            // Push scrcpy-server.jar
            UploadMobileServer();

            // Create port reverse rule
            adb.CreateReverseForward(device, "localabstract:scrcpy", $"tcp:{this.port}", true);
        }

        /// <summary>
        /// Remove ADB forwards/reverses.
        /// </summary>
        private void MobileServerCleanup()
        {
            // Remove any existing network stuff.
            adb.RemoveAllForwards(device);
            adb.RemoveAllReverseForwards(device);
        }

        /// <summary>
        /// Start the scrcpy server on the android device.
        /// </summary>
        /// <param name="bitrate"></param>
        private void MobileServerStart()
        {
            log.Information("Starting scrcpy server...");

            var cts = new CancellationTokenSource();
            var receiver = new SerilogOutputReceiver();

            string version = "1.23";
            int maxFramerate = 60;
            ScrcpyLockVideoOrientation orientation = ScrcpyLockVideoOrientation.Unlocked; // -1 means allow rotate
            bool control = true;
            bool showTouches = false;
            bool stayAwake = false;

            var cmds = new List<string>
            {
                "CLASSPATH=/data/local/tmp/scrcpy-server.jar",
                "app_process",

                // Unused
                "/",

                // App entry point, or something like that.
                "com.genymobile.scrcpy.Server",

                version,
                "log_level=debug",
                $"bit_rate={Bitrate}"
            };

            if (maxFramerate != 0)
                cmds.Add($"max_fps={maxFramerate}");

            if (orientation != ScrcpyLockVideoOrientation.Unlocked)
                cmds.Add($"lock_video_orientation={(int)orientation}");

            cmds.Add("tunnel_forward=false");
            //cmds.Add("crop=-");
            cmds.Add($"control={control}");
            cmds.Add("display_id=0");
            cmds.Add($"show_touches={showTouches}");
            cmds.Add($"stay_awake={stayAwake}");
            cmds.Add("power_off_on_close=false");
            cmds.Add("downsize_on_error=true");
            cmds.Add("cleanup=true");

            string command = string.Join(" ", cmds);

            log.Information("Start command: " + command);
            _ = adb.ExecuteRemoteCommandAsync(command, device, receiver, cts.Token);
        }

        private void UploadMobileServer()
        {
            using SyncService service = new(new AdbSocket(new IPEndPoint(IPAddress.Loopback, AdbClient.AdbServerPort)), device);
            using Stream stream = File.OpenRead(ScrcpyServerFile);
            service.Push(stream, "/data/local/tmp/scrcpy-server.jar", 444, DateTime.Now, null, CancellationToken.None);
        }

        public struct BufferInfo
        {
            public byte[]? Buffer { get; set; }
            public long Pts { get; set; }
        }
    }
}
