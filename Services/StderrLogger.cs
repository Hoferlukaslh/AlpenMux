/*
    Fichier      :  StderrLogger.cs
    Projet       :  AlpenMux

    Description  :
        Fournisseur de journalisation (Microsoft.Extensions.Logging) qui écrit les messages d'avertissement
        et d'erreur sur la sortie d'erreur, tels quels : « [Settings] Lecture impossible : … ».
        Les messages de niveau Debug ne sont visibles que dans le débogueur (fournisseur Debug).
*/

using System;
using Microsoft.Extensions.Logging;

namespace AlpenMux.Services;

/// <summary> Crée les <see cref="StderrLogger"/> (un par catégorie, sans état partagé). </summary>
public sealed class StderrLoggerProvider : ILoggerProvider
{
    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new StderrLogger();

    /// <inheritdoc />
    public void Dispose() { }
}

/// <summary> Écrit le message formaté (et l'exception éventuelle) sur la sortie d'erreur, à partir du niveau Information. </summary>
public sealed class StderrLogger : ILogger
{
    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information && logLevel != LogLevel.None;

    /// <inheritdoc />
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        Console.Error.WriteLine(formatter(state, exception));
    }
}
