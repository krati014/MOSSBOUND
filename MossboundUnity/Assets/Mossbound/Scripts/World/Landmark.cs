using UnityEngine;

namespace Mossbound
{
    /// <summary>A named point of interest. Used ONLY by the rover's proximity sensor
    /// (to tell the VLM what is physically next to it) - never to choose targets or steer.</summary>
    public class Landmark : MonoBehaviour
    {
        public string label;
        public string displayName;
        public float radius = 1f;
        public Color color = Color.white;
    }
}
