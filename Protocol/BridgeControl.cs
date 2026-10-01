using System.Text;
using System.Globalization;
using UnifiedPicoModule.Core;

namespace UnifiedPicoModule.Protocol;

public static class BridgeControl
{
    public static ReadOnlySpan<byte> Discover => "DISCOVER_DAEMON"u8;
    public static ReadOnlySpan<byte> Ping => "MARCO\0"u8;
    public static ReadOnlySpan<byte> Polo => "POLO"u8;
    public static ReadOnlySpan<byte> Stop => "STOP"u8;
    public static bool IsSubscriptionLine(ReadOnlySpan<byte> data) => data.StartsWith("PXR_SUB_ACK"u8);
    public static byte[] Subscribe(string id, int mask) => Encoding.ASCII.GetBytes($"PXR_SUB id={id} mask={mask}");
    public static bool TryParseSubscription(ReadOnlySpan<byte> data, out string id, out int mask, out ulong epoch)
    {
        id = ""; mask = 0; epoch = 0;
        // Exact canonical ASCII grammar: no duplicate keys, extension fields or whitespace.
        if (data.Length != 61) return false;
        foreach (var b in data) if (b is < 32 or > 126) return false;
        var text = Encoding.ASCII.GetString(data);
        if (!text.StartsWith("PXR_SUB_ACK id=", StringComparison.Ordinal) ||
            text.Substring(31, 6) != " mask=" || text.Substring(38, 7) != " epoch=" || text[37] is < '0' or > '3') return false;
        id = text.Substring(15, 16);
        var epochText = text.Substring(45, 16);
        if (!id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ||
            !epochText.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ||
            !ulong.TryParse(epochText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out epoch) ||
            epoch is 0 or > long.MaxValue) return false;
        mask = text[37] - '0'; return true;
    }

    public static bool IsPing(ReadOnlySpan<byte> data) => data.SequenceEqual(Ping);
    public static bool IsModeLine(ReadOnlySpan<byte> data) => data.StartsWith("PXR_MODE"u8);

    public static bool TryParseMode(ReadOnlySpan<byte> data, out CapabilitySnapshot capabilities)
    {
        capabilities = new();
        if (data.Length is < 10 or > 256) return false;
        // No replacement decoding: non-ASCII/control bytes cannot masquerade as capability tokens.
        foreach (var b in data)
            if (b is < 32 or > 126) return false;
        var tokens = Encoding.ASCII.GetString(data).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0 || tokens[0] != "PXR_MODE") return false;
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var token in tokens.AsSpan(1))
        {
            var separator = token.IndexOf('=');
            if (separator <= 0 || separator == token.Length - 1 ||
                !fields.TryAdd(token[..separator], token[(separator + 1)..])) return false;
        }
        // Unknown keys never unlock enhancement. The Bridge sender emits exactly these five fields.
        if (fields.Count != 5 ||
            !fields.TryGetValue("mode", out var mode) || mode is not ("normal" or "enhance") ||
            !Binary("rooted", fields, out var rooted) || !Binary("enhance", fields, out var module) ||
            !Binary("gate", fields, out var gate) || !fields.TryGetValue("plugin", out var plugin)) return false;
        var enhanced = mode == "enhance" && rooted && module && gate;
        var knownPlugin = plugin is "off" or "left" or "right" or "dual" ? plugin : "off";
        capabilities = new(true, enhanced, enhanced && knownPlugin == "dual", knownPlugin);
        return true;
    }

    private static bool Binary(string name, Dictionary<string, string> fields, out bool value)
    {
        value = false;
        if (!fields.TryGetValue(name, out var text) || text is not ("0" or "1")) return false;
        value = text == "1";
        return true;
    }
}
