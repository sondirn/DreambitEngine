using System;
using System.Collections.Generic;
using Dreambit.ECS;
using FontStashSharp;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Dreambit;

public static class SpriteBatchExtensions
{
    public static Texture2D PixelTexture;

    public static void EnsurePixelTextureExists(GraphicsDevice graphicsDevice)
    {
        if (PixelTexture == null || PixelTexture.IsDisposed || PixelTexture.GraphicsDevice != graphicsDevice)
        {
            PixelTexture?.Dispose();
            PixelTexture = new Texture2D(graphicsDevice, 1, 1);
            PixelTexture.SetData([Color.White]);
        }
    }

    public static void DrawWorldSprite(
        this SpriteBatch spriteBatch,
        Texture2D texture,
        Vector2 worldPosition,
        Rectangle? sourceRectangle,
        Color color,
        float rotation,
        Vector2 originInPixels,
        Vector2 worldScale,
        SpriteEffects effects = SpriteEffects.None,
        float layerDepth = 0f)
    {
        ArgumentNullException.ThrowIfNull(spriteBatch);
        ArgumentNullException.ThrowIfNull(texture);

        spriteBatch.Draw(
            texture,
            worldPosition,
            sourceRectangle,
            color,
            rotation,
            originInPixels,
            worldScale,
            effects,
            layerDepth);
    }

    public static float GetLineHeight(SpriteFontBase font, float lineSpacingMultiplier = 1f)
    {
        float h = font.LineHeight;
        if (h <= 0f)
            // "Ay" or "Mg" tends to give a decent vertical extent if you need a fallback
            h = font.MeasureString("Ay").Y;
        return h * lineSpacingMultiplier;
    }

    public static List<string> SplitTextIntoLines(SpriteFontBase spriteFont, string text, float maxWidth)
    {
        var lines = new List<string>();
        if (text.Length == 0)
            return lines;

        // Hard breaks must occupy their own rows in both measurement and drawing.
        // Leaving newlines inside a wrapped row underestimates the block height
        // and clips later entries in bottom-aligned text such as console output.
        foreach (var paragraph in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var currentLine = string.Empty;

            foreach (var word in paragraph.Split(' '))
            {
                var testLine = currentLine + (currentLine.Length > 0 ? " " : "") + word;
                var size = spriteFont.MeasureString(testLine);

                if (size.X > maxWidth)
                {
                    if (currentLine.Length > 0)
                        lines.Add(currentLine);

                    currentLine = word;
                }
                else
                {
                    currentLine = testLine;
                }
            }

            // Preserve blank rows, including a trailing newline.
            lines.Add(currentLine);
        }

        return lines;
    }

    //draw multi lined text
    public static void DrawMultiLineText(this SpriteBatch spriteBatch,
        SpriteFontBase font,
        string text,
        Vector2 position,
        Color color,
        HorizontalAlignment horizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment verticalAlignment = VerticalAlignment.Center,
        float maxWidth = float.MaxValue,
        float lineSpacingMultiplier = 1f)
    {
        var lines = SplitTextIntoLines(font, text, maxWidth);

        //calculate the total height
        var lineHeight = GetLineHeight(font, lineSpacingMultiplier);
        var totalHeight = lines.Count * lineHeight;

        switch (verticalAlignment)
        {
            case VerticalAlignment.Center: position.Y -= totalHeight * 0.5f; break;
            case VerticalAlignment.Bottom: position.Y -= totalHeight; break;
            // Top = no change
        }


        for (var i = 0; i < lines.Count; i++)
        {
            //adjust the horizontal position based on alignment

            var alignmentOffset = GetAlignmentOffset(font, lines[i], horizontalAlignment);
            var linePos = new Vector2(position.X + alignmentOffset.X, position.Y + i * lineHeight);
            spriteBatch.DrawString(font, lines[i], linePos, color);
        }
    }

    public static void DrawTextAligned(this SpriteBatch spriteBatch,
        SpriteFontBase font,
        string text,
        Vector2 position,
        HorizontalAlignment horizontalAlignment,
        VerticalAlignment verticalAlignment,
        Color color)
    {
        var size = font.MeasureString(text);

        // Vertical
        position.Y -= verticalAlignment switch
        {
            VerticalAlignment.Top => 0f,
            VerticalAlignment.Center => size.Y * 0.5f,
            VerticalAlignment.Bottom => size.Y,
            _ => 0f
        };

        // Horizontal
        position.X -= horizontalAlignment switch
        {
            HorizontalAlignment.Left => 0f,
            HorizontalAlignment.Center => size.X * 0.5f,
            HorizontalAlignment.Right => size.X,
            _ => 0f
        };

        spriteBatch.DrawString(font, text, new Vector2(position.X, position.Y), color);
    }

