using UnityEngine;
using UnityEngine.UI;
using EnhancedTouchSupport = UnityEngine.InputSystem.EnhancedTouch.EnhancedTouchSupport;
using Touch = UnityEngine.InputSystem.EnhancedTouch.Touch;
using TouchPhase = UnityEngine.InputSystem.TouchPhase;
using MobileGame.Input;

namespace MobileGame.UI
{
    /// <summary>
    /// On-screen virtual movement joystick for touch devices.
    ///
    /// <para>
    /// Designed for a 3D action RPG: a single finger anywhere inside the (larger, invisible)
    /// touch area grabs the stick; the knob follows the finger, the value is dead-zone filtered,
    /// smoothed, and fed into <see cref="MobileGame.Input.GameInput.SetMobileMove"/> so it joins
    /// the same <c>Move</c> input action the keyboard binds to. The third-person player controller
    /// consumes it unchanged through <see cref="MobileGame.Input.GameInput.Move"/>.
    /// </para>
    ///
    /// <para>
    /// Features:
    /// <list type="bullet">
    /// <item>Touch input via the Input System's Enhanced Touch (multi-touch safe: the stick is
    /// claimed by the finger that touched down inside its area and is released only by that
    /// finger, leaving other fingers free for future attack/ability buttons).</item>
    /// <item>Configurable dead zone with a smooth linear remap (no snap at the dead-zone edge).</item>
    /// <item>Smooth, frame-rate-independent input; instant release so the character stops crisply.</item>
    /// <item>Adjustable size and position, plus an enlarged touch area that is comfortable on
    /// phones (the visible base is smaller than the interactive region).</item>
    /// <item>Optional dynamic mode: the stick appears wherever a finger lands inside a configured
    /// screen zone instead of sitting at a fixed spot.</item>
    /// <item>Self-building visuals (procedural ring/knob sprites, no imported assets needed).</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// A <see cref="RuntimeInitializeOnLoadMethod"/> bootstrap creates the joystick canvas
    /// automatically (mirroring <see cref="MobileGame.Input.GameInput"/>) unless a joystick was
    /// already placed in the scene, in which case the scene instance wins. The visual stick is
    /// built lazily on the first play-mode frame.
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(RectTransform))]
    [DefaultExecutionOrder(-50)] // Poll touches before gameplay scripts read input this frame.
    public sealed class VirtualJoystick : MonoBehaviour
    {
        public enum TouchAreaMode
        {
            /// <summary>Stick is always visible at its configured spot; only touches that begin inside its (padded) area activate it.</summary>
            Fixed,
            /// <summary>Stick appears wherever a touch begins inside the configured screen zone; ideal for reach-anywhere thumbs.</summary>
            Dynamic
        }

        [Header("Placement (canvas reference-resolution units)")]
        [Tooltip("Anchor of the joystick area on screen. Defaults to the bottom-left corner.")]
        [SerializeField] private Vector2 anchorPoint = new Vector2(0f, 0f);

        [Tooltip("Position of the area center relative to the anchor, in reference-resolution units.")]
        [SerializeField] private Vector2 screenPosition = new Vector2(300f, 320f);

        [Header("Size (canvas reference-resolution units)")]
        [Tooltip("Diameter of the visible base ring. Also defines the travel radius (half of it).")]
        [SerializeField] private float baseSize = 340f;

        [Tooltip("Diameter of the knob. Zero derives it from the base size.")]
        [SerializeField] private float knobSize = 0f;

        [Header("Touch Area")]
        [Tooltip("Fixed: stick sits at its configured spot. Dynamic: stick appears where the finger lands inside the zone.")]
        [SerializeField] private TouchAreaMode touchAreaMode = TouchAreaMode.Fixed;

        [Tooltip("Fixed mode only: extra invisible padding around the base that still counts as the touch area, so the stick is easy to grab.")]
        [SerializeField] private float touchPadding = 140f;

        [Tooltip("Dynamic mode only: screen-fraction rectangle (anchorMinX, anchorMinY, anchorMaxX, anchorMaxY) in which any touch-down spawns the stick.")]
        [SerializeField] private Vector4 dynamicZoneAnchors = new Vector4(0f, 0f, 0.55f, 0.8f);

        [Header("Feel")]
        [Tooltip("Fraction of the travel radius ignored before input starts (0 = none). Removes jitter when the thumb rests near the center.")]
        [SerializeField, Range(0f, 0.9f)] private float deadZone = 0.15f;

        [Tooltip("How quickly the output value follows the finger (per second, exponential). 0 = raw, no smoothing.")]
        [SerializeField, Range(0f, 60f)] private float smoothingSpeed = 20f;

        [Tooltip("How quickly the knob visually returns to the center after release (per second).")]
        [SerializeField, Range(0f, 60f)] private float knobReturnSpeed = 18f;

        [Header("Appearance")]
        [Tooltip("Color of the base ring.")]
        [SerializeField] private Color baseColor = new Color(1f, 1f, 1f, 0.30f);

        [Tooltip("Color of the knob.")]
        [SerializeField] private Color knobColor = new Color(1f, 1f, 1f, 0.85f);

        [Header("Editor Testing")]
        [Tooltip("In the Editor, drive the joystick with the mouse through the Input System's touch simulation (device builds always use real touch).")]
        [SerializeField] private bool simulateTouchWithMouseInEditor = true;

        // Runtime UI (built lazily in Play Mode).
        private Canvas m_canvas;
        private RectTransform m_root;   // This object's rect; sized to the interactive touch area.
        private RectTransform m_base;   // Visible ring.
        private RectTransform m_knob;   // Visible knob, child of the base.
        private bool m_uiBuilt;

        // Input state.
        private int m_touchId = -1;     // Finger currently driving the stick (-1 = none).
        private bool m_active;
        private Vector2 m_rawLocal;     // Knob offset from the base center, canvas units.
        private Vector2 m_normalized;   // m_rawLocal clamped to the unit circle (pre-dead-zone).
        private Vector2 m_output;       // Smoothed, dead-zone-filtered value fed to GameInput.
        private Vector2 m_knobVisual;   // Displayed knob offset (springs back after release).
        private bool m_warnedNoGameInput;
        private bool m_touchSimulationEnabled;

        private static Sprite s_ringSprite;
        private static Sprite s_circleSprite;

        /// <summary>Singleton instance (bootstrap-created or scene-placed).</summary>
        public static VirtualJoystick Instance { get; private set; }

        /// <summary>Current smoothed output fed into the shared Move input. Zero when idle.</summary>
        public Vector2 Output => m_output;

        /// <summary>Raw finger deflection clamped to the unit circle, before the dead zone.</summary>
        public Vector2 RawInput => m_normalized;

        /// <summary>Offset of the displayed knob from the base center, in canvas units.</summary>
        public Vector2 KnobVisualOffset => m_knobVisual;

        /// <summary>True while a finger is driving the stick.</summary>
        public bool IsActive => m_active;

        /// <summary>True once the runtime visuals exist.</summary>
        public bool IsUiBuilt => m_uiBuilt;

        /// <summary>Configured dead zone as a fraction of the travel radius.</summary>
        public float DeadZone => deadZone;

        /// <summary>Travel radius (base half-width) in canvas units.</summary>
        public float BaseRadiusUnits => BaseRadius;

        /// <summary>The joystick's root rect, sized to the interactive touch area.</summary>
        public RectTransform TouchAreaRect => m_root;

        /// <summary>The visible base ring rect.</summary>
        public RectTransform BaseRect => m_base;

        /// <summary>Canvas hosting the joystick UI.</summary>
        public Canvas HostCanvas => m_canvas;

        private float BaseRadius => Mathf.Max(1f, baseSize * 0.5f);
        private float EffectiveKnobSize => knobSize > 0f ? knobSize : baseSize * 0.42f;
        private float TouchAreaSize => baseSize + 2f * Mathf.Max(0f, touchPadding);

        // ------------------------------------------------------------------
        // Bootstrap / lifecycle
        // ------------------------------------------------------------------

        /// <summary>
        /// Creates the joystick HUD automatically for every scene unless one was already placed.
        /// Runs after the first scene's objects have awoken so scene-placed instances win.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (Instance != null)
                return; // A scene-placed joystick takes precedence over the auto-created one.

            Canvas canvas = CreateAndConfigureCanvas();
            DontDestroyOnLoad(canvas.gameObject);

            GameObject joystickGo = new GameObject("VirtualJoystick", typeof(RectTransform));
            joystickGo.transform.SetParent(canvas.transform, false);
            joystickGo.AddComponent<VirtualJoystick>();
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
                // Lets the mouse drive the stick when testing in the Editor Game view.
                // Enable() is idempotent; we only disable what we enabled.
                UnityEngine.InputSystem.TouchSimulation.Enable();
                m_touchSimulationEnabled = true;
            }
