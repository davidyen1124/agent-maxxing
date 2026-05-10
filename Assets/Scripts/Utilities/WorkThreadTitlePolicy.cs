using System;

namespace Forest
{
    public static class WorkThreadTitlePolicy
    {
        public const string DefaultTitlePrefix = "Underwater task ";
        public const string NextTitleNumberPrefsKey = "Underwater.UnderwaterTask.NextThreadNumber";

        public static string CreateDefaultTitle(int number)
        {
            return $"{DefaultTitlePrefix}{Math.Max(1, number)}";
        }

        public static bool TryReadDefaultTitleNumber(string title, out int number)
        {
            number = 0;

            if (string.IsNullOrWhiteSpace(title))
            {
                return false;
            }

            string trimmedTitle = title.Trim();

            if (!trimmedTitle.StartsWith(DefaultTitlePrefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string suffix = trimmedTitle.Substring(DefaultTitlePrefix.Length).Trim();
            return int.TryParse(suffix, out number) && number > 0;
        }
    }
}
