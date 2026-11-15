using System;
using System.Collections.Generic;
using UnityEngine;

namespace Mossbound
{
    public class QuestObjective
    {
        public string text;          // what the player sees (deliberately a little riddle-like)
        public string label;         // landmark that completes it (game referee only - never shown to the AI as an answer)
        public string doneMsg;
        public int group;            // objectives of the lowest unfinished group are active together (any order)
        public bool pickUp, drop;    // gives MOSS an item / hands carried items over
        public Color itemColor = Palette.Amber;
        public string itemName = "";
        public bool dance;           // MOSS must dance there (not just arrive)
        public string hint = "";     // an example command shown as a quick chip
        public bool done;
        public GameObject marker;    // optional visible item in the world (star seed)
    }

    public class Quest
    {
        public string title, story;
        public bool day;
        public List<QuestObjective> objectives = new List<QuestObjective>();
    }

    /// <summary>
    /// The game layer: one mission per run with riddle-like objectives. The player completes it by telling MOSS what to do
    /// (any wording) and/or driving. Completion is refereed by proximity to landmarks, independent of the AI.
    /// </summary>
    public class QuestManager : MonoBehaviour
    {
        public static QuestManager Instance { get; private set; }
        public RoverController rover;
        public Quest Current { get; private set; }
        public bool Started { get; private set; }
        public bool Complete { get; private set; }
        public int Commands { get; set; }
        public readonly List<string> Inventory = new List<string>();
        public int HelpCount { get; set; }
        public float StartTime { get; private set; }
        public float Elapsed => !Started ? 0f : (Complete ? endTime : Time.time) - StartTime;
        float endTime, manualAtStart, nextTick;
        int lastIndex = -1;

        public event Action<QuestObjective> ObjectiveDone;
        public event Action QuestComplete;
        public event Action Changed;

        void Awake() { Instance = this; }

        public float ManualSeconds => rover ? rover.ManualSeconds - manualAtStart : 0f;

        public IEnumerable<QuestObjective> Active
        {
            get
            {
                if (Current == null) yield break;
                int g = int.MaxValue;
                foreach (var o in Current.objectives) if (!o.done) g = Mathf.Min(g, o.group);
                foreach (var o in Current.objectives) if (!o.done && o.group == g) yield return o;
            }
        }

        /// <summary>Text for the planner when the player asks about the mission.</summary>
        public string ObjectiveForPlanner()
        {
            if (Current == null || Complete) return "";
            var list = new List<string>();
            foreach (var o in Active) list.Add(o.text);
            return $"\"{Current.title}\" - {Current.story} Next objective: {string.Join("; ", list)}.";
        }

        public void NewQuest(int index = -1)
        {
            foreach (var o in Current?.objectives ?? new List<QuestObjective>()) if (o.marker) Destroy(o.marker);
            if (index < 0) { do index = UnityEngine.Random.Range(0, 5); while (index == lastIndex); }
            lastIndex = index;
            Current = Build(index);
            Started = false; Complete = false; Commands = 0; HelpCount = 0; Inventory.Clear();
            Changed?.Invoke();
        }

        public void Begin()
        {
            Started = true; Complete = false; StartTime = Time.time;
            manualAtStart = rover ? rover.ManualSeconds : 0f;
            if (DayNight.Instance && DayNight.Instance.IsDay != Current.day) DayNight.Instance.Toggle();
            Changed?.Invoke();
        }

        public int Stars
        {
            get
            {
                int n = Current?.objectives.Count ?? 3;
                int s = 1;
                if (Commands <= n * 2 + 2) s++;
                if (Commands <= n + 2 && ManualSeconds < 15f && HelpCount == 0) s++;
                return s;
            }
        }

        void Update()
        {
            if (!Started || Complete || Time.time < nextTick) return;
            nextTick = Time.time + 0.2f;
            Tick();
        }

