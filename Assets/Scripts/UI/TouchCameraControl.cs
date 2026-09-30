using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using EnhancedTouchSupport = UnityEngine.InputSystem.EnhancedTouch.EnhancedTouchSupport;
using Touch = UnityEngine.InputSystem.EnhancedTouch.Touch;
using TouchPhase = UnityEngine.InputSystem.TouchPhase;
using MobileGame.Camera;
using MobileGame.Input;

namespace MobileGame.UI
{
    /// <summary>
    /// Mobile touch camera control for rotating the third-person camera by dragging on the
    /// designated screen touch area.
    ///
    /// <para>
    /// Features:
    /// <list type="bullet">
    /// <item><b>Touch detection &amp; multi-touch ownership</b> — claims the first finger that
    /// touches down (<c>TouchPhase.Began</c>) inside the camera touch zone and tracks only that
    /// finger until it lifts, allowing simultaneous thumb movement on the <see cref="VirtualJoystick"/>
    /// and future button presses with other fingers.</item>
    /// <item><b>UI touch separation</b> — touches that begin on the movement <see cref="VirtualJoystick"/>
    /// or on any interactive UI element (such as future attack/ability <see cref="Button"/>s,
    /// <see cref="Selectable"/>s, pointer handlers, raycast-target <see cref="Graphic"/>s, or
    /// registered exclusion rects) are strictly ignored and never rotate the camera.</item>
    /// <item><b>Smooth rotation</b> — cumulative finger displacement is smoothed with
    /// frame-rate-independent exponential filtering (<see cref="TouchCameraMath.StepSmoothedDisplacement"/>)
    /// and paired with <see cref="ThirdPersonCamera"/>'s critically damped orbital smoothing.</item>
    /// <item><b>Adjustable sensitivity</b> — overall and per-axis (horizontal/vertical) sensitivity
    /// multipliers, optional axis inversion, and canvas scale normalization for consistent feel
    /// across all screen resolutions and aspect ratios.</item>
    /// <item><b>Horizontal &amp; clamped vertical rotation</b> — full 360° horizontal yaw orbit and
    /// configurable vertical pitch limits synchronized with <see cref="ThirdPersonCamera"/>.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Self-bootstraps onto the shared <c>MobileControlsCanvas</c> via
    /// <see cref="RuntimeInitializeOnLoadMethodAttribute"/> unless an instance is already placed
    /// in the scene. Scope is strictly limited to mobile camera rotation: no combat, no target
    /// lock, and no abilities.
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(RectTransform))]
    [DefaultExecutionOrder(-40)] // Runs right after VirtualJoystick (-50) and before gameplay/camera (0..100).
    public sealed class TouchCameraControl : MonoBehaviour
    {
        [Header("Touch Area (normalized screen anchors 0..1)")]
        [Tooltip("Normalized lower-left corner of the camera touch zone (default: right half of the screen, x=0.5..1.0, y=0.0..1.0).")]
        [SerializeField] private Vector2 touchZoneMin = new Vector2(0.5f, 0.0f);

        [Tooltip("Normalized upper-right corner of the camera touch zone.")]
        [SerializeField] private Vector2 touchZoneMax = new Vector2(1.0f, 1.0f);

        [Header("Sensitivity")]
        [Tooltip("Overall touch camera look sensitivity multiplier.")]
        [SerializeField, Range(0.05f, 10f)] private float sensitivity = 1.0f;

        [Tooltip("Horizontal (yaw) touch sensitivity multiplier.")]
        [SerializeField, Range(0.05f, 10f)] private float horizontalSensitivity = 1.0f;

        [Tooltip("Vertical (pitch) touch sensitivity multiplier.")]
        [SerializeField, Range(0.05f, 10f)] private float verticalSensitivity = 1.0f;

        [Tooltip("Inverts horizontal touch drag direction.")]
        [SerializeField] private bool invertHorizontal = false;

        [Tooltip("Inverts vertical touch drag direction.")]
        [SerializeField] private bool invertVertical = false;

        [Tooltip("Normalizes screen-pixel drag deltas by the canvas scale factor so the same visual swipe rotates by the same angle on every resolution and aspect ratio.")]
        [SerializeField] private bool normalizeByCanvasScale = true;

        [Header("Smoothing")]
        [Tooltip("Exponential smoothing speed for touch drag displacement (per second). 0 = raw unsmoothed delta.")]
        [SerializeField, Range(0f, 80f)] private float smoothingSpeed = 25.0f;

        [Header("Vertical Camera Limits")]
        [Tooltip("Minimum vertical pitch angle in degrees (looking up). Synchronized with ThirdPersonCamera.")]
        [SerializeField] private float minVerticalAngle = -35.0f;

        [Tooltip("Maximum vertical pitch angle in degrees (looking down). Synchronized with ThirdPersonCamera.")]
        [SerializeField] private float maxVerticalAngle = 70.0f;

        [Tooltip("Synchronizes minVerticalAngle and maxVerticalAngle with the active ThirdPersonCamera.")]
        [SerializeField] private bool syncVerticalLimitsWithCamera = true;

        [Header("UI Touch Separation")]
        [Tooltip("Prevents touches inside or owned by the movement VirtualJoystick from rotating the camera.")]
        [SerializeField] private bool ignoreTouchesOnJoystick = true;

        [Tooltip("Prevents touches that begin on UI buttons or interactive UI elements from rotating the camera.")]
        [SerializeField] private bool ignoreTouchesOnUIButtons = true;

        [Tooltip("Optional additional UI RectTransforms where touch-downs must not start camera rotation.")]
        [SerializeField] private List<RectTransform> additionalExclusionRects = new List<RectTransform>();

        [Header("Editor Testing")]
        [Tooltip("In the Editor, enables Input System TouchSimulation so mouse drags in the Game view act as touches.")]
        [SerializeField] private bool simulateTouchWithMouseInEditor = true;

        // Runtime UI references.
        private Canvas m_canvas;
        private CanvasScaler m_scaler;
        private RectTransform m_root;
        private ThirdPersonCamera m_camera;
        private bool m_uiBuilt;
        private int m_lastScreenWidth;
        private int m_lastScreenHeight;

        // Active touch tracking state.
        private int m_touchId = -1;
        private bool m_active;
        private Vector2 m_startScreenPos;
        private Vector2 m_lastScreenPos;
        private Vector2 m_cumulativeNormalizedDelta;
        private Vector2 m_smoothedCumulativeDelta;
        private Vector2 m_rawFrameDelta;
        private Vector2 m_smoothedFrameDelta;
        private Vector2 m_lookOutput;
        private bool m_warnedNoGameInput;
        private bool m_touchSimulationEnabled;

        // Reusable EventSystem raycast buffers (zero per-frame GC allocation).
        private PointerEventData m_pointerEventData;
        private EventSystem m_cachedEventSystem;
        private readonly List<RaycastResult> m_raycastResults = new List<RaycastResult>(16);

        /// <summary>Singleton instance (bootstrap-created or scene-placed).</summary>
        public static TouchCameraControl Instance { get; private set; }

        /// <summary>True while a finger is actively controlling the camera touch zone.</summary>
        public bool IsActive => m_active;

        /// <summary>Touch ID of the finger currently controlling the camera, or -1 when idle.</summary>
        public int ActiveTouchId => m_active ? m_touchId : -1;

        /// <summary>True once the touch zone RectTransform and canvas hosting are initialized.</summary>
        public bool IsUiBuilt => m_uiBuilt;

        /// <summary>Current frame's sensitivity-scaled look delta fed to <see cref="GameInput.SetMobileLook"/>.</summary>
        public Vector2 LookOutput => m_lookOutput;

        /// <summary>Current frame's raw (unsmoothed) normalized drag delta.</summary>
        public Vector2 RawFrameDelta => m_rawFrameDelta;

        /// <summary>Current frame's smoothed normalized drag delta before sensitivity scaling.</summary>
        public Vector2 SmoothedFrameDelta => m_smoothedFrameDelta;

        /// <summary>Overall touch look sensitivity multiplier.</summary>
        public float Sensitivity => sensitivity;

        /// <summary>Horizontal (yaw) touch sensitivity multiplier.</summary>
        public float HorizontalSensitivity => horizontalSensitivity;

        /// <summary>Vertical (pitch) touch sensitivity multiplier.</summary>
        public float VerticalSensitivity => verticalSensitivity;

        /// <summary>True when horizontal touch drag is inverted.</summary>
        public bool InvertHorizontal => invertHorizontal;

        /// <summary>True when vertical touch drag is inverted.</summary>
        public bool InvertVertical => invertVertical;

        /// <summary>Configured exponential smoothing speed (1/s).</summary>
        public float SmoothingSpeed => smoothingSpeed;

        /// <summary>Minimum vertical camera pitch angle in degrees.</summary>
        public float MinVerticalAngle => minVerticalAngle;

        /// <summary>Maximum vertical camera pitch angle in degrees.</summary>
        public float MaxVerticalAngle => maxVerticalAngle;

        /// <summary>Normalized lower-left anchor of the camera touch area.</summary>
        public Vector2 TouchZoneMin => touchZoneMin;

        /// <summary>Normalized upper-right anchor of the camera touch area.</summary>
        public Vector2 TouchZoneMax => touchZoneMax;

        /// <summary>The RectTransform defining the camera touch zone.</summary>
        public RectTransform TouchZoneRect => m_root;

        /// <summary>Canvas hosting the touch camera control.</summary>
        public Canvas HostCanvas => m_canvas;

        // ------------------------------------------------------------------
        // Bootstrap / lifecycle
        // ------------------------------------------------------------------

        /// <summary>
        /// Automatically creates the touch camera control on the shared <c>MobileControlsCanvas</c>
        /// after scene load unless an instance already exists.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (Instance != null)
                return;

            Canvas canvas = FindOrCreateMobileCanvas();
            DontDestroyOnLoad(canvas.gameObject);

            GameObject go = new GameObject("TouchCameraControl", typeof(RectTransform));
            go.transform.SetParent(canvas.transform, false);
            go.transform.SetAsFirstSibling(); // Sit behind joystick and future HUD buttons.
            go.AddComponent<TouchCameraControl>();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
        }

        private void OnEnable()
        {
            EnhancedTouchSupport.Enable();

#if UNITY_EDITOR
            if (simulateTouchWithMouseInEditor)
            {
                UnityEngine.InputSystem.EnhancedTouch.TouchSimulation.Enable();
                m_touchSimulationEnabled = true;
            }
#endif

            if (Application.isPlaying)
            {
                if (Cursor.lockState == CursorLockMode.Locked)
                {
                    Cursor.lockState = CursorLockMode.None;
                    Cursor.visible = true;
                }

                BuildUI();
                SyncCameraLimits();
            }
        }

        private void OnDisable()
        {
            Release();

#if UNITY_EDITOR
            if (m_touchSimulationEnabled)
            {
                UnityEngine.InputSystem.EnhancedTouch.TouchSimulation.Disable();
                m_touchSimulationEnabled = false;
            }
#endif

            EnhancedTouchSupport.Disable();
        }

        private void OnDestroy()
        {
            Release();

            if (Instance == this)
                Instance = null;
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (!hasFocus)
                Release();
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused)
                Release();
        }

        // ------------------------------------------------------------------
        // Per-frame update & touch processing
        // ------------------------------------------------------------------

        private void Update()
        {
            if (!m_uiBuilt)
            {
                if (Application.isPlaying)
                    BuildUI();

                if (!m_uiBuilt)
                    return;
            }

            // Detect Game view aspect ratio / resolution changes dynamically.
            if (Screen.width != m_lastScreenWidth || Screen.height != m_lastScreenHeight)
            {
                m_lastScreenWidth = Screen.width;
                m_lastScreenHeight = Screen.height;
                ApplyLayout();
                Canvas.ForceUpdateCanvases();
            }

            if (m_camera == null && syncVerticalLimitsWithCamera)
                SyncCameraLimits();

            PollTouches();

            float deltaTime = Time.deltaTime;
            if (m_active)
            {
                m_smoothedCumulativeDelta = TouchCameraMath.StepSmoothedDisplacement(
                    m_smoothedCumulativeDelta,
                    m_cumulativeNormalizedDelta,
                    smoothingSpeed,
                    deltaTime,
                    out m_smoothedFrameDelta);

                m_lookOutput = TouchCameraMath.ApplySensitivity(
                    m_smoothedFrameDelta,
                    sensitivity,
                    horizontalSensitivity,
                    verticalSensitivity,
                    invertHorizontal,
                    invertVertical);

                FeedGameInput();
            }
            else
            {
                m_rawFrameDelta = Vector2.zero;
                m_smoothedFrameDelta = Vector2.zero;
                m_lookOutput = Vector2.zero;
            }
        }

        private void PollTouches()
        {
            var touches = Touch.activeTouches;

            if (m_active)
            {
                bool fingerFound = false;
                for (int i = 0; i < touches.Count; i++)
                {
                    Touch touch = touches[i];
                    if (touch.touchId != m_touchId)
                        continue;

                    fingerFound = true;
                    if (touch.phase == TouchPhase.Ended || touch.phase == TouchPhase.Canceled)
                    {
                        Release();
                        return;
                    }

                    Vector2 currentPos = touch.screenPosition;
                    Vector2 rawPixelDelta = currentPos - m_lastScreenPos;
                    m_lastScreenPos = currentPos;

                    float scaleFactor = GetCanvasScaleFactor();
                    m_rawFrameDelta = TouchCameraMath.NormalizeScreenDelta(
                        rawPixelDelta,
                        scaleFactor,
                        normalizeByCanvasScale);
                    m_cumulativeNormalizedDelta += m_rawFrameDelta;
                    break;
                }

                if (!fingerFound)
                    Release();

                return;
            }

            // Idle: look for a new TouchPhase.Began inside the camera touch zone that is
            // not on the movement joystick and not on any UI button.
            for (int i = 0; i < touches.Count; i++)
            {
                Touch touch = touches[i];
                if (touch.phase != TouchPhase.Began)
                    continue;

                if (!CanClaimScreenPoint(touch.screenPosition, touch.touchId))
                    continue;

                BeginTouch(touch.touchId, touch.screenPosition);
                return;
            }
        }

        private void BeginTouch(int touchId, Vector2 screenPosition)
        {
            m_touchId = touchId;
            m_active = true;
            m_startScreenPos = screenPosition;
            m_lastScreenPos = screenPosition;
            m_cumulativeNormalizedDelta = Vector2.zero;
            m_smoothedCumulativeDelta = Vector2.zero;
            m_rawFrameDelta = Vector2.zero;
            m_smoothedFrameDelta = Vector2.zero;
            m_lookOutput = Vector2.zero;
        }

        private void Release()
        {
            if (!m_active)
                return;

            m_active = false;
            m_touchId = -1;
            m_cumulativeNormalizedDelta = Vector2.zero;
            m_smoothedCumulativeDelta = Vector2.zero;
            m_rawFrameDelta = Vector2.zero;
            m_smoothedFrameDelta = Vector2.zero;
            m_lookOutput = Vector2.zero;

            if (GameInput.Instance != null)
                GameInput.Instance.SetMobileLook(Vector2.zero);
        }

        private void FeedGameInput()
        {
            if (!m_active)
                return;

            if (GameInput.Instance == null)
            {
                if (!m_warnedNoGameInput)
                {
                    Debug.LogWarning("[TouchCameraControl] GameInput facade is unavailable; camera look input cannot reach gameplay.", this);
                    m_warnedNoGameInput = true;
                }

                return;
            }

            GameInput.Instance.SetMobileLook(m_lookOutput);
        }

        // ------------------------------------------------------------------
        // Touch area & UI separation checks
        // ------------------------------------------------------------------

        /// <summary>
        /// Returns true if a touch-down at <paramref name="screenPosition"/> (with optional
        /// <paramref name="touchId"/>) is eligible to claim the camera control:
        /// inside the camera touch zone, not on the movement joystick, and not over any UI button.
        /// </summary>
        public bool CanClaimScreenPoint(Vector2 screenPosition, int touchId = -1)
        {
            EnsureUiBuilt();

            // 1. Must lie inside the configured camera touch area.
            if (!ContainsScreenPoint(screenPosition))
                return false;

            // 2. Must NOT be claimed by or lie inside the movement VirtualJoystick.
            if (ignoreTouchesOnJoystick && IsTouchOnVirtualJoystick(screenPosition, touchId))
                return false;

            // 3. Must NOT lie over any interactive UI button / control.
            if (ignoreTouchesOnUIButtons && IsScreenPointOverInteractiveUI(screenPosition))
                return false;

            return true;
        }

        /// <summary>
        /// True when <paramref name="screenPosition"/> lies inside the camera touch zone RectTransform.
        /// </summary>
        public bool ContainsScreenPoint(Vector2 screenPosition)
        {
            EnsureUiBuilt();
            if (!m_uiBuilt || m_root == null)
                return false;

            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    m_root, screenPosition, ResolveUICamera(m_canvas), out Vector2 local))
            {
                return false;
            }

            return m_root.rect.Contains(local);
        }

        /// <summary>
        /// True when the touch is owned by <see cref="VirtualJoystick"/> or <paramref name="screenPosition"/>
        /// falls inside the joystick's interactive touch area.
        /// </summary>
        public bool IsTouchOnVirtualJoystick(Vector2 screenPosition, int touchId = -1)
        {
            VirtualJoystick joystick = VirtualJoystick.Instance;
            if (joystick == null || !joystick.isActiveAndEnabled)
                return false;

            if (touchId >= 0 && joystick.ActiveTouchId == touchId)
                return true;

            return joystick.ContainsScreenPoint(screenPosition);
        }

        /// <summary>
        /// True when <paramref name="screenPosition"/> is over any interactive UI element
        /// (buttons, selectables, pointer handlers, raycastable UI graphics, or registered
        /// exclusion rects), excluding this camera touch area and the non-raycast joystick visuals.
        /// Works both when an <see cref="EventSystem"/> is present and when no EventSystem exists.
        /// </summary>
        public bool IsScreenPointOverInteractiveUI(Vector2 screenPosition)
        {
            // 1. Registered exclusion RectTransforms.
            for (int i = 0; i < additionalExclusionRects.Count; i++)
            {
                RectTransform rect = additionalExclusionRects[i];
                if (rect == null || !rect.gameObject.activeInHierarchy)
                    continue;

                Canvas rectCanvas = rect.GetComponentInParent<Canvas>();
                if (RectTransformUtility.RectangleContainsScreenPoint(rect, screenPosition, ResolveUICamera(rectCanvas)))
                    return true;
            }

            // 2. EventSystem raycast (when an EventSystem exists in the scene).
            EventSystem eventSystem = EventSystem.current;
            if (eventSystem != null)
            {
                if (m_pointerEventData == null || m_cachedEventSystem != eventSystem)
                {
                    m_cachedEventSystem = eventSystem;
                    m_pointerEventData = new PointerEventData(eventSystem);
                }

                m_pointerEventData.position = screenPosition;
                m_raycastResults.Clear();
                eventSystem.RaycastAll(m_pointerEventData, m_raycastResults);

                for (int i = 0; i < m_raycastResults.Count; i++)
                {
                    GameObject hitGo = m_raycastResults[i].gameObject;
                    if (hitGo == null)
                        continue;

                    if (IsSelfOrJoystickTransform(hitGo.transform))
                        continue;

                    return true;
                }
            }

            // 3. Direct UI inspection (guarantees UI button separation even without an EventSystem).
            Selectable[] selectables = FindObjectsByType<Selectable>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < selectables.Length; i++)
            {
                Selectable selectable = selectables[i];
                if (selectable == null || !selectable.isActiveAndEnabled || !selectable.IsInteractable())
                    continue;

                if (IsSelfOrJoystickTransform(selectable.transform))
                    continue;

                if (selectable.transform is RectTransform rect)
                {
                    Canvas canvas = selectable.GetComponentInParent<Canvas>();
                    if (RectTransformUtility.RectangleContainsScreenPoint(rect, screenPosition, ResolveUICamera(canvas)))
                        return true;
                }
            }

            Graphic[] graphics = FindObjectsByType<Graphic>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < graphics.Length; i++)
            {
                Graphic graphic = graphics[i];
                if (graphic == null || !graphic.isActiveAndEnabled || !graphic.raycastTarget)
                    continue;

                if (IsSelfOrJoystickTransform(graphic.transform))
                    continue;

                if (IsBlockedByCanvasGroup(graphic.transform))
                    continue;

                RectTransform rect = graphic.rectTransform;
                if (rect != null && RectTransformUtility.RectangleContainsScreenPoint(rect, screenPosition, ResolveUICamera(graphic.canvas)))
                    return true;
            }

            return false;
        }

        private bool IsSelfOrJoystickTransform(Transform candidate)
        {
            if (candidate == null)
                return true;

            if (candidate == transform || candidate.IsChildOf(transform))
                return true;

            VirtualJoystick joystick = VirtualJoystick.Instance;
            if (joystick != null && (candidate == joystick.transform || candidate.IsChildOf(joystick.transform)))
                return true;

            return false;
        }

        private static bool IsBlockedByCanvasGroup(Transform target)
        {
            Transform current = target;
            while (current != null)
            {
                if (current.TryGetComponent(out CanvasGroup group))
                {
                    if (!group.blocksRaycasts)
                        return true;
                    if (group.ignoreParentGroups)
                        break;
                }

                current = current.parent;
            }

            return false;
        }

        // ------------------------------------------------------------------
        // Public configuration API
        // ------------------------------------------------------------------

        /// <summary>Sets the overall touch look sensitivity multiplier.</summary>
        public void SetSensitivity(float overallSensitivity)
        {
            sensitivity = Mathf.Max(0.01f, overallSensitivity);
        }

        /// <summary>Sets the horizontal and vertical touch sensitivity multipliers.</summary>
        public void SetSensitivity(float horizontal, float vertical)
        {
            horizontalSensitivity = Mathf.Max(0.01f, horizontal);
            verticalSensitivity = Mathf.Max(0.01f, vertical);
        }

        /// <summary>Configures horizontal and vertical touch inversion.</summary>
        public void SetInvert(bool invertX, bool invertY)
        {
            invertHorizontal = invertX;
            invertVertical = invertY;
        }

        /// <summary>Sets the exponential touch smoothing speed (0 = raw, unsmoothed).</summary>
        public void SetSmoothingSpeed(float speed)
        {
            smoothingSpeed = Mathf.Max(0f, speed);
        }

        /// <summary>
        /// Sets the vertical camera pitch limits in degrees and synchronizes them with
        /// <see cref="ThirdPersonCamera"/> when present.
        /// </summary>
        public void SetVerticalLimits(float minAngle, float maxAngle)
        {
            minVerticalAngle = minAngle;
            maxVerticalAngle = Mathf.Max(minAngle, maxAngle);
            SyncCameraLimits();
        }

        /// <summary>
        /// Configures the normalized screen rectangle (0..1) for the camera touch area.
        /// </summary>
        public void SetTouchZone(Vector2 minAnchor, Vector2 maxAnchor)
        {
            touchZoneMin = new Vector2(Mathf.Clamp01(minAnchor.x), Mathf.Clamp01(minAnchor.y));
            touchZoneMax = new Vector2(
                Mathf.Clamp(maxAnchor.x, touchZoneMin.x, 1f),
                Mathf.Clamp(maxAnchor.y, touchZoneMin.y, 1f));
            ApplyLayout();
        }

        /// <summary>Registers an additional UI RectTransform where touches must not rotate the camera.</summary>
        public void RegisterExclusionRect(RectTransform rect)
        {
            if (rect != null && !additionalExclusionRects.Contains(rect))
                additionalExclusionRects.Add(rect);
        }

        /// <summary>Unregisters a previously added UI exclusion RectTransform.</summary>
        public void UnregisterExclusionRect(RectTransform rect)
        {
            if (rect != null)
                additionalExclusionRects.Remove(rect);
        }

        // ------------------------------------------------------------------
        // Screen-space & aspect-ratio diagnostic helpers
        // ------------------------------------------------------------------

        /// <summary>Screen-space center of the camera touch zone for the current Game view resolution.</summary>
        public Vector2 GetTouchZoneCenterScreen()
        {
            Rect rect = GetTouchZoneScreenRect();
            return rect.center;
        }

        /// <summary>Screen-space rectangle (in pixels) of the camera touch zone.</summary>
        public Rect GetTouchZoneScreenRect()
        {
            EnsureUiBuilt();
            if (m_root == null)
                return new Rect(Screen.width * touchZoneMin.x, Screen.height * touchZoneMin.y,
                    Screen.width * (touchZoneMax.x - touchZoneMin.x), Screen.height * (touchZoneMax.y - touchZoneMin.y));

            Vector3[] corners = new Vector3[4];
            m_root.GetWorldCorners(corners);
            UnityEngine.Camera uiCam = ResolveUICamera(m_canvas);
            Vector2 min = RectTransformUtility.WorldToScreenPoint(uiCam, corners[0]);
            Vector2 max = RectTransformUtility.WorldToScreenPoint(uiCam, corners[2]);
            return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
        }

        /// <summary>Current canvas scale factor used to normalize screen-pixel deltas.</summary>
        public float GetCanvasScaleFactor()
        {
            if (m_canvas != null && m_canvas.scaleFactor > 0.0001f)
                return m_canvas.scaleFactor;

            Vector2 refRes = m_scaler != null ? m_scaler.referenceResolution : new Vector2(1920f, 1080f);
            float match = m_scaler != null ? m_scaler.matchWidthOrHeight : 0.5f;
            return TouchCameraMath.ComputeReferenceScaleFactor(Screen.width, Screen.height, refRes, match);
        }

        /// <summary>
        /// Evaluates the screen-pixel rectangles of the camera touch zone and the movement
        /// joystick touch area for any target Game view resolution / aspect ratio.
        /// </summary>
        public void EvaluateViewportLayout(
            int viewportWidth,
            int viewportHeight,
            out Rect cameraZonePixels,
            out Rect joystickTouchAreaPixels,
            out float scaleFactor)
        {
            Vector2 refRes = m_scaler != null ? m_scaler.referenceResolution : new Vector2(1920f, 1080f);
            float match = m_scaler != null ? m_scaler.matchWidthOrHeight : 0.5f;
            scaleFactor = TouchCameraMath.ComputeReferenceScaleFactor(viewportWidth, viewportHeight, refRes, match);

            cameraZonePixels = new Rect(
                viewportWidth * touchZoneMin.x,
                viewportHeight * touchZoneMin.y,
                viewportWidth * (touchZoneMax.x - touchZoneMin.x),
                viewportHeight * (touchZoneMax.y - touchZoneMin.y));

            VirtualJoystick joystick = VirtualJoystick.Instance;
            if (joystick != null && joystick.TouchAreaRect != null)
            {
                RectTransform jRoot = joystick.TouchAreaRect;
                Vector2 anchorCenter = (jRoot.anchorMin + jRoot.anchorMax) * 0.5f;
                Vector2 centerPx = new Vector2(
                    viewportWidth * anchorCenter.x + jRoot.anchoredPosition.x * scaleFactor,
                    viewportHeight * anchorCenter.y + jRoot.anchoredPosition.y * scaleFactor);
                Vector2 sizePx = jRoot.sizeDelta * scaleFactor;
                joystickTouchAreaPixels = new Rect(
                    centerPx.x - sizePx.x * 0.5f,
                    centerPx.y - sizePx.y * 0.5f,
                    sizePx.x,
                    sizePx.y);
            }
            else
            {
                float defaultSizePx = 620f * scaleFactor;
                Vector2 defaultCenterPx = new Vector2(300f * scaleFactor, 320f * scaleFactor);
                joystickTouchAreaPixels = new Rect(
                    defaultCenterPx.x - defaultSizePx * 0.5f,
                    defaultCenterPx.y - defaultSizePx * 0.5f,
                    defaultSizePx,
                    defaultSizePx);
            }
        }

        // ------------------------------------------------------------------
        // Internal UI setup & camera sync
        // ------------------------------------------------------------------

        private void EnsureUiBuilt()
        {
            if (!m_uiBuilt && Application.isPlaying)
                BuildUI();
        }

        private void BuildUI()
        {
            if (m_uiBuilt)
                return;

            m_root = (RectTransform)transform;
            m_canvas = GetComponentInParent<Canvas>();
            if (m_canvas == null)
            {
                m_canvas = FindOrCreateMobileCanvas();
                transform.SetParent(m_canvas.transform, false);
                transform.SetAsFirstSibling();
            }

            if (m_canvas.GetComponent<GraphicRaycaster>() == null)
                m_canvas.gameObject.AddComponent<GraphicRaycaster>();

            m_scaler = m_canvas.GetComponent<CanvasScaler>();
            m_lastScreenWidth = Screen.width;
            m_lastScreenHeight = Screen.height;

            ApplyLayout();
            m_uiBuilt = true;
        }

        private void ApplyLayout()
        {
            if (m_root == null)
                return;

            m_root.anchorMin = touchZoneMin;
            m_root.anchorMax = touchZoneMax;
            m_root.offsetMin = Vector2.zero;
            m_root.offsetMax = Vector2.zero;
            m_root.pivot = new Vector2(0.5f, 0.5f);
        }

        private void SyncCameraLimits()
        {
            if (!syncVerticalLimitsWithCamera)
                return;

            if (m_camera == null)
                m_camera = FindFirstObjectByType<ThirdPersonCamera>();

            if (m_camera != null)
                m_camera.SetPitchLimits(minVerticalAngle, maxVerticalAngle);
        }

        private static Canvas FindOrCreateMobileCanvas()
        {
            if (VirtualJoystick.Instance != null && VirtualJoystick.Instance.HostCanvas != null)
            {
                Canvas host = VirtualJoystick.Instance.HostCanvas;
                if (host.GetComponent<GraphicRaycaster>() == null)
                    host.gameObject.AddComponent<GraphicRaycaster>();
                return host;
            }

            GameObject existing = GameObject.Find("MobileControlsCanvas");
            if (existing != null)
            {
                Canvas existingCanvas = existing.GetComponent<Canvas>();
                if (existingCanvas != null)
                {
                    if (existing.GetComponent<GraphicRaycaster>() == null)
                        existing.AddComponent<GraphicRaycaster>();
                    return existingCanvas;
                }
            }

            GameObject canvasGo = new GameObject(
                "MobileControlsCanvas",
                typeof(Canvas),
                typeof(CanvasScaler),
                typeof(GraphicRaycaster));

            Canvas canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;

            CanvasScaler scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            return canvas;
        }

        private static UnityEngine.Camera ResolveUICamera(Canvas canvas)
        {
            if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                return canvas.worldCamera;

            return null;
        }

        private void OnDrawGizmosSelected()
        {
            if (!m_uiBuilt || m_root == null)
                return;

            Vector3[] corners = new Vector3[4];
            m_root.GetWorldCorners(corners);
            Gizmos.color = new Color(0.25f, 1.0f, 0.55f, 0.9f);
            for (int i = 0; i < 4; i++)
                Gizmos.DrawLine(corners[i], corners[(i + 1) % 4]);
        }
    }
}
