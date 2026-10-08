using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Gate1
{
    /// <summary>
    /// Gate 2: receives JPEG frames from XrssStream over TCP (127.0.0.1:5600, reached through `adb reverse`), decodes the newest one per
    /// rendered frame onto a panel, and acknowledges each shown frame so the PC can measure the round trip. Receive/decode statistics go to the log.
    /// Wire format is described in Tools/XrssStream/Program.cs.
    /// </summary>
    public sealed class Gate2Stream : MonoBehaviour
    {
        public const string Host = "127.0.0.1";
        public const int Port = 5600;
        const uint FrameMagic = 0x46535258;

        Texture2D m_Tex;
        Renderer m_Panel;
        TextMesh m_Label;
        Thread m_Thread;
        volatile bool m_Quit;
        readonly object m_Lock = new object();
        byte[] m_Latest; int m_LatestW, m_LatestH; uint m_LatestSeq; long m_Received, m_Dropped;
        NetworkStream m_Net;
        string m_State = "connecting";

        // stats (main thread)
        int m_Shown; double m_DecodeMs, m_DecodeMax; long m_Bytes; float m_StatStart;
        public string Summary = "no stream yet";

        public static Gate2Stream Create(Vector3 centre, float width)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            UnityEngine.Object.Destroy(go.GetComponent<Collider>());
            go.name = "StreamPanel";
            go.transform.position = centre;
            go.transform.rotation = Quaternion.LookRotation(centre - new Vector3(0, centre.y, 0));
            go.transform.localScale = new Vector3(width, width * 9f / 16f, 1f);
            var s = go.AddComponent<Gate2Stream>();
            s.m_Panel = go.GetComponent<Renderer>();
            s.m_Tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            s.m_Tex.SetPixels32(new[] { new Color32(40, 40, 40, 255), new Color32(40, 40, 40, 255), new Color32(40, 40, 40, 255), new Color32(40, 40, 40, 255) }); s.m_Tex.Apply();
            var mat = new Material(Shader.Find("Unlit/Texture")) { mainTexture = s.m_Tex };
            s.m_Panel.sharedMaterial = mat;
            return s;
        }

        readonly object m_WriteLock = new object();
        public string LastInputSent = "none";
        int m_SentInputs;

        void Write(byte[] msg)
        {
            var net = m_Net;
            if (net == null) return;
            try { lock (m_WriteLock) net.Write(msg, 0, msg.Length); } catch { }
        }

        /// <summary>Pointer event for the PC: kind 0 move, 1 left down, 2 left up, 3 right click, 4 wheel; (u, v) on the streamed image, origin top-left.</summary>
        public void SendPointer(byte kind, float u, float v, float wheel)
        {
            var m = new byte[14]; m[0] = (byte)'P'; m[1] = kind;
            BitConverter.GetBytes(u).CopyTo(m, 2); BitConverter.GetBytes(v).CopyTo(m, 6); BitConverter.GetBytes(wheel).CopyTo(m, 10);
            Write(m);
            if (kind != 0) { m_SentInputs++; LastInputSent = $"#{m_SentInputs} kind {kind} at {u:0.000},{v:0.000}"; }
        }

        public void SendLost() => Write(new[] { (byte)'L' });

        public Transform PanelTransform => m_Panel.transform;
        public float Width => m_Panel.transform.localScale.x;
        public void SetWidth(float w) { var t = m_Panel.transform; float aspect = t.localScale.x / t.localScale.y; t.localScale = new Vector3(w, w / aspect, 1f); }

        /// <summary>Intersects a ray with the panel; uv is the position on the streamed image (0..1, origin top-left).</summary>
        public bool Raycast(Ray ray, out Vector2 uv, out Vector3 point)
        {
            uv = default; point = default;
            var t = m_Panel.transform;
            var plane = new Plane(-t.forward, t.position);
            if (!plane.Raycast(ray, out float d) || d <= 0f) return false;
            point = ray.GetPoint(d);
            var local = t.InverseTransformPoint(point);
            if (Mathf.Abs(local.x) > 0.5f || Mathf.Abs(local.y) > 0.5f) return false;
            uv = new Vector2(local.x + 0.5f, 0.5f - local.y);
            return true;
        }

        void Start()
        {
            m_Thread = new Thread(Receive) { IsBackground = true, Name = "Gate2Receive" };
            m_Thread.Start();
            m_StatStart = Time.unscaledTime;
        }

        void OnDestroy() { m_Quit = true; try { m_Net?.Close(); } catch { } }

        void Receive()
        {
            var hdr = new byte[20];
            while (!m_Quit)
            {
                try
                {
                    using var c = new TcpClient { NoDelay = true };
                    c.Connect(Host, Port);
                    var net = c.GetStream(); m_Net = net; m_State = "connected";
                    Debug.Log("[Gate2] connected to PC sender");
                    while (!m_Quit)
                    {
                        Read(net, hdr, 20);
                        if (BitConverter.ToUInt32(hdr, 0) != FrameMagic) throw new Exception("bad frame magic");
                        int len = (int)BitConverter.ToUInt32(hdr, 4), w = BitConverter.ToInt32(hdr, 8), h = BitConverter.ToInt32(hdr, 12);
                        uint seq = BitConverter.ToUInt32(hdr, 16);
                        var data = new byte[len];
                        Read(net, data, len);
                        lock (m_Lock) { if (m_Latest != null) m_Dropped++; m_Latest = data; m_LatestW = w; m_LatestH = h; m_LatestSeq = seq; m_Received++; }
                    }
                }
                catch (Exception e)
                {
                    if (!m_Quit) { m_State = "waiting for PC sender (" + e.GetType().Name + ")"; Thread.Sleep(1000); }
                }
                m_Net = null;
            }
        }

        static void Read(NetworkStream s, byte[] buf, int n)
        {
            int got = 0;
            while (got < n) { int r = s.Read(buf, got, n - got); if (r <= 0) throw new System.IO.EndOfStreamException(); got += r; }
        }

        void Update()
        {
            byte[] data; int w, h; uint seq;
            lock (m_Lock) { data = m_Latest; w = m_LatestW; h = m_LatestH; seq = m_LatestSeq; m_Latest = null; }
            if (data != null)
            {
                var sw = Stopwatch.StartNew();
                bool ok = m_Tex.LoadImage(data, false);           // replaces the texture contents and size
                sw.Stop();
                if (ok)
                {
                    double ms = sw.Elapsed.TotalMilliseconds; m_DecodeMs += ms; if (ms > m_DecodeMax) m_DecodeMax = ms;
                    m_Shown++; m_Bytes += data.Length;
                    float aspect = (float)m_Tex.width / m_Tex.height;
                    var t = m_Panel.transform; t.localScale = new Vector3(t.localScale.x, t.localScale.x / aspect, 1f);
                    var ack = new byte[5]; ack[0] = (byte)'A'; BitConverter.GetBytes(seq).CopyTo(ack, 1); Write(ack);
                }
            }
            float dt = Time.unscaledTime - m_StatStart;
            if (dt >= 3f)
            {
                long rec, drop; lock (m_Lock) { rec = m_Received; drop = m_Dropped; }
                Summary = $"{m_State} | shown {m_Shown / dt:0.0} fps, {m_Bytes * 8 / dt / 1e6:0.0} Mbit/s, decode avg {(m_Shown > 0 ? m_DecodeMs / m_Shown : 0):0.0} ms max {m_DecodeMax:0.0} ms | {m_Tex.width}x{m_Tex.height} | received {rec}, dropped {drop}";
                Debug.Log("[Gate2] " + Summary);
                m_Shown = 0; m_DecodeMs = 0; m_DecodeMax = 0; m_Bytes = 0; m_StatStart = Time.unscaledTime;
            }
        }
    }
}
