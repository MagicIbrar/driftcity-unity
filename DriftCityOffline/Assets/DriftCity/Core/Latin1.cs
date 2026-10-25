namespace DriftCity.Core
{
    /// <summary>Byte-to-char decoding that works without codepage providers (Unity Mono / .NET 8).</summary>
    public static class Latin1
    {
        public static string Decode(byte[] data, int offset, int count)
        {
            var chars = new char[count];
            for (int i = 0; i < count; i++) chars[i] = (char)data[offset + i];
            return new string(chars);
        }
    }
}
