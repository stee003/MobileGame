using UnityEngine;

namespace MobileGame.UI
{
    /// <summary>
    /// Pure math shared by the virtual movement joystick.
    ///
    /// <para>
    /// Kept stateless and free of gameplay dependencies so it can be reasoned about (and
    /// verified) in isolation. Covers the three stages of joystick input processing:
    /// <list type="number">
    /// <item>Convert the raw knob offset from the base center into a clamped unit-circle value.</item>
    /// <item>Remap that value through the configurable dead zone (zero below, linear 0..1 above).</item>
    /// <item>Smooth toward the target in a frame-rate-independent way.</item>
    /// </list>
    /// </para>
    /// </summary>
    public static class VirtualJoystickMath
    {
        /// <summary>
        /// Divides the raw offset by the radius and clamps the result to the unit circle so the
        /// corners of the travel area cannot exceed full deflection (diagonals are never faster
        /// than straight movement).
        /// </summary>
        public static Vector2 NormalizeToUnitCircle(Vector2 rawOffset, float radius)
        {
            if (radius <= 0.0001f)
                return Vector2.zero;

            return Vector2.ClampMagnitude(rawOffset / radius, 1f);
        }

        /// <summary>
        /// Remaps the normalized value through the dead zone.
        ///
        /// <para>
        /// Values with a magnitude at or below <paramref name="deadZone"/> return exactly zero.
        /// Above it, the magnitude is scaled linearly so that the output starts at zero right at
        /// the dead-zone edge (no snap) and reaches 1.0 at full deflection. Direction is preserved.
        /// </para>
        /// </summary>
        public static Vector2 ApplyDeadZone(Vector2 normalized, float deadZone)
        {
            deadZone = Mathf.Clamp(deadZone, 0f, 0.99f);

            float magnitude = normalized.magnitude;
            if (magnitude <= deadZone)
                return Vector2.zero;

            float remappedMagnitude = (magnitude - deadZone) / (1f - deadZone);
            return normalized * (remappedMagnitude / magnitude);
        }

        /// <summary>
        /// Frame-rate-independent exponential smoothing toward a target. A speed of zero or less
        /// snaps straight to the target (raw input, no smoothing).
        /// </summary>
        public static Vector2 ExponentialApproach(Vector2 current, Vector2 target, float speed, float deltaTime)
        {
            if (speed <= 0f)
                return target;

            float blend = 1f - Mathf.Exp(-speed * Mathf.Max(0f, deltaTime));
            return Vector2.LerpUnclamped(current, target, blend);
        }
    }
}
