using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif

namespace Mossbound
{
    public enum LogKind { User, Ai, Move, Warn, Error, Info }

    /// <summary>
    /// Game HUD (uGUI, built in code):
    ///  - mission card (top-left) and a chat with MOSS (speech bubbles)
    ///  - "MOSS's Eyes" (top-right): its front camera with the objects the VLM is detecting, boxed and labelled
    ///  - floating labels over the detected objects in the 3D view while MOSS drives
    ///  - dialogue box with live portrait, question buttons when MOSS double-checks, quick-command chips
    ///  - help card, takeover banner, GPU warning, start / complete screens with confetti
    ///  - the detailed "Brain" panel (TAB) with everything the AI saw, planned and decided
    /// </summary>
    public class UIController : MonoBehaviour
    {
        public static bool InputFocused { get; private set; }
        public static bool PointerOverUI => EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();

        public CommandExecutor executor;
        public RoverController rover;
        public VLMClient client;
        public QuestManager quest;
        public DayNight dayNight;

        // candy palette for the HUD
        static readonly Color Ink = new Color(0.035f, 0.05f, 0.12f, 0.9f);
        static readonly Color InkSolid = new Color(0.05f, 0.07f, 0.16f, 0.97f);
        static readonly Color Mint = new Color(0.35f, 0.96f, 0.76f);
        static readonly Color Sun = new Color(1f, 0.83f, 0.3f);
        static readonly Color Peach = new Color(1f, 0.6f, 0.45f);
        static readonly Color Lilac = new Color(0.74f, 0.62f, 1f);
        static readonly Color Sky = new Color(0.45f, 0.8f, 1f);
        static readonly Color Pink = new Color(1f, 0.5f, 0.76f);
        static readonly Color Dark = new Color(0.07f, 0.06f, 0.1f);
        static readonly Color Good = new Color(0.45f, 1f, 0.6f);
        static readonly Color[] Candy = { Mint, Sun, Lilac, Sky, Pink, Peach };

        RectTransform root;
        InputField input;
        Button goBtn, stopBtn, dayBtn, brainBtn;
        Text connText, missionTitle, missionStory, missionTimer, missionStats, toastText, dialogueText, dialogueMode,
             dayLabel, understandText, actionText, targetText, statText, obsText, reasonText, notesText, feedTag, bboxTag, helpText,
             startMission, completeStars, completeStats, completeTitle, eyesCaption, eyesStatus, eyesAge, takeoverText, gpuText,
             questionText, techLog, progressText;
        Image connDot, toastBg, scanLine, recDot, modeDot, portraitRing, takeoverBg, gpuBg, eyesFeedImg;
        RawImage eyesFeed;
        RectTransform brainPanel, eyesPanel, feedRect, bboxRect, helpCard, startOverlay, completeOverlay, objectiveList, planList,
                      chatArea, chipsRow, questionCard, questionButtons, eyesBoxes, worldTags, confettiLayer, dialoguePanel, startTitle;
        readonly RectTransform[] tiles = new RectTransform[4];
        readonly Image[] tileFrames = new Image[4];
        readonly List<Text> objectiveRows = new List<Text>();
        readonly List<Image> objectiveDots = new List<Image>();
        readonly List<Text> planRows = new List<Text>();
        readonly List<Text> eyesPlanRows = new List<Text>();
        readonly List<string> techLines = new List<string>();
        Plan shownPlan;
        StepState[] planStates = new StepState[0];
        string dialogueTarget = ""; float dialogueShown; bool dialogueDots; float dialoguePop;
        float toastUntil, toastPop, thinkingSince, detectionsAt = -99f;
        bool busy, connected, brainVisible, takeoverOn;
        int submittedFrame = -10, gpuPercent = -1;
        RenderTexture liveFront;
        string questKey = "";

        // chat bubbles
        struct Bubble { public RectTransform rt; public LogKind kind; }
        readonly List<Bubble> bubbles = new List<Bubble>();

        // detection overlay pools
        class Box { public RectTransform rt; public Image frame, tagBg; public Text tag; }
        readonly List<Box> eyeBoxes = new List<Box>();
        class WorldTag { public RectTransform rt; public Image bg; public Text text; public Vector3 world; public bool on, target; public float born; }
        readonly List<WorldTag> tags = new List<WorldTag>();
        WorldTag pinned;

        // confetti
        class Bit { public RectTransform rt; public Image img; public Vector2 v; public float life; }
        readonly List<Bit> bits = new List<Bit>();

        // ================================================================== build
        public void Build(RenderTexture[] views, RenderTexture portrait)
        {
            liveFront = views[0];
            var cgo = new GameObject("HUD", typeof(RectTransform));
            cgo.transform.SetParent(transform, false);
            var canvas = cgo.AddComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 10;
            var scaler = cgo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = 0.6f;
            cgo.AddComponent<GraphicRaycaster>();
            if (FindAnyObjectByType<EventSystem>() == null)
            {
                var es = new GameObject("EventSystem", typeof(EventSystem));
                es.transform.SetParent(transform, false);
#if ENABLE_INPUT_SYSTEM
                es.AddComponent<InputSystemUIInputModule>();
#else
                es.AddComponent<StandaloneInputModule>();
#endif
            }
            root = (RectTransform)cgo.transform;

            worldTags = UIKit.Stretch(UIKit.Rect("WorldTags", root));
            BuildMissionCard();
            BuildChat();
            BuildTopBar();
            BuildEyes();
            BuildBrain(views);
            BuildDialogue(portrait);
            BuildQuestion();
            BuildCommandBar();
            BuildHelpCard();
            BuildBanners();
            BuildToast();
            BuildStartOverlay();
            BuildCompleteOverlay();
            confettiLayer = UIKit.Stretch(UIKit.Rect("Confetti", root));

            if (quest)
            {
                quest.Changed += RefreshQuest;
                quest.ObjectiveDone += o => { Toast("✓  " + o.doneMsg, Good); Confetti(new Vector2(0, -60), 26); Log(o.doneMsg, LogKind.Info); };
                quest.QuestComplete += () => Invoke(nameof(ShowComplete), 2.2f);
            }
            RefreshQuest();
            SetBrain(false);
            MossSays("Hi! I'm MOSS. Tell me what to do - any way you like!", false);
            Log("Hi! I'm MOSS. Type anything, or drive me with WASD!", LogKind.Ai);
            SetIdleBrain();
        }

        Text Header(Transform parent, string text, float x, float y, Color? c = null)
        {
            var h = UIKit.Label(parent, text, 12, c ?? Mint, TextAnchor.MiddleLeft, FontStyle.Bold);
            UIKit.Place(h.rectTransform, new Vector2(0, 1), new Vector2(x, y), new Vector2(400, 18));
            return h;
        }

        Text At(Transform parent, string text, int size, Color c, float x, float y, float w, float h, FontStyle st = FontStyle.Normal, TextAnchor a = TextAnchor.UpperLeft)
        {
            var t = UIKit.Label(parent, text, size, c, a, st);
            UIKit.Place(t.rectTransform, new Vector2(0, 1), new Vector2(x, y), new Vector2(w, h));
            return t;
        }

        Image Card(Transform parent, string name, Color outline, Color? bg = null)
        {
            var p = UIKit.Panel(parent, name, bg ?? Ink, 1.8f);
            p.transform.Find("Outline").GetComponent<Image>().color = new Color(outline.r, outline.g, outline.b, 0.75f);
            var sh = p.gameObject.AddComponent<Shadow>(); sh.effectColor = new Color(0, 0, 0, 0.45f); sh.effectDistance = new Vector2(4, -5);
            return p;
        }

        Button Pill(Transform parent, string label, Color c, int size, System.Action onClick, bool solid = true)
        {
            var b = UIKit.Button(parent, "Pill", label, c, solid ? Dark : c, size, onClick, !solid);
            return b;
        }

