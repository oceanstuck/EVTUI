using System;
using System.ComponentModel;
using System.IO;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;

using EVTUI.ViewModels;

namespace EVTUI.Views;

public partial class EditorWindow : Window
{
    public EditorWindow()
    {
        InitializeComponent();
    }

    public async void SaveMod(object? sender, RoutedEventArgs args)
    {
        try
        {
            ((EditorWindowViewModel)DataContext).SaveMod(((MenuItem)sender).Name);
            await Utils.RaiseModal(this, "Saved successfully!");
        }
        catch (IOException)
        {
            await Utils.RaiseModal(this, "Failed to save because the game files are in use.\nIf the game is currently open, close it before trying again.");
        }
        catch (Exception ex)
        {
            await Utils.RaiseModal(this, "Failed to save due to unhandled exception: '" + ex.ToString() + "'");
        }
    }

    private void ToggleTheme(object? sender, RoutedEventArgs e)
    {
        if (Application.Current!.RequestedThemeVariant == ThemeVariant.Dark)
            Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        else
            Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
    }
}