        public void Tick()
        {
            if (Current == null || Complete || !rover) return;
            var near = new HashSet<string>(rover.Nearby());
            foreach (var o in new List<QuestObjective>(Active))
            {
                if (!near.Contains(o.label)) continue;
                if (o.dance && !rover.Dancing) continue;
                o.done = true;
                var lm = Find(o.label);
                if (o.pickUp) Inventory.Add(string.IsNullOrEmpty(o.itemName) ? "a glowing item" : o.itemName);
                if (o.drop) Inventory.Clear();
                if (o.pickUp) rover.Carry(o.itemColor, 0.16f, o.marker ? o.marker.transform.position : (Vector3?)null);
                if (o.marker) Destroy(o.marker);
                if (o.drop && lm) rover.DropCarried(lm.transform.position + Vector3.up * 1.5f);
                rover.Hop(30);
                ObjectiveDone?.Invoke(o);
            }
            bool all = true;
            foreach (var o in Current.objectives) all &= o.done;
            if (all && !Complete) { Complete = true; endTime = Time.time; rover.Dance(); QuestComplete?.Invoke(); }
            Changed?.Invoke();
        }

        static Landmark Find(string label)
        {
            var env = EnvironmentController.Instance;
            if (!env) return null;
            foreach (var l in env.landmarks) if (l.label == label) return l;
            return null;
        }

        // ------------------------------------------------------------------ mission pool
        // Objectives name things by their sign names (Big Tree, Pond, Mushrooms, Crystal, Red Flower, Magic Gate, Home)
        // so the player always knows what to look for. How to get MOSS there is up to the player's words.
        static QuestObjective O(string text, string label, string done, int group = 0, bool pick = false, bool drop = false,
                                Color? c = null, string item = "", bool dance = false, string hint = "")
            => new QuestObjective { text = text, label = label, doneMsg = done, group = group, pickUp = pick, drop = drop,
                                    itemColor = c ?? Palette.Amber, itemName = item, dance = dance, hint = hint };

        static readonly Color Gold = new Color(1f, 0.82f, 0.3f);

        Quest Build(int i)
        {
            var q = new Quest();
            switch (i)
            {
                case 0:
                    q.title = "Snack Time"; q.story = "MOSS's tummy is rumbling! Find a snack and share it.";
                    q.objectives.Add(O("Pick a golden apple at the Big Tree", "largest_tree", "Got a golden apple!", 0, pick: true, c: new Color(1f, 0.75f, 0.2f), item: "a golden apple", hint: "I'm hungry"));
                    q.objectives.Add(O("Share the apple with the Mushrooms", "glowing_mushrooms", "The mushrooms love it!", 1, drop: true, hint: "Share it with the mushrooms"));
                    q.objectives.Add(O("Have a drink at the Pond", "pond_water", "Gulp! So refreshing.", 2, hint: "I'm thirsty"));
                    break;
                case 1:
                    q.title = "Dance Party"; q.story = "The garden is throwing a party tonight. Let's get the music going!";
                    q.objectives.Add(O("Visit the Crystal to start the music", "blue_crystal", "The Crystal starts to hum a tune!", 0, hint: "Go to the crystal"));
                    q.objectives.Add(O("Do a dance next to the Red Flower", "red_flower", "What moves! The flower is swaying too.", 1, dance: true, hint: "Go to the red flower and dance"));
                    q.objectives.Add(O("Go back Home for a rest", "home", "Best party ever!", 2, hint: "Go home"));
                    break;
                case 2:
                    q.title = "Falling Stars"; q.story = "Two little stars fell into the garden. Collect them and bring them home!";
                    var pool = new List<string> { "red_flower", "blue_crystal", "glowing_mushrooms", "portal" };
                    for (int k = 0; k < 2; k++)
                    {
                        var lbl = pool[UnityEngine.Random.Range(0, pool.Count)]; pool.Remove(lbl);
                        string name = CommandParser.PrettyTarget(lbl);
                        var o = O($"Pick up the star by the {name}", lbl, "Caught a star!", 0, pick: true, c: new Color(1f, 0.92f, 0.45f), item: "a little star", hint: $"Go to the {name.ToLowerInvariant()}");
                        o.marker = SpawnItem(lbl, false);
                        q.objectives.Add(o);
                    }
                    q.objectives.Add(O("Bring the stars Home", "home", "The stars are safe at home!", 1, drop: true, hint: "Bring them home"));
                    break;
                case 3:
                    q.title = "The Magic Gate"; q.story = "The Magic Gate is locked. A golden key is hidden somewhere nearby...";
                    var key = O("Find the golden key by the Pond", "pond_water", "Found the golden key!", 0, pick: true, c: Gold, item: "a golden key", hint: "Go to the pond");
                    key.marker = SpawnItem("pond_water", true);
                    q.objectives.Add(key);
                    q.objectives.Add(O("Unlock the Magic Gate with the key", "portal", "Click! The gate swirls open!", 1, drop: true, hint: "Take the key to the magic gate"));
                    q.objectives.Add(O("Do a happy dance at the gate", "portal", "Hooray! Adventure awaits!", 2, dance: true, hint: "Dance!"));
                    break;
                default:
                    q.title = "Morning Tour"; q.story = "A visitor wants a tour on this sunny morning! Show them three sights (any order), then go home."; q.day = true;
                    q.objectives.Add(O("Show them the Red Flower", "red_flower", "\"Wow, so pretty!\"", 0, hint: "Show me the prettiest flower"));
                    q.objectives.Add(O("Show them the Mushrooms", "glowing_mushrooms", "\"Boing! Boing!\"", 0, hint: "Take me to the mushrooms"));
                    q.objectives.Add(O("Show them the Big Tree", "largest_tree", "\"It's enormous!\"", 0, hint: "Visit the biggest tree"));
                    q.objectives.Add(O("Bring them back Home", "home", "Tour complete!", 1, hint: "Go home"));
                    break;
            }
            return q;
        }