        // ------------------------------------------------------------------ mission + chat (left)
        void BuildMissionCard()
        {
            var p = Card(root, "Mission", Sun);
            UIKit.Place(p.rectTransform, new Vector2(0, 1), new Vector2(28, -24), new Vector2(460, 296));
            var t = p.transform;
            var band = UIKit.Panel(t, "Band", Sun, 1.8f, false);
            UIKit.Place(band.rectTransform, new Vector2(0, 1), new Vector2(0, 0), new Vector2(460, 62));
            At(band.transform, "★ MISSION", 12, Dark, 20, -8, 200, 16, FontStyle.Bold);
            missionTitle = At(band.transform, "", 26, Dark, 20, -24, 320, 34, FontStyle.Bold);
            missionTimer = At(band.transform, "", 18, Dark, 300, -26, 140, 28, FontStyle.Bold, TextAnchor.UpperRight);
            missionStory = At(t, "", 14, new Color(0.8f, 0.85f, 0.95f), 20, -72, 420, 40, FontStyle.Italic);
            objectiveList = UIKit.Place(UIKit.Rect("Objectives", t), new Vector2(0, 1), new Vector2(20, -116), new Vector2(420, 140));
            for (int i = 0; i < 4; i++)
            {
                var dot = UIKit.Dot(objectiveList, "Check", Sun, 22);
                UIKit.Place(dot.rectTransform, new Vector2(0, 1), new Vector2(0, -i * 34 - 3), new Vector2(22, 22));
                objectiveDots.Add(dot);
                objectiveRows.Add(At(objectiveList, "", 17, Palette.UiText, 32, -i * 34, 390, 30, FontStyle.Bold));
            }
            progressText = At(t, "", 13, Sun, 20, -262, 200, 22, FontStyle.Bold);
            missionStats = At(t, "", 12, Palette.UiMuted, 200, -264, 240, 22, FontStyle.Normal, TextAnchor.UpperRight);
        }

        void BuildChat()
        {
            var p = Card(root, "Chat", Lilac);
            UIKit.Place(p.rectTransform, new Vector2(0, 1), new Vector2(28, -336), new Vector2(460, 380));
            Header(p.transform, "CHAT WITH MOSS", 20, -14, Lilac);
            chatArea = UIKit.Stretch(UIKit.Rect("Bubbles", p.transform), 14, 14, 38, 12);
            chatArea.gameObject.AddComponent<RectMask2D>();
        }

        void AddBubble(string text, LogKind kind)
        {
            if (chatArea == null) return;
            bool user = kind == LogKind.User, moss = kind == LogKind.Ai;
            bool center = !user && !moss;
            var rt = UIKit.Rect("Bubble", chatArea);
            rt.anchorMin = rt.anchorMax = new Vector2(user ? 1 : center ? 0.5f : 0, 0);
            rt.pivot = new Vector2(user ? 1 : center ? 0.5f : 0, 0);
            Color bg = user ? Sun : moss ? Mint : kind == LogKind.Warn ? new Color(1f, 0.6f, 0.3f, 0.22f) : kind == LogKind.Error ? new Color(1f, 0.3f, 0.35f, 0.25f) : new Color(0.45f, 1f, 0.6f, 0.16f);
            var img = rt.gameObject.AddComponent<Image>(); img.sprite = UIKit.Rounded; img.type = Image.Type.Sliced; img.pixelsPerUnitMultiplier = 2.6f; img.color = bg; img.raycastTarget = false;
            var tx = UIKit.Label(rt, text, center ? 13 : 15, center ? (kind == LogKind.Error ? Palette.UiError : kind == LogKind.Warn ? Peach : Good) : Dark,
                                 center ? TextAnchor.MiddleCenter : TextAnchor.MiddleLeft, center ? FontStyle.Bold : FontStyle.Normal);
            float maxW = center ? 410 : 330;
            tx.horizontalOverflow = HorizontalWrapMode.Overflow;
            float w = Mathf.Min(tx.preferredWidth, maxW);
            tx.horizontalOverflow = HorizontalWrapMode.Wrap; tx.verticalOverflow = VerticalWrapMode.Overflow;
            UIKit.Place(tx.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(w, 10));
            float h = tx.preferredHeight;
            tx.rectTransform.sizeDelta = new Vector2(w, h);
            rt.sizeDelta = new Vector2(w + 24, h + 14);
            rt.localScale = Vector3.one * 0.6f;
            bubbles.Add(new Bubble { rt = rt, kind = kind });
            while (bubbles.Count > 12) { Kill(bubbles[0].rt.gameObject); bubbles.RemoveAt(0); }
            float y = 4;
            for (int i = bubbles.Count - 1; i >= 0; i--)
            {
                bubbles[i].rt.anchoredPosition = new Vector2(bubbles[i].kind == LogKind.User ? -4 : bubbles[i].kind == LogKind.Ai ? 4 : 0, y);
                y += bubbles[i].rt.sizeDelta.y + 8;
            }
        }

        // ------------------------------------------------------------------ top bar (right)
        void BuildTopBar()
        {
            var c = Card(root, "Connection", Mint);
            UIKit.Place(c.rectTransform, new Vector2(1, 1), new Vector2(-316, -24), new Vector2(330, 44));
            connDot = UIKit.Dot(c.transform, "Dot", Palette.UiError, 12); UIKit.Place(connDot.rectTransform, new Vector2(0, 0.5f), new Vector2(16, 0), new Vector2(12, 12));
            connText = UIKit.Label(c.transform, "BRAIN OFFLINE", 12, Palette.UiText, TextAnchor.MiddleLeft, FontStyle.Bold);
            UIKit.Place(connText.rectTransform, new Vector2(0, 0.5f), new Vector2(36, 0), new Vector2(290, 30));

            dayBtn = Pill(root, "", Sun, 13, () => { if (dayNight) dayNight.Toggle(); }, false);
            UIKit.Place((RectTransform)dayBtn.transform, new Vector2(1, 1), new Vector2(-172, -24), new Vector2(136, 44));
            dayLabel = dayBtn.transform.Find("Text").GetComponent<Text>();
            brainBtn = Pill(root, "BRAIN  [TAB]", Lilac, 13, () => SetBrain(!brainVisible), false);
            UIKit.Place((RectTransform)brainBtn.transform, new Vector2(1, 1), new Vector2(-28, -24), new Vector2(136, 44));
        }

        // ------------------------------------------------------------------ MOSS's eyes (right)
        void BuildEyes()
        {
            var p = Card(root, "Eyes", Sky);
            eyesPanel = UIKit.Place(p.rectTransform, new Vector2(1, 1), new Vector2(-28, -80), new Vector2(480, 470));
            var t = p.transform;
            Header(t, "MOSS'S EYES  ·  WHAT THE AI SEES", 20, -14, Sky);
            eyesAge = At(t, "", 11, Palette.UiMuted, 250, -11, 210, 16, FontStyle.Bold, TextAnchor.UpperRight);
            var frame = UIKit.Panel(t, "FeedFrame", new Color(0, 0, 0, 0.7f), 3f, false);
            UIKit.Place(frame.rectTransform, new Vector2(0, 1), new Vector2(16, -36), new Vector2(448, 253));
            frame.gameObject.AddComponent<RectMask2D>();
            var feedRt = UIKit.Stretch(UIKit.Rect("Feed", frame.transform), 2, 2, 2, 2);
            eyesFeed = feedRt.gameObject.AddComponent<RawImage>(); eyesFeed.texture = liveFront; eyesFeed.raycastTarget = false;
            eyesBoxes = UIKit.Stretch(UIKit.Rect("Boxes", feedRt));
            eyesCaption = At(t, "", 15, Palette.UiText, 20, -298, 440, 44, FontStyle.Bold);
            eyesStatus = At(t, "", 13, Sky, 20, -344, 440, 20, FontStyle.Bold);
            for (int i = 0; i < 4; i++) eyesPlanRows.Add(At(t, "", 14, Palette.UiText, 20, -368 - i * 24, 440, 22));
        }

        Box GetEyeBox(int i)
        {
            while (eyeBoxes.Count <= i)
            {
                var rt = UIKit.Rect("Box", eyesBoxes);
                rt.anchorMin = rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0, 1);
                var fr = rt.gameObject.AddComponent<Image>(); fr.sprite = UIKit.Outline; fr.type = Image.Type.Sliced; fr.pixelsPerUnitMultiplier = 4f; fr.raycastTarget = false;
                var tagBg = UIKit.Panel(rt, "Tag", Mint, 5f, false);
                UIKit.Place(tagBg.rectTransform, new Vector2(0, 1), new Vector2(0, 17), new Vector2(100, 17));
                var tag = UIKit.Label(tagBg.transform, "", 11, Dark, TextAnchor.MiddleCenter, FontStyle.Bold);
                UIKit.Stretch(tag.rectTransform);
                eyeBoxes.Add(new Box { rt = rt, frame = fr, tagBg = tagBg, tag = tag });
            }
            return eyeBoxes[i];
        }

