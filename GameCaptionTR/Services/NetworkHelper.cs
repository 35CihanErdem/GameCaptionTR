using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace GameCaptionTR.Services;

public static class NetworkHelper
{
    public static bool IsInternetAvailable()
    {
        if (!NetworkInterface.GetIsNetworkAvailable())
        {
            return false;
        }

        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync("1.1.1.1", 53);
            return task.Wait(800) && client.Connected;
        }
        catch
        {
            return false;
        }
    }
}
