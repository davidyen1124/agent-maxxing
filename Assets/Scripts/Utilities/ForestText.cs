using System;
using System.Text;

namespace Forest
{
    public static class ForestText
    {
        public static string Shorten(string text, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(text) || maxLength < 4)
            {
                return string.Empty;
            }

            string trimmed = text.Trim();

            if (trimmed.Length <= maxLength)
            {
                return trimmed;
            }

            return trimmed.Substring(0, Math.Max(0, maxLength - 3)).TrimEnd() + "...";
        }

        public static string CollapseWhitespace(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            StringBuilder builder = new StringBuilder(value.Length);
            bool pendingSpace = false;

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];

                if (char.IsWhiteSpace(c))
                {
                    pendingSpace = builder.Length > 0;
                    continue;
                }

                if (pendingSpace)
                {
                    builder.Append(' ');
                    pendingSpace = false;
                }

                builder.Append(c);
            }

            return builder.ToString().Trim();
        }
    }
}