    private static Vector2 GetAlignmentOffset(SpriteFontBase spriteFont, string text,
        HorizontalAlignment horizontalAlignment)
    {
        var textSize = spriteFont.MeasureString(text);

        return horizontalAlignment switch
        {
            HorizontalAlignment.Center => new Vector2(-textSize.X / 2, textSize.Y / 2),
            HorizontalAlignment.Left => new Vector2(0, textSize.Y / 2),
            HorizontalAlignment.Right => new Vector2(-textSize.X, textSize.Y / 2),
            _ => Vector2.Zero
        };
    }

    public static Vector2 TransformPrimitivePoint(
        Vector2 localPoint,
        Vector2 position,
        float rotation,
        Vector2 origin,
        Vector2 scale)
    {
        var point = (localPoint - origin) * scale;

        if (MathF.Abs(rotation) > Mathf.Epsilon)
        {
            var cosine = MathF.Cos(rotation);
            var sine = MathF.Sin(rotation);

            point = new Vector2(
                point.X * cosine - point.Y * sine,
                point.X * sine + point.Y * cosine);
        }

        return position + point;
    }

    public static Vector2[] TransformPrimitivePoints(
        IReadOnlyList<Vector2> localPoints,
        Vector2 position,
        float rotation = 0f,
        Vector2? origin = null,
        Vector2? scale = null)
    {
        ArgumentNullException.ThrowIfNull(localPoints);

        var resolvedOrigin = origin ?? Vector2.Zero;
        var resolvedScale = scale ?? Vector2.One;
        var transformed = new Vector2[localPoints.Count];

        for (var i = 0; i < localPoints.Count; i++)
            transformed[i] = TransformPrimitivePoint(
                localPoints[i],
                position,
                rotation,
                resolvedOrigin,
                resolvedScale);

        return transformed;
    }

    public static Vector2[] CreateTransformedRectanglePoints(
        Vector2 position,
        Vector2 size,
        float rotation = 0f,
        Vector2? origin = null,
        Vector2? scale = null)
    {
        size = Abs(size);
        var localPoints = CreateRectanglePoints(size);
        return TransformPrimitivePoints(localPoints, position, rotation, origin ?? size * 0.5f, scale);
    }

