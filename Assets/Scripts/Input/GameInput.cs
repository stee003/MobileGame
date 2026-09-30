using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MobileGame.Input
{
    /// <summary>Stable identifiers for the shared gameplay input vocabulary.</summary>
    public enum GameInputAction
    {
        Move,
        Look,
        LightAttack,
        HeavyAttack,
        Dodge,
        Ability1,
        Ability2,
        Ability3,
        Ultimate,
        Interact
    }

    /// <summary>
    /// Shared input facade for gameplay systems. Keyboard/mouse are configured in the
    /// GameInputActions asset; future virtual controls feed the same values/actions through
    /// SetMobileMove, SetMobileLook, and SetMobileButton.
    /// </summary>
    [DefaultExecutionOrder(-200)]
    public sealed class GameInput : MonoBehaviour
    {
        private const string ActionsResourcePath = "Input/GameInputActions";
        private const string GameplayMapName = "Gameplay";

        private static GameInput s_instance;

        private InputActionAsset m_actionsAsset;
        private InputActionMap m_gameplayMap;
        private InputAction m_moveAction;
        private InputAction m_lookAction;
        private readonly Dictionary<GameInputAction, InputAction> m_buttonActions =
            new Dictionary<GameInputAction, InputAction>();
        private readonly HashSet<GameInputAction> m_mobileButtonsDown =
            new HashSet<GameInputAction>();
        private readonly Dictionary<GameInputAction, int> m_mobilePressFrame =
            new Dictionary<GameInputAction, int>();
        private Vector2 m_mobileMove;
        private Vector2 m_mobileLook;

        public static GameInput Instance => s_instance;

        /// <summary>Raised whenever any action is performed (including Move and Look).</summary>
        public event Action<GameInputAction> ActionPerformed;
        /// <summary>Raised when an action is canceled/released.</summary>
        public event Action<GameInputAction> ActionCanceled;
        /// <summary>Raised on the press edge of any discrete gameplay action.</summary>
        public event Action<GameInputAction> ButtonPressed;
        /// <summary>Raised on the release edge of any discrete gameplay action.</summary>
        public event Action<GameInputAction> ButtonReleased;

        /// <summary>Combined keyboard/mobile movement input, clamped to a unit circle.</summary>
        public Vector2 Move
        {
            get
            {
                Vector2 keyboard = m_moveAction != null && m_moveAction.enabled
                    ? m_moveAction.ReadValue<Vector2>() : Vector2.zero;
                return Vector2.ClampMagnitude(keyboard + m_mobileMove, 1f);
            }
        }

        /// <summary>Combined mouse/mobile look delta. Consumers choose sensitivity and interpretation.</summary>
        public Vector2 Look
        {
            get
            {
                Vector2 mouse = (!ShouldSuppressRawMouseLook() && m_lookAction != null && m_lookAction.enabled)
                    ? m_lookAction.ReadValue<Vector2>() : Vector2.zero;
                return mouse + m_mobileLook;
            }
        }

        private static bool ShouldSuppressRawMouseLook()
        {
            if (UnityEngine.InputSystem.EnhancedTouch.TouchSimulation.instance != null &&
                UnityEngine.InputSystem.EnhancedTouch.TouchSimulation.instance.enabled)
            {
                return true;
            }

            if (UnityEngine.InputSystem.EnhancedTouch.EnhancedTouchSupport.enabled &&
                UnityEngine.InputSystem.EnhancedTouch.Touch.activeTouches.Count > 0)
            {
                return true;
            }

            return Cursor.lockState != CursorLockMode.Locked;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Bootstrap()
        {
            if (s_instance == null)
                new GameObject("GameInput").AddComponent<GameInput>();
        }

        private void Awake()
        {
            if (s_instance != null && s_instance != this)
            {
                Destroy(gameObject);
                return;
            }

            s_instance = this;
            DontDestroyOnLoad(gameObject);
            LoadActions();
        }

        private void OnEnable()
        {
            if (m_gameplayMap == null)
                return; // Awake loads the map before this callback; also permits an inspector-created instance.

            Subscribe();
            m_gameplayMap.Enable();
        }

        private void OnDisable()
        {
            if (m_gameplayMap != null)
            {
                Unsubscribe();
                m_gameplayMap.Disable();
            }
        }

        private void OnDestroy()
        {
            if (s_instance == this)
                s_instance = null;
        }

        /// <summary>Read the pressed state of a discrete action.</summary>
        public bool IsPressed(GameInputAction action)
        {
            return m_mobileButtonsDown.Contains(action) ||
                   (m_buttonActions.TryGetValue(action, out InputAction inputAction) && inputAction.IsPressed());
        }

        /// <summary>True on the frame the keyboard/mouse or mobile button was pressed.</summary>
        public bool WasPressedThisFrame(GameInputAction action)
        {
            bool mobilePressedThisFrame = m_mobilePressFrame.TryGetValue(action, out int frame) && frame == Time.frameCount;
            return mobilePressedThisFrame ||
                   (m_buttonActions.TryGetValue(action, out InputAction inputAction) && inputAction.WasPressedThisFrame());
        }

        /// <summary>
        /// Supply the current virtual-stick value. Call with zero when the stick is released.
        /// This input is composed with keyboard movement and clamped to unit magnitude.
        /// </summary>
        public void SetMobileMove(Vector2 value) => m_mobileMove = Vector2.ClampMagnitude(value, 1f);

        /// <summary>Supply a virtual camera-look delta for this frame; call with zero when idle.</summary>
        public void SetMobileLook(Vector2 value) => m_mobileLook = value;

        /// <summary>
        /// Set a future on-screen button state. Move and Look are vectors and must use their
        /// corresponding setters. Repeated calls with the same state do not repeat events.
        /// </summary>
        public void SetMobileButton(GameInputAction action, bool isDown)
        {
            if (!m_buttonActions.ContainsKey(action))
            {
                Debug.LogWarning($"[GameInput] '{action}' is not a discrete gameplay action; use the vector setters for Move and Look.", this);
                return;
            }

            bool wasDown = m_mobileButtonsDown.Contains(action);
            if (wasDown == isDown)
                return;

            if (isDown)
            {
                m_mobileButtonsDown.Add(action);
                m_mobilePressFrame[action] = Time.frameCount;
                ButtonPressed?.Invoke(action);
                ActionPerformed?.Invoke(action);
            }
            else
            {
                m_mobileButtonsDown.Remove(action);
                ButtonReleased?.Invoke(action);
                ActionCanceled?.Invoke(action);
            }
        }

        /// <summary>Check whether the required action map and every declared action are available.</summary>
        public bool ValidateConfiguration(out string report)
        {
            if (m_gameplayMap == null)
            {
                report = "Gameplay action map was not loaded.";
                return false;
            }

            foreach (GameInputAction id in Enum.GetValues(typeof(GameInputAction)))
            {
                if (m_gameplayMap.FindAction(id.ToString(), false) == null)
                {
                    report = $"Required action '{id}' is missing from the Gameplay map.";
                    return false;
                }
            }

            report = $"Gameplay map ready: {m_gameplayMap.actions.Count} actions, {m_gameplayMap.bindings.Count} bindings.";
            return true;
        }

        private void LoadActions()
        {
            m_actionsAsset = Resources.Load<InputActionAsset>(ActionsResourcePath);
            if (m_actionsAsset == null)
            {
                Debug.LogError($"[GameInput] Could not load Resources/{ActionsResourcePath}.inputactions.", this);
                return;
            }

            m_gameplayMap = m_actionsAsset.FindActionMap(GameplayMapName, false);
            if (m_gameplayMap == null)
            {
                Debug.LogError($"[GameInput] Action map '{GameplayMapName}' is missing.", this);
                return;
            }

            m_moveAction = m_gameplayMap.FindAction(nameof(GameInputAction.Move), true);
            m_lookAction = m_gameplayMap.FindAction(nameof(GameInputAction.Look), true);
            foreach (GameInputAction id in Enum.GetValues(typeof(GameInputAction)))
            {
                if (id == GameInputAction.Move || id == GameInputAction.Look)
                    continue;
                m_buttonActions.Add(id, m_gameplayMap.FindAction(id.ToString(), true));
            }
        }

        private void Subscribe()
        {
            foreach (GameInputAction id in Enum.GetValues(typeof(GameInputAction)))
            {
                InputAction action = m_gameplayMap.FindAction(id.ToString(), false);
                if (action == null)
                    continue;
                action.performed += OnActionPerformed;
                action.canceled += OnActionCanceled;
            }
        }

        private void Unsubscribe()
        {
            foreach (GameInputAction id in Enum.GetValues(typeof(GameInputAction)))
            {
                InputAction action = m_gameplayMap.FindAction(id.ToString(), false);
                if (action == null)
                    continue;
                action.performed -= OnActionPerformed;
                action.canceled -= OnActionCanceled;
            }
        }

        private void OnActionPerformed(InputAction.CallbackContext context)
        {
            if (!TryGetActionId(context.action, out GameInputAction id))
                return;

            ActionPerformed?.Invoke(id);
            if (m_buttonActions.ContainsKey(id))
                ButtonPressed?.Invoke(id);
        }

        private void OnActionCanceled(InputAction.CallbackContext context)
        {
            if (!TryGetActionId(context.action, out GameInputAction id))
                return;

            ActionCanceled?.Invoke(id);
            if (m_buttonActions.ContainsKey(id))
                ButtonReleased?.Invoke(id);
        }

        private static bool TryGetActionId(InputAction action, out GameInputAction id)
        {
            return Enum.TryParse(action.name, out id);
        }
    }
}
