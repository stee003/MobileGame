using UnityEngine;

namespace MobileGame.Core
{
    /// <summary>
    /// Guards for every vector this project hands to an engine API that expects a direction.
    ///
    /// <para>
    /// Unity validates those vectors inside its native code and prints
    /// <c>"Assertion failed on expression: 'IsNormalized(dir, 0.001f)'"</c> when the vector it is
    /// about to normalize is degenerate (zero length, non-finite) or is not unit length within
    /// 0.001. Known call sites that assert this way are the physics casts / linecasts
    /// (<c>Physics.Raycast</c>, <c>Physics.SphereCast</c>, <c>Physics.Linecast</c>, …) and
    /// <c>CharacterController.Move</c>, which normalizes its motion internally — a zero-length
    /// motion makes it assert exactly like an unnormalized query direction.
    /// </para>
    ///
    /// <para>
    /// The assertion itself is stripped from release builds, but it is never harmless: it means a
    /// degenerate vector reached the engine, i.e. the gameplay code above it produced garbage
    /// (a zero velocity, a NaN, an inverted axis). Every engine-facing vector is therefore checked
    /// here instead of being passed straight through.
    /// </para>
    ///
    /// <para>
    /// The checks are branch-only (no allocations, squared comparisons where a square root is not
    /// needed) so they are cheap enough to run every frame on mobile.
    /// </para>
    /// </summary>
    public static class PhysicsQueryGuard
    {
        /// <summary>Tolerance Unity uses when it asserts that a direction is normalized.</summary>
        public const float DirectionTolerance = 0.001f;

        /// <summary>True when every component of the vector is finite (neither NaN nor infinite).</summary>
        public static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        /// <summary>True when every component of the vector is finite (neither NaN nor infinite).</summary>
        public static bool IsFinite(Vector2 value)
        {
            return IsFinite(value.x) && IsFinite(value.y);
        }

        /// <summary>True when the scalar is finite (neither NaN nor infinite).</summary>
        public static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        /// <summary>
        /// True when the vector is a legal direction for a physics query: finite and unit length
        /// within <paramref name="tolerance"/>. Query directions that fail this are exactly what
        /// produces the engine's <c>IsNormalized</c> assertion.
        /// </summary>
        public static bool IsUsableDirection(Vector3 direction, float tolerance = DirectionTolerance)
        {
            if (!IsFinite(direction))
                return false;

            return Mathf.Abs(direction.magnitude - 1f) <= Mathf.Max(0f, tolerance);
        }

        /// <summary>
        /// True when a motion vector is worth handing to <c>CharacterController.Move</c>: finite and
        /// longer than both <paramref name="minDistance"/> (the controller's <c>minMoveDistance</c>,
        /// below which the controller ignores the motion anyway) and one zero-length step.
        /// <para>
        /// Move normalizes its motion internally, so a zero-length motion makes it assert
        /// <c>IsNormalized(dir, 0.001f)</c>; callers should skip the call instead.
        /// </para>
        /// </summary>
        public static bool IsUsableMotion(Vector3 motion, float minDistance = 0f)
        {
            if (!IsFinite(motion))
                return false;

            float threshold = Mathf.Max(Mathf.Abs(minDistance), DirectionTolerance);
            return motion.sqrMagnitude > threshold * threshold;
        }
    }
}
