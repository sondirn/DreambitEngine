using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace Dreambit.ECS;

[BlueprintType(nameof(RigidBody2D))]
public class RigidBody2D : Component
{
    #region Constants

    /*
     * Multiple contacts can exist at once:
     *
     *     | wall
     *     |
     *   O |____ floor
     *
     * Resolve one contact, refresh the collider, then query again.
     *
     * Six iterations is intentionally small and deterministic. Normal
     * character/world contacts usually settle in one or two.
     */
    private const int MaxResolutionIterations =
        6;

    /*
     * Keep the resolved collider microscopically outside the surface.
     *
     * Without a separation skin, floating-point error can leave two shapes
     * barely intersecting and cause repeated zero-distance corrections.
     */
    private const float SkinWidth =
        0.0001f;

    private const float NormalEpsilonSquared =
        0.000000000001f;

    #endregion

    #region Private Members / Fields

    private bool _warnedUser;

    #endregion

    #region Life Cycle Overrides

    public override void OnPhysicsUpdate()
    {
        if (Collider is null)
        {
            if (_warnedUser)
                return;

            Logger.Warn(
                "Collider is null!");

            _warnedUser =
                true;

            return;
        }

        /*
         * Preserve the beginning-of-step position for interpolation and
         * external systems that inspect Transform.LastWorldPosition.
         *
         * Collision resolution no longer rolls back to this position.
         */
        Transform
            .CaptureLastWorldPosition();

        var translation =
            Velocity *
            Time.PhysicsDeltaTime;

        if (!float.IsFinite(
                translation.X) ||
            !float.IsFinite(
                translation.Y))
        {
            Logger.Warn(
                "RigidBody2D velocity produced a non-finite physics translation.");

            return;
        }

        MoveAndResolve(
            translation);
    }

    #endregion

    #region Movement / Resolution

    private void MoveAndResolve(
        Vector2 translation)
    {
        /*
         * Apply the entire intended movement first.
         *
         * This is important.
         *
         * Consider moving diagonally into a slope:
         *
         *          /
         *       O /
         *        /
         *
         * The attempted movement contains:
         *
         *     1. a component INTO the slope
         *     2. a component ALONG the slope
         *
         * After the move, depenetrating only along the collision normal
         * removes component #1 while preserving component #2.
         *
         * The result is natural sliding without separately testing world X/Y.
         */
        if (translation !=
            Vector2.Zero)
        {
            Transform
                .TranslateWorld2D(
                    translation);

            /*
             * The transform changed, so immediately update cached world
             * geometry and the spatial hash before asking for contacts.
             */
            Collider
                .RefreshSpatialHash();
        }
        else
        {
            /*
             * Still refresh before resolution. This allows the rigidbody to
             * recover from an externally teleported/edited overlap even if
             * its current velocity is zero.
             */
            Collider
                .RefreshSpatialHash();
        }

        ResolvePenetrations();
    }

    private void ResolvePenetrations()
    {
        for (var iteration = 0;
             iteration <
             MaxResolutionIterations;
             iteration++)
        {
            if (!PhysicsSystem.Instance
                    .TryGetBestSolidContact(
                        Collider,
                        InterestedTags,
                        out var normal,
                        out var penetration))
            {
                return;
            }

            if (!float.IsFinite(
                    penetration) ||
                penetration <= 0f)
            {
                return;
            }

            var normalLengthSquared =
                normal.LengthSquared();

            if (!float.IsFinite(
                    normalLengthSquared) ||
                normalLengthSquared <=
                NormalEpsilonSquared)
            {
                return;
            }

            /*
             * Manifold normals should already be normalized.
             *
             * Normalize again only if numerical error has meaningfully moved
             * it away from unit length. This avoids unnecessary sqrt calls in
             * the normal case.
             */
            if (normalLengthSquared <
                    0.9999f ||
                normalLengthSquared >
                    1.0001f)
            {
                normal /=
                    Mathf.Sqrt(
                        normalLengthSquared);
            }

            var correction =
                normal *
                (
                    penetration +
                    SkinWidth
                );

            Transform
                .TranslateWorld2D(
                    correction);

            /*
             * The next iteration must query the corrected geometry, not the
             * geometry from before positional resolution.
             */
            Collider
                .RefreshSpatialHash();
        }
    }

    #endregion

    #region Public Properties / Fields

    [DreambitSerialize]
    public Collider Collider
    {
        get;
        private set;
    }

    [DreambitSerialize]
    public Vector2 Velocity =
        Vector2.Zero;

    [DreambitSerialize]
    public HashSet<string> InterestedTags
    {
        get;
        private set;
    } = [];

    #endregion

    #region Public Functions

    public void SetInterestedTags(
        params string[] tags)
    {
        foreach (var tag in tags)
            InterestedTags.Add(tag);
    }

    public void SetCollider(
        Collider collider)
    {
        Collider =
            collider;

        _warnedUser =
            false;
    }

    #endregion
}