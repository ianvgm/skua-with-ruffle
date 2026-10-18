using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using Skua.Core.Interfaces;
using Skua.WPF.Flash;
using System.Windows;
using System.Windows.Controls;

namespace Skua.WPF.UserControls;

public partial class GameContainerUserControl : UserControl
{
    private IScriptInterface _bot;

    public GameContainerUserControl()
    {
        InitializeComponent();
        _bot = Ioc.Default.GetRequiredService<IScriptInterface>();
        gameContainer.Visibility = Visibility.Hidden;
        WeakReferenceMessenger.Default.Register<GameContainerUserControl, RuffleViewChangedMessage>(this, (r, m) => r.gameContainer.Child = m.View);
        Loaded += GameContainer_Loaded;
    }

    private void GameContainer_Loaded(object sender, RoutedEventArgs e)
    {
        _bot.Flash.FlashCall += LoadingFlash;
        _bot.Flash.InitializeFlash();
        Loaded -= GameContainer_Loaded;
    }

    private void LoadingFlash(string function, params object[] args)
    {
        if (function == "loaded")
        {
            LoadingBar.Visibility = Visibility.Hidden;
            gameContainer.Visibility = Visibility.Visible;
            _bot.Flash.FlashCall -= LoadingFlash;
        }
    }
}
