/*
    Fichier      :  RingBuffer.cs
    Projet       :  AlpenMux

    Description  :
        Historique de taille fixe pour les courbes (Sparkline).
        Levé Changed à chaque ajout pour redessiner le graphique.
*/

using System;
using System.Collections.Generic;
using System.Linq;

namespace AlpenMux.Services;

/// <summary> Tampon circulaire de valeurs (courbes du moniteur). </summary>
public sealed class RingBuffer
{
    private double[] _data;
    private int _start, _count;

    /// <summary> Crée un tampon de cette capacité. </summary>
    public RingBuffer(int capacity) => _data = new double[capacity];

    /// <summary> Nombre maximal de valeurs. </summary>
    public int Capacity => _data.Length;
    /// <summary> Nombre de valeurs présentes. </summary>
    public int Count => _count;

    /// <summary> Levé après chaque ajout ou effacement. </summary>
    public event Action? Changed;

    /// <summary> Ajoute une valeur (la plus ancienne est retirée si le tampon est plein). </summary>
    public void Add(double v)
    {
        if (_count < _data.Length) _data[(_start + _count++) % _data.Length] = v;
        else { _data[_start] = v; _start = (_start + 1) % _data.Length; }
        Changed?.Invoke();
    }

    /// <summary> Change la taille en gardant les valeurs les plus récentes. </summary>
    public void Resize(int capacity)
    {
        capacity = Math.Max(2, capacity);
        if (capacity == _data.Length) return;
        var keep = Values().TakeLast(capacity).ToArray();
        _data = new double[capacity];
        Array.Copy(keep, _data, keep.Length);
        (_start, _count) = (0, keep.Length);
        Changed?.Invoke();
    }

    /// <summary> Vide le tampon. </summary>
    public void Clear() { _start = _count = 0; Changed?.Invoke(); }

    /// <summary> Valeurs, de la plus ancienne à la plus récente. </summary>
    public IEnumerable<double> Values()
    {
        for (int i = 0; i < _count; i++) yield return _data[(_start + i) % _data.Length];
    }

    /// <summary> Plus grande valeur (0 si vide). </summary>
    public double Max()
    {
        double m = 0;
        foreach (var v in Values()) if (v > m) m = v;
        return m;
    }
}
