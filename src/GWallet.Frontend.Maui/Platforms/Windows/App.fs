// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace GWallet.Frontend.Maui.WinUI

open Microsoft.UI.Xaml
open Microsoft.Maui
open Microsoft.Maui.Controls.Xaml
open System

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>

type App() =
    inherit FSharp.Maui.WinUICompat.App()
    
    override this.CreateMauiApp() = GWallet.Frontend.Maui.MauiProgram.CreateMauiApp()


module Program =
    [<EntryPoint>]
    [<STAThread>]
    let main args =
        do FSharp.Maui.WinUICompat.Program.Main(args, typeof<App>)
        0