#endif

            if (Application.isPlaying)
                BuildUI();
        }

        private void OnDisable()
        {
            Release(); // Never leave stuck movement input behind.

#if UNITY_EDITOR
            if (m_touchSimulationEnabled)
            {
                UnityEngine.InputSystem.TouchSimulation.Disable();
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
        // Per-frame input processing
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

            PollTouches();

            float deltaTime = Time.deltaTime;
            if (m_active)
            {
                m_normalized = VirtualJoystickMath.NormalizeToUnitCircle(m_rawLocal, BaseRadius);
                Vector2 target = VirtualJoystickMath.ApplyDeadZone(m_normalized, deadZone);
                m_output = VirtualJoystickMath.ExponentialApproach(m_output, target, smoothingSpeed, deltaTime);

                // The knob tracks the finger directly (clamped to the ring) for 1:1 feel.
                m_knobVisual = Vector2.ClampMagnitude(m_rawLocal, BaseRadius);
            }
            else
            {
                m_normalized = Vector2.zero;
                m_output = Vector2.zero;

                // Cosmetic spring-back only; the gameplay value is already zeroed on release.
                m_knobVisual = VirtualJoystickMath.ExponentialApproach(m_knobVisual, Vector2.zero, knobReturnSpeed, deltaTime);
            }

            FeedGameInput();

            if (m_knob != null)
                m_knob.anchoredPosition = m_knobVisual;
        }

        /// <summary>
        /// Claims the first finger that touches down inside the touch area and tracks only that
        /// finger until it lifts. Other fingers are ignored (they stay free for future buttons).
        /// </summary>
        private void PollTouches()
        {
            var touches = Touch.activeTouches;

            if (m_active)
            {
                bool fingerFound = false;
                for (int i = 0; i < touches.Count; i++)
                {
                    if (touches[i].touchId != m_touchId)
                        continue;

                    fingerFound = true;
                    if (touches[i].phase == TouchPhase.Ended || touches[i].phase == TouchPhase.Canceled)
                    {
                        Release();
                        return;
                    }

                    if (TryScreenToLocal(touches[i].screenPosition, out Vector2 local))
                        m_rawLocal = local - BaseCenterLocal();

                    break;
                }

                // Safety net: the finger vanished without an Ended event (device hiccup).
                if (!fingerFound)
                    Release();

                return;
            }

            for (int i = 0; i < touches.Count; i++)
            {
                if (touches[i].phase != TouchPhase.Began)
                    continue;

                if (!TryScreenToLocal(touches[i].screenPosition, out Vector2 local))
                    continue;

                if (!m_root.rect.Contains(local))
                    continue;

                BeginTouch(touches[i].touchId, local);
                return; // Claim at most one finger per frame.
            }
        }

        private void BeginTouch(int touchId, Vector2 localPoint)
        {
            m_touchId = touchId;
            m_active = true;

            if (touchAreaMode == TouchAreaMode.Dynamic && m_base != null)
            {
                // Spawn the stick under the finger.
                m_base.anchoredPosition = localPoint;
                m_base.gameObject.SetActive(true);
            }

            m_rawLocal = localPoint - BaseCenterLocal();
            m_normalized = Vector2.zero;
            m_output = Vector2.zero;
            m_knobVisual = Vector2.zero;
        }

        private void Release()
        {
            if (!m_active)
                return;

            m_active = false;
            m_touchId = -1;
            m_rawLocal = Vector2.zero;
            m_normalized = Vector2.zero;
            m_output = Vector2.zero;

            if (GameInput.Instance != null)
                GameInput.Instance.SetMobileMove(Vector2.zero);
        }

        private Vector2 BaseCenterLocal()
        {
            // The base is anchored to the center of the root; its anchored position is its
            // offset from there (zero in fixed mode, the spawn point in dynamic mode).
            return m_base != null ? m_base.anchoredPosition : Vector2.zero;
        }

        private bool TryScreenToLocal(Vector2 screenPoint, out Vector2 localPoint)
        {
            return RectTransformUtility.ScreenPointToLocalPointInRectangle(m_root, screenPoint, ResolveUICamera(), out localPoint);
        }

        private Camera ResolveUICamera()
        {
            // Screen-space overlay canvases must pass a null camera; camera-space canvases use theirs.
            if (m_canvas != null && m_canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                return m_canvas.worldCamera;

            return null;
        }

        private void FeedGameInput()
        {
            if (!m_active)
                return; // Idle: never stomp on other SetMobileMove users (e.g. diagnostics).

            if (GameInput.Instance == null)
            {
                if (!m_warnedNoGameInput)
                {
                    Debug.LogWarning("[VirtualJoystick] GameInput facade is unavailable; movement input cannot reach gameplay.", this);
                    m_warnedNoGameInput = true;
                }

                return;
            }

            GameInput.Instance.SetMobileMove(m_output);
        }

        // ------------------------------------------------------------------
        // Public configuration API
        // ------------------------------------------------------------------

        /// <summary>Repositions the joystick area (fixed mode). Units are canvas reference-resolution units.</summary>
        public void SetScreenPosition(Vector2 anchor, Vector2 position)
        {
            anchorPoint = anchor;
            screenPosition = position;
            ApplyLayout();
        }

        /// <summary>Resizes the stick. A knob size of zero re-derives it from the base size.</summary>
        public void SetSize(float newBaseSize, float newKnobSize)
        {
            baseSize = Mathf.Max(80f, newBaseSize);
            knobSize = Mathf.Max(0f, newKnobSize);
            ApplyLayout();
        }

        // ------------------------------------------------------------------
        // Test/diagnostic helpers (screen-space queries)
        // ------------------------------------------------------------------

        /// <summary>Screen-space position of the base center (used by the Play Mode test suite).</summary>
        public Vector2 GetBaseCenterScreen()
        {
            EnsureUiBuilt();
            return (Vector2)RectTransformUtility.WorldToScreenPoint(ResolveUICamera(), m_base.position);
        }

        /// <summary>Screen-space travel radius of the base ring (used by the Play Mode test suite).</summary>
        public float GetBaseScreenRadius()
        {
            EnsureUiBuilt();
            Vector2 center = RectTransformUtility.WorldToScreenPoint(ResolveUICamera(), m_base.position);
            Vector3 edgeWorld = m_base.TransformPoint(new Vector3(BaseRadius, 0f, 0f));
            Vector2 edge = RectTransformUtility.WorldToScreenPoint(ResolveUICamera(), edgeWorld);
            return Vector2.Distance(center, edge);
        }

        private void EnsureUiBuilt()
        {
            if (!m_uiBuilt && Application.isPlaying)
                BuildUI();
        }

        // ------------------------------------------------------------------
        // UI construction
        // ------------------------------------------------------------------

        private void BuildUI()
        {
            if (m_uiBuilt)
                return;

            m_root = (RectTransform)transform;

            m_canvas = GetComponentInParent<Canvas>();
            if (m_canvas == null)
            {
                // Placed manually without a canvas: host one.
                m_canvas = CreateAndConfigureCanvas();
                transform.SetParent(m_canvas.transform, false);
            }

            CreateBase();
            CreateKnob();
            ApplyLayout();

            m_uiBuilt = true;
        }

        private static Canvas CreateAndConfigureCanvas()
        {
            GameObject canvasGo = new GameObject("MobileControlsCanvas", typeof(Canvas), typeof(CanvasScaler));
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

        private void CreateBase()
        {
            m_base = CreateChildRect("Base", transform);
            Image image = m_base.gameObject.AddComponent<Image>();
            image.sprite = GetOrCreateRingSprite();
            image.color = baseColor;
            image.raycastTarget = false; // Touch handling is manual; no EventSystem required.
        }

        private void CreateKnob()
        {
            m_knob = CreateChildRect("Knob", m_base);
            Image image = m_knob.gameObject.AddComponent<Image>();
            image.sprite = GetOrCreateCircleSprite();
            image.color = knobColor;
            image.raycastTarget = false;
        }

        private static RectTransform CreateChildRect(string name, Transform parent)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            RectTransform rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            return rect;
        }

        private void ApplyLayout()
        {
            if (m_root == null)
                return;

            if (touchAreaMode == TouchAreaMode.Fixed)
            {
                m_root.anchorMin = anchorPoint;
                m_root.anchorMax = anchorPoint;
                m_root.pivot = new Vector2(0.5f, 0.5f);
                m_root.anchoredPosition = screenPosition;
                m_root.sizeDelta = new Vector2(TouchAreaSize, TouchAreaSize);
            }
            else
            {
                m_root.anchorMin = new Vector2(dynamicZoneAnchors.x, dynamicZoneAnchors.y);
                m_root.anchorMax = new Vector2(dynamicZoneAnchors.z, dynamicZoneAnchors.w);
                m_root.offsetMin = Vector2.zero;
                m_root.offsetMax = Vector2.zero;
                m_root.pivot = new Vector2(0.5f, 0.5f);
            }

            if (m_base != null)
            {
                m_base.sizeDelta = new Vector2(baseSize, baseSize);
                if (touchAreaMode == TouchAreaMode.Fixed)
                    m_base.anchoredPosition = Vector2.zero;

                m_base.gameObject.SetActive(touchAreaMode == TouchAreaMode.Fixed || m_active);
            }

            if (m_knob != null)
            {
                float knob = EffectiveKnobSize;
                m_knob.sizeDelta = new Vector2(knob, knob);
                m_knob.anchoredPosition = m_knobVisual;
            }
        }

        private static Sprite GetOrCreateRingSprite()
        {
            if (s_ringSprite == null)
                s_ringSprite = CreateSprite(CreateCircleTexture(128, ring: true));

            return s_ringSprite;
        }

        private static Sprite GetOrCreateCircleSprite()
        {
            if (s_circleSprite == null)
                s_circleSprite = CreateSprite(CreateCircleTexture(128, ring: false));

            return s_circleSprite;
        }

        private static Sprite CreateSprite(Texture2D texture)
        {
            return Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height),
                new Vector2(0.5f, 0.5f), 100f, 0u, SpriteMeshType.FullRect);
        }

        /// <summary>Builds an anti-aliased ring (annulus) or filled disc with a soft edge as a white alpha mask.</summary>
        private static Texture2D CreateCircleTexture(int size, bool ring)
        {
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.DontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };

            Color32[] pixels = new Color32[size * size];
            float center = (size - 1) * 0.5f;
            float halfSize = size * 0.5f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x - center) / halfSize;
                    float dy = (y - center) / halfSize;
                    float distance = Mathf.Sqrt(dx * dx + dy * dy);

                    float alpha;
                    if (ring)
                    {
                        float inner = SmoothStep(0.56f, 0.64f, distance);
                        float outer = 1f - SmoothStep(0.86f, 0.96f, distance);
                        alpha = inner * outer;
                    }
                    else
                    {
                        alpha = 1f - SmoothStep(0.80f, 0.97f, distance);
                    }

                    byte a = (byte)Mathf.RoundToInt(255f * Mathf.Clamp01(alpha));
                    pixels[y * size + x] = new Color32(255, 255, 255, a);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return texture;
        }

        private static float SmoothStep(float edge0, float edge1, float value)
        {
            float t = Mathf.Clamp01((value - edge0) / Mathf.Max(0.0001f, edge1 - edge0));
            return t * t * (3f - 2f * t);
        }

        // ------------------------------------------------------------------
        // Editor support
        // ------------------------------------------------------------------

        private void OnDrawGizmosSelected()
        {
            if (!m_uiBuilt || m_root == null)
                return;

            // Interactive touch area.
            Vector3[] corners = new Vector3[4];
            m_root.GetWorldCorners(corners);
            Gizmos.color = new Color(0.35f, 0.85f, 1f, 0.9f);
            for (int i = 0; i < 4; i++)
                Gizmos.DrawLine(corners[i], corners[(i + 1) % 4]);

            // Visible base.
            if (m_base != null)
            {
                m_base.GetWorldCorners(corners);
                Gizmos.color = new Color(1f, 0.9f, 0.3f, 0.9f);
                for (int i = 0; i < 4; i++)
                    Gizmos.DrawLine(corners[i], corners[(i + 1) % 4]);
            }
        }
    }
}
