using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace Mossbound
{
    /// <summary>
    /// Entry point. Press Play in ANY scene: the whole MOSSBOUND experience is generated at runtime
    /// (environment, rover, cameras, HUD, AI loop). No manual scene setup required.
    /// </summary>
    public class MossboundApp : MonoBehaviour
    {
        public EnvironmentController env;
        public RoverController rover;
        public CameraCapture capture;
        public Camera mainCam;
        public FollowCamera follow;
        public VLMClient client;
        public CommandExecutor executor;
        public UIController ui;
        public DayNight dayNight;
        public QuestManager quest;
        public RenderTexture portrait;

        public const int NormalFps = 60;
        public const int ThinkingFps = 15;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (FindAnyObjectByType<MossboundApp>() != null) return;
            var app = new GameObject("MOSSBOUND").AddComponent<MossboundApp>();
            app.BuildAll(true);
        }

        public void BuildAll(bool interactive)
        {
            if (Application.isPlaying)
            {
                // Share the GPU politely with the local VLM (see CommandExecutor for the "thinking" throttle).
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = NormalFps;
                // Remove template scene content (default camera, light, volume) - MOSSBOUND brings its own.
                foreach (var go in SceneManager.GetActiveScene().GetRootGameObjects())
                    if (go != gameObject) Destroy(go);
            }

            env = new GameObject("Environment").AddComponent<EnvironmentController>();
            env.transform.SetParent(transform, false);
            env.Build();
            Physics.SyncTransforms();
            NavGrid.Build(env.groundCollider, RoverController.RoverLayer);
            dayNight = DayNight.Create(env, System.Environment.GetEnvironmentVariable("MOSS_DAY") == "1");

            var start = EnvironmentController.RoverStart;
            rover = RoverController.Create(transform, EnvironmentController.OnGround(start.x, start.z, 0.05f), 0f);
            capture = rover.gameObject.AddComponent<CameraCapture>();
            capture.Init(rover.visionCam);
            SetupUrpCamera(rover.visionCam, false);

            var camGo = new GameObject("MainCamera") { tag = "MainCamera" };
            camGo.transform.SetParent(transform, false);
            mainCam = camGo.AddComponent<Camera>();
            mainCam.fieldOfView = 50f; mainCam.nearClipPlane = 0.2f; mainCam.farClipPlane = 220f;
            mainCam.clearFlags = CameraClearFlags.Skybox; mainCam.backgroundColor = Palette.Fog;
            SetupUrpCamera(mainCam, true);
            camGo.AddComponent<AudioListener>();
            follow = camGo.AddComponent<FollowCamera>();
            follow.target = rover.transform;
            follow.Snap();

            if (!interactive) return;

            portrait = PortraitCam.Create(rover);
            client = gameObject.AddComponent<VLMClient>();
            quest = gameObject.AddComponent<QuestManager>();
            quest.rover = rover;
            executor = gameObject.AddComponent<CommandExecutor>();
            ui = gameObject.AddComponent<UIController>();
            executor.rover = rover; executor.capture = capture; executor.client = client; executor.ui = ui; executor.quest = quest;
            ui.executor = executor; ui.rover = rover; ui.client = client; ui.quest = quest; ui.dayNight = dayNight;
            quest.NewQuest();
            ui.Build(capture.Views, portrait);
            client.HealthChanged += ui.OnHealth;
        }

        static void SetupUrpCamera(Camera cam, bool main)
        {
            var data = cam.GetUniversalAdditionalCameraData();
            if (data == null) return;
            data.renderPostProcessing = true;
            data.antialiasing = main ? AntialiasingMode.SubpixelMorphologicalAntiAliasing : AntialiasingMode.FastApproximateAntialiasing;
            data.renderShadows = true;
        }
    }
}
