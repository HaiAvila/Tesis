using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// Star (preferida) and heart (favorita) icons - plus play/pause and a plain circle - as
// sprites generated in code, instead of font glyphs. With the legacy Text font, the filled heart (♥) kept rendering invisible in the
// director's session - on the replay panel, the session list and Favoritos - while ☆/♡/★
// rendered fine, and it couldn't be reproduced in isolation. A sprite doesn't depend on
// which font (or OS fallback font) supplies a glyph, so it always looks the same.
//
// Shapes are polygons rasterized once into a white texture with an antialiased edge (from
// the signed distance to the outline), then tinted with Image.color. Cached for the app's
// lifetime and flagged DontUnloadUnusedAsset so a scene change never unloads them.
public static class UIIcons
{
    const int TextureSize = 128;
    const float Margin = 0.06f;            // fraction of the texture left empty around the shape
    const float OutlineThickness = 0.085f; // fraction of the texture width

    static Sprite starFilled;
    static Sprite starOutline;
    static Sprite heartFilled;
    static Sprite heartOutline;
    static Sprite circle;
    static Sprite play;
    static Sprite pause;

    // Filled circle, e.g. for a slider handle.
    public static Sprite Circle()
    {
        if (circle == null)
            circle = Build("Circle", CirclePolygon(), true);
        return circle;
    }

    // Transport icons. The pause glyph (❚❚) rendered as one solid block with the legacy font.
    public static Sprite Play()
    {
        if (play == null)
            play = Build("Play", FitToUnitSquare(new List<Vector2> { new Vector2(0f, 0f), new Vector2(0.866f, 0.5f), new Vector2(0f, 1f) }), true);
        return play;
    }

    public static Sprite Pause()
    {
        if (pause == null)
        {
            // Two bars, each 30% of the icon wide, with a 20% gap - laid out directly inside
            // the margins rather than fitted one by one, so they stay side by side.
            float bottom = Margin;
            float top = 1f - Margin;
            float barWidth = (1f - 2f * Margin) * 0.3f;
            float gap = (1f - 2f * Margin) * 0.2f;
            float left = 0.5f - gap / 2f - barWidth;
            float right = 0.5f + gap / 2f;
            pause = Build("Pause", new List<List<Vector2>>
            {
                Rectangle(left, bottom, left + barWidth, top),
                Rectangle(right, bottom, right + barWidth, top),
            }, true);
        }
        return pause;
    }

    static List<Vector2> Rectangle(float xMin, float yMin, float xMax, float yMax)
    {
        return new List<Vector2> { new Vector2(xMin, yMin), new Vector2(xMax, yMin), new Vector2(xMax, yMax), new Vector2(xMin, yMax) };
    }

    public static Sprite Star(bool filled)
    {
        if (filled)
        {
            if (starFilled == null)
                starFilled = Build("StarFilled", StarPolygon(), true);
            return starFilled;
        }

        if (starOutline == null)
            starOutline = Build("StarOutline", StarPolygon(), false);
        return starOutline;
    }

    public static Sprite Heart(bool filled)
    {
        if (filled)
        {
            if (heartFilled == null)
                heartFilled = Build("HeartFilled", HeartPolygon(), true);
            return heartFilled;
        }

        if (heartOutline == null)
            heartOutline = Build("HeartOutline", HeartPolygon(), false);
        return heartOutline;
    }

    // A centered icon inside a button (or any rect), filling sizeFraction of it. Display-only
    // - raycastTarget is off so clicks go to the button underneath.
    public static Image CreateIcon(Transform parent, Sprite sprite, Color color, float sizeFraction = 0.6f)
    {
        var iconObject = new GameObject("Icon", typeof(RectTransform), typeof(Image));
        iconObject.transform.SetParent(parent, false);
        iconObject.layer = parent.gameObject.layer;

        RectTransform iconRect = (RectTransform)iconObject.transform;
        float inset = (1f - sizeFraction) / 2f;
        iconRect.anchorMin = new Vector2(inset, inset);
        iconRect.anchorMax = new Vector2(1f - inset, 1f - inset);
        iconRect.offsetMin = Vector2.zero;
        iconRect.offsetMax = Vector2.zero;

        Image image = iconObject.GetComponent<Image>();
        image.sprite = sprite;
        image.color = color;
        image.preserveAspect = true;
        image.raycastTarget = false;
        return image;
    }

