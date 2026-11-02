/*
    Fichier      :  KnowledgePage.axaml.cs
    Projet       :  AlpenMux

    Description  :
        Page Connaissances : code-behind vide, la page est entièrement liée à KnowledgeViewModel (RAG).
*/

using Avalonia.Controls;

namespace AlpenMux.Views.Pages;

/// <summary> Page Connaissances : bases de connaissances pour discuter avec ses documents. </summary>
public partial class KnowledgePage : UserControl
{
    /// <summary> Crée la page. </summary>
    public KnowledgePage() => InitializeComponent();
}
