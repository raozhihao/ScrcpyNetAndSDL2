using FFmpeg.AutoGen;
using ScrcpyNet;
using SDL2;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SdlWinApp
{
    public partial class ScrcpyFrm : Form
    {
        public ScrcpyFrm()
        {
            InitializeComponent();
            this.FormClosing += Form1_FormClosing;
        }

        private void Form1_FormClosing(object? sender, FormClosingEventArgs e)
        {
            this.scrcpy.Stop();
            SDL2.SDL.SDL_DestroyTexture(this.sdlTexture);
            SDL2.SDL.SDL_DestroyRenderer(this.sdlRender);
            SDL2.SDL.SDL_DestroyWindow(this.sdlWinPtr);
        }

        [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)]
        public static extern int SDL_UpdateYUVTexture(IntPtr texture, IntPtr rect, IntPtr yPlane, int yPitch, IntPtr uPlane, int uPitch, IntPtr vPlane, int vPitch);

        [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)]
        public static extern int SDL_RenderCopy(IntPtr renderer, IntPtr texture, IntPtr srcrect, ref SDL2.SDL.SDL_Rect dstrect);

        private IntPtr sdlWinPtr;
        private IntPtr sdlRender;
        private IntPtr sdlTexture;
        private ScrcpyNet.Scrcpy scrcpy;
        private void Form1_Load(object sender, EventArgs e)
        {
            FFmpeg.AutoGen.ffmpeg.RootPath = "ScrcpyNet";
            SDL2.SDL.SDL_Init(SDL2.SDL.SDL_INIT_VIDEO);
            this.pictureBox1.SizeChanged += PictureBox1_SizeChanged;
            this.pictureBox1.MouseDown += PictureBox1_MouseDown;
            this.pictureBox1.MouseUp += PictureBox1_MouseUp;
            this.pictureBox1.MouseMove += PictureBox1_MouseMove;

            //sdlWinPtr = SDL2.SDL.SDL_CreateWindow("scrcpy", 0, 0, 0, h: 720, SDL2.SDL.SDL_WindowFlags.SDL_WINDOW_HIDDEN | SDL2.SDL.SDL_WindowFlags.SDL_WINDOW_RESIZABLE);
            sdlWinPtr = SDL2.SDL.SDL_CreateWindowFrom(this.pictureBox1.Handle);
            SharpAdbClient.AdbServer.Instance.StartServer("ScrcpyNet\\adb.exe", false);

            var first = new SharpAdbClient.AdbClient().GetDevices().First();
            this.Text = first.Model;
            scrcpy = new ScrcpyNet.Scrcpy(first, null);
            scrcpy.OnLoadSizeEvent += Scrcpy_OnLoadSizeEvent;
            scrcpy.VideoStreamDecoder.NewFrameEvent += VideoStreamDecoder_NewFrameEvent;
            scrcpy.Start();
        }

        private void PictureBox1_MouseMove(object? sender, MouseEventArgs e)
        {
            var mousePos = e.Location;
            var pos = this.GetTouchPosition(mousePos);
            Trace.WriteLine(pos.Point.X + "," + pos.Point.Y);
        }

        private void PictureBox1_MouseUp(object? sender, MouseEventArgs e)
        {
            var mousePos = e.Location;
            if (mousePos.X <= this.updateRect.x || mousePos.Y <= this.updateRect.y)
            {
                return;
            }

            var pos = this.GetTouchPosition(mousePos);
            var msg = new TouchEventControlMessage();
            msg.Action = AndroidMotionEventAction.AMOTION_EVENT_ACTION_UP;
            msg.Position = pos;
            this.scrcpy.SendControlCommand(msg);
        }

        private void PictureBox1_MouseDown(object? sender, MouseEventArgs e)
        {
            var mousePos = e.Location;
            if (mousePos.X <= this.updateRect.x || mousePos.Y <= this.updateRect.y)
            {
                return;
            }

            var pos = this.GetTouchPosition(mousePos);
            var msg = new TouchEventControlMessage();
            msg.Action = AndroidMotionEventAction.AMOTION_EVENT_ACTION_DOWN;
            msg.Position = pos;
            this.scrcpy.SendControlCommand(msg);
        }

        private Position GetTouchPosition(System.Drawing.Point point)
        {
            var pos = new Position();
            //Mouse point to readl pixel point
            var x = point.X - this.updateRect.x;// real x
            var y = point.Y - this.updateRect.y;// real y
            var scale = this.updateRect.w * 1.0 / this.renderSize.Width;
            var mx = x / scale;
            var my = y / scale;
            pos.Point = new ScrcpyNet.Point() { X = (ushort)mx, Y = (ushort)my };
            pos.ScreenSize = new ScreenSize() { Width = (ushort)this.renderSize.Width, Height = (ushort)this.renderSize.Height };
            return pos;
        }

        private Size renderSize;
        private bool isResize;
        private static readonly object locker = new object();
        private void PictureBox1_SizeChanged(object? sender, EventArgs e)
        {
            //Lock render image
            lock (locker)
            {
                isResize = true;
                this.InitRender();
                isResize = false;
            }
        }

        private void InitRender()
        {
            if (this.sdlTexture != IntPtr.Zero)
                SDL2.SDL.SDL_DestroyTexture(this.sdlTexture);
            if (this.sdlRender != IntPtr.Zero)
                SDL2.SDL.SDL_DestroyRenderer(this.sdlRender);

            #region Scale Image

            SDL2.SDL.SDL_GetWindowSize(this.sdlWinPtr, out var winW, out var winH);
            var pw = this.renderSize.Width;
            var ph = this.renderSize.Height;

            this.updateRect.w = this.renderSize.Width;
            this.updateRect.h = this.renderSize.Height;
            this.updateRect.x = 0;
            this.updateRect.y = 0;

           var scaleW = pw > winW && ph > winH && (pw / ph > winW / winH);
           

            this.updateRect = this.MakeThumb(pw, ph, winW, winH, scaleW);

            #endregion


            var access = SDL2.SDL.SDL_RendererFlags.SDL_RENDERER_ACCELERATED;
            sdlRender = SDL2.SDL.SDL_CreateRenderer(sdlWinPtr, -1, access);
            var pixFormat = SDL2.SDL.SDL_PIXELFORMAT_IYUV;
            sdlTexture = SDL2.SDL.SDL_CreateTexture(sdlRender, pixFormat,
                (int)SDL2.SDL.SDL_TextureAccess.SDL_TEXTUREACCESS_TARGET, renderSize.Width, renderSize.Height);
        }

        private SDL.SDL_Rect MakeThumb(int pw, int ph, int ww, int wh, bool scaleW)
        {
            int x, y, w = pw, h = ph;
            if (scaleW)
            {
                w = ww;
                h = (int)(ph / (pw * 1.0 / ww));
            }
            else
            {
                h = wh;
                w = (int)(pw / (ph * 1.0 / wh));
            }
          
            x = (ww - w) / 2;
            y = (wh - h) / 2;
            return new SDL.SDL_Rect()
            {
                x = x,
                y = y,
                w = w,
                h = h
            };
        }
        private SDL2.SDL.SDL_Rect updateRect = new SDL2.SDL.SDL_Rect();
        private AVFrame lastFrame;
        private unsafe void VideoStreamDecoder_NewFrameEvent(AVFrame frame)
        {
            this.lastFrame = frame;
            lock (locker)
            {
                if (isResize)
                    return;//更新期间不绘制  

                if (frame.width != this.renderSize.Width || frame.height != this.renderSize.Height)
                {
                    //重新初始化
                    this.renderSize = new Size(frame.width, frame.height);
                    this.InitRender();
                }
                var ret = SDL_UpdateYUVTexture(sdlTexture, IntPtr.Zero,
                    new IntPtr(frame.data[0]), frame.linesize[0],
                    new IntPtr(frame.data[1]), frame.linesize[1],
                    new IntPtr(frame.data[2]), frame.linesize[2]
                     );

                if (ret != 0)
                    return;


                //refresh
                ret = SDL2.SDL.SDL_RenderClear(sdlRender);
                ret = SDL_RenderCopy(sdlRender, sdlTexture, IntPtr.Zero, ref updateRect);

                SDL2.SDL.SDL_RenderPresent(sdlRender);
            }
        }

        private void Scrcpy_OnLoadSizeEvent(Size size)
        {
            this.renderSize = size;
            this.InitRender();
        }

        private void pictureBox1_Click(object sender, EventArgs e)
        {

        }

        private unsafe void getBimapToolStripMenuItem_Click(object sender, EventArgs e)
        {
            //No surface , so, Getbitmap by scrcpy frame
            //var surface= SDL2.SDL.SDL_GetWindowSurface(this.sdlWinPtr);
            //var sufaceStruct = Marshal.PtrToStructure<SDL2.SDL.SDL_Surface>(surface);
            // SDL2.SDL.SDL_SaveBMP(surface, "1.bmp");

            var frameData = this.scrcpy.VideoStreamDecoder.GetFrameData(this.lastFrame);
            if (frameData == null)
                return;
            var bitmap = new Bitmap(frameData.Width, frameData.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), System.Drawing.Imaging.ImageLockMode.ReadWrite, bitmap.PixelFormat);

            var dest = new Span<byte>(data.Scan0.ToPointer(), frameData.Data.Length);
            frameData.Data.CopyTo(dest);

            bitmap.UnlockBits(data);
            this.pictureBox2.Image?.Dispose();
            this.pictureBox2.Image = bitmap;
        }
    }
}