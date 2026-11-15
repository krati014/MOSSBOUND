using UnityEngine;

namespace Mossbound
{
    /// <summary>Small camera looking at MOSS's face; feeds the circular portrait in the dialogue box (rendered ~15x/s).</summary>
    public class PortraitCam : MonoBehaviour
    {
        Camera cam;
        float next;

        public static RenderTexture Create(RoverController rover)
        {
            var rt = new RenderTexture(256, 256, 24, RenderTextureFormat.ARGB32) { name = "MossPortrait" };
            rt.Create();
            var go = new GameObject("PortraitCam");
            go.transform.SetParent(rover.Head, false);
            go.transform.localPosition = new Vector3(0.25f, 0.08f, 1.7f);
            go.transform.localRotation = Quaternion.LookRotation(new Vector3(-0.25f, -0.05f, -1.7f));
            var cam = go.AddComponent<Camera>();
            cam.fieldOfView = 34f; cam.nearClipPlane = 0.1f; cam.farClipPlane = 60f;
            cam.cullingMask = ~((1 << 5) | (1 << RoverController.SignLayer));
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.targetTexture = rt; cam.depth = -6;
            var pc = go.AddComponent<PortraitCam>(); pc.cam = cam;
            if (Application.isPlaying) cam.enabled = false;
            return rt;
        }

        void LateUpdate()
        {
            if (!cam || Time.unscaledTime < next) return;
            next = Time.unscaledTime + 1f / 15f;
            cam.Render();
        }
    }
}
