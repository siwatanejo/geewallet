namespace GWallet.Frontend.Maui

open System 
open Gtk 
open Microsoft.Maui 
open Microsoft.Maui.Graphics 
open Microsoft.Maui.Hosting 

type GtkApp() = 
    inherit MauiGtkApplication()
    
    // force Gtk to not use DBus
    override _.ApplicationId = null

    override _.CreateMauiApp() = MauiProgram.CreateMauiApp()
