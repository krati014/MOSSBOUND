using System;
using System.Collections;
using UnityEngine;

namespace Mossbound
{
    /// <summary>
    /// MOSS's 360-degree vision: one camera rendered at four yaws (FRONT, RIGHT, BACK, LEFT) into four RenderTextures.
    /// Frames are JPEG/base64 encoded for the VLM. The exact camera pose of each view is remembered so the
    /// VLM's bounding box can later be projected back into the 3D world (see TargetLocator).
    /// </summary>
    public class CameraCapture : MonoBehaviour
    {
        public const int Width = 644, Height = 364;
        public const float HorizontalFov = 90f;
        public static readonly string[] ViewNames = { "FRONT", "RIGHT", "BACK", "LEFT" };
        public static readonly float[] ViewYaws = { 0f, 90f, 180f, -90f };

        public Camera cam;
        public RenderTexture[] Views { get; private set; }
        public RenderTexture Texture => Views[0];
        public Pose[] CapturePoses { get; } = new Pose[4];

        Texture2D readback;
        Quaternion baseLocalRot;
        float nextPreview;
        int previewIndex;
        public float previewRendersPerSecond = 20f; // round-robin across the 4 views (GPU-friendly)

        public void Init(Camera c)
        {
            cam = c;
            baseLocalRot = cam.transform.localRotation;
            Views = new RenderTexture[4];
            for (int i = 0; i < 4; i++)
            {
                Views[i] = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32) { name = "MossVision_" + ViewNames[i], antiAliasing = 1 };
                Views[i].Create();
            }
            cam.targetTexture = Views[0];
            cam.fieldOfView = Camera.HorizontalToVerticalFieldOfView(HorizontalFov, (float)Width / Height);
            readback = new Texture2D(Width, Height, TextureFormat.RGB24, false);
        }

        void RenderView(int i)
        {
            cam.transform.localRotation = Quaternion.Euler(0, ViewYaws[i], 0) * baseLocalRot;
            cam.targetTexture = Views[i];
            cam.Render();
            CapturePoses[i] = new Pose(cam.transform.position, cam.transform.rotation);
            cam.transform.localRotation = baseLocalRot;
        }

        string Read(int i)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = Views[i];
            readback.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
            readback.Apply();
            RenderTexture.active = prev;
            return Convert.ToBase64String(readback.EncodeToJPG(85));
        }

        /// <summary>Render all four views now and return them as base64 JPEGs (FRONT, RIGHT, BACK, LEFT).</summary>
        public string[] RenderAll()
        {
            var r = new string[4];
            for (int i = 0; i < 4; i++) { RenderView(i); r[i] = Read(i); }
            return r;
        }

        public IEnumerator Capture(Action<string[]> done)
        {
            yield return new WaitForEndOfFrame();
            done(RenderAll());
        }

        /// <summary>Render + encode only the FRONT view now; returns the pose it was taken from (for the detection overlay).</summary>
        public string CaptureFront(out Pose pose)
        {
            RenderView(0);
            pose = CapturePoses[0];
            return Read(0);
        }

        /// <summary>World-space ray through a pixel of a frame taken from an explicit camera pose.</summary>
        public Ray PixelRayFrom(Pose pose, float px, float py)
        {
            var t = cam.transform;
            Vector3 savedPos = t.position; Quaternion savedRot = t.rotation;
            t.SetPositionAndRotation(pose.position, pose.rotation);
            var ray = cam.ViewportPointToRay(new Vector3(px / Width, 1f - py / Height, 0f));
            t.SetPositionAndRotation(savedPos, savedRot);
            return ray;
        }

        /// <summary>World-space ray through a pixel (view-local, y down) of a captured view.</summary>
        public Ray PixelRay(int view, float px, float py)
        {
            var t = cam.transform;
            Vector3 savedPos = t.position; Quaternion savedRot = t.rotation;
            t.SetPositionAndRotation(CapturePoses[view].position, CapturePoses[view].rotation);
            var ray = cam.ViewportPointToRay(new Vector3(px / Width, 1f - py / Height, 0f));
            t.SetPositionAndRotation(savedPos, savedRot);
            return ray;
        }

        // Not rendered every frame (that would starve the VLM of GPU time): live HUD preview is round-robin.
        void Start() { if (Application.isPlaying && cam) cam.enabled = false; }

        void LateUpdate()
        {
            if (!cam || Views == null || Time.unscaledTime < nextPreview) return;
            nextPreview = Time.unscaledTime + 1f / previewRendersPerSecond;
            // FRONT gets every other slot, the side views share the rest.
            int i = (previewIndex++ % 2 == 0) ? 0 : 1 + (previewIndex / 2) % 3;
            RenderView(i);
        }
    }
}
