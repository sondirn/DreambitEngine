using Microsoft.Xna.Framework;

namespace Dreambit;

public static class Vector2Extensions
{
    public static Vector3 ToVector3(this Vector2 vector)
    {
        return new Vector3(vector.X, vector.Y, 0);
    }

    public static float Angle(this Vector2 vector)
    {
        return Mathf.Atan2(vector.Y, vector.X);
    }

    public static bool IsFinite(this Vector2 vector)
    {
        return float.IsFinite(vector.X) &&  float.IsFinite(vector.Y);
    }
}