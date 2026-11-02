/*
    Fichier      :  ImagePreview.axaml.cs
    Projet       :  AlpenMux

    Description  :
        Visionneuse d'image partagée (chat et carrousel des images) : zoom (boutons, molette, glisser, double-clic),
        copie dans le presse-papiers et téléchargement en PNG. Le contrôle agit seul sur les services ; la page
        ne fournit que l'image, le nom suggéré pour le fichier et la commande de fermeture.
*/

using System;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using AlpenMux.Services;

namespace AlpenMux.Controls;

/// <summary> Image zoomable avec barre d'outils (copier, télécharger, fermer). </summary>
public partial class ImagePreview : UserControl
{
    /// <summary> Image affichée. </summary>
    public static readonly StyledProperty<Bitmap?> PictureProperty =
        AvaloniaProperty.Register<ImagePreview, Bitmap?>(nameof(Picture));

    /// <summary> Commande du bouton fermer (null = pas de fermeture à gérer ici). </summary>
    public static readonly StyledProperty<ICommand?> CloseCommandProperty =
        AvaloniaProperty.Register<ImagePreview, ICommand?>(nameof(CloseCommand));

    /// <summary> Nom de fichier proposé au téléchargement. </summary>
    public static readonly StyledProperty<string> SuggestedNameProperty =
        AvaloniaProperty.Register<ImagePreview, string>(nameof(SuggestedName), "image.png");

    /// <summary> Image affichée. </summary>
    public Bitmap? Picture
    {
        get => GetValue(PictureProperty);
        set => SetValue(PictureProperty, value);
    }

    /// <summary> Commande du bouton fermer. </summary>
    public ICommand? CloseCommand
    {
        get => GetValue(CloseCommandProperty);
        set => SetValue(CloseCommandProperty, value);
    }

    /// <summary> Nom de fichier proposé au téléchargement. </summary>
    public string SuggestedName
    {
        get => GetValue(SuggestedNameProperty);
        set => SetValue(SuggestedNameProperty, value);
    }

    static ImagePreview()
    {
        PictureProperty.Changed.AddClassHandler<ImagePreview>((p, _) => p.View.Source = p.Picture);
    }

    /// <summary> Crée la visionneuse. </summary>
    public ImagePreview()
    {
        InitializeComponent();
        View.ZoomChanged += (_, _) => ZoomLabel.Text = $"{View.Zoom * 100:0} %";
    }

    private void OnZoomIn(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => View.ZoomBy(1.25);

    private void OnZoomOut(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => View.ZoomBy(0.8);

    private void OnFit(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => View.Reset();

    private async void OnCopy(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Picture is { } image) await App.Services.GetRequiredService<IUiServices>().CopyImageAsync(image);
    }

    private async void OnDownload(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Picture is { } image) await App.Services.GetRequiredService<IUiServices>().SaveImageAsync(image, SuggestedName);
    }

    private void OnClose(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (CloseCommand is { } command && command.CanExecute(null)) command.Execute(null);
    }
}
