/*
    Fichier      :  BackgroundTicker.cs
    Projet       :  AlpenMux

    Description  :
        Action répétée en arrière-plan à intervalle fixe pendant une opération longue (suivi d'un téléchargement
        ou d'une installation). La libération (await using) arrête la boucle et attend sa fin : une erreur
        levée par l'action est alors transmise à l'appelant.
*/

using System;
using System.Threading;
using System.Threading.Tasks;

namespace AlpenMux.Services;

/// <summary> Boucle d'arrière-plan qui appelle une action à intervalle fixe jusqu'à sa libération. </summary>
public sealed class BackgroundTicker : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts;
    private readonly Task _loop;

    private BackgroundTicker(TimeSpan period, Action tick, CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;
        _loop = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(period, token); } catch (OperationCanceledException) { break; }
                tick();
            }
        });
    }

    /// <summary> Démarre la boucle : première exécution après une période. </summary>
    /// <param name="period"> Intervalle entre deux exécutions. </param>
    /// <param name="tick"> Action exécutée (sur un thread du pool). </param>
    /// <param name="ct"> Arrête aussi la boucle. </param>
    /// <returns> La boucle, à libérer avec await using. </returns>
    public static BackgroundTicker Start(TimeSpan period, Action tick, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tick);
        return new BackgroundTicker(period, tick, ct);
    }

    /// <summary> Arrête la boucle et attend la fin de l'exécution en cours. </summary>
    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _loop; }
        finally { _cts.Dispose(); }
    }
}