    // Five-pointed star, point up, in 0-1 space.
    static List<Vector2> StarPolygon()
    {
        var points = new List<Vector2>();
        const float outerRadius = 0.5f;
        const float innerRadius = 0.2f;
        for (int i = 0; i < 10; i++)
        {
            float angle = Mathf.PI / 2f + i * Mathf.PI / 5f;
            float radius = i % 2 == 0 ? outerRadius : innerRadius;
            points.Add(new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius);
        }
        return FitToUnitSquare(points);
    }

    static List<Vector2> CirclePolygon()
    {
        var points = new List<Vector2>();
        const int samples = 96;
        for (int i = 0; i < samples; i++)
        {
            float angle = i * 2f * Mathf.PI / samples;
            points.Add(new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)));
        }
        return FitToUnitSquare(points);
    }

    // Classic parametric heart curve, sampled into a polygon.
    static List<Vector2> HeartPolygon()
    {
        var points = new List<Vector2>();
        const int samples = 120;
        for (int i = 0; i < samples; i++)
        {
            float t = i * 2f * Mathf.PI / samples;
            float x = 16f * Mathf.Pow(Mathf.Sin(t), 3f);
            float y = 13f * Mathf.Cos(t) - 5f * Mathf.Cos(2f * t) - 2f * Mathf.Cos(3f * t) - Mathf.Cos(4f * t);
            points.Add(new Vector2(x, y));
        }
        return FitToUnitSquare(points);
    }

    // Scales and centers the polygon into [Margin, 1 - Margin], keeping its aspect ratio.
    static List<Vector2> FitToUnitSquare(List<Vector2> points)
    {
        Vector2 min = points[0];
        Vector2 max = points[0];
        foreach (Vector2 p in points)
        {
            min = Vector2.Min(min, p);
            max = Vector2.Max(max, p);
        }

        Vector2 size = max - min;
        float scale = (1f - 2f * Margin) / Mathf.Max(size.x, size.y);
        Vector2 offset = new Vector2(0.5f, 0.5f) - (min + size / 2f) * scale;

        var fitted = new List<Vector2>(points.Count);
        foreach (Vector2 p in points)
            fitted.Add(p * scale + offset);
        return fitted;
    }

    static Sprite Build(string name, List<Vector2> polygon, bool filled)
    {
        return Build(name, new List<List<Vector2>> { polygon }, filled);
    }

    // Several separate polygons in one icon (e.g. the two pause bars): their union, i.e. the
    // smallest signed distance to any of them.
    static Sprite Build(string name, List<List<Vector2>> polygons, bool filled)
    {
        var texture = new Texture2D(TextureSize, TextureSize, TextureFormat.RGBA32, true)
        {
            name = name,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Trilinear,
            hideFlags = HideFlags.DontUnloadUnusedAsset,
        };

        float pixel = 1f / TextureSize;
        var pixels = new Color32[TextureSize * TextureSize];
        for (int y = 0; y < TextureSize; y++)
        {
            for (int x = 0; x < TextureSize; x++)
            {
                Vector2 p = new Vector2((x + 0.5f) * pixel, (y + 0.5f) * pixel);
                float distance = float.MaxValue; // negative inside
                foreach (List<Vector2> polygon in polygons)
                    distance = Mathf.Min(distance, SignedDistance(p, polygon));

                // Filled: covered inside the edge. Outline: covered in a band just inside it.
                float coverage = filled
                    ? Mathf.Clamp01(0.5f - distance / pixel)
                    : Mathf.Clamp01(0.5f + (OutlineThickness / 2f - Mathf.Abs(distance + OutlineThickness / 2f)) / pixel);

                pixels[y * TextureSize + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(coverage * 255f));
            }
        }

        texture.SetPixels32(pixels);
        texture.Apply(true, true);

        Sprite sprite = Sprite.Create(texture, new Rect(0, 0, TextureSize, TextureSize), new Vector2(0.5f, 0.5f), TextureSize);
        sprite.name = name;
        sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return sprite;
    }

    static float SignedDistance(Vector2 p, List<Vector2> polygon)
    {
        float minDistance = float.MaxValue;
        bool inside = false;

        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            Vector2 a = polygon[j];
            Vector2 b = polygon[i];

            Vector2 ab = b - a;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
            minDistance = Mathf.Min(minDistance, Vector2.Distance(p, a + ab * t));

            // Even-odd crossing test.
            if ((a.y > p.y) != (b.y > p.y) && p.x < a.x + (p.y - a.y) / (b.y - a.y) * (b.x - a.x))
                inside = !inside;
        }

        return inside ? -minDistance : minDistance;
    }
}
