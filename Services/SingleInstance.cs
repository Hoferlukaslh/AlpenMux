/*
    Fichier      :  SingleInstance.cs
    Projet       :  AlpenMux

    Description  :
        Une seule instance de l'application à la fois : si elle tourne déjà (par exemple en
        arrière-plan), un nouveau lancement demande simplement à l'instance existante de
        réafficher sa fenêtre, puis se termine. Communication par socket Unix dans $XDG_RUNTIME_DIR.
*/

using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace AlpenMux.Services;

/// <summary> Instance unique de l'application : un second lancement affiche la fenêtre de la première (socket local). </summary>
public static class SingleInstance
{
    private static Socket? _listener;

    private static string SocketPath =>
        Path.Combine(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } d ? d : Path.GetTempPath(),
            $"{AppPaths.Id}-{Environment.UserName}.sock");

    /// <summary> Si une instance tourne déjà, lui demande de s'afficher et retourne true. </summary>
    public static bool NotifyExisting()
    {
        if (!File.Exists(SocketPath)) return false;
        try
        {
            using var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            s.Connect(new UnixDomainSocketEndPoint(SocketPath));
            s.Send(Encoding.UTF8.GetBytes("show\n"));
            return true;
        }
        catch
        {
            // Socket orphelin (application arrêtée brutalement)
            try { File.Delete(SocketPath); } catch { /* suppression facultative (fichier déjà absent ou verrouillé) */ }
            return false;
        }
    }

    /// <summary> Écoute les demandes des lancements suivants ; <paramref name="onShow"/> est appelé pour chacune. </summary>
    public static void Listen(Action onShow)
    {
        try
        {
            if (File.Exists(SocketPath)) File.Delete(SocketPath);
            _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
            _listener.Listen(4);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SingleInstance] {ex.Message}");
            return;
        }

        Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var client = await _listener.AcceptAsync();
                    var buf = new byte[64];
                    var n = await client.ReceiveAsync(buf);
                    if (Encoding.UTF8.GetString(buf, 0, n).StartsWith("show")) onShow();
                }
                catch { return; }
            }
        });
    }

    /// <summary> Ferme l'écoute et supprime le socket. </summary>
    public static void Release()
    {
        try { _listener?.Close(); File.Delete(SocketPath); } catch { /* suppression facultative (fichier déjà absent ou verrouillé) */ }
    }
}