    public static Rectangle GetAxisAlignedBounds(IReadOnlyList<Vector2> points)
    {
        ArgumentNullException.ThrowIfNull(points);

        if (points.Count == 0)
            return Rectangle.Empty;

        var minX = points[0].X;
        var minY = points[0].Y;
        var maxX = points[0].X;
        var maxY = points[0].Y;

        for (var i = 1; i < points.Count; i++)
        {
            minX = MathF.Min(minX, points[i].X);
            minY = MathF.Min(minY, points[i].Y);
            maxX = MathF.Max(maxX, points[i].X);
            maxY = MathF.Max(maxY, points[i].Y);
        }

        var left = (int)MathF.Floor(minX);
        var top = (int)MathF.Floor(minY);
        var right = (int)MathF.Ceiling(maxX);
        var bottom = (int)MathF.Ceiling(maxY);

        return new Rectangle(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    // -------------------------------------------------------------------------
    // Basic primitives
    // -------------------------------------------------------------------------

    public static void DrawLine(
        this SpriteBatch spriteBatch,
        Vector2 start,
        Vector2 end,
        Color color,
        float thickness = 1f)
    {
        EnsurePixelTextureExists(spriteBatch.GraphicsDevice);

        thickness = MathF.Max(0f, thickness);
        var delta = end - start;
        var distance = delta.Length();

        if (distance <= Mathf.Epsilon)
        {
            spriteBatch.DrawPoint(start, color, thickness);
            return;
        }

        var angle = MathF.Atan2(delta.Y, delta.X);

        spriteBatch.Draw(
            PixelTexture,
            start,
            null,
            color,
            angle,
            new Vector2(0f, 0.5f),
            new Vector2(distance, thickness),
            SpriteEffects.None,
            0f);
    }

    public static void DrawLine(
        this SpriteBatch spriteBatch,
        Vector3 start,
        Vector3 end,
        Color color,
        float thickness = 1f)
    {
        spriteBatch.DrawLine(start.ToVector2(), end.ToVector2(), color, thickness);
    }

    public static void DrawPath(
        this SpriteBatch spriteBatch,
        IReadOnlyList<Vector2> points,
        Color color,
        float thickness = 1f)
    {
        ArgumentNullException.ThrowIfNull(points);

        for (var i = 0; i < points.Count - 1; i++)
            spriteBatch.DrawLine(points[i], points[i + 1], color, thickness);
    }

    public static void DrawPath(
        this SpriteBatch spriteBatch,
        IReadOnlyList<Vector3> points,
        Color color,
        float thickness = 1f)
    {
        ArgumentNullException.ThrowIfNull(points);

        for (var i = 0; i < points.Count - 1; i++)
            spriteBatch.DrawLine(points[i], points[i + 1], color, thickness);
    }

    public static void DrawPoint(
        this SpriteBatch spriteBatch,
        Vector2 point,
        Color color,
        float size = 1f)
    {
        EnsurePixelTextureExists(spriteBatch.GraphicsDevice);

        size = MathF.Max(0f, size);

        spriteBatch.Draw(
            PixelTexture,
            point,
            null,
            color,
            0f,
            new Vector2(0.5f),
            new Vector2(size),
            SpriteEffects.None,
            0f);
    }

    // -------------------------------------------------------------------------
    // Polygon core
    // -------------------------------------------------------------------------

    // Backwards-compatible alias. DrawPolygon remains an outline operation.
    public static void DrawPolygon(
        this SpriteBatch spriteBatch,
        Vector2[] points,
        Color color,
        float thickness = 1f)
    {
        spriteBatch.DrawHollowPolygon(points, color, thickness);
    }

    public static void DrawHollowPolygon(
        this SpriteBatch spriteBatch,
        IReadOnlyList<Vector2> points,
        Color color,
        float thickness = 1f)
    {
        ArgumentNullException.ThrowIfNull(points);

        if (points.Count < 2)
            return;

        for (var i = 0; i < points.Count; i++)
        {
            var next = (i + 1) % points.Count;
            spriteBatch.DrawLine(points[i], points[next], color, thickness);
        }
    }

    public static void DrawHollowPolygon(
        this SpriteBatch spriteBatch,
        IReadOnlyList<Vector2> localPoints,
        Vector2 position,
        Color color,
        float rotation = 0f,
        Vector2? origin = null,
        Vector2? scale = null,
        float thickness = 1f)
    {
        var transformed = TransformPrimitivePoints(localPoints, position, rotation, origin, scale);
        spriteBatch.DrawHollowPolygon(transformed, color, thickness);
    }

    public static void DrawWireframePolygon(
        this SpriteBatch spriteBatch,
        IReadOnlyList<Vector2> points,
        Color color,
        float thickness = 1f)
    {
        ArgumentNullException.ThrowIfNull(points);

        if (points.Count < 2)
            return;

        spriteBatch.DrawHollowPolygon(points, color, thickness);

        if (points.Count < 3)
            return;

        var center = GetAveragePoint(points);
        for (var i = 0; i < points.Count; i++)
            spriteBatch.DrawLine(center, points[i], color, thickness);
    }

    public static void DrawWireframePolygon(
        this SpriteBatch spriteBatch,
        IReadOnlyList<Vector2> localPoints,
        Vector2 position,
        Color color,
        float rotation = 0f,
        Vector2? origin = null,
        Vector2? scale = null,
        float thickness = 1f)
    {
        var transformed = TransformPrimitivePoints(localPoints, position, rotation, origin, scale);
        spriteBatch.DrawWireframePolygon(transformed, color, thickness);
    }

    public static void DrawSolidPolygon(
        this SpriteBatch spriteBatch,
        IReadOnlyList<Vector2> points,
        Color color)
    {
        ArgumentNullException.ThrowIfNull(points);

        if (points.Count < 3)
            return;

        EnsurePixelTextureExists(spriteBatch.GraphicsDevice);

        var minY = points[0].Y;
        var maxY = points[0].Y;

        for (var i = 1; i < points.Count; i++)
        {
            minY = MathF.Min(minY, points[i].Y);
            maxY = MathF.Max(maxY, points[i].Y);
        }

        var firstScanline = (int)MathF.Floor(minY);
        var lastScanline = (int)MathF.Ceiling(maxY);
        var intersections = new float[points.Count];

        for (var y = firstScanline; y < lastScanline; y++)
        {
            var scanY = y + 0.5f;
            var intersectionCount = 0;

            for (var i = 0; i < points.Count; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % points.Count];

                // Half-open edge test avoids counting shared vertices twice.
                var crossesScanline =
                    (a.Y <= scanY && b.Y > scanY) ||
                    (b.Y <= scanY && a.Y > scanY);

                if (!crossesScanline)
                    continue;

                var t = (scanY - a.Y) / (b.Y - a.Y);
                intersections[intersectionCount++] = MathHelper.Lerp(a.X, b.X, t);
            }

            if (intersectionCount < 2)
                continue;

            Array.Sort(intersections, 0, intersectionCount);

            for (var i = 0; i + 1 < intersectionCount; i += 2)
            {
                var left = intersections[i];
                var right = intersections[i + 1];

                if (right - left <= Mathf.Epsilon)
                    continue;

                spriteBatch.DrawLine(
                    new Vector2(left, scanY),
                    new Vector2(right, scanY),
                    color,
                    1.05f);
            }
        }
    }

    public static void DrawSolidPolygon(
        this SpriteBatch spriteBatch,
        IReadOnlyList<Vector2> localPoints,
        Vector2 position,
        Color color,
        float rotation = 0f,
        Vector2? origin = null,
        Vector2? scale = null)
    {
        var transformed = TransformPrimitivePoints(localPoints, position, rotation, origin, scale);
        spriteBatch.DrawSolidPolygon(transformed, color);
    }

    public static void DrawFilledPolygon(
        this SpriteBatch spriteBatch,
        IReadOnlyList<Vector2> points,
        Color color)
    {
        spriteBatch.DrawSolidPolygon(points, color);
    }

    public static void DrawFilledPolygon(
        this SpriteBatch spriteBatch,
        IReadOnlyList<Vector2> localPoints,
        Vector2 position,
        Color color,
        float rotation = 0f,
        Vector2? origin = null,
        Vector2? scale = null)
    {
        spriteBatch.DrawSolidPolygon(localPoints, position, color, rotation, origin, scale);
    }

    // -------------------------------------------------------------------------
    // Rectangle and square
    // -------------------------------------------------------------------------

    public static void DrawFilledRectangle(
        this SpriteBatch spriteBatch,
        RectangleF rectangle,
        Color color)
    {
        spriteBatch.DrawSolidRectangle(
            new Vector2(rectangle.X, rectangle.Y),
            new Vector2(rectangle.Width, rectangle.Height),
            color,
            origin: Vector2.Zero);
    }

    public static void DrawHollowRectangle(
        this SpriteBatch spriteBatch,
        RectangleF rectangle,
        Color color,
        float thickness = 1f,
        float rotation = 0f)
    {
        spriteBatch.DrawHollowRectangle(
            new Vector2(rectangle.X, rectangle.Y),
            new Vector2(rectangle.Width, rectangle.Height),
            color,
            rotation,
            Vector2.Zero,
            Vector2.One,
            thickness);
    }

    public static void DrawHollowRectangle(
        this SpriteBatch spriteBatch,
        Vector2 position,
        Vector2 size,
        Color color,
        float rotation = 0f,
        Vector2? origin = null,
        Vector2? scale = null,
        float thickness = 1f)
    {
        var runtimeCamera = Core.Instance?.CurrentScene?.MainCamera;
        if (runtimeCamera is not null && thickness < runtimeCamera.WorldUnitsPerScreenPixel)
            thickness = runtimeCamera.WorldUnitsPerScreenPixel;

        var points = CreateTransformedRectanglePoints(position, size, rotation, origin, scale);
        spriteBatch.DrawHollowPolygon(points, color, thickness);
    }

    public static void DrawWireframeRectangle(
        this SpriteBatch spriteBatch,
        Vector2 position,
        Vector2 size,
        Color color,
        float rotation = 0f,
        Vector2? origin = null,
        Vector2? scale = null,
        float thickness = 1f)
    {
        var points = CreateTransformedRectanglePoints(position, size, rotation, origin, scale);
        spriteBatch.DrawHollowPolygon(points, color, thickness);
        spriteBatch.DrawLine(points[0], points[2], color, thickness);
        spriteBatch.DrawLine(points[1], points[3], color, thickness);
    }

    public static void DrawSolidRectangle(
        this SpriteBatch spriteBatch,
        Vector2 position,
        Vector2 size,
        Color color,
        float rotation = 0f,
        Vector2? origin = null,
        Vector2? scale = null)
    {
        EnsurePixelTextureExists(spriteBatch.GraphicsDevice);

        size = Abs(size);
        if (size.X <= Mathf.Epsilon || size.Y <= Mathf.Epsilon)
            return;

        var resolvedOrigin = origin ?? size * 0.5f;
        var resolvedScale = scale ?? Vector2.One;
        var normalizedOrigin = new Vector2(resolvedOrigin.X / size.X, resolvedOrigin.Y / size.Y);

        spriteBatch.Draw(
            PixelTexture,
            position,
            null,
            color,
            rotation,
            normalizedOrigin,
            size * resolvedScale,
            SpriteEffects.None,
            0f);
    }

    public static void DrawFilledRectangle(
        this SpriteBatch spriteBatch,
        Vector2 position,
        Vector2 size,
        Color color,
        float rotation = 0f,
        Vector2? origin = null,
        Vector2? scale = null)
    {
        spriteBatch.DrawSolidRectangle(position, size, color, rotation, origin, scale);
    }

    public static void DrawHollowSquare(
        this SpriteBatch spriteBatch,
        Vector2 position,
        float size,
        Color color,
        float rotation = 0f,
        Vector2? origin = null,
        Vector2? scale = null,
        float thickness = 1f)
    {
        spriteBatch.DrawHollowRectangle(
            position,
            new Vector2(size),
            color,
            rotation,
            origin,
            scale,
            thickness);
    }

    public static void DrawWireframeSquare(
        this SpriteBatch spriteBatch,
        Vector2 position,
        float size,
        Color color,
        float rotation = 0f,
        Vector2? origin = null,
        Vector2? scale = null,
        float thickness = 1f)
    {
        spriteBatch.DrawWireframeRectangle(
            position,
            new Vector2(size),
            color,
            rotation,
            origin,
            scale,
            thickness);
    }

    public static void DrawSolidSquare(
        this SpriteBatch spriteBatch,
        Vector2 position,
        float size,
        Color color,
        float rotation = 0f,
        Vector2? origin = null,
        Vector2? scale = null)
    {
        spriteBatch.DrawSolidRectangle(position, new Vector2(size), color, rotation, origin, scale);
    }

    public static void DrawFilledSquare(
        this SpriteBatch spriteBatch,
        Vector2 position,
        float size,
        Color color,
        float rotation = 0f,
        Vector2? origin = null,
        Vector2? scale = null)
    {
        spriteBatch.DrawSolidSquare(position, size, color, rotation, origin, scale);
    }

    // -------------------------------------------------------------------------
    // Circle and ellipse
    // -------------------------------------------------------------------------

    // Backwards-compatible alias. DrawCircle remains an outline operation.
    public static void DrawCircle(
        this SpriteBatch spriteBatch,
        Vector2 center,
        float radius,
        Color color,
        int segments = 32,
        float thickness = 1f)
    {
        spriteBatch.DrawHollowCircle(center, radius, color, 0f, Vector2.One, segments, thickness);
    }

    public static void DrawHollowCircle(
        this SpriteBatch spriteBatch,
        Vector2 center,
        float radius,
        Color color,
        float rotation = 0f,
        Vector2? scale = null,
        int segments = 32,
        float thickness = 1f)
    {
        spriteBatch.DrawHollowEllipse(
            center,
            new Vector2(MathF.Abs(radius)),
            color,
            rotation,
            scale,
            segments,
            thickness);
    }

    public static void DrawWireframeCircle(
        this SpriteBatch spriteBatch,
        Vector2 center,
        float radius,
        Color color,
        float rotation = 0f,
        Vector2? scale = null,
        int segments = 32,
        float thickness = 1f)
    {
        spriteBatch.DrawWireframeEllipse(
            center,
            new Vector2(MathF.Abs(radius)),
            color,
            rotation,
            scale,
            segments,
            thickness);
    }

    public static void DrawSolidCircle(
        this SpriteBatch spriteBatch,
        Vector2 center,
        float radius,
        Color color,
        float rotation = 0f,
        Vector2? scale = null,
        int segments = 32)
    {
        spriteBatch.DrawSolidEllipse(
            center,
            new Vector2(MathF.Abs(radius)),
            color,
            rotation,
            scale,
            segments);
    }

    public static void DrawFilledCircle(
        this SpriteBatch spriteBatch,
        Vector2 center,
        float radius,
        Color color,
        float rotation = 0f,
        Vector2? scale = null,
        int segments = 32)
    {
        spriteBatch.DrawSolidCircle(center, radius, color, rotation, scale, segments);
    }

    public static void DrawHollowEllipse(
        this SpriteBatch spriteBatch,
        Vector2 center,
        Vector2 radii,
        Color color,
        float rotation = 0f,
        Vector2? scale = null,
        int segments = 32,
        float thickness = 1f)
    {
        var localPoints = CreateEllipsePoints(radii, segments);
        var transformed = TransformPrimitivePoints(localPoints, center, rotation, Vector2.Zero, scale);
        spriteBatch.DrawHollowPolygon(transformed, color, thickness);
    }

    public static void DrawWireframeEllipse(
        this SpriteBatch spriteBatch,
        Vector2 center,
        Vector2 radii,
        Color color,
        float rotation = 0f,
        Vector2? scale = null,
        int segments = 32,
        float thickness = 1f)
    {
        radii = Abs(radii);
        var resolvedScale = scale ?? Vector2.One;

        spriteBatch.DrawHollowEllipse(center, radii, color, rotation, resolvedScale, segments, thickness);

        // Four diameters produce an actual 2D wireframe without turning it into a porcupine.
        for (var i = 0; i < 4; i++)
        {
            var angle = i * MathHelper.PiOver4;
            var direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            var localEnd = direction * radii;

            var start = TransformPrimitivePoint(-localEnd, center, rotation, Vector2.Zero, resolvedScale);
            var end = TransformPrimitivePoint(localEnd, center, rotation, Vector2.Zero, resolvedScale);
            spriteBatch.DrawLine(start, end, color, thickness);
        }
    }

    public static void DrawSolidEllipse(
        this SpriteBatch spriteBatch,
        Vector2 center,
        Vector2 radii,
        Color color,
        float rotation = 0f,
        Vector2? scale = null,
        int segments = 32)
    {
        var localPoints = CreateEllipsePoints(radii, segments);
        var transformed = TransformPrimitivePoints(localPoints, center, rotation, Vector2.Zero, scale);
        spriteBatch.DrawSolidPolygon(transformed, color);
    }

    public static void DrawFilledEllipse(
        this SpriteBatch spriteBatch,
        Vector2 center,
        Vector2 radii,
        Color color,
        float rotation = 0f,
        Vector2? scale = null,
        int segments = 32)
    {
        spriteBatch.DrawSolidEllipse(center, radii, color, rotation, scale, segments);
    }

    // -------------------------------------------------------------------------
    // Triangle
    // -------------------------------------------------------------------------

    public static void DrawHollowTriangle(
        this SpriteBatch spriteBatch,
        Vector2 center,
        Vector2 size,
        Color color,
        float rotation = 0f,
        Vector2? scale = null,
        float thickness = 1f)
    {
        var localPoints = CreateTrianglePoints(size);
        var transformed = TransformPrimitivePoints(localPoints, center, rotation, Vector2.Zero, scale);
        spriteBatch.DrawHollowPolygon(transformed, color, thickness);
    }

    public static void DrawWireframeTriangle(
        this SpriteBatch spriteBatch,
        Vector2 center,
        Vector2 size,
        Color color,
        float rotation = 0f,
        Vector2? scale = null,
        float thickness = 1f)
    {
        var localPoints = CreateTrianglePoints(size);
        var transformed = TransformPrimitivePoints(localPoints, center, rotation, Vector2.Zero, scale);
        spriteBatch.DrawHollowPolygon(transformed, color, thickness);

        var centroid = GetAveragePoint(transformed);
        for (var i = 0; i < transformed.Length; i++)
            spriteBatch.DrawLine(centroid, transformed[i], color, thickness);
    }

    public static void DrawSolidTriangle(
        this SpriteBatch spriteBatch,
        Vector2 center,
        Vector2 size,
        Color color,
        float rotation = 0f,
        Vector2? scale = null)
    {
        var localPoints = CreateTrianglePoints(size);
        var transformed = TransformPrimitivePoints(localPoints, center, rotation, Vector2.Zero, scale);
        spriteBatch.DrawSolidPolygon(transformed, color);
    }

    public static void DrawFilledTriangle(
        this SpriteBatch spriteBatch,
        Vector2 center,
        Vector2 size,
        Color color,
        float rotation = 0f,
        Vector2? scale = null)
    {
        spriteBatch.DrawSolidTriangle(center, size, color, rotation, scale);
    }

    public static void DrawHollowTriangle(
        this SpriteBatch spriteBatch,
        Vector2 a,
        Vector2 b,
        Vector2 c,
        Color color,
        float thickness = 1f)
    {
        spriteBatch.DrawHollowPolygon([a, b, c], color, thickness);
    }

    public static void DrawWireframeTriangle(
        this SpriteBatch spriteBatch,
        Vector2 a,
        Vector2 b,
        Vector2 c,
        Color color,
        float thickness = 1f)
    {
        spriteBatch.DrawWireframePolygon([a, b, c], color, thickness);
    }

    public static void DrawSolidTriangle(
        this SpriteBatch spriteBatch,
        Vector2 a,
        Vector2 b,
        Vector2 c,
        Color color)
    {
        spriteBatch.DrawSolidPolygon([a, b, c], color);
    }

    public static void DrawFilledTriangle(
        this SpriteBatch spriteBatch,
        Vector2 a,
        Vector2 b,
        Vector2 c,
        Color color)
    {
        spriteBatch.DrawSolidTriangle(a, b, c, color);
    }

    // -------------------------------------------------------------------------
    // Capsule
    // -------------------------------------------------------------------------

    public static void DrawHollowCapsule(
        this SpriteBatch spriteBatch,
        Vector2 center,
        Vector2 size,
        Color color,
        float rotation = 0f,
        Vector2? scale = null,
        int arcSegments = 12,
        float thickness = 1f)
    {
        var localPoints = CreateCapsulePoints(size, arcSegments);
        var transformed = TransformPrimitivePoints(localPoints, center, rotation, Vector2.Zero, scale);
        spriteBatch.DrawHollowPolygon(transformed, color, thickness);
    }

    public static void DrawWireframeCapsule(
        this SpriteBatch spriteBatch,
        Vector2 center,
        Vector2 size,
        Color color,
        float rotation = 0f,
        Vector2? scale = null,
        int arcSegments = 12,
        float thickness = 1f)
    {
        size = Abs(size);
        var resolvedScale = scale ?? Vector2.One;

        spriteBatch.DrawHollowCapsule(
            center,
            size,
            color,
            rotation,
            resolvedScale,
            arcSegments,
            thickness);

        if (size.X >= size.Y)
        {
            var radius = size.Y * 0.5f;
            var straightHalfLength = MathF.Max(0f, size.X * 0.5f - radius);

            DrawTransformedLocalLine(
                spriteBatch,
                new Vector2(-straightHalfLength, 0f),
                new Vector2(straightHalfLength, 0f),
                center,
                rotation,
                resolvedScale,
                color,
                thickness);

            DrawTransformedLocalLine(
                spriteBatch,
                new Vector2(-straightHalfLength, -radius),
                new Vector2(-straightHalfLength, radius),
                center,
                rotation,
                resolvedScale,
                color,
                thickness);

            DrawTransformedLocalLine(
                spriteBatch,
                new Vector2(straightHalfLength, -radius),
                new Vector2(straightHalfLength, radius),
                center,
                rotation,
                resolvedScale,
                color,
                thickness);
        }
        else
        {
            var radius = size.X * 0.5f;
            var straightHalfLength = MathF.Max(0f, size.Y * 0.5f - radius);

            DrawTransformedLocalLine(
                spriteBatch,
                new Vector2(0f, -straightHalfLength),
                new Vector2(0f, straightHalfLength),
                center,
                rotation,
                resolvedScale,
                color,
                thickness);

            DrawTransformedLocalLine(
                spriteBatch,
                new Vector2(-radius, -straightHalfLength),
                new Vector2(radius, -straightHalfLength),
                center,
                rotation,
                resolvedScale,
                color,
                thickness);

            DrawTransformedLocalLine(
                spriteBatch,
                new Vector2(-radius, straightHalfLength),
                new Vector2(radius, straightHalfLength),
                center,
                rotation,
                resolvedScale,
                color,
                thickness);
        }
    }

    public static void DrawSolidCapsule(
        this SpriteBatch spriteBatch,
        Vector2 center,
        Vector2 size,
        Color color,
        float rotation = 0f,
        Vector2? scale = null,
        int arcSegments = 12)
    {
        var localPoints = CreateCapsulePoints(size, arcSegments);
        var transformed = TransformPrimitivePoints(localPoints, center, rotation, Vector2.Zero, scale);
        spriteBatch.DrawSolidPolygon(transformed, color);
    }

    public static void DrawFilledCapsule(
        this SpriteBatch spriteBatch,
        Vector2 center,
        Vector2 size,
        Color color,
        float rotation = 0f,
        Vector2? scale = null,
        int arcSegments = 12)
    {
        spriteBatch.DrawSolidCapsule(center, size, color, rotation, scale, arcSegments);
    }

    // -------------------------------------------------------------------------
    // Regular polygon
    // -------------------------------------------------------------------------

    public static void DrawHollowRegularPolygon(
        this SpriteBatch spriteBatch,
        Vector2 center,
        float radius,
        int sides,
        Color color,
        float rotation = 0f,
        Vector2? scale = null,
        float thickness = 1f)
    {
        var localPoints = CreateRegularPolygonPoints(radius, sides);
        var transformed = TransformPrimitivePoints(localPoints, center, rotation, Vector2.Zero, scale);
        spriteBatch.DrawHollowPolygon(transformed, color, thickness);
    }

    public static void DrawWireframeRegularPolygon(
        this SpriteBatch spriteBatch,
        Vector2 center,
        float radius,
        int sides,
        Color color,
        float rotation = 0f,
        Vector2? scale = null,
        float thickness = 1f)
    {
        var localPoints = CreateRegularPolygonPoints(radius, sides);
        var transformed = TransformPrimitivePoints(localPoints, center, rotation, Vector2.Zero, scale);
        spriteBatch.DrawWireframePolygon(transformed, color, thickness);
    }

    public static void DrawSolidRegularPolygon(
        this SpriteBatch spriteBatch,
        Vector2 center,
        float radius,
        int sides,
        Color color,
        float rotation = 0f,
        Vector2? scale = null)
    {
        var localPoints = CreateRegularPolygonPoints(radius, sides);
        var transformed = TransformPrimitivePoints(localPoints, center, rotation, Vector2.Zero, scale);
        spriteBatch.DrawSolidPolygon(transformed, color);
    }

    public static void DrawFilledRegularPolygon(
        this SpriteBatch spriteBatch,
        Vector2 center,
        float radius,
        int sides,
        Color color,
        float rotation = 0f,
        Vector2? scale = null)
    {
        spriteBatch.DrawSolidRegularPolygon(center, radius, sides, color, rotation, scale);
    }

    // -------------------------------------------------------------------------
    // Local geometry helpers
    // -------------------------------------------------------------------------

    private static Vector2[] CreateRectanglePoints(Vector2 size)
    {
        return
        [
            Vector2.Zero,
            new Vector2(size.X, 0f),
            size,
            new Vector2(0f, size.Y)
        ];
    }

    private static Vector2[] CreateTrianglePoints(Vector2 size)
    {
        size = Abs(size);
        var half = size * 0.5f;

        return
        [
            new Vector2(0f, -half.Y),
            new Vector2(half.X, half.Y),
            new Vector2(-half.X, half.Y)
        ];
    }

    private static Vector2[] CreateEllipsePoints(Vector2 radii, int segments)
    {
        radii = Abs(radii);
        segments = Math.Max(3, segments);

        var points = new Vector2[segments];
        var angleStep = MathHelper.TwoPi / segments;

        for (var i = 0; i < segments; i++)
        {
            var angle = -MathHelper.PiOver2 + i * angleStep;
            points[i] = new Vector2(
                MathF.Cos(angle) * radii.X,
                MathF.Sin(angle) * radii.Y);
        }

        return points;
    }

    private static Vector2[] CreateCapsulePoints(Vector2 size, int arcSegments)
    {
        size = Abs(size);
        arcSegments = Math.Max(2, arcSegments);

        if (size.X <= Mathf.Epsilon || size.Y <= Mathf.Epsilon)
            return CreateRectanglePoints(size);

        if (MathF.Abs(size.X - size.Y) <= Mathf.Epsilon)
            return CreateEllipsePoints(size * 0.5f, arcSegments * 2);

        var points = new Vector2[(arcSegments + 1) * 2];
        var index = 0;

        if (size.X > size.Y)
        {
            var radius = size.Y * 0.5f;
            var straightHalfLength = size.X * 0.5f - radius;

            for (var i = 0; i <= arcSegments; i++)
            {
                var angle = -MathHelper.PiOver2 + MathHelper.Pi * i / arcSegments;
                points[index++] = new Vector2(
                    straightHalfLength + MathF.Cos(angle) * radius,
                    MathF.Sin(angle) * radius);
            }

            for (var i = 0; i <= arcSegments; i++)
            {
                var angle = MathHelper.PiOver2 + MathHelper.Pi * i / arcSegments;
                points[index++] = new Vector2(
                    -straightHalfLength + MathF.Cos(angle) * radius,
                    MathF.Sin(angle) * radius);
            }
        }
        else
        {
            var radius = size.X * 0.5f;
            var straightHalfLength = size.Y * 0.5f - radius;

            for (var i = 0; i <= arcSegments; i++)
            {
                var angle = 0f + MathHelper.Pi * i / arcSegments;
                points[index++] = new Vector2(
                    MathF.Cos(angle) * radius,
                    straightHalfLength + MathF.Sin(angle) * radius);
            }

            for (var i = 0; i <= arcSegments; i++)
            {
                var angle = MathHelper.Pi + MathHelper.Pi * i / arcSegments;
                points[index++] = new Vector2(
                    MathF.Cos(angle) * radius,
                    -straightHalfLength + MathF.Sin(angle) * radius);
            }
        }

        return points;
    }

    private static Vector2[] CreateRegularPolygonPoints(float radius, int sides)
    {
        sides = Math.Max(3, sides);
        radius = MathF.Abs(radius);

        var points = new Vector2[sides];
        var angleStep = MathHelper.TwoPi / sides;

        for (var i = 0; i < sides; i++)
        {
            var angle = -MathHelper.PiOver2 + i * angleStep;
            points[i] = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
        }

        return points;
    }

    private static void DrawTransformedLocalLine(
        SpriteBatch spriteBatch,
        Vector2 localStart,
        Vector2 localEnd,
        Vector2 position,
        float rotation,
        Vector2 scale,
        Color color,
        float thickness)
    {
        var start = TransformPrimitivePoint(localStart, position, rotation, Vector2.Zero, scale);
        var end = TransformPrimitivePoint(localEnd, position, rotation, Vector2.Zero, scale);
        spriteBatch.DrawLine(start, end, color, thickness);
    }

    private static Vector2 GetAveragePoint(IReadOnlyList<Vector2> points)
    {
        var total = Vector2.Zero;

        for (var i = 0; i < points.Count; i++)
            total += points[i];

        return total / points.Count;
    }

    private static Vector2 Abs(Vector2 value)
    {
        return new Vector2(MathF.Abs(value.X), MathF.Abs(value.Y));
    }
}
