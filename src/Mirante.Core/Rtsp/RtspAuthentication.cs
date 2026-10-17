using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Mirante.Core.Security;

namespace Mirante.Core.Rtsp;

/// <summary>RTSP authentication (RFC 2617 Digest preferred, Basic as fallback).</summary>
internal static class RtspAuthentication
{
    public static string? BuildAuthorization(IEnumerable<string> challenges, CameraCredential credential,
        string method, string uri)
    {
        var list = challenges.ToList();
        var digest = list.FirstOrDefault(c => c.StartsWith("Digest ", StringComparison.OrdinalIgnoreCase));
        if (digest is not null)
        {
            return BuildDigest(ParseParameters(digest["Digest ".Length..]), credential, method, uri);
        }

        if (list.Any(c => c.StartsWith("Basic", StringComparison.OrdinalIgnoreCase)))
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credential.UserName}:{credential.Password}"));
            return $"Basic {token}";
        }

        return null;
    }

    internal static string? BuildDigest(IReadOnlyDictionary<string, string> challenge, CameraCredential credential,
        string method, string uri, string? clientNonce = null)
    {
        if (!challenge.TryGetValue("realm", out var realm) || !challenge.TryGetValue("nonce", out var nonce))
        {
            return null;
        }

        if (challenge.TryGetValue("algorithm", out var algorithm)
            && !algorithm.Equals("MD5", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var ha1 = Md5Hex($"{credential.UserName}:{realm}:{credential.Password}");
        var ha2 = Md5Hex($"{method}:{uri}");
        var header = new StringBuilder()
            .Append(CultureInfo.InvariantCulture,
                $"Digest username=\"{Escape(credential.UserName)}\", realm=\"{Escape(realm)}\", ")
            .Append(CultureInfo.InvariantCulture, $"nonce=\"{Escape(nonce)}\", uri=\"{Escape(uri)}\", ");

        var qopOffered = challenge.TryGetValue("qop", out var qop)
            && qop.Split(',').Any(q => q.Trim().Equals("auth", StringComparison.OrdinalIgnoreCase));
        if (qopOffered)
        {
            const string nonceCount = "00000001";
            var cnonce = clientNonce ?? Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
            var response = Md5Hex($"{ha1}:{nonce}:{nonceCount}:{cnonce}:auth:{ha2}");
            header.Append(CultureInfo.InvariantCulture,
                $"qop=auth, nc={nonceCount}, cnonce=\"{cnonce}\", response=\"{response}\"");
        }
        else
        {
            header.Append(CultureInfo.InvariantCulture, $"response=\"{Md5Hex($"{ha1}:{nonce}:{ha2}")}\"");
        }

        if (challenge.TryGetValue("opaque", out var opaque))
        {
            header.Append(CultureInfo.InvariantCulture, $", opaque=\"{Escape(opaque)}\"");
        }

        return header.ToString();
    }

    internal static Dictionary<string, string> ParseParameters(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && (text[i] == ',' || char.IsWhiteSpace(text[i])))
            {
                i++;
            }

            var eq = text.IndexOf('=', i);
            if (eq < 0)
            {
                break;
            }

            var key = text[i..eq].Trim();
            i = eq + 1;
            string value;
            if (i < text.Length && text[i] == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        i++;
                    }

                    sb.Append(text[i++]);
                }

                i++; // closing quote
                value = sb.ToString();
            }
            else
            {
                var end = text.IndexOf(',', i);
                end = end < 0 ? text.Length : end;
                value = text[i..end].Trim();
                i = end;
            }

            if (key.Length > 0)
            {
                result[key] = value;
            }
        }

        return result;
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal);

#pragma warning disable CA5351 // RTSP Digest authentication (RFC 2617) mandates MD5; it is not used to protect data.
    private static string Md5Hex(string value) => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(value)));
#pragma warning restore CA5351
}
