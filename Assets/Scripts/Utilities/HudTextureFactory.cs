using UnityEngine;

namespace Forest
{
    public static class HudTextureFactory
    {
        public static Texture2D CreateRoundedRect(int width, int height, float radius, Color fillColor, Color borderColor, float borderWidth)
        {
            Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                name = "Forest Rounded Rect"
            };
            Color clear = new Color(1f, 1f, 1f, 0f);
            float maxX = width - 1f;
            float maxY = height - 1f;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float distanceFromEdge = Mathf.Min(Mathf.Min(x, maxX - x), Mathf.Min(y, maxY - y));
                    float cornerX = x < radius ? radius : maxX - x < radius ? maxX - radius : x;
                    float cornerY = y < radius ? radius : maxY - y < radius ? maxY - radius : y;
                    float cornerDistance = Vector2.Distance(new Vector2(x, y), new Vector2(cornerX, cornerY));

                    if (cornerDistance > radius)
                    {
                        texture.SetPixel(x, y, clear);
                    }
                    else if (borderWidth > 0f && (distanceFromEdge < borderWidth || cornerDistance > radius - borderWidth))
                    {
                        texture.SetPixel(x, y, borderColor);
                    }
                    else
                    {
                        texture.SetPixel(x, y, fillColor);
                    }
                }
            }

            texture.Apply();
            return texture;
        }

        public static Texture2D CreateCircle(int size, Color fillColor, Color borderColor, float borderWidth)
        {
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "Forest Circle"
            };
            Color clear = new Color(1f, 1f, 1f, 0f);
            float center = (size - 1f) * 0.5f;
            float radius = center;
            float innerRadius = Mathf.Max(0f, radius - borderWidth);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float distance = Vector2.Distance(new Vector2(x, y), new Vector2(center, center));

                    if (distance > radius)
                    {
                        texture.SetPixel(x, y, clear);
                    }
                    else if (distance >= innerRadius)
                    {
                        texture.SetPixel(x, y, borderColor);
                    }
                    else
                    {
                        texture.SetPixel(x, y, fillColor);
                    }
                }
            }

            texture.Apply();
            return texture;
        }

        public static Texture2D CreateMiniMapFrame(int size, Color fillColor, Color edgeShadeColor, Color arcColor, float arcWidth)
        {
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "Forest Mini Map Frame"
            };
            Color clear = new Color(1f, 1f, 1f, 0f);
            float center = (size - 1f) * 0.5f;
            float outerRadius = center;
            float innerArcRadius = Mathf.Max(0f, outerRadius - arcWidth);
            float edgeShadeRadius = Mathf.Max(0f, outerRadius - 11f);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float distance = Vector2.Distance(new Vector2(x, y), new Vector2(center, center));

                    if (distance > outerRadius)
                    {
                        texture.SetPixel(x, y, clear);
                    }
                    else if (distance >= innerArcRadius)
                    {
                        texture.SetPixel(x, y, arcColor);
                    }
                    else if (distance >= edgeShadeRadius)
                    {
                        texture.SetPixel(x, y, edgeShadeColor);
                    }
                    else
                    {
                        texture.SetPixel(x, y, fillColor);
                    }
                }
            }

            texture.Apply();
            return texture;
        }
    }
}
