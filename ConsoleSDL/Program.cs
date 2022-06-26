using System;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;

namespace ConsoleSDL
{
    class Program
    {
        static IntPtr sdlWinPtr;
        static IntPtr sdlRender;
        static IntPtr sdlTexture;
        static void Main(string[] args)
        {

            Console.WriteLine("Hello World!");
            FFmpeg.AutoGen.ffmpeg.RootPath = "ScrcpyNet";
            SDL2.SDL.SDL_Init(SDL2.SDL.SDL_INIT_EVERYTHING);

            sdlWinPtr = SDL2.SDL.SDL_CreateWindow("scrcpy", 0, 0, 540, 720, SDL2.SDL.SDL_WindowFlags.SDL_WINDOW_HIDDEN | SDL2.SDL.SDL_WindowFlags.SDL_WINDOW_RESIZABLE);

            SharpAdbClient.AdbServer.Instance.StartServer("ScrcpyNet\\adb.exe", false);

            var first = new SharpAdbClient.AdbClient().GetDevices().First();
            var scrcpy = new ScrcpyNet.Scrcpy(first, null);
            scrcpy.OnLoadSizeEvent += Scrcpy_OnLoadSizeEvent;
            scrcpy.VideoStreamDecoder.NewFrameEvent += VideoStreamDecoder_NewFrameEvent;
            scrcpy.Start();


            //var quit = false;
            //while (true)
            //{
            //    SDL2.SDL.SDL_PollEvent(out var @event);
            //    switch (@event.type)
            //    {
            //        case SDL2.SDL.SDL_EventType.SDL_FIRSTEVENT:
            //            break;
            //        case SDL2.SDL.SDL_EventType.SDL_QUIT:
            //            quit = true;
            //            break;

            //        default:
            //            break;
            //    }

            //    if (quit) break;
            //}

            Console.ReadKey();
            scrcpy.Stop();

            SDL2.SDL.SDL_DestroyWindow(sdlWinPtr);
            SDL2.SDL.SDL_DestroyRenderer(sdlRender);
            SDL2.SDL.SDL_DestroyTexture(sdlTexture);
            SDL2.SDL.SDL_Quit();
        }

        [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)]
        public static extern int SDL_UpdateYUVTexture(IntPtr texture, IntPtr rect, IntPtr yPlane, int yPitch, IntPtr uPlane, int uPitch, IntPtr vPlane, int vPitch);

        [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)]
        public static extern int SDL_RenderCopy(IntPtr renderer, IntPtr texture, IntPtr srcrect, ref SDL2.SDL.SDL_Rect dstrect);

        static DateTime preTime;
        static SDL2.SDL.SDL_Rect rect = new SDL2.SDL.SDL_Rect();
        private unsafe static void VideoStreamDecoder_NewFrameEvent(FFmpeg.AutoGen.AVFrame frame)
        {
            if (frame.width != prevSize.Width || frame.height != prevSize.Height)
            {
                SetRenderSize(new Size(frame.width, frame.height));
                //根据窗体大小来居中渲染
               
                InitTexture();
            }

            var ret = SDL_UpdateYUVTexture(sdlTexture, IntPtr.Zero,
                 new IntPtr(frame.data[0]), frame.linesize[0],
                 new IntPtr(frame.data[1]), frame.linesize[1],
                 new IntPtr(frame.data[2]), frame.linesize[2]
                  );

            if (ret != 0)
                return;

            rect.w = prevSize.Width;
            rect.h = prevSize.Height;
            rect.x = 0;
            rect.y = 0;


            //refresh
            ret = SDL2.SDL.SDL_RenderClear(sdlRender);
            ret = SDL_RenderCopy(sdlRender, sdlTexture, IntPtr.Zero, ref rect);
            SDL2.SDL.SDL_RenderPresent(sdlRender);

            Console.WriteLine($"Texture Time -> {(DateTime.Now - preTime).TotalMilliseconds} ms");
            preTime = DateTime.Now;
        }

        private static void Scrcpy_OnLoadSizeEvent(System.Drawing.Size size)
        {
            SDL2.SDL.SDL_SetWindowSize(sdlWinPtr, 850, 480);
            //设置渲染大小
            SetRenderSize(size);
            SDL2.SDL.SDL_ShowWindow(sdlWinPtr);
            prevSize = size;
            InitTexture();
        }

        private static void SetRenderSize(Size size)
        {
            SDL2.SDL.SDL_GetWindowSize(sdlWinPtr, out var w, out var h);
            //设置渲染大小，居中
            var srcScale = size.Width * 1.0f / size.Height;
            var destScale = w * 1.0f / h;

            var renderWidth = w*1.0f;
            var renderHeight = h*1.0f;
            //计算长宽比
            if (srcScale - destScale >= 0 && srcScale - destScale <= 0.001)
            {
                //长宽比相同

            }
            else if (srcScale < destScale)
            {
                //源长宽比小于目标长宽比，源的高度大于目标的高度
                var newHeight = h * 1.0f * size.Width / h;
                renderHeight = newHeight;
            }
            else
            {
                //源长宽比大于目标长宽比，源的宽度大于目标的宽度
                var newWidth = w * 1.0f * size.Height / h;
                renderWidth = newWidth;
            }
            prevSize = new Size((int)renderWidth, (int)renderHeight);
        }

        static System.Drawing.Size prevSize;
        static void InitTexture()
        {
            if (sdlRender != IntPtr.Zero)
                SDL2.SDL.SDL_DestroyRenderer(sdlRender);
            if (sdlTexture != IntPtr.Zero)
                SDL2.SDL.SDL_DestroyTexture(sdlTexture);


            var access = SDL2.SDL.SDL_RendererFlags.SDL_RENDERER_ACCELERATED;
            sdlRender = SDL2.SDL.SDL_CreateRenderer(sdlWinPtr, -1, access);
            var pixFormat = SDL2.SDL.SDL_PIXELFORMAT_IYUV;
            sdlTexture = SDL2.SDL.SDL_CreateTexture(sdlRender, pixFormat,
                (int)SDL2.SDL.SDL_TextureAccess.SDL_TEXTUREACCESS_STREAMING, prevSize.Width, prevSize.Height);

        }
    }
}
