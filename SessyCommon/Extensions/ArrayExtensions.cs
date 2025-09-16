namespace SessyCommon.Extensions
{
    public static class ArrayExtensions
    {
        /// <summary>
        /// Used to deserialize a JSON string to an array of type T.
        /// </summary>
        public static T[] StringToArray<T>(this string? input)
        {
            return string.IsNullOrEmpty(input) ? Array.Empty<T>() : System.Text.Json.JsonSerializer.Deserialize<T[]>(input)!;
        }

        /// <summary>
        /// Used to serialize an array of type T to a JSON string.
        /// </summary>
        public static string StringFromArray<T>(this T[]? array)
        {
            return System.Text.Json.JsonSerializer.Serialize(array);
        }
    }
}
