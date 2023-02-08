namespace GWallet.Frontend.Maui

#if GTK
open Gdk
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.DependencyInjection.Extensions
#endif
open Microsoft.Maui
open Microsoft.Maui.Controls
open Microsoft.Maui.Controls.Compatibility
open Microsoft.Maui.Controls.Compatibility.Hosting
open Microsoft.Maui.Controls.Hosting
open Microsoft.Maui.Hosting
open Microsoft.Maui.LifecycleEvents
open Microsoft.Maui.Controls.Internals

type MauiProgram =
    static member CreateMauiApp() =
        let appBuilder = 
            MauiApp
                .CreateBuilder()
                .UseMauiApp<App>()
#if GTK 
                .UseMauiCompatibility()
#endif
        appBuilder
            .ConfigureFonts(fun fonts ->
                fonts
                    .AddFont("OpenSans-Regular.ttf", "OpenSansRegular")
                    .AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold")
                |> ignore
            ) |> ignore

        appBuilder.Build()


        