        WorldTag GetTag(int i)
        {
            while (tags.Count <= i)
            {
                var bg = UIKit.Panel(worldTags, "Tag", Mint, 3f, false);
                bg.rectTransform.anchorMin = bg.rectTransform.anchorMax = Vector2.zero; bg.rectTransform.pivot = new Vector2(0.5f, 0);
                var tx = UIKit.Label(bg.transform, "", 14, Dark, TextAnchor.MiddleCenter, FontStyle.Bold); UIKit.Stretch(tx.rectTransform);
                var pin = UIKit.Dot(bg.transform, "Pin", Mint, 10); UIKit.Place(pin.rectTransform, new Vector2(0.5f, 0), new Vector2(0, -12), new Vector2(10, 10));
                bg.gameObject.SetActive(false);
                tags.Add(new WorldTag { rt = bg.rectTransform, bg = bg, text = tx });
            }
            return tags[i];
        }

        // ------------------------------------------------------------------ detailed brain (TAB)
        void BuildBrain(RenderTexture[] views)
        {
            var p = Card(root, "Brain", Lilac);
            brainPanel = UIKit.Place(p.rectTransform, new Vector2(1, 1), new Vector2(-28, -80), new Vector2(480, 900));
            var t = p.transform;
            Header(t, "MOSS'S BRAIN  ·  EVERY STEP OF THE AI", 20, -14, Lilac);

            var frame = UIKit.Panel(t, "FeedFrame", new Color(0, 0, 0, 0.6f), 3f, false);
            UIKit.Place(frame.rectTransform, new Vector2(0, 1), new Vector2(16, -36), new Vector2(448, 253));
            feedRect = UIKit.Stretch(UIKit.Rect("Feed", frame.transform), 3, 3, 3, 3);
            for (int i = 0; i < 4; i++)
            {
                var tr = UIKit.Rect("View_" + CameraCapture.ViewNames[i], feedRect);
                tr.anchorMin = new Vector2((i % 2) * 0.5f, i < 2 ? 0.5f : 0f); tr.anchorMax = new Vector2((i % 2) * 0.5f + 0.5f, i < 2 ? 1f : 0.5f);
                tr.offsetMin = new Vector2(1, 1); tr.offsetMax = new Vector2(-1, -1);
                var raw = tr.gameObject.AddComponent<RawImage>(); raw.texture = views[i]; raw.raycastTarget = false;
                tiles[i] = tr;
                var lb = UIKit.Label(tr, CameraCapture.ViewNames[i], 9, new Color(1, 1, 1, 0.8f), TextAnchor.LowerLeft, FontStyle.Bold);
                UIKit.Place(lb.rectTransform, new Vector2(0, 0), new Vector2(5, 3), new Vector2(80, 14));
                var fr = UIKit.Stretch(UIKit.Rect("Hi", tr)).gameObject.AddComponent<Image>();
                fr.sprite = UIKit.Outline; fr.type = Image.Type.Sliced; fr.pixelsPerUnitMultiplier = 5f; fr.color = new Color(1, 0.62f, 0.18f, 0f); fr.raycastTarget = false;
                tileFrames[i] = fr;
            }
            bboxRect = UIKit.Rect("BBox", tiles[0]);
            bboxRect.anchorMin = bboxRect.anchorMax = new Vector2(0, 1); bboxRect.pivot = new Vector2(0, 1);
            var bimg = bboxRect.gameObject.AddComponent<Image>(); bimg.sprite = UIKit.Outline; bimg.type = Image.Type.Sliced; bimg.pixelsPerUnitMultiplier = 4f; bimg.color = Sun; bimg.raycastTarget = false;
            var tagBg = UIKit.Panel(bboxRect, "Tag", Sun, 6f, false);
            UIKit.Place(tagBg.rectTransform, new Vector2(0, 1), new Vector2(0, 16), new Vector2(120, 15));
            bboxTag = UIKit.Label(tagBg.transform, "", 9, Dark, TextAnchor.MiddleCenter, FontStyle.Bold);
            UIKit.Stretch(bboxTag.rectTransform);
            bboxRect.gameObject.SetActive(false);
            scanLine = UIKit.Line(feedRect, new Vector2(0.5f, 1), Vector2.zero, new Vector2(402, 2), new Color(0.6f, 0.45f, 1f, 0.9f));
            scanLine.rectTransform.anchorMin = new Vector2(0, 1); scanLine.rectTransform.anchorMax = new Vector2(1, 1); scanLine.rectTransform.sizeDelta = new Vector2(0, 2);
            scanLine.gameObject.SetActive(false);
            recDot = UIKit.Dot(feedRect, "Rec", Palette.Ember, 8); UIKit.Place(recDot.rectTransform, new Vector2(0, 1), new Vector2(10, -10), new Vector2(8, 8));
            feedTag = UIKit.Label(feedRect, "LIVE · 360°", 10, Palette.UiText, TextAnchor.MiddleLeft, FontStyle.Bold);
            UIKit.Place(feedTag.rectTransform, new Vector2(0, 1), new Vector2(24, -6), new Vector2(300, 16));

            Header(t, "1 · UNDERSTOOD  (language planner)", 20, -300, Lilac);
            understandText = At(t, "", 14, Sun, 20, -319, 440, 54, FontStyle.Bold);
            Header(t, "2 · PLAN  (validated skills)", 20, -378, Lilac);
            planList = UIKit.Place(UIKit.Rect("Plan", t), new Vector2(0, 1), new Vector2(20, -398), new Vector2(440, 100));
            for (int i = 0; i < 4; i++) planRows.Add(At(planList, "", 13, Palette.UiText, 0, -i * 23, 440, 22));
            Header(t, "3 · EYES  (vision grounding)", 20, -498, Lilac);
            actionText = At(t, "STANDING BY", 20, Palette.UiText, 20, -516, 440, 28, FontStyle.Bold);
            targetText = At(t, "", 13, Sun, 20, -544, 440, 20);
            statText = At(t, "", 12, Palette.UiMuted, 20, -564, 440, 34);
            Header(t, "SEES", 20, -600, Lilac);
            obsText = At(t, "", 12, Palette.UiText, 20, -616, 440, 32, FontStyle.Italic);
            Header(t, "REASONING", 20, -650, Lilac);
            reasonText = At(t, "", 12, Palette.UiText, 20, -666, 440, 32);
            Header(t, "SAFETY VALIDATOR", 20, -700, Lilac);
            notesText = At(t, "", 11, Palette.UiMuted, 20, -716, 440, 50);
            Header(t, "MOVEMENT LOG", 20, -770, Lilac);
            techLog = At(t, "", 11, Palette.UiMuted, 20, -786, 440, 106);
        }

        // ------------------------------------------------------------------ dialogue + question + command bar (bottom)
        void BuildDialogue(RenderTexture portrait)
        {
            var p = Card(root, "Dialogue", Mint, InkSolid);
            dialoguePanel = UIKit.Place(p.rectTransform, new Vector2(0.5f, 0), new Vector2(-20, 164), new Vector2(860, 116));
            portraitRing = UIKit.Dot(p.transform, "PortraitRing", Mint, 104); UIKit.Place(portraitRing.rectTransform, new Vector2(0, 0.5f), new Vector2(10, 4), new Vector2(104, 104));
            var mask = UIKit.Dot(portraitRing.transform, "Mask", Color.white, 96); UIKit.Place(mask.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(96, 96));
            mask.gameObject.AddComponent<Mask>().showMaskGraphic = false;
            if (portrait)
            {
                var raw = UIKit.Stretch(UIKit.Rect("Portrait", mask.transform)).gameObject.AddComponent<RawImage>();
                raw.texture = portrait; raw.raycastTarget = false;
            }
            var nameBg = UIKit.Panel(p.transform, "Name", Mint, 4f, false);
            UIKit.Place(nameBg.rectTransform, new Vector2(0, 1), new Vector2(126, 12), new Vector2(84, 26));
            var nm = UIKit.Label(nameBg.transform, "MOSS", 15, Dark, TextAnchor.MiddleCenter, FontStyle.Bold); UIKit.Stretch(nm.rectTransform);
            modeDot = UIKit.Dot(p.transform, "ModeDot", Mint, 10); UIKit.Place(modeDot.rectTransform, new Vector2(0, 1), new Vector2(222, -4), new Vector2(10, 10));
            dialogueMode = At(p.transform, "IDLE", 12, Palette.UiMuted, 238, -2, 400, 18, FontStyle.Bold);
            dialogueText = At(p.transform, "", 21, Palette.UiText, 126, -26, 716, 84, FontStyle.Bold);
        }

