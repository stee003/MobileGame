using UnityEngine;

namespace MobileGame.UI
{
    /// <summary>
    /// Pure, stateless math shared by the mobile touch camera control (<see cref="TouchCameraControl"/>).
    ///
    /// <para>
    /// Kept free of scene and MonoBehaviour state so the entire touch-to-camera pipeline can be
    /// reasoned about and verified in isolation across resolutions and aspect ratios:
    /// <list type="number">
    /// <item>Compute the reference-resolution scale factor for any screen aspect ratio / size.</item>
    /// <item>Normalize raw screen-pixel drag deltas into resolution-independent canvas units.</item>
    /// <item>Smooth cumulative finger displacement exponentially and emit exact frame-to-frame
    /// deltas (telescoping sum: zero integration drift across variable frame rates).</item>
    /// <item>Apply overall and per-axis sensitivity multipliers and optional axis inversions.</item>
    /// <item>Evaluate horizontal (yaw 360° wrap) and vertical (pitch clamped to limits) orbit angles.</item>
    /// </list>
    /// </para>
    /// </summary>
    public static class TouchCameraMath
    {
        /// <summary>
        /// Computes the scale factor matching Unity's <c>CanvasScaler.ScaleMode.ScaleWithScreenSize</c>
        /// with <c>ScreenMatchMode.MatchWidthOrHeight</c>.
        /// </summary>
        public static float ComputeReferenceScaleFactor(
            float screenWidth,
            float screenHeight,
            Vector2 referenceResolution,
            float matchWidthOrHeight)
        {
            if (screenWidth <= 0f || screenHeight <= 0f ||
                referenceResolution.x <= 0f || referenceResolution.y <= 0f)
            {
                return 1f;
            }

            float logWidth = Mathf.Log(screenWidth / referenceResolution.x, 2f);
            float logHeight = Mathf.Log(screenHeight / referenceResolution.y, 2f);
            float logWeighted = Mathf.Lerp(logWidth, logHeight, Mathf.Clamp01(matchWidthOrHeight));
            return Mathf.Pow(2f, logWeighted);
        }

        /// <summary>
        /// Converts a screen-pixel drag delta into reference-resolution units so the same visual
        /// swipe produces identical camera rotation across different screen resolutions and aspect ratios.
        /// </summary>
        public static Vector2 NormalizeScreenDelta(Vector2 screenDelta, float canvasScaleFactor, bool normalizeByScale)
        {
            if (!normalizeByScale || canvasScaleFactor <= 0.0001f)
                return screenDelta;

            return screenDelta / canvasScaleFactor;
        }

        /// <summary>
        /// Steps the smoothed cumulative drag displacement toward <paramref name="targetCumulative"/>
        /// using frame-rate-independent exponential smoothing and outputs the per-frame delta
        /// <c>nextSmoothed - currentSmoothed</c>.
        ///
        /// <para>
        /// Because the per-frame deltas form a telescoping sum, the total rotation accumulated over
        /// a drag is strictly independent of frame rate fluctuations while still ramping smoothly on
        /// start and gliding smoothly to rest when the finger stops.
        /// </para>
        /// </summary>
        public static Vector2 StepSmoothedDisplacement(
            Vector2 currentSmoothed,
            Vector2 targetCumulative,
            float smoothingSpeed,
            float deltaTime,
            out Vector2 frameDelta)
        {
            Vector2 nextSmoothed = VirtualJoystickMath.ExponentialApproach(
                currentSmoothed,
                targetCumulative,
                smoothingSpeed,
                deltaTime);

            frameDelta = nextSmoothed - currentSmoothed;
            return nextSmoothed;
        }

        /// <summary>
        /// Scales a normalized drag delta by the overall sensitivity, horizontal/vertical multipliers,
        /// and optional axis inversions to produce the look delta vector fed into <c>GameInput.SetMobileLook</c>.
        /// </summary>
        public static Vector2 ApplySensitivity(
            Vector2 delta,
            float sensitivity,
            float horizontalSensitivity,
            float verticalSensitivity,
            bool invertHorizontal,
            bool invertVertical)
        {
            float overall = Mathf.Max(0.001f, sensitivity);
            float hScale = overall * Mathf.Max(0.001f, horizontalSensitivity) * (invertHorizontal ? -1f : 1f);
            float vScale = overall * Mathf.Max(0.001f, verticalSensitivity) * (invertVertical ? -1f : 1f);
            return new Vector2(delta.x * hScale, delta.y * vScale);
        }

        /// <summary>
        /// Pure-math mirror of <c>ThirdPersonCamera.ProcessLook</c>: applies horizontal yaw rotation
        /// (wrapping 0..360 degrees) and vertical pitch rotation strictly clamped between
        /// <paramref name="minVerticalAngle"/> and <paramref name="maxVerticalAngle"/>.
        /// </summary>
        public static void StepOrbitAngles(
            float currentYaw,
            float currentPitch,
            Vector2 lookDelta,
            float cameraHorizontalSensitivity,
            float cameraVerticalSensitivity,
            bool cameraInvertPitch,
            float minVerticalAngle,
            float maxVerticalAngle,
            out float nextYaw,
            out float nextPitch)
        {
            float clampedMax = Mathf.Max(minVerticalAngle, maxVerticalAngle);
            nextYaw = currentYaw % 360f;
            if (nextYaw < 0f) nextYaw += 360f;
            nextPitch = Mathf.Clamp(currentPitch, minVerticalAngle, clampedMax);

            if (lookDelta.sqrMagnitude < 0.0001f)
                return;

            nextYaw += lookDelta.x * cameraHorizontalSensitivity;
            nextYaw %= 360f;
            if (nextYaw < 0f) nextYaw += 360f;

            float pitchDelta = lookDelta.y * cameraVerticalSensitivity * (cameraInvertPitch ? 1f : -1f);
            nextPitch = Mathf.Clamp(nextPitch + pitchDelta, minVerticalAngle, clampedMax);
        }

        /// <summary>
        /// True when <paramref name="screenPoint"/> lies inside the normalized screen rectangle
        /// defined by <paramref name="zoneMin"/> and <paramref name="zoneMax"/> for the given screen dimensions.
        /// </summary>
        public static bool IsScreenPointInTouchZone(
            Vector2 screenPoint,
            float screenWidth,
            float screenHeight,
            Vector2 zoneMin,
            Vector2 zoneMax)
        {
            if (screenWidth <= 0f || screenHeight <= 0f)
                return false;

            float nx = screenPoint.x / screenWidth;
            float ny = screenPoint.y / screenHeight;
            return nx >= zoneMin.x && nx <= zoneMax.x &&
                   ny >= zoneMin.y && ny <= zoneMax.y;
        }
    }
}
