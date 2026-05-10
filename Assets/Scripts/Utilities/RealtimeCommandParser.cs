using System;
using System.Collections.Generic;

namespace Forest
{
    public static class RealtimeCommandParser
    {
        public static string NormalizeTimeOfDay(string value, string currentValue)
        {
            string normalized = NormalizeToken(value);

            if (ShouldPreserveOption(normalized))
            {
                return currentValue;
            }

            switch (normalized)
            {
                case "dawn":
                case "morning":
                case "earlymorning":
                case "sunrise":
                case "daybreak":
                case "firstlight":
                    return "dawn";
                case "day":
                case "daylight":
                case "daytime":
                case "noon":
                case "midday":
                case "afternoon":
                case "sunny":
                case "bright":
                    return "day";
                case "sunset":
                case "evening":
                case "dusk":
                case "twilight":
                case "goldenhour":
                case "sundown":
                    return "sunset";
                case "night":
                case "nighttime":
                case "midnight":
                case "moonlight":
                case "moonlit":
                case "dark":
                    return "night";
                default:
                    return currentValue;
            }
        }

        public static string NormalizeWeather(string value, string currentValue)
        {
            string normalized = NormalizeToken(value);

            if (ShouldPreserveOption(normalized))
            {
                return currentValue;
            }

            switch (normalized)
            {
                case "clear":
                case "clearsky":
                case "sunny":
                case "sunshine":
                case "nice":
                case "calm":
                    return "clear";
                case "fog":
                case "foggy":
                case "mist":
                case "misty":
                case "haze":
                case "hazy":
                case "cloudy":
                case "clouds":
                case "overcast":
                case "smoky":
                    return "fog";
                case "rain":
                case "rainy":
                case "drizzle":
                case "drizzly":
                case "shower":
                case "showers":
                case "downpour":
                case "pouring":
                case "wet":
                    return "rain";
                case "storm":
                case "stormy":
                case "thunder":
                case "thunderstorm":
                case "lightning":
                case "tempest":
                case "squall":
                    return "storm";
                case "snow":
                case "snowy":
                case "snowing":
                case "flurry":
                case "flurries":
                case "blizzard":
                case "sleet":
                case "hail":
                case "icy":
                case "frost":
                    return "snow";
                default:
                    return currentValue;
            }
        }

        public static string CleanAtmosphereMood(string mood)
        {
            if (string.IsNullOrWhiteSpace(mood))
            {
                return "calm";
            }

            return ForestText.Shorten(mood.Trim().ToLowerInvariant(), 32);
        }

        public static string CleanWorkThreadRequest(string request)
        {
            return ForestText.Shorten(ForestText.CollapseWhitespace(request), 360);
        }

        public static string CleanWorkThreadTitle(string title, string request)
        {
            string candidate = ForestText.CollapseWhitespace(title);

            if (string.IsNullOrWhiteSpace(candidate))
            {
                candidate = ForestText.CollapseWhitespace(request);
            }

            if (string.IsNullOrWhiteSpace(candidate))
            {
                return string.Empty;
            }

            candidate = candidate.Trim().TrimEnd('.', '?', '!');

            const string worldPrefix = "Underwater:";

            if (candidate.StartsWith(worldPrefix, StringComparison.OrdinalIgnoreCase))
            {
                candidate = candidate.Substring(worldPrefix.Length).Trim();
            }

            return ForestText.Shorten(candidate, 72);
        }

        public static string ReadString(Dictionary<string, object> arguments, params string[] names)
        {
            if (arguments == null)
            {
                return null;
            }

            for (int i = 0; i < names.Length; i++)
            {
                if (arguments.TryGetValue(names[i], out object value) && value != null)
                {
                    return value as string ?? Convert.ToString(value);
                }
            }

            return null;
        }

        public static float ReadFloat(Dictionary<string, object> arguments, string name, float defaultValue)
        {
            if (arguments == null || !arguments.TryGetValue(name, out object value) || value == null)
            {
                return defaultValue;
            }

            if (value is float floatValue)
            {
                return floatValue;
            }

            if (value is double doubleValue)
            {
                return (float)doubleValue;
            }

            if (value is int intValue)
            {
                return intValue;
            }

            return float.TryParse(Convert.ToString(value), out float parsed) ? parsed : defaultValue;
        }

        private static string NormalizeToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return value.Trim().ToLowerInvariant().Replace("-", string.Empty).Replace("_", string.Empty).Replace(" ", string.Empty);
        }

        private static bool ShouldPreserveOption(string normalized)
        {
            return string.IsNullOrEmpty(normalized)
                || normalized == "preserve"
                || normalized == "same"
                || normalized == "current"
                || normalized == "unchanged";
        }
    }
}