        void BuildQuestion()
        {
            var p = Card(root, "Question", Sun, InkSolid);
            questionCard = UIKit.Place(p.rectTransform, new Vector2(0.5f, 0), new Vector2(-20, 290), new Vector2(860, 112));
            At(p.transform, "MOSS WANTS TO CHECK", 12, Sun, 20, -10, 300, 16, FontStyle.Bold);
            questionText = At(p.transform, "", 17, Palette.UiText, 20, -28, 820, 26, FontStyle.Bold);
            questionButtons = UIKit.Place(UIKit.Rect("Buttons", p.transform), new Vector2(0, 0), new Vector2(20, 12), new Vector2(820, 42));
            var hl = questionButtons.gameObject.AddComponent<HorizontalLayoutGroup>();
            hl.spacing = 10; hl.childAlignment = TextAnchor.MiddleLeft; hl.childControlWidth = false; hl.childControlHeight = false; hl.childForceExpandWidth = false;
            questionCard.gameObject.SetActive(false);
        }

        void BuildCommandBar()
        {
            chipsRow = UIKit.Place(UIKit.Rect("Chips", root), new Vector2(0.5f, 0), new Vector2(-20, 126), new Vector2(1000, 32));
            var hl = chipsRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            hl.spacing = 8; hl.childAlignment = TextAnchor.MiddleCenter; hl.childControlWidth = false; hl.childControlHeight = false; hl.childForceExpandWidth = false;

            var bar = Card(root, "CommandBar", Sun, InkSolid);
            UIKit.Place(bar.rectTransform, new Vector2(0.5f, 0), new Vector2(-20, 56), new Vector2(860, 62));
            var prompt = UIKit.Label(bar.transform, "›", 34, Sun, TextAnchor.MiddleCenter, FontStyle.Bold);
            UIKit.Place(prompt.rectTransform, new Vector2(0, 0.5f), new Vector2(12, 3), new Vector2(26, 50));
            var field = UIKit.Place(UIKit.Rect("Input", bar.transform), new Vector2(0, 0.5f), new Vector2(42, 0), new Vector2(560, 50));
            var fimg = field.gameObject.AddComponent<Image>(); fimg.color = new Color(0, 0, 0, 0.001f);
            input = field.gameObject.AddComponent<InputField>();
            var txt = UIKit.Label(field, "", 20, Palette.UiText); UIKit.Stretch(txt.rectTransform, 6, 6, 4, 4); txt.supportRichText = false;
            txt.horizontalOverflow = HorizontalWrapMode.Overflow; txt.alignment = TextAnchor.MiddleLeft;
            var ph = UIKit.Label(field, "Press ENTER and tell MOSS anything…  \"I'm thirsty\", \"go to the crystal\"", 16,
                                 new Color(0.6f, 0.65f, 0.8f, 0.8f), TextAnchor.MiddleLeft, FontStyle.Italic);
            UIKit.Stretch(ph.rectTransform, 6, 6, 4, 4);
            input.textComponent = txt; input.placeholder = ph; input.lineType = InputField.LineType.SingleLine;
            input.caretColor = Sun; input.customCaretColor = true; input.caretWidth = 2;
            input.selectionColor = new Color(1f, 0.83f, 0.3f, 0.3f); input.characterLimit = 240;
            input.onEndEdit.AddListener(_ => { if (InputCompat.EnterDown()) Submit(); });

            stopBtn = Pill(bar.transform, "STOP", Peach, 14, () => executor.Cancel(), false);
            UIKit.Place((RectTransform)stopBtn.transform, new Vector2(1, 0.5f), new Vector2(-168, 0), new Vector2(84, 44));
            goBtn = Pill(bar.transform, "SEND  →", Sun, 16, Submit);
            UIKit.Place((RectTransform)goBtn.transform, new Vector2(1, 0.5f), new Vector2(-12, 0), new Vector2(146, 46));

            var hint = UIKit.Label(root, "WASD  drive     ·     SPACE  hop     ·     ENTER  talk     ·     N  day / night     ·     TAB  brain     ·     ESC  stop     ·     drive any time - MOSS carries on when you let go",
                                   12, new Color(0.75f, 0.8f, 0.9f, 0.85f), TextAnchor.MiddleCenter);
            UIKit.Place(hint.rectTransform, new Vector2(0.5f, 0), new Vector2(-20, 22), new Vector2(1300, 20));
        }

        void RebuildChips()
        {
            for (int i = chipsRow.childCount - 1; i >= 0; i--) Kill(chipsRow.GetChild(i).gameObject);
            var list = new List<string>();
            if (quest && quest.Current != null && quest.Started && !quest.Complete)
                foreach (var o in quest.Active) if (!string.IsNullOrEmpty(o.hint) && !list.Contains(o.hint)) list.Add(o.hint);
            foreach (var s in new[] { "What should we do?", "What do you see?", "Dance!", "Come home" })
                if (list.Count < 6 && !list.Contains(s)) list.Add(s);
            int k = 0;
            foreach (var s in list)
            {
                string cmd = s;
                var b = Pill(chipsRow, s, Candy[k++ % Candy.Length], 13, () => { input.text = cmd; Submit(); });
                ((RectTransform)b.transform).sizeDelta = new Vector2(Mathf.Max(96, s.Length * 7.6f + 28), 30);
            }
        }

        // ------------------------------------------------------------------ cards, banners, overlays
        void BuildHelpCard()
        {
            var p = Card(root, "Help", Peach, new Color(0.14f, 0.06f, 0.05f, 0.96f));
            helpCard = UIKit.Place(p.rectTransform, new Vector2(0.5f, 0), new Vector2(-20, 290), new Vector2(640, 160));
            At(p.transform, "HELP! MOSS IS STUCK", 20, Peach, 22, -14, 560, 28, FontStyle.Bold);
            helpText = At(p.transform, "", 15, Palette.UiText, 22, -44, 596, 60);
            var retry = Pill(p.transform, "TRY AGAIN  [R]", Peach, 14, () => executor.Retry());
            UIKit.Place((RectTransform)retry.transform, new Vector2(0, 0), new Vector2(22, 14), new Vector2(170, 40));
            var give = Pill(p.transform, "NEVER MIND", Palette.UiMuted, 13, () => executor.GiveUp(), false);
            UIKit.Place((RectTransform)give.transform, new Vector2(0, 0), new Vector2(204, 14), new Vector2(140, 40));
            At(p.transform, "…or type a hint like \"it's behind you\"", 12, Palette.UiMuted, 360, -116, 270, 30, FontStyle.Italic);
            helpCard.gameObject.SetActive(false);
        }

        void BuildBanners()
        {
            takeoverBg = UIKit.Panel(root, "Takeover", Sky, 3f, false);
            UIKit.Place(takeoverBg.rectTransform, new Vector2(0.5f, 1), new Vector2(0, -90), new Vector2(620, 40));
            takeoverText = UIKit.Label(takeoverBg.transform, "YOU'RE DRIVING!  MOSS WILL CARRY ON WHEN YOU LET GO", 15, Dark, TextAnchor.MiddleCenter, FontStyle.Bold);
            UIKit.Stretch(takeoverText.rectTransform);
            takeoverBg.gameObject.SetActive(false);

            gpuBg = UIKit.Panel(root, "GpuWarn", Peach, 3f, false);
            UIKit.Place(gpuBg.rectTransform, new Vector2(0.5f, 1), new Vector2(0, -24), new Vector2(760, 52));
            gpuText = UIKit.Label(gpuBg.transform, "", 13, Dark, TextAnchor.MiddleCenter, FontStyle.Bold);
            UIKit.Stretch(gpuText.rectTransform, 12, 12, 2, 2);
            gpuBg.gameObject.SetActive(false);
        }

