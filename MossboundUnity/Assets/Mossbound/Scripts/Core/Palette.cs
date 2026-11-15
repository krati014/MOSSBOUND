using UnityEngine;

namespace Mossbound
{
    /// <summary>MOSSBOUND design tokens: one palette shared by the world and the HUD.</summary>
    public static class Palette
    {
        // World
        public static readonly Color Midnight    = new Color(0.020f, 0.035f, 0.075f);
        public static readonly Color Fog         = new Color(0.030f, 0.065f, 0.095f);
        public static readonly Color DeepForest  = new Color(0.030f, 0.095f, 0.065f);
        public static readonly Color Moss        = new Color(0.100f, 0.260f, 0.150f);
        public static readonly Color MossTeal    = new Color(0.060f, 0.220f, 0.195f);
        public static readonly Color Soil        = new Color(0.075f, 0.065f, 0.080f);
        public static readonly Color Turquoise   = new Color(0.120f, 0.950f, 0.820f);
        public static readonly Color Violet      = new Color(0.600f, 0.360f, 1.000f);
        public static readonly Color Amber       = new Color(1.000f, 0.620f, 0.180f);
        public static readonly Color CrystalBlue = new Color(0.200f, 0.500f, 1.000f);
        public static readonly Color Ember       = new Color(1.000f, 0.120f, 0.180f);
        public static readonly Color Ivory       = new Color(0.880f, 0.860f, 0.800f);
        public static readonly Color Bark        = new Color(0.200f, 0.140f, 0.150f);
        public static readonly Color Stone       = new Color(0.240f, 0.270f, 0.310f);
        public static readonly Color Graphite    = new Color(0.060f, 0.070f, 0.080f);

        // HUD
        public static readonly Color UiPanel     = new Color(0.015f, 0.040f, 0.055f, 0.80f);
        public static readonly Color UiPanelHi   = new Color(0.050f, 0.100f, 0.125f, 0.92f);
        public static readonly Color UiLine      = new Color(0.120f, 0.950f, 0.820f, 0.20f);
        public static readonly Color UiText      = new Color(0.880f, 0.950f, 0.930f);
        public static readonly Color UiMuted     = new Color(0.500f, 0.620f, 0.640f);
        public static readonly Color UiError     = new Color(1.000f, 0.360f, 0.400f);

        public static string Hex(Color c) => ColorUtility.ToHtmlStringRGB(c);
    }
}
