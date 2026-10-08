using System.Collections.Generic;

namespace XrSpatial.Spatial
{
    /// <summary>
    /// Several laser-pokeable panels (the keyboard in front of the palette) behind the tool's single UI hook. The first layer the ray hits gets the press; layers behind it are still
    /// updated for hover but see no trigger, so one press never lands on two panels.
    /// </summary>
    public sealed class UiLayers : IUiLayer
    {
        readonly List<IUiLayer> m_Layers = new List<IUiLayer>();

        public UiLayers(params IUiLayer[] layers) { m_Layers.AddRange(layers); }

        public bool HandlePointer(in PointerState s, out float hitDistance)
        {
            hitDistance = 0f;
            bool hit = false;
            foreach (var layer in m_Layers)
            {
                var st = s;
                if (hit) { st.triggerDown = st.triggerHeld = st.triggerUp = false; st.paletteToggle = false; }
                bool h = layer.HandlePointer(st, out float d);
                if (h && !hit) { hit = true; hitDistance = d; }
            }
            return hit;
        }
    }
}
