/*
    Fichier      :  ViewModelBase.cs
    Projet       :  AlpenMux

    Description  :
        Base des ViewModels de page : notification des propriétés et appel à l'affichage de la page.
*/

using CommunityToolkit.Mvvm.ComponentModel;

namespace AlpenMux.ViewModels;

/// <summary> Base des ViewModels de page. </summary>
public abstract class ViewModelBase : ObservableObject
{
    /// <summary> Appelé quand la page devient visible (rafraîchir ce qui a pu changer). </summary>
    public virtual void OnNavigatedTo() { }
}