        /// <summary>A visible glowing item next to a landmark: a little star, or a golden key.</summary>
        GameObject SpawnItem(string label, bool isKey)
        {
            var lm = Find(label);
            if (!lm) return null;
            // on the side of the landmark that faces the garden centre, so it is easy to spot
            var toC = new Vector3(-lm.transform.position.x, 0, -lm.transform.position.z).normalized;
            float a = Mathf.Atan2(toC.z, toC.x) + UnityEngine.Random.Range(-0.6f, 0.6f);
            var p = lm.transform.position + new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * (lm.radius + 0.6f);
            p = EnvironmentController.OnGround(p.x, p.z, 1.1f);
            var go = new GameObject(isKey ? "GoldenKey" : "Star");
            go.transform.SetParent(transform, false);
            go.transform.position = p;
            var col = isKey ? Gold : new Color(1f, 0.9f, 0.4f);
            go.AddComponent<MeshFilter>().sharedMesh = isKey
                ? Prim.Make(mb => { mb.M = Matrix4x4.Translate(new Vector3(0, 0.28f, 0)); Prim.Torus(mb, 0.18f, 0.055f, 16, 6);
                                    mb.M = Matrix4x4.Translate(new Vector3(0, -0.25f, 0)); Prim.Cylinder(mb, 0.05f, 0.05f, 0.45f, 6);
                                    mb.M = Matrix4x4.TRS(new Vector3(0.08f, -0.2f, 0), Quaternion.identity, new Vector3(0.14f, 0.06f, 0.06f)); Prim.Sphere(mb, 1); }, "Key")
                : Prim.Make(mb => { Prim.Crystal(mb, 0.2f, 0.45f, 5); mb.M = Matrix4x4.Rotate(Quaternion.Euler(180, 0, 0)); Prim.Crystal(mb, 0.2f, 0.45f, 5); }, "Star");
            var mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterial = Mats.Glow(col, col, 5f);
            go.AddComponent<Spinner>().speed = new Vector3(0, 90, 0);
            go.AddComponent<Bobber>().amplitude = 0.15f;
            var l = new GameObject("Light").AddComponent<Light>(); l.transform.SetParent(go.transform, false);
            l.type = LightType.Point; l.range = 4f; l.intensity = 2f; l.color = col;
            return go;
        }
    }
}
