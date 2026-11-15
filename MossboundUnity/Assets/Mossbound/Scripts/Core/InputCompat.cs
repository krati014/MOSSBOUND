using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Mossbound
{
    /// <summary>Works with both the new Input System package and the legacy Input Manager.</summary>
    public static class InputCompat
    {
#if ENABLE_INPUT_SYSTEM
        public static bool EnterDown() => Keyboard.current != null && (Keyboard.current.enterKey.wasPressedThisFrame || Keyboard.current.numpadEnterKey.wasPressedThisFrame);
        public static bool F1Down() => Keyboard.current != null && Keyboard.current.f1Key.wasPressedThisFrame;
        public static bool EscDown() => Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame;
        public static float Vertical()
        {
            var k = Keyboard.current; if (k == null) return 0;
            return (k.wKey.isPressed || k.upArrowKey.isPressed ? 1 : 0) - (k.sKey.isPressed || k.downArrowKey.isPressed ? 1 : 0);
        }
        public static float Horizontal()
        {
            var k = Keyboard.current; if (k == null) return 0;
            return (k.dKey.isPressed || k.rightArrowKey.isPressed ? 1 : 0) - (k.aKey.isPressed || k.leftArrowKey.isPressed ? 1 : 0);
        }
        public static bool RightMouse() => Mouse.current != null && Mouse.current.rightButton.isPressed;
        public static Vector2 MouseDelta() => Mouse.current != null ? Mouse.current.delta.ReadValue() : Vector2.zero;
        public static float Scroll() => Mouse.current != null ? Mouse.current.scroll.ReadValue().y / 120f : 0f;
        public static bool LeftClickDown() => Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame;
        public static bool SpaceDown() => Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame;
        public static bool ShiftHeld() => Keyboard.current != null && Keyboard.current.leftShiftKey.isPressed;
        public static bool TabDown() => Keyboard.current != null && Keyboard.current.tabKey.wasPressedThisFrame;
        public static bool KeyDown(char c)
        {
            var k = Keyboard.current; if (k == null) return false;
            switch (char.ToLowerInvariant(c))
            {
                case 'n': return k.nKey.wasPressedThisFrame;
                case 'h': return k.hKey.wasPressedThisFrame;
                case 'r': return k.rKey.wasPressedThisFrame;
                default: return false;
            }
        }
        public static Vector2 MousePosition() => Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero;
#else
        public static bool LeftClickDown() => Input.GetMouseButtonDown(0);
        public static bool SpaceDown() => Input.GetKeyDown(KeyCode.Space);
        public static bool ShiftHeld() => Input.GetKey(KeyCode.LeftShift);
        public static bool TabDown() => Input.GetKeyDown(KeyCode.Tab);
        public static bool KeyDown(char c) => Input.GetKeyDown((KeyCode)char.ToLowerInvariant(c));
        public static Vector2 MousePosition() => Input.mousePosition;
        public static bool EnterDown() => Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter);
        public static bool F1Down() => Input.GetKeyDown(KeyCode.F1);
        public static bool EscDown() => Input.GetKeyDown(KeyCode.Escape);
        public static float Vertical() => Input.GetAxisRaw("Vertical");
        public static float Horizontal() => Input.GetAxisRaw("Horizontal");
        public static bool RightMouse() => Input.GetMouseButton(1);
        public static Vector2 MouseDelta() => new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y")) * 10f;
        public static float Scroll() => Input.mouseScrollDelta.y;
#endif
    }
}