        void BuildToast()
        {
            toastBg = UIKit.Panel(root, "Toast", InkSolid, 2.5f);
            UIKit.Place(toastBg.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0, 180), new Vector2(620, 60));
            toastText = UIKit.Label(toastBg.transform, "", 20, Palette.UiText, TextAnchor.MiddleCenter, FontStyle.Bold);
            UIKit.Stretch(toastText.rectTransform);
            toastBg.gameObject.SetActive(false);
        }

        RectTransform Overlay(string name, out Transform card, Vector2 size, Color outline)
        {
            var dim = UIKit.Stretch(UIKit.Rect(name, root));
            var di = dim.gameObject.AddComponent<Image>(); di.color = new Color(0.02f, 0.02f, 0.06f, 0.6f);
            var c = Card(dim, "Card", outline, InkSolid);
            UIKit.Place(c.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, size);
            card = c.transform;
            return dim;
        }

        void BuildStartOverlay()
        {
            startOverlay = Overlay("Start", out var c, new Vector2(840, 620), Mint);
            var title = At(c, "MOSSBOUND", 72, Mint, 0, -26, 840, 86, FontStyle.Bold, TextAnchor.UpperCenter);
            title.gameObject.AddComponent<Shadow>().effectColor = new Color(1f, 0.5f, 0.76f, 0.7f);
            title.GetComponent<Shadow>().effectDistance = new Vector2(4, -4);
            startTitle = title.rectTransform;
            At(c, "a tiny robot  ·  a magical garden  ·  a real AI brain", 16, Lilac, 0, -112, 840, 22, FontStyle.Bold, TextAnchor.UpperCenter);
            var mc = UIKit.Panel(c, "MissionBox", new Color(1f, 0.83f, 0.3f, 0.12f), 2f, false);
            UIKit.Place(mc.rectTransform, new Vector2(0.5f, 1), new Vector2(0, -150), new Vector2(740, 240));
            startMission = At(mc.transform, "", 18, Palette.UiText, 24, -16, 700, 220);
            var how = At(c, "", 15, Palette.UiText, 50, -404, 740, 110);
            how.text = $"<color=#{Palette.Hex(Sun)}><b>TALK</b></color>  press ENTER and type anything - \"I'm hungry\", \"go to the big tree\", \"spin around\"\n" +
                       $"<color=#{Palette.Hex(Sky)}><b>DRIVE</b></color>  WASD any time - MOSS carries on when you let go\n" +
                       $"<color=#{Palette.Hex(Lilac)}><b>WATCH</b></color>  MOSS'S EYES shows what the AI sees  ·  if MOSS isn't sure, it will ask you";
            var b = Pill(c, "START MISSION   [ENTER]", Sun, 19, StartMission);
            UIKit.Place((RectTransform)b.transform, new Vector2(0.5f, 0), new Vector2(-120, 34), new Vector2(330, 60));
            var other = Pill(c, "ANOTHER MISSION", Mint, 15, () => { quest.NewQuest(); }, false);
            UIKit.Place((RectTransform)other.transform, new Vector2(0.5f, 0), new Vector2(180, 34), new Vector2(230, 60));
        }

        void BuildCompleteOverlay()
        {
            completeOverlay = Overlay("Complete", out var c, new Vector2(700, 460), Sun);
            completeTitle = At(c, "MISSION COMPLETE!", 40, Sun, 0, -34, 700, 50, FontStyle.Bold, TextAnchor.UpperCenter);
            completeStars = At(c, "", 76, Sun, 0, -92, 700, 92, FontStyle.Bold, TextAnchor.UpperCenter);
            completeStats = At(c, "", 17, Palette.UiText, 0, -200, 700, 130, FontStyle.Normal, TextAnchor.UpperCenter);
            var b = Pill(c, "NEXT MISSION   [ENTER]", Sun, 17, NextMission);
            UIKit.Place((RectTransform)b.transform, new Vector2(0.5f, 0), new Vector2(-120, 34), new Vector2(300, 56));
            var free = Pill(c, "FREE PLAY", Mint, 15, () => completeOverlay.gameObject.SetActive(false), false);
            UIKit.Place((RectTransform)free.transform, new Vector2(0.5f, 0), new Vector2(150, 34), new Vector2(180, 56));
            completeOverlay.gameObject.SetActive(false);
        }

        // ================================================================== game flow
        bool OverlayOpen => (startOverlay && startOverlay.gameObject.activeSelf) || (completeOverlay && completeOverlay.gameObject.activeSelf);

        void StartMission()
        {
            startOverlay.gameObject.SetActive(false);
            if (quest) quest.Begin();
            var first = quest ? new List<QuestObjective>(quest.Active) : null;
            string next = first != null && first.Count > 0 ? first[0].text : "";
            MossSays($"{quest?.Current?.title}! First: {next}. Tell me what to do!", false);
            Log($"Mission: {quest?.Current?.title}", LogKind.Info);
            rover.Say("Let's go!", 2f); rover.Hop(20);
            Confetti(Vector2.zero, 30);
            RebuildChips();
        }

        void NextMission()
        {
            completeOverlay.gameObject.SetActive(false);
            executor.Cancel(false);
            if (quest) quest.NewQuest();
            startOverlay.gameObject.SetActive(true);
        }

        void ShowComplete()
        {
            if (!quest || !quest.Complete) return;
            int s = quest.Stars;
            completeStars.text = new string('★', s) + "<color=#2c3350>" + new string('★', 3 - s) + "</color>";
            completeStars.rectTransform.localScale = Vector3.one * 0.2f;
            completeTitle.text = quest.Current.title.ToUpperInvariant() + " COMPLETE!";
            float sec = quest.Elapsed;
            completeStats.text = $"Time  <b>{(int)sec / 60}:{(int)sec % 60:00}</b>     ·     Commands  <b>{quest.Commands}</b>     ·     Help given  <b>{quest.HelpCount}</b>\n" +
                                 $"You drove MOSS yourself for <b>{quest.ManualSeconds:0} s</b>\n\n" +
                                 "<color=#8a93b8>★★★ = few commands, little driving, no help needed</color>";
            completeOverlay.gameObject.SetActive(true);
            Confetti(new Vector2(0, 120), 60);
        }

        void RefreshQuest()
        {
            if (!quest || quest.Current == null || missionTitle == null) return;
            var q = quest.Current;
            missionTitle.text = q.title;
            missionStory.text = q.story;
            int activeGroup = int.MaxValue, done = 0;
            foreach (var o in q.objectives) { if (!o.done) activeGroup = Mathf.Min(activeGroup, o.group); else done++; }
            for (int i = 0; i < objectiveRows.Count; i++)
            {
                bool show = i < q.objectives.Count;
                objectiveRows[i].gameObject.SetActive(show); objectiveDots[i].gameObject.SetActive(show);
                if (!show) continue;
                var o = q.objectives[i];
                bool active = !o.done && o.group == activeGroup;
                objectiveRows[i].text = o.done ? $"<color=#{Palette.Hex(Palette.UiMuted)}>{o.text}</color>" : active ? o.text : $"<color=#{Palette.Hex(Palette.UiMuted)}>{o.text}</color>";
                objectiveRows[i].fontStyle = active ? FontStyle.Bold : FontStyle.Normal;
                objectiveDots[i].color = o.done ? Good : active ? Sun : new Color(1, 1, 1, 0.15f);
            }
            progressText.text = new string('★', done) + $"<color=#3a4266>{new string('★', q.objectives.Count - done)}</color>   {done}/{q.objectives.Count}";
            if (startMission)
            {
                var lines = new List<string>();
                foreach (var o in q.objectives) lines.Add($"   <color=#{Palette.Hex(Sun)}>●</color>  " + o.text);
                startMission.text = $"<color=#{Palette.Hex(Sun)}><b>{q.title.ToUpperInvariant()}</b></color>   <color=#{Palette.Hex(Palette.UiMuted)}>{(q.day ? "daytime" : "night-time")} mission</color>\n<i>{q.story}</i>\n\n" + string.Join("\n", lines);
            }
            string key = q.title + done + quest.Started;
            if (key != questKey) { questKey = key; RebuildChips(); }
        }

        // ================================================================== input
        void Submit()
        {
            string s = input.text.Trim();
            submittedFrame = Time.frameCount;
            input.text = "";
            input.DeactivateInputField();
            if (EventSystem.current) EventSystem.current.SetSelectedGameObject(null);
            if (s.Length == 0) return;
            if (!connected) { Toast("MOSS'S BRAIN IS OFFLINE  ·  start backend/server.py", Palette.UiError); return; }
            if (OverlayOpen) return;
            executor.Execute(s);
        }

        static void Kill(GameObject g) { if (Application.isPlaying) { g.transform.SetParent(null); Destroy(g); } else DestroyImmediate(g); }

        public void FocusInput() { if (input) { input.ActivateInputField(); input.Select(); } }

        void SetBrain(bool on)
        {
            brainVisible = on;
            brainPanel.gameObject.SetActive(on);
            eyesPanel.gameObject.SetActive(!on);
        }

        // ================================================================== API used by the executor
        public void SetBusy(bool b) { busy = b; if (!b && scanLine) scanLine.gameObject.SetActive(false); }

        public void MossSays(string text, bool dots)
        {
            if (dialogueText == null) return;
            if (text == dialogueTarget && dots == dialogueDots) return;
            dialogueTarget = text ?? ""; dialogueDots = dots; dialogueShown = 0f; dialoguePop = 1f;
        }

        public void ShowPlanning()
        {
            thinkingSince = Time.time;
            understandText.text = "thinking about what you said…";
            for (int i = 0; i < planRows.Count; i++) planRows[i].text = "";
            foreach (var r in eyesPlanRows) r.text = "";
            eyesStatus.text = "THINKING ABOUT WHAT YOU SAID…";
            actionText.text = "LISTENING…";
            targetText.text = "Sending your words + 4 camera views to the planner";
            statText.text = ""; scanLine.gameObject.SetActive(true);
            foreach (var f in tileFrames) f.color = new Color(1, 0.62f, 0.18f, 0f);
            bboxRect.gameObject.SetActive(false);
        }

        public void ShowPlan(Plan p)
        {
            shownPlan = p;
            planStates = new StepState[p.steps.Length];
            scanLine.gameObject.SetActive(false);
            understandText.text = string.IsNullOrEmpty(p.understanding) ? "—" : p.understanding;
            notesText.text = (p.warnings != null && p.warnings.Length > 0 ? "• " + string.Join("\n• ", p.warnings) : "• plan JSON valid · skills whitelisted · amounts clamped")
                             + $"\n• planner {p.latency_ms / 1000f:0.0} s" + (p.unsure ? "  ·  MOSS is unsure -> asks first" : "") + (p.mode == "mock" ? "   ·   DEV MODE: scripted plan, not from a VLM" : "");
            actionText.text = p.unsure ? "ASKING YOU" : "PLAN READY"; targetText.text = ""; statText.text = "";
            eyesStatus.text = p.unsure ? "NOT SURE - CHECKING WITH YOU" : $"PLAN  ·  thought for {p.latency_ms / 1000f:0.0} s";
            RefreshPlanRows();
        }

        public void SetPlanStep(int i, StepState s)
        {
            if (shownPlan == null || i < 0 || i >= planStates.Length) return;
            planStates[i] = s;
            RefreshPlanRows();
        }

        void RefreshPlanRows()
        {
            for (int i = 0; i < planRows.Count; i++)
            {
                if (shownPlan == null || i >= shownPlan.steps.Length) { planRows[i].text = ""; eyesPlanRows[i].text = ""; continue; }
                var st = planStates[i];
                string icon = st == StepState.Done ? $"<color=#{Palette.Hex(Good)}>✓</color>" : st == StepState.Active ? $"<color=#{Palette.Hex(Sun)}>▶</color>"
                            : st == StepState.Failed ? $"<color=#{Palette.Hex(Palette.UiError)}>✗</color>" : $"<color=#{Palette.Hex(Palette.UiMuted)}>○</color>";
                var step = shownPlan.steps[i];
                string req = string.IsNullOrEmpty(step.request) ? "" : $"  <color=#{Palette.Hex(Palette.UiMuted)}><i>“{step.request}”</i></color>";
                planRows[i].text = $"{icon}  <b>{step.skill}</b>  {CommandParser.DescribeStep(step)}{req}";
                eyesPlanRows[i].text = $"{icon}  {(st == StepState.Active ? "<b>" : "")}{CommandParser.DescribeStep(step)}{(st == StepState.Active ? "</b>" : "")}";
            }
        }

        public void ShowThinking(int step, int max)
        {
            thinkingSince = Time.time;
            actionText.text = "LOOKING…";
            targetText.text = $"vision step {step + 1}/{max}: 4 views → VLM → bounding box";
            eyesStatus.text = step == 0 ? "LOOKING AROUND WITH ALL 4 CAMERAS…" : "LOOKING AGAIN…";
            statText.text = "";
            foreach (var f in tileFrames) f.color = new Color(1, 0.62f, 0.18f, 0f);
            scanLine.gameObject.SetActive(true);
        }

        public void ShowCommand(Command c, int step, int max)
        {
            scanLine.gameObject.SetActive(false);
            actionText.text = CommandParser.Pretty(c.action);
            string dir = c.direction != "none" && c.target_visible ? $"  ·  {c.direction} ({c.view.ToLowerInvariant()} camera)" : "";
            string nice = CommandParser.PrettyTarget(c.target);
            targetText.text = c.target_visible ? "→ " + nice + dir : "searching for " + nice;
            eyesStatus.text = c.target_visible ? $"FOUND THE {nice.ToUpperInvariant()}  ·  {c.direction}" : $"CAN'T SEE THE {nice.ToUpperInvariant()} YET - SEARCHING";
            string bearing = c.target_visible && c.tile >= 0 ? $"bearing {c.bearing_deg:+0;-0;0}°" : "no bearing";
            statText.text = $"{bearing}   ·   VLM {c.latency_ms / 1000f:0.0} s   ·   step {step + 1}/{max}   ·   status {c.status}";
            obsText.text = string.IsNullOrEmpty(c.observation) ? "—" : c.observation;
            reasonText.text = string.IsNullOrEmpty(c.reason) ? "—" : c.reason;
            notesText.text = (c.warnings != null && c.warnings.Length > 0 ? "• " + string.Join("\n• ", c.warnings) : "• JSON valid · action whitelisted · limits enforced")
                           + (c.mode == "mock" ? "\n• DEV MODE (mock backend): reply is scripted, not from a VLM" : "");
            bool hasBox = c.target_visible && c.tile >= 0 && c.target_bbox != null && c.target_bbox.Length == 4 && c.target_bbox[2] > c.target_bbox[0];
            if (hasBox)
            {
                var tile = tiles[c.tile];
                bboxRect.SetParent(tile, false);
                tileFrames[c.tile].color = new Color(1, 0.62f, 0.18f, 0.9f);
                float sx = tile.rect.width / CameraCapture.Width, sy = tile.rect.height / CameraCapture.Height;
                bboxRect.anchoredPosition = new Vector2(c.target_bbox[0] * sx, -c.target_bbox[1] * sy);
                bboxRect.sizeDelta = new Vector2((c.target_bbox[2] - c.target_bbox[0]) * sx, (c.target_bbox[3] - c.target_bbox[1]) * sy);
                bboxTag.text = nice.ToUpperInvariant();
                bboxRect.gameObject.SetActive(true);
                // the grounding box also shows in MOSS'S EYES when it was found by the front camera
                if (c.tile == 0)
                {
                    eyesFeed.texture = liveFront;
                    for (int i = 0; i < eyeBoxes.Count; i++) eyeBoxes[i].rt.gameObject.SetActive(false);
                    PlaceEyeBox(0, c.target_bbox, "★ " + nice.ToUpperInvariant(), true);
                    detectionsAt = Time.time;
                }
            }
            else bboxRect.gameObject.SetActive(false);
        }

        void PlaceEyeBox(int i, int[] b, string label, bool target)
        {
            var box = GetEyeBox(i);
            var area = eyesBoxes.rect;
            float sx = area.width / CameraCapture.Width, sy = area.height / CameraCapture.Height;
            box.rt.anchoredPosition = new Vector2(b[0] * sx, -b[1] * sy);
            box.rt.sizeDelta = new Vector2(Mathf.Max(8, (b[2] - b[0]) * sx), Mathf.Max(8, (b[3] - b[1]) * sy));
            var col = target ? Sun : Candy[(i + 2) % Candy.Length];
            box.frame.color = col; box.tagBg.color = col;
            box.tag.text = label;
            box.tagBg.rectTransform.sizeDelta = new Vector2(label.Length * 7.4f + 14, 17);
            box.rt.gameObject.SetActive(true);
        }

        /// <summary>Live detections from the VLM on the front camera: boxes in MOSS'S EYES + labels floating over the objects.</summary>
        public void ShowDetections(List<SeenThing> seen, Texture2D frame, int latencyMs)
        {
            if (eyesFeed == null) return;
            eyesFeed.texture = frame;
            detectionsAt = Time.time;
            for (int i = 0; i < eyeBoxes.Count; i++) eyeBoxes[i].rt.gameObject.SetActive(false);
            var names = new List<string>();
            for (int i = 0; i < seen.Count; i++)
            {
                var s = seen[i];
                PlaceEyeBox(i, s.bbox, (s.isTarget ? "★ " : "") + s.label.ToUpperInvariant(), s.isTarget);
                names.Add(s.isTarget ? $"<color=#{Palette.Hex(Sun)}>{s.label}</color>" : s.label);
            }
            eyesCaption.text = seen.Count == 0 ? "I don't see anything special here." : "I SEE:  " + string.Join("  ·  ", names);
            eyesAge.text = $"VLM DETECTION · {latencyMs / 1000f:0.0} s";
            // world tags
            foreach (var t in tags) if (t != pinned) t.on = false;
            int k = 0;
            var labelled = new HashSet<string>();
            foreach (var s in seen)
            {
                if (!s.world.HasValue || !labelled.Add(s.label.ToLowerInvariant())) continue;   // one floating label per kind
                var t = GetTag(k++);
                if (t == pinned) t = GetTag(k++);
                t.on = true; t.target = s.isTarget; t.world = s.world.Value + Vector3.up * 1.2f; t.born = Time.time;
                t.text.text = (s.isTarget ? "★ " : "") + s.label;
                t.bg.color = s.isTarget ? Sun : new Color(1f, 1f, 1f, 0.85f);
                t.rt.sizeDelta = new Vector2(t.text.text.Length * 8.6f + 22, 28);
            }
        }

        /// <summary>Pin the goal the VLM grounded (gold label over it in the 3D view).</summary>
        public void PinTarget(string name, Vector3 world)
        {
            if (pinned == null) pinned = GetTag(tags.Count);
            pinned.on = true; pinned.target = true; pinned.world = world + Vector3.up * 2.2f; pinned.born = Time.time;
            pinned.text.text = "★ " + name.ToUpperInvariant();
            pinned.bg.color = Sun;
            pinned.rt.sizeDelta = new Vector2(pinned.text.text.Length * 9f + 26, 30);
        }

        public void ClearDetections()
        {
            foreach (var t in tags) { t.on = false; if (t.rt) t.rt.gameObject.SetActive(false); }
            foreach (var b in eyeBoxes) b.rt.gameObject.SetActive(false);
            if (eyesFeed) eyesFeed.texture = liveFront;
            if (eyesCaption) eyesCaption.text = "";
            if (eyesAge) eyesAge.text = "LIVE · FRONT CAMERA";
        }

        public void SetTakeover(bool on) { takeoverOn = on; if (takeoverBg) takeoverBg.gameObject.SetActive(on); }

        public void ShowQuestion(string question, string guess, string[] options)
        {
            questionText.text = question;
            for (int i = questionButtons.childCount - 1; i >= 0; i--) Kill(questionButtons.GetChild(i).gameObject);
            if (!string.IsNullOrEmpty(guess))
            {
                var y = Pill(questionButtons, "✓ YES!", Good, 15, () => executor.ConfirmYes());
                ((RectTransform)y.transform).sizeDelta = new Vector2(110, 40);
            }
            int k = 0;
            foreach (var o in options ?? new string[0])
            {
                string opt = o;
                var b = Pill(questionButtons, opt, Candy[(k++ + 3) % Candy.Length], 14, () => executor.Execute(opt));
                ((RectTransform)b.transform).sizeDelta = new Vector2(Mathf.Min(260, opt.Length * 8f + 30), 40);
            }
            var no = Pill(questionButtons, string.IsNullOrEmpty(guess) ? "SOMETHING ELSE" : "NO", Palette.UiMuted, 14, () => executor.ConfirmNo(), false);
            ((RectTransform)no.transform).sizeDelta = new Vector2(string.IsNullOrEmpty(guess) ? 160 : 80, 40);
            questionCard.gameObject.SetActive(true);
            questionCard.localScale = Vector3.one * 0.85f;
        }

        public void HideQuestion() { if (questionCard) questionCard.gameObject.SetActive(false); }

        public void SetGoal(float distance, string how) => statText.text += $"\n3D goal {distance:0.0} m away · {how}";
        public void SetMovementResult(string r) { if (!string.IsNullOrEmpty(r)) Tech("↳ " + r); }

        void Tech(string s)
        {
            techLines.Add(System.Text.RegularExpressions.Regex.Replace(s, "<.*?>", ""));
            while (techLines.Count > 7) techLines.RemoveAt(0);
            if (techLog) techLog.text = string.Join("\n", techLines);
        }

        public void ShowError(string e)
        {
            scanLine.gameObject.SetActive(false);
            actionText.text = "ERROR"; targetText.text = ""; reasonText.text = e;
            eyesStatus.text = "CAN'T REACH MY BRAIN";
            Toast("CAN'T REACH MOSS'S BRAIN", Palette.UiError);
        }

        public void ShowHelp(string why, PlanStep s)
        {
            helpText.text = $"While trying to <b>{CommandParser.DescribeStep(s).ToLowerInvariant()}</b>, MOSS says: <i>“{why}.”</i>\n" +
                            "Drive MOSS closer with <b>WASD</b>, then press <b>TRY AGAIN</b>.";
            helpCard.gameObject.SetActive(true);
            helpCard.localScale = Vector3.one * 0.85f;
        }

        public void HideHelp() { if (helpCard) helpCard.gameObject.SetActive(false); }

        public void LogCommand(Command c, int step)
        {
            string tgt = string.IsNullOrEmpty(c.target) ? "" : " " + CommandParser.PrettyTarget(c.target);
            string extra = c.action == "MOVE_TO_TARGET" ? $" ({c.bearing_deg:+0;-0;0}°)" : c.duration > 0 ? $" ({c.duration:0.0}s)" : "";
            Tech($"look {step + 1}: {CommandParser.Pretty(c.action)}{tgt}{extra} [{c.status}] {c.latency_ms / 1000f:0.0}s");
        }

        /// <summary>
        /// Chat: the player's words and MOSS's replies are bubbles; game events are small centred lines.
        /// Technical movement lines (↳ ...) go to the Brain panel's movement log instead.
        /// </summary>
        public void Log(string s, LogKind kind)
        {
            if (!Application.isPlaying) Debug.Log("[MOSS] log: " + System.Text.RegularExpressions.Regex.Replace(s, "<.*?>", ""));
            if (kind == LogKind.Move || s.TrimStart().StartsWith("↳") || s.StartsWith("▸")) { Tech(s.Trim()); return; }
            string clean = s.Replace("<b>You:</b> ", "").Replace("<b>MOSS:</b> ", "").Replace("<b>MOSS asks:</b> ", "");
            if (s.StartsWith("<i>MOSS understood:</i>")) { clean = s.Replace("<i>MOSS understood:</i> ", ""); kind = LogKind.Info; clean = "<i>" + clean + "</i>"; AddBubble(clean, LogKind.Info); return; }
            AddBubble(clean, kind);
        }

        public void Toast(string s, Color c)
        {
            if (toastText == null) return;
            toastText.text = s; toastText.color = c;
            toastBg.transform.Find("Outline").GetComponent<Image>().color = new Color(c.r, c.g, c.b, 0.8f);
            toastBg.gameObject.SetActive(true);
            toastUntil = Time.time + 3f; toastPop = 1f;
        }

        void Confetti(Vector2 at, int n)
        {
            if (confettiLayer == null || !Application.isPlaying) return;
            for (int i = 0; i < n; i++)
            {
                Bit b = null;
                foreach (var x in bits) if (x.life <= 0) { b = x; break; }
                if (b == null)
                {
                    var img = UIKit.Dot(confettiLayer, "Bit", Color.white, 10);
                    img.rectTransform.anchorMin = img.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
                    b = new Bit { rt = img.rectTransform, img = img };
                    bits.Add(b);
                }
                b.img.color = Candy[Random.Range(0, Candy.Length)];
                float s = Random.Range(7f, 14f); b.rt.sizeDelta = new Vector2(s, s * Random.Range(0.6f, 1.4f));
                b.rt.anchoredPosition = at;
                float a = Random.Range(0f, Mathf.PI * 2f), sp = Random.Range(250f, 700f);
                b.v = new Vector2(Mathf.Cos(a) * sp, Mathf.Abs(Mathf.Sin(a)) * sp + 200f);
                b.life = Random.Range(1.2f, 2f);
                b.rt.gameObject.SetActive(true);
            }
        }

        void SetIdleBrain()
        {
            understandText.text = "—"; actionText.text = "STANDING BY"; targetText.text = "Waiting for your words";
            obsText.text = "—"; reasonText.text = "—"; statText.text = "";
            notesText.text = "• Every AI reply is schema-checked and clamped before MOSS moves.";
            eyesStatus.text = "WAITING FOR YOUR WORDS"; eyesAge.text = "LIVE · FRONT CAMERA";
        }

        public void OnHealth(bool ok, HealthDto h)
        {
            connected = ok;
            gpuPercent = h != null && h.loaded ? h.gpu_percent : -1;
            if (ok)
            {
                bool mock = h.mode == "mock";
                connText.text = mock ? "MOCK BACKEND · DEV MODE (no AI)" : "BRAIN ONLINE · " + (h.model ?? "VLM").ToUpperInvariant() + (gpuPercent >= 100 ? " · GPU" : "");
                connDot.color = mock ? Sun : gpuPercent >= 0 && gpuPercent < 100 ? Peach : Good;
            }
            else if (h != null && h.ollama && !h.model_available) { connText.text = "MODEL NOT INSTALLED"; connDot.color = Sun; }
            else if (h != null && !h.ollama) { connText.text = "OLLAMA NOT RUNNING"; connDot.color = Palette.UiError; }
            else { connText.text = "BRAIN OFFLINE · start backend"; connDot.color = Palette.UiError; }
            bool slow = ok && gpuPercent >= 0 && gpuPercent < 100;
            if (gpuBg)
            {
                gpuBg.gameObject.SetActive(slow);
                gpuText.text = gpuPercent == 0
                    ? "SLOW BRAIN: the AI is running on the CPU, not the GPU (replies take 20-80 s).\nRestart Ollama (or the PC), start the backend first, then press Play."
                    : $"SLOW BRAIN: only {gpuPercent}% of the AI fits in GPU memory (replies are slower).\nClose other GPU-heavy apps, restart the backend, then press Play.";
            }
        }

        /// <summary>Editor screenshots only: apply one frame of HUD state with the dialogue fully typed.</summary>
        public void Update_ForPreview()
        {
            dialogueShown = 9999f; dialoguePop = 0f; toastPop = 0f;
            foreach (var b in bubbles) b.rt.localScale = Vector3.one;
            if (questionCard) questionCard.localScale = Vector3.one;
            if (helpCard) helpCard.localScale = Vector3.one;
            Update();
        }

        // ================================================================== per frame
        void Update()
        {
            InputFocused = input != null && input.isFocused;
            bool overlay = OverlayOpen;
            RoverController.DriveInputAllowed = !InputFocused && !overlay;
            float dt = Application.isPlaying ? Time.deltaTime : 0.033f;

            // keys
            bool enter = InputCompat.EnterDown() && Time.frameCount - submittedFrame > 1;
            if (overlay)
            {
                if (enter) { if (startOverlay.gameObject.activeSelf) StartMission(); else NextMission(); }
            }
            else if (!InputFocused)
            {
                if (enter) FocusInput();
                if (InputCompat.TabDown()) SetBrain(!brainVisible);
                if (InputCompat.KeyDown('n') && dayNight) dayNight.Toggle();
                if (InputCompat.KeyDown('r') && executor.NeedsHelp) executor.Retry();
                if (InputCompat.EscDown() && (executor.Running || executor.Asking)) executor.Cancel();
                // grabbing the keyboard while MOSS works hands it the wheel; it carries on when the keys are released
                if (executor.Running && (Mathf.Abs(InputCompat.Vertical()) > 0.1f || Mathf.Abs(InputCompat.Horizontal()) > 0.1f)) executor.PlayerTookWheel();
            }
            else if (InputCompat.EscDown()) { input.DeactivateInputField(); if (EventSystem.current) EventSystem.current.SetSelectedGameObject(null); }

            if (!overlay && InputCompat.LeftClickDown() && !PointerOverUI && Camera.main != null)
            {
                var ray = Camera.main.ScreenPointToRay(InputCompat.MousePosition());
                if (Physics.Raycast(ray, out var hit, 200f, 1 << RoverController.RoverLayer, QueryTriggerInteraction.Ignore) && hit.collider.GetComponentInParent<RoverController>())
                    rover.Poke();
            }

            // dialogue typewriter + pop
            dialogueShown += dt * 75f;
            int n = Mathf.Min(dialogueTarget.Length, (int)dialogueShown);
            dialogueText.text = dialogueTarget.Substring(0, n) + (dialogueDots && n >= dialogueTarget.Length ? new string('.', 1 + (int)(Time.time * 3f) % 3) : "");
            dialoguePop = Mathf.MoveTowards(dialoguePop, 0f, dt * 4f);
            dialoguePanel.localScale = Vector3.one * (1f + 0.035f * Mathf.Sin(dialoguePop * Mathf.PI));

            // mood indicator
            var mode = rover.Mode;
            Color c; string label;
            switch (mode)
            {
                case RoverMode.Thinking: c = Lilac; label = "THINKING"; break;
                case RoverMode.Moving: c = Sun; label = "ON THE WAY"; break;
                case RoverMode.Arrived: c = Good; label = "HAPPY!"; break;
                case RoverMode.Error: c = Palette.UiError; label = "CONFUSED"; break;
                case RoverMode.Manual: c = Sky; label = "YOU'RE DRIVING"; break;
                case RoverMode.NeedsHelp: c = Peach; label = "NEEDS HELP!"; break;
                default: c = Mint; label = executor.Asking ? "WAITING FOR YOUR ANSWER" : "LISTENING"; break;
            }
            if (mode == RoverMode.Thinking) label += $"  {Time.time - thinkingSince:0.0}s";
            float pulse = mode == RoverMode.Thinking || mode == RoverMode.Moving || mode == RoverMode.NeedsHelp ? 0.55f + 0.45f * Mathf.Sin(Time.time * 6f) : 1f;
            dialogueMode.text = label; dialogueMode.color = c;
            modeDot.color = new Color(c.r, c.g, c.b, pulse);
            portraitRing.color = Color.Lerp(portraitRing.color, c, dt * 6f);
            portraitRing.rectTransform.localScale = Vector3.one * (mode == RoverMode.Thinking ? 1f + 0.04f * Mathf.Sin(Time.time * 8f) : 1f);
            connDot.transform.localScale = Vector3.one * (connected ? 1f + 0.15f * Mathf.Sin(Time.time * 3f) : 1f);
            recDot.color = new Color(Palette.Ember.r, Palette.Ember.g, Palette.Ember.b, mode == RoverMode.Thinking ? (Mathf.Repeat(Time.time, 0.6f) < 0.3f ? 1f : 0.2f) : 0.85f);
            feedTag.text = mode == RoverMode.Thinking ? $"VIEWS SENT TO VLM  ·  {Time.time - thinkingSince:0.0}s" : "LIVE · 360°";
            if (scanLine.gameObject.activeSelf && feedRect.rect.height > 0)
                scanLine.rectTransform.anchoredPosition = new Vector2(0, -Mathf.PingPong(Time.time * 160f, feedRect.rect.height));
            if (Time.time - detectionsAt > 6f && eyesFeed.texture != liveFront) ClearDetections();

            // pops, banners, toast
            questionCard.localScale = Vector3.Lerp(questionCard.localScale, Vector3.one, dt * 12f);
            helpCard.localScale = Vector3.Lerp(helpCard.localScale, Vector3.one, dt * 12f);
            completeStars.rectTransform.localScale = Vector3.Lerp(completeStars.rectTransform.localScale, Vector3.one, dt * 5f);
            if (startTitle) startTitle.anchoredPosition = new Vector2(0, -26 + 5f * Mathf.Sin(Time.time * 2f));
            if (takeoverOn) takeoverBg.color = new Color(Sky.r, Sky.g, Sky.b, 0.75f + 0.25f * Mathf.Sin(Time.time * 6f));
            toastPop = Mathf.MoveTowards(toastPop, 0f, dt * 5f);
            toastBg.rectTransform.localScale = Vector3.one * (1f + 0.25f * toastPop);
            if (toastBg.gameObject.activeSelf && Time.time > toastUntil) toastBg.gameObject.SetActive(false);
            foreach (var b in bubbles) b.rt.localScale = Vector3.Lerp(b.rt.localScale, Vector3.one, dt * 14f);

            // floating labels over detected objects
            var cam = Camera.main;
            foreach (var t in tags)
            {
                bool vis = t.on && cam && Time.time - t.born < (t == pinned ? 60f : 5f);
                Vector3 sp = vis ? cam.WorldToScreenPoint(t.world) : Vector3.zero;
                vis &= sp.z > 0;
                t.rt.gameObject.SetActive(vis);
                if (!vis) continue;
                float scale = root.rect.width / Mathf.Max(1, Screen.width);
                t.rt.anchoredPosition = new Vector2(sp.x * scale, sp.y * scale + 6f * Mathf.Sin(Time.time * 3f + t.world.x));
            }

            // confetti
            foreach (var b in bits)
            {
                if (b.life <= 0) continue;
                b.life -= dt; b.v.y -= 900f * dt;
                b.rt.anchoredPosition += b.v * dt;
                b.rt.Rotate(0, 0, 300f * dt);
                if (b.life <= 0) b.rt.gameObject.SetActive(false);
            }

            goBtn.interactable = connected;
            stopBtn.interactable = executor.Running || executor.Asking;
            if (dayNight) dayLabel.text = dayNight.IsDay ? "DAY  [N]" : "NIGHT  [N]";

            if (quest && quest.Current != null)
            {
                float sec = quest.Elapsed;
                missionTimer.text = quest.Started ? $"{(int)sec / 60}:{(int)sec % 60:00}" : "";
                missionStats.text = $"{quest.Commands} commands  ·  {quest.HelpCount} helps";
            }
        }
    }
}
