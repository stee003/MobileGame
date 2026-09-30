using UnityEngine;

namespace MobileGame.Input
{
    /// <summary>
    /// Lightweight Play Mode diagnostic. Add this to a scene object, then press any mapped
    /// key/button or move the mouse; detected actions and vector input are reported in Console.
    /// This observes input only and deliberately performs no gameplay behavior.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class InputArchitectureTest : MonoBehaviour
    {
        private GameInput m_input;
        private Vector2 m_lastMove;
        private Vector2 m_lastLook;

        private void OnEnable()
        {
            m_input = GameInput.Instance;
            if (m_input == null)
            {
                Debug.LogError("[InputTest] GameInput is unavailable. Ensure its bootstrap and input asset are present.", this);
                return;
            }

            m_input.ActionPerformed += OnActionPerformed;
            m_input.ActionCanceled += OnActionCanceled;
            Debug.Log("[InputTest] Listening. Move: WASD/arrows; Look: mouse; buttons: LMB, RMB, Space, 1/2/3, R, E.", this);
        }

        private void OnDisable()
        {
            if (m_input == null)
                return;

            m_input.ActionPerformed -= OnActionPerformed;
            m_input.ActionCanceled -= OnActionCanceled;
        }

        private void Update()
        {
            if (m_input == null)
                return;

            Vector2 move = m_input.Move;
            if ((move - m_lastMove).sqrMagnitude > 0.0001f)
            {
                if (move.sqrMagnitude > 0f || m_lastMove.sqrMagnitude > 0f)
                    Debug.Log($"[InputTest] Move = {move}", this);
                m_lastMove = move;
            }

            Vector2 look = m_input.Look;
            if ((look - m_lastLook).sqrMagnitude > 0.0001f)
            {
                if (look.sqrMagnitude > 0f || m_lastLook.sqrMagnitude > 0f)
                    Debug.Log($"[InputTest] Look = {look}", this);
                m_lastLook = look;
            }
        }

        /// <summary>Run in Play Mode from the component's context menu to check the whole action list.</summary>
        [ContextMenu("Verify: Gameplay Input Actions")]
        public void VerifyInputActions()
        {
            if (m_input == null)
                m_input = GameInput.Instance;

            if (m_input == null)
            {
                Debug.LogError("[InputTest] GameInput is unavailable.", this);
                return;
            }

            if (m_input.ValidateConfiguration(out string report))
                Debug.Log($"[InputTest] VERIFICATION PASSED. {report}", this);
            else
                Debug.LogError($"[InputTest] VERIFICATION FAILED. {report}", this);
        }

        private void OnActionPerformed(GameInputAction action)
        {
            Debug.Log($"[InputTest] Detected {action}.", this);
        }

        private void OnActionCanceled(GameInputAction action)
        {
            Debug.Log($"[InputTest] Released/canceled {action}.", this);
        }
    }
}
