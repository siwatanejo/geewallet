#if XAMARIN
namespace GWallet.Frontend.XF
#else
namespace GWallet.Frontend.Maui
#endif

open System

#if !XAMARIN
open Microsoft.Maui.Controls
open Microsoft.Maui.Controls.Xaml
open Microsoft.Maui.ApplicationModel
open Microsoft.Maui.ApplicationModel.DataTransfer
open Microsoft.Maui.Networking

open ZXing.Net.Maui
open ZXing.Net.Maui.Controls
#else
open Xamarin.Forms
open Xamarin.Forms.Xaml
open Xamarin.Essentials
open ZXing
open ZXing.Net.Mobile.Forms
open ZXing.Common
#endif

open GWallet.Backend

type ReceivePage() =
    inherit ContentPage()
    let _ = base.LoadFromXaml(typeof<ReceivePage>)

    (*
    override self.OnBackButtonPressed() =
        MainThread.BeginInvokeOnMainThread(fun _ ->
            balancesPage.Navigation.PopAsync() |> FrontendHelpers.DoubleCheckCompletion
        )
        true
    *)
