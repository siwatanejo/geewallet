namespace GWallet.Frontend.Maui

#if GTK
open Gdk
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.DependencyInjection.Extensions
open Microsoft.Maui.Graphics.Platform.Gtk
#endif
open Microsoft.Maui
open Microsoft.Maui.Controls
open Microsoft.Maui.Controls.Compatibility
open Microsoft.Maui.Controls.Compatibility.Hosting
open Microsoft.Maui.Controls.Hosting
open Microsoft.Maui.Hosting
open Microsoft.Maui.LifecycleEvents
open Microsoft.Maui.Handlers

type MauiProgram =
    static member CreateMauiApp() =
        let app =
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
#if GTK        
        Handlers.LabelHandler.Mapper.PrependToMapping(
            "Label.LineBreakMode",
            fun (handler: ILabelHandler) (view: ILabel) ->
                let lineBreakMode = (view :?> Label).LineBreakMode
                handler.PlatformView.LineWrapMode <-
                    match lineBreakMode with
                    | LineBreakMode.CharacterWrap -> Pango.WrapMode.Char
                    | _ -> Pango.WrapMode.Word
                handler.PlatformView.LineWrap <-
                    match lineBreakMode with
                    | LineBreakMode.NoWrap -> false
                    | _ -> true )
#endif        
        app
