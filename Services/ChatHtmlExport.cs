/*
    Fichier      :  ChatHtmlExport.cs
    Projet       :  AlpenMux

    Description  :
        Export PDF d'une conversation : page HTML autonome (marked.js embarqué, pas de réseau), ouverte dans le
        navigateur de l'utilisateur qui lance la boîte d'impression (« Enregistrer au format PDF » dans Chrome et
        Firefox). Les messages passent en JSON dans un bloc <script type="application/json"> : System.Text.Json
        échappe < et >, donc le texte du modèle ne peut pas fermer la balise.
        Les réponses sont rendues en Markdown côté navigateur ; les messages de l'utilisateur sont affichés tels quels.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using Avalonia.Platform;

namespace AlpenMux.Services;

/// <summary> Message exporté. </summary>
/// <param name="IsUser"> Message de l'utilisateur (affiché tel quel) ou de l'assistant (rendu en Markdown). </param>
/// <param name="Text"> Texte d'origine ou texte affiché (diagrammes déjà dessinés remplacés par leur image). </param>
/// <param name="ImageUris"> Images jointes (data URI). </param>
/// <param name="DocumentLines"> Lignes à afficher sous le message (documents joints). </param>
public sealed record ChatExportMessage(bool IsUser, string Text, IReadOnlyList<string> ImageUris, IReadOnlyList<string> DocumentLines);

/// <summary> Libellés de la page exportée (traduits par l'appelant). </summary>
/// <param name="You"> Titre des messages de l'utilisateur. </param>
/// <param name="Assistant"> Titre des réponses. </param>
/// <param name="Print"> Libellé du bouton d'impression. </param>
public sealed record ChatExportLabels(string You, string Assistant, string Print);

/// <summary> Construction de la page HTML d'export. </summary>
public static class ChatHtmlExport
{
    /// <summary> Texte de marked.js (Markdown → HTML), embarqué dans l'application. </summary>
    public static string ReadMarkedScript()
    {
        using var s = AssetLoader.Open(new Uri("avares://alpenmux/Assets/Scripts/marked.min.js"));
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    /// <summary> Page HTML complète pour la conversation. </summary>
    /// <param name="title"> Titre de la conversation. </param>
    /// <param name="messages"> Messages, dans l'ordre. </param>
    /// <param name="lang"> Langue de la page (fr, en). </param>
    /// <param name="labels"> Libellés de l'interface. </param>
    /// <param name="markedScript"> Texte de marked.js. </param>
    public static string Build(string title, IReadOnlyList<ChatExportMessage> messages, string lang, ChatExportLabels labels, string markedScript)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(markedScript);
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default,
        };
        var data = new
        {
            labels,
            messages = messages.Select(m => new { user = m.IsUser, text = m.Text, images = m.ImageUris, docs = m.DocumentLines }),
        };
        var json = JsonSerializer.Serialize(data, options);
        var t = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(title) ? "AlpenMux" : title.Trim());

        return $$"""
<!doctype html>
<html lang="{{lang}}">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{{t}}</title>
<style>
  body { font-family: system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif; color: #1d1f23; background: #fff;
         line-height: 1.55; font-size: 15px; max-width: 820px; margin: 2rem auto; padding: 0 1rem; }
  h1 { font-size: 1.5rem; color: #1F78AB; border-bottom: 2px solid #E8641A; padding-bottom: .4rem; }
  .toolbar { margin: 1rem 0; }
  .toolbar button { font: inherit; padding: .4rem .9rem; border: 1px solid #1F78AB; background: #1F78AB; color: #fff; border-radius: 6px; cursor: pointer; }
  .msg { margin: 1.2rem 0; padding-bottom: .8rem; border-bottom: 1px solid #e3e5e8; }
  .role { font-weight: 600; color: #1F78AB; font-size: .8rem; text-transform: uppercase; letter-spacing: .05em; margin-bottom: .3rem; }
  .user .body { white-space: pre-wrap; word-break: break-word; }
  .att { color: #666; font-size: .85rem; }
  img { max-width: 100%; height: auto; }
  pre { background: #f4f5f7; padding: .7rem; border-radius: 6px; white-space: pre-wrap; word-break: break-word; font-size: .85rem; }
  code { font-family: ui-monospace, Consolas, monospace; font-size: .9em; }
  table { border-collapse: collapse; margin: .5rem 0; }
  th, td { border: 1px solid #ccc; padding: .3rem .5rem; text-align: left; }
  blockquote { border-left: 3px solid #ccc; margin: .5rem 0; padding-left: .8rem; color: #555; }
  a { color: #1F78AB; }
  @page { margin: 18mm; }
  @media print {
    body { max-width: none; margin: 0; padding: 0; }
    .toolbar { display: none; }
    .msg { break-inside: auto; }
    pre, table, img { break-inside: avoid; }
  }
</style>
</head>
<body>
<h1>{{t}}</h1>
<div class="toolbar"><button id="print" type="button"></button></div>
<main id="chat"></main>
<script>{{markedScript}}</script>
<script id="data" type="application/json">{{json}}</script>
<script>
  const data = JSON.parse(document.getElementById('data').textContent);
  const chat = document.getElementById('chat');
  document.getElementById('print').textContent = data.labels.print;
  document.getElementById('print').addEventListener('click', () => window.print());
  for (const m of data.messages) {
    const section = document.createElement('section');
    section.className = 'msg ' + (m.user ? 'user' : 'assistant');
    const role = document.createElement('div');
    role.className = 'role';
    role.textContent = m.user ? data.labels.you : data.labels.assistant;
    section.appendChild(role);
    for (const d of m.docs) {
      const line = document.createElement('div');
      line.className = 'att';
      line.textContent = d;
      section.appendChild(line);
    }
    for (const src of m.images) {
      const img = document.createElement('img');
      img.src = src;
      section.appendChild(img);
    }
    const body = document.createElement('div');
    body.className = 'body';
    if (m.user) body.textContent = m.text;
    else body.innerHTML = marked.parse(m.text, { gfm: true });
    section.appendChild(body);
    chat.appendChild(section);
  }
  // Ouverture de la boîte d'impression : l'utilisateur choisit « Enregistrer au format PDF »
  window.addEventListener('load', () => setTimeout(() => window.print(), 400));
</script>
</body>
</html>
""";
    }
}
