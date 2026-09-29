using System.Net;
using System.Net.Sockets;

namespace PaymentGateway.Services.Webhooks;

/// <summary>
/// Rejeita destinos internos de webhooks. A validação sintática é usada no
/// cadastro; a validação de IP ocorre novamente na conexão TCP efetiva.
/// </summary>
public static class WebhookTargetValidator
{
    public static bool TryValidate(string? url, bool allowLoopback, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(url) || url.Length > 2048 ||
            url.Contains('\\') || url.Any(char.IsControl) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var candidate) ||
            !string.IsNullOrEmpty(candidate.UserInfo) ||
            !string.IsNullOrEmpty(candidate.Fragment))
            return false;

        var host = candidate.DnsSafeHost.TrimEnd('.');
        var isLoopbackHost = IsLoopbackHost(host);

        if (allowLoopback && isLoopbackHost &&
            candidate.Scheme is "http" or "https")
        {
            uri = candidate;
            return true;
        }

        // Portas arbitrárias aumentam muito a superfície de SSRF. Para
        // destinos públicos, só HTTPS padrão é aceito.
        if (candidate.Scheme != Uri.UriSchemeHttps || candidate.Port != 443 ||
            candidate.HostNameType != UriHostNameType.Dns || !host.Contains('.') ||
            host.StartsWith('.') || host.EndsWith('.') ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".test", StringComparison.OrdinalIgnoreCase))
            return false;

        uri = candidate;
        return true;
    }

    internal static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context, bool allowLoopback,
        CancellationToken cancellationToken)
    {
        var endpoint = context.DnsEndPoint;
        var host = endpoint.Host.TrimEnd('.');
        var loopbackHost = allowLoopback && IsLoopbackHost(host);

        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literalAddress))
            addresses = [literalAddress];
        else
            addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);

        if (addresses.Length == 0 ||
            addresses.Any(address => loopbackHost
                ? !IPAddress.IsLoopback(address)
                : !IsPublicAddress(address)))
            throw new HttpRequestException("Unsafe webhook destination.");

        // DNS é resolvido uma vez, verificado e conectado pelo endereço IP
        // aprovado. O HttpClient conserva o hostname original para TLS/SNI.
        Exception? lastError = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };
            try
            {
                await socket.ConnectAsync(address, endpoint.Port, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                if (cancellationToken.IsCancellationRequested)
                    throw;
                lastError = ex;
            }
        }

        throw new HttpRequestException("Webhook destination unavailable.", lastError);
    }

    private static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var a = bytes[0];
            var b = bytes[1];
            var c = bytes[2];
            return a is > 0 and < 224 &&
                   a != 10 && a != 127 &&
                   !(a == 100 && b is >= 64 and <= 127) &&
                   !(a == 169 && b == 254) &&
                   !(a == 172 && b is >= 16 and <= 31) &&
                   !(a == 192 && (b == 0 || b == 168)) &&
                   !(a == 192 && b == 88 && c == 99) &&
                   !(a == 198 && (b is 18 or 19 || b == 51 && c == 100)) &&
                   !(a == 203 && b == 0 && c == 113);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return bytes[0] is >= 0x20 and <= 0x3f &&
                   !(bytes[0] == 0x20 && bytes[1] == 0x01 &&
                     bytes[2] == 0x0d && bytes[3] == 0xb8);

        return false;
    }
}
