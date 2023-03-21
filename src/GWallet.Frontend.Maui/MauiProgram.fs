namespace GWallet.Frontend.Maui

#if GTK
open Gdk
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.DependencyInjection.Extensions
open Microsoft.Maui.Graphics.Platform.Gtk
open GWallet.Backend.FSharpUtil.ReflectionlessPrint
#endif
open Microsoft.Maui
open Microsoft.Maui.Controls
open Microsoft.Maui.Controls.Compatibility.Hosting
open Microsoft.Maui.Controls.Hosting
open Microsoft.Maui.Handlers
open Microsoft.Maui.Hosting


type MauiProgram =
    static member CreateMauiApp() =
#if GTK
        ContentViewHandler.ViewMapper.AppendToMapping(
             "BorderColor",
             fun handler view ->
                 match view with
                 | :? Frame as frame ->
                    let contentView = handler.PlatformView :?> Microsoft.Maui.Platform.ContentView
                    let colorString = frame.BorderColor.ToGdkRgba().ToString()
                    let mainNode = contentView.CssMainNode()
                    contentView.SetStyleValueNode(SPrintF1 "1px solid %s" colorString, mainNode, "border")
                 | _ -> ())
        
        ContentViewHandler.ViewMapper.AppendToMapping(
             "CornerRadius",
             fun handler view ->
                 match view with
                 | :? Frame as frame ->
                    let contentView = handler.PlatformView :?> Microsoft.Maui.Platform.ContentView
                    let mainNode = contentView.CssMainNode()
                    contentView.SetStyleValueNode(SPrintF1 "%fpx" frame.CornerRadius, mainNode, "border-radius")
                 | _ -> ())
#endif        
        MauiApp
            .CreateBuilder()
            .UseMauiApp<App>()
#if GTK 
            .UseMauiCompatibility()
#endif
            .ConfigureFonts(fun fonts ->
                fonts
                    .AddFont("OpenSans-Regular.ttf", "OpenSansRegular")
                    .AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold")
                |> ignore
            )
            .Build()
