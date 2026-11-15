using System;

namespace Mossbound
{
    // JSON contracts shared with the Python backend (backend/schema.py). Field names must match.

    [Serializable]
    public class RoverStateDto
    {
        public int step;
        public int max_steps;
        public float heading_deg;
        public bool obstacle_ahead;
        public float obstacle_distance;
        public string[] nearby = new string[0];
        public string last_action = "";
        public string last_result = "";
    }

    [Serializable]
    public class ActRequest
    {
        public string instruction;
        public string image_b64 = "";
        public string[] images_b64 = new string[0];   // FRONT, RIGHT, BACK, LEFT
        public int image_width;
        public int image_height;
        public float hfov_deg;
        public RoverStateDto state;
        public string[] history = new string[0];
        public string mission_target = "";
        public string target_description = "";
        public string view_hint = "";
        public string time_of_day = "night";
    }

    [Serializable]
    public class PlanRequest
    {
        public string instruction;
        public string[] images_b64 = new string[0];
        public int image_width;
        public int image_height;
        public string time_of_day = "night";
        public string quest = "";
        public string context = "";
        public string[] nearby = new string[0];
        public string[] recent = new string[0];
    }

    [Serializable]
    public class PlanStep
    {
        public string request = "";
        public string skill = "SAY";
        public string target = "";
        public string description = "";
        public string view = "";
        public float amount;
    }

    [Serializable]
    public class Plan
    {
        public string[] requests = new string[0];
        public string understanding = "";
        public string reply = "";
        public bool unsure;                            // MOSS wants to double-check before acting
        public string[] options = new string[0];       // other things the player might mean
        public PlanStep[] steps = new PlanStep[0];
        public string[] warnings = new string[0];
        public int latency_ms;
        public string model = "";
        public string mode = "vlm";
    }

    [Serializable]
    public class Command
    {
        public string action = "STOP";
        public string target = "";
        public string intent = "";
        public string direction = "none";
        public string view = "";
        public int tile = -1;
        public float bearing_deg;
        public float duration;
        public float speed;
        public float turn_rate = 70f;
        public string status = "executing";
        public string reason = "";
        public string observation = "";
        public bool target_visible;
        public int[] target_bbox = new int[4];        // pixels of ONE view (Width x Height), y down
        public string[] warnings = new string[0];
        public int step;
        public int latency_ms;
        public string model = "";
        public string mode = "vlm";
    }

    [Serializable]
    public class HealthDto
    {
        public string status;
        public string mode;
        public bool ollama;
        public string model;
        public bool model_available;
        public string error;
        public bool loaded;
        public int gpu_percent = -1;                   // share of the model in GPU memory (100 = fast)
    }

    [Serializable]
    public class DetectRequest
    {
        public string image_b64;
        public string target = "";
        public string time_of_day = "night";
    }

    [Serializable]
    public class Detection
    {
        public string label = "";
        public string landmark = "";
        public int[] bbox = new int[4];                // pixels of the FRONT view (Width x Height), y down
    }

    [Serializable]
    public class DetectResult
    {
        public Detection[] objects = new Detection[0];
        public int latency_ms;
    }
}
