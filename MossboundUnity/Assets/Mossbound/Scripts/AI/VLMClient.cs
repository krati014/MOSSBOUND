using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace Mossbound
{
    /// <summary>HTTP/JSON client for the Python FastAPI backend.</summary>
    public class VLMClient : MonoBehaviour
    {
        public string baseUrl = "http://127.0.0.1:8765";
        public bool Connected { get; private set; }
        public HealthDto Health { get; private set; }
        public event Action<bool, HealthDto> HealthChanged;

        void Start() { StartCoroutine(HealthLoop()); }

        IEnumerator HealthLoop()
        {
            while (true)
            {
                using (var www = UnityWebRequest.Get(baseUrl + "/health"))
                {
                    www.timeout = 3;
                    yield return www.SendWebRequest();
                    bool ok = www.result == UnityWebRequest.Result.Success;
                    HealthDto h = null;
                    if (ok) { try { h = JsonUtility.FromJson<HealthDto>(www.downloadHandler.text); } catch { ok = false; } }
                    Connected = ok && h != null && h.ollama && (h.model_available || h.mode == "mock");
                    Health = h;
                    HealthChanged?.Invoke(Connected, h);
                }
                yield return new WaitForSeconds(Connected ? 4f : 2f);
            }
        }

        static UnityWebRequest BuildAct(string baseUrl, ActRequest req) => Post(baseUrl + "/act", JsonUtility.ToJson(req));

        static UnityWebRequest Post(string url, string json)
        {
            var www = new UnityWebRequest(url, "POST")
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json)),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = 150
            };
            www.SetRequestHeader("Content-Type", "application/json");
            return www;
        }

        public IEnumerator Act(ActRequest req, Action<Command> ok, Action<string> fail)
        {
            using (var www = BuildAct(baseUrl, req))
            {
                yield return www.SendWebRequest();
                if (www.result != UnityWebRequest.Result.Success)
                {
                    fail($"Backend unreachable ({www.error}). Is 'python server.py' running?");
                    yield break;
                }
                Command c = null;
                try { c = JsonUtility.FromJson<Command>(www.downloadHandler.text); }
                catch (Exception e) { fail("Malformed response: " + e.Message); yield break; }
                ok(c);
            }
        }

        /// <summary>Stage 0: free-form command -> validated skill plan.</summary>
        public IEnumerator PlanFor(PlanRequest req, Action<Plan> ok, Action<string> fail)
        {
            using (var www = Post(baseUrl + "/plan", JsonUtility.ToJson(req)))
            {
                yield return www.SendWebRequest();
                if (www.result != UnityWebRequest.Result.Success) { fail($"Backend unreachable ({www.error}). Is 'python server.py' running?"); yield break; }
                Plan p = null;
                try { p = JsonUtility.FromJson<Plan>(www.downloadHandler.text); }
                catch (Exception e) { fail("Malformed plan: " + e.Message); yield break; }
                ok(p);
            }
        }

        /// <summary>Live detection on the FRONT view (for the "what MOSS sees" overlay).</summary>
        public IEnumerator Detect(DetectRequest req, Action<DetectResult> ok)
        {
            using (var www = Post(baseUrl + "/detect", JsonUtility.ToJson(req)))
            {
                yield return www.SendWebRequest();
                if (www.result != UnityWebRequest.Result.Success) yield break;
                DetectResult r = null;
                try { r = JsonUtility.FromJson<DetectResult>(www.downloadHandler.text); } catch { yield break; }
                if (r != null) ok(r);
            }
        }

        /// <summary>Blocking variant used only by editor/batch tests.</summary>
        public static Command ActBlocking(string baseUrl, ActRequest req, out string error)
        {
            error = null;
            using (var www = BuildAct(baseUrl, req))
            {
                var op = www.SendWebRequest();
                var t0 = DateTime.Now;
                while (!op.isDone)
                {
                    System.Threading.Thread.Sleep(20);
                    if ((DateTime.Now - t0).TotalSeconds > 160) { error = "timeout"; return null; }
                }
                if (www.result != UnityWebRequest.Result.Success) { error = www.error; return null; }
                return JsonUtility.FromJson<Command>(www.downloadHandler.text);
            }
        }
    }
}
