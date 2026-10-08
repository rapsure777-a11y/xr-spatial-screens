#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using UnityEngine;

namespace XrSpatial.Capture
{
    /// <summary>
    /// Decodes a JPEG with Android's own decoder (android.graphics.BitmapFactory, libjpeg-turbo) on a worker thread and leaves the pixels in a direct byte buffer,
    /// so the main thread only has to copy them into the texture. One instance per stream: it reuses its bitmap and its buffer while the picture size stays the same.
    /// The pixels are RGBA bytes, top row first (the order the panel shader expects). Not thread safe: one decode at a time, and the pixels stay valid until the next Decode.
    /// </summary>
    public sealed class AndroidJpegDecoder : IDisposable
    {
        [ThreadStatic] static bool s_Attached;
        static readonly object s_InitLock = new object();
        static AndroidJavaClass s_Factory, s_ByteBuffer;
        static AndroidJavaObject s_Argb;

        AndroidJavaObject m_Bitmap, m_Buf;
        int m_BufBytes;
        public IntPtr Pixels { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int Bytes => Width * Height * 4;

        static void Init()
        {
            lock (s_InitLock)
            {
                if (s_Factory != null) return;
                s_Factory = new AndroidJavaClass("android.graphics.BitmapFactory");
                s_ByteBuffer = new AndroidJavaClass("java.nio.ByteBuffer");
                using (var cfg = new AndroidJavaClass("android.graphics.Bitmap$Config")) s_Argb = cfg.GetStatic<AndroidJavaObject>("ARGB_8888");
            }
        }

        /// <summary>Decodes into this instance's buffer. Returns false when the data is not a decodable JPEG.</summary>
        public unsafe bool Decode(byte[] jpeg, int length, int expectedWidth, int expectedHeight)
        {
            if (!s_Attached) { AndroidJNI.AttachCurrentThread(); s_Attached = true; }
            Init();
            using (var opts = new AndroidJavaObject("android.graphics.BitmapFactory$Options"))
            {
                opts.Set<AndroidJavaObject>("inPreferredConfig", s_Argb);
                opts.Set<bool>("inMutable", true);
                if (m_Bitmap != null && m_Bitmap.Call<int>("getWidth") == expectedWidth && m_Bitmap.Call<int>("getHeight") == expectedHeight)
                    opts.Set<AndroidJavaObject>("inBitmap", m_Bitmap);                      // reuse the previous bitmap's memory
                var signed = (sbyte[])(Array)jpeg;
                var bmp = s_Factory.CallStatic<AndroidJavaObject>("decodeByteArray", signed, 0, length, opts);
                if (bmp == null || bmp.GetRawObject() == IntPtr.Zero) { bmp?.Dispose(); return false; }
                if (m_Bitmap == null || bmp.GetRawObject() != m_Bitmap.GetRawObject()) { m_Bitmap?.Dispose(); m_Bitmap = bmp; } else bmp.Dispose();
            }
            Width = m_Bitmap.Call<int>("getWidth"); Height = m_Bitmap.Call<int>("getHeight");
            int need = Width * Height * 4;
            if (m_Buf == null || m_BufBytes < need)
            {
                m_Buf?.Dispose();
                m_Buf = s_ByteBuffer.CallStatic<AndroidJavaObject>("allocateDirect", need);
                m_BufBytes = need;
                Pixels = (IntPtr)AndroidJNI.GetDirectBufferAddress(m_Buf.GetRawObject());
            }
            using (m_Buf.Call<AndroidJavaObject>("rewind")) { }
            m_Bitmap.Call("copyPixelsToBuffer", m_Buf);
            return Pixels != IntPtr.Zero;
        }

        public void Dispose() { m_Bitmap?.Dispose(); m_Buf?.Dispose(); m_Bitmap = null; m_Buf = null; Pixels = IntPtr.Zero; }
    }
}
#endif
