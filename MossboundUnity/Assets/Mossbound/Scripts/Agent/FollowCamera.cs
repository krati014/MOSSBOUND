using UnityEngine;

namespace Mossbound
{
    /// <summary>Cinematic third-person camera: smooth follow, right-drag orbit, scroll zoom.</summary>
    public class FollowCamera : MonoBehaviour
    {
        public Transform target;
        public float distance = 10.5f, pitch = 24f, yawOffset = 0f;
        float yaw;
        Vector3 vel;

        Vector3 Focus => target.position + Vector3.up * 1.1f;

        Vector3 Desired()
        {
            var rot = Quaternion.Euler(pitch, yaw, 0);
            var p = Focus - rot * Vector3.forward * distance;
            float gh = EnvironmentController.Height(p.x, p.z) + 0.8f;
            if (p.y < gh) p.y = gh;
            return p;
        }

        public void Snap()
        {
            if (!target) return;
            yaw = target.eulerAngles.y + yawOffset;
            transform.position = Desired();
            transform.rotation = Quaternion.LookRotation(Focus - transform.position);
        }

        void LateUpdate()
        {
            if (!target) return;
            if (InputCompat.RightMouse())
            {
                var d = InputCompat.MouseDelta();
                yawOffset += d.x * 0.25f;
                pitch = Mathf.Clamp(pitch - d.y * 0.18f, 6f, 70f);
            }
            if (!UIController.PointerOverUI) distance = Mathf.Clamp(distance - InputCompat.Scroll() * 1.2f, 4f, 26f);
            yaw = Mathf.LerpAngle(yaw, target.eulerAngles.y + yawOffset, 1f - Mathf.Exp(-2.2f * Time.deltaTime));
            transform.position = Vector3.SmoothDamp(transform.position, Desired(), ref vel, 0.22f);
            transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(Focus - transform.position), 1f - Mathf.Exp(-10f * Time.deltaTime));
        }
    }
}